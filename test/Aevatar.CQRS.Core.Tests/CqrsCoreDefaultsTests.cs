using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Core.Tests;

public class DefaultCommandContextPolicyTests
{
    [Fact]
    public void Create_ShouldGenerateIds_AndCloneHeaders_WhenIdsNotProvided()
    {
        var policy = new DefaultCommandContextPolicy();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["k"] = "v",
        };

        var context = policy.Create("actor-1", headers);

        context.TargetId.Should().Be("actor-1");
        context.CommandId.Should().NotBeNullOrWhiteSpace();
        context.CorrelationId.Should().Be(context.CommandId);
        context.Headers.Should().ContainKey("k").WhoseValue.Should().Be("v");
        context.Headers.Should().NotBeSameAs(headers);
    }

    [Fact]
    public void Create_ShouldUseProvidedIds_WhenSpecified()
    {
        var policy = new DefaultCommandContextPolicy();

        var context = policy.Create(
            "actor-1",
            commandId: "cmd-1",
            correlationId: "corr-1");

        context.CommandId.Should().Be("cmd-1");
        context.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public void Create_ShouldThrow_WhenTargetIsBlank()
    {
        var policy = new DefaultCommandContextPolicy();

        Action act = () => policy.Create("   ");

        act.Should().Throw<ArgumentException>();
    }
}

public class CommandDispatchPipelineTests
{
    [Fact]
    public async Task DispatchAsync_ShouldResolveDispatchAndCreateReceipt()
    {
        var order = new List<string>();
        var target = new FakeCommandTarget("actor-1");
        var resolver = new RecordingResolver(target, order);
        var envelopeFactory = new RecordingEnvelopeFactory(new EventEnvelope { Id = "evt-1" }, order);
        var dispatcher = new RecordingTargetDispatcher(order);
        var receiptFactory = new RecordingReceiptFactory("receipt-1", order);
        var pipeline = new DefaultCommandDispatchPipeline<string, FakeCommandTarget, string, FakeError>(
            resolver,
            new DefaultCommandContextPolicy(),
            envelopeFactory,
            dispatcher,
            receiptFactory);

        var result = await pipeline.DispatchAsync("hello");

        result.Succeeded.Should().BeTrue();
        result.Target.Should().NotBeNull();
        result.Target!.Target.TargetId.Should().Be("actor-1");
        result.Target.Context.TargetId.Should().Be("actor-1");
        result.Target.Envelope.Id.Should().Be("evt-1");
        result.Target.Receipt.Should().Be("receipt-1");
        result.Target.Admission.Should().NotBeNull();
        result.Target.Admission!.Accepted.Should().BeTrue();
        result.Target.Admission.CommandId.Should().Be("evt-1");
        dispatcher.Calls.Should().ContainSingle(x => x.Target == target && x.Envelope.Id == "evt-1");
        receiptFactory.Calls.Should().ContainSingle(x => x.Target == target);
        order.Should().Equal("resolve", "envelope", "receipt", "dispatch");
    }

    [Fact]
    public async Task PrepareAsync_ShouldResolveCreateEnvelopeAndReceipt_WithoutBindingOrDispatch()
    {
        var order = new List<string>();
        var target = new FakeCommandTarget("actor-1");
        var dispatcher = new RecordingTargetDispatcher(order);
        var pipeline = new DefaultCommandDispatchPipeline<string, FakeCommandTarget, string, FakeError>(
            new RecordingResolver(target, order),
            new DefaultCommandContextPolicy(),
            new RecordingEnvelopeFactory(new EventEnvelope { Id = "evt-1" }, order),
            dispatcher,
            new RecordingReceiptFactory("receipt-1", order));

        var result = await pipeline.PrepareAsync("hello");

        result.Succeeded.Should().BeTrue();
        dispatcher.Calls.Should().BeEmpty();
        order.Should().Equal("resolve", "envelope", "receipt");
    }

    [Fact]
    public async Task PrepareAsync_ShouldUseTargetAwareEnvelopeFactory_WhenRegistered()
    {
        var order = new List<string>();
        var target = new FakeCommandTarget("actor-1");
        var targetEnvelopeFactory = new RecordingTargetEnvelopeFactory(new EventEnvelope { Id = "evt-1" }, order);
        var pipeline = new DefaultCommandDispatchPipeline<string, FakeCommandTarget, string, FakeError>(
            new RecordingResolver(target, order),
            new DefaultCommandContextPolicy(),
            targetEnvelopeFactory,
            new RecordingTargetDispatcher(order),
            new RecordingReceiptFactory("receipt-1", order));

        var result = await pipeline.PrepareAsync("hello");

        result.Succeeded.Should().BeTrue();
        targetEnvelopeFactory.Calls.Should().ContainSingle()
            .Which.Should().Be(("hello", target, result.Target!.Context));
        result.Target.Envelope.Id.Should().Be("evt-1");
        order.Should().Equal("resolve", "target-envelope", "receipt");
    }

    [Fact]
    public async Task DispatchAsync_ShouldCleanupTarget_WhenDispatcherFails()
    {
        var target = new FakeCommandTarget("actor-1");
        var pipeline = new DefaultCommandDispatchPipeline<string, FakeCommandTarget, string, FakeError>(
            new RecordingResolver(target),
            new DefaultCommandContextPolicy(),
            new RecordingEnvelopeFactory(new EventEnvelope { Id = "evt-1" }),
            new ThrowingTargetDispatcher(),
            new RecordingReceiptFactory("unused"));

        var act = () => pipeline.DispatchAsync("hello");

        await act.Should().ThrowAsync<InvalidOperationException>();
        target.CleanupCalls.Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_ShouldCleanupTarget_WhenAdmissionIsRejected()
    {
        var target = new FakeCommandTarget("actor-1");
        var pipeline = new DefaultCommandDispatchPipeline<string, FakeCommandTarget, string, FakeError>(
            new RecordingResolver(target),
            new DefaultCommandContextPolicy(),
            new RecordingEnvelopeFactory(new EventEnvelope { Id = "evt-rejected" }),
            new RejectedTargetDispatcher(),
            new RecordingReceiptFactory("unused"));

        var result = await pipeline.DispatchAsync("hello");

        result.Succeeded.Should().BeTrue();
        result.Target!.Admission.Should().NotBeNull();
        result.Target.Admission!.Accepted.Should().BeFalse();
        target.CleanupCalls.Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_ShouldHonorCommandContextSeed_WhenProvidedByCommand()
    {
        var target = new FakeCommandTarget("actor-1");
        var seeded = new SeededCommand(
            "hello",
            "cmd-seeded",
            "corr-seeded",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tenant"] = "t-1",
            });
        var envelopeFactory = new SeededCommandEnvelopeFactory(new EventEnvelope { Id = "evt-1" });
        var receiptFactory = new SeededCommandReceiptFactory("receipt-1");
        var pipeline = new DefaultCommandDispatchPipeline<SeededCommand, FakeCommandTarget, string, FakeError>(
            new SeededCommandResolver(target),
            new DefaultCommandContextPolicy(),
            envelopeFactory,
            new RecordingTargetDispatcher(),
            receiptFactory);

        var result = await pipeline.DispatchAsync(seeded);

        result.Succeeded.Should().BeTrue();
        envelopeFactory.Calls.Should().ContainSingle();
        envelopeFactory.Calls[0].Context.CommandId.Should().Be("cmd-seeded");
        receiptFactory.Calls.Should().ContainSingle();
        receiptFactory.Calls[0].Context.CorrelationId.Should().Be("corr-seeded");
    }

    [Fact]
    public async Task DispatchService_ShouldMapSuccessfulPipelineExecutionToReceipt()
    {
        var target = new FakeCommandTarget("actor-1");
        var pipeline = new DefaultCommandDispatchPipeline<string, FakeCommandTarget, string, FakeError>(
            new RecordingResolver(target),
            new DefaultCommandContextPolicy(),
            new RecordingEnvelopeFactory(new EventEnvelope { Id = "evt-1" }),
            new RecordingTargetDispatcher(),
            new RecordingReceiptFactory("receipt-1"));
        var service = new DefaultCommandDispatchService<string, FakeCommandTarget, string, FakeError>(pipeline);

        var result = await service.DispatchAsync("hello");

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be("receipt-1");
        result.Admission.Should().NotBeNull();
        result.Admission!.CommandId.Should().Be("evt-1");
    }

}

public class ActorCommandTargetDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_ShouldUseActorRuntimeDispatch()
    {
        var runtime = new RecordingActorRuntime();
        var dispatcher = new ActorCommandTargetDispatcher<FakeActorCommandTarget>(runtime);
        var target = new FakeActorCommandTarget("actor-1");
        var envelope = new EventEnvelope { Id = "evt-1" };

        await dispatcher.DispatchAsync(target, envelope, CancellationToken.None);

        runtime.DispatchCalls.Should().ContainSingle()
            .Which.Should().Be(("actor-1", envelope));
    }
}

public class ActorCommandTargetDispatcherPortTests
{
    [Fact]
    public void Constructor_WhenDispatchPortIsNull_ShouldThrowArgumentNullException()
    {
        var act = () => new ActorCommandTargetDispatcher<FakeCommandTarget>(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("dispatchPort");
    }

    [Fact]
    public async Task DispatchAsync_ShouldDelegateToHandledDispatchPort()
    {
        var port = new RecordingDispatchPort();
        var dispatcher = new ActorCommandTargetDispatcher<FakeCommandTarget>(port);
        var target = new FakeCommandTarget("actor-1");
        var envelope = new EventEnvelope { Id = "command-1" };

        var admission = await dispatcher.DispatchAsync(target, envelope, CancellationToken.None);

        admission.Accepted.Should().BeTrue();
        admission.ActorId.Should().Be("actor-1");
        admission.CommandId.Should().Be("command-1");
        port.Calls.Should().ContainSingle(x => x.ActorId == "actor-1" && ReferenceEquals(x.Envelope, envelope));
    }

    [Fact]
    public async Task DispatchAsync_ShouldValidateInputsBeforeDelegating()
    {
        var port = new RecordingDispatchPort();
        var dispatcher = new ActorCommandTargetDispatcher<FakeCommandTarget>(port);
        var envelope = new EventEnvelope();

        await dispatcher.Invoking(x => x.DispatchAsync(null!, envelope, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("target");
        await dispatcher.Invoking(x => x.DispatchAsync(new FakeCommandTarget("actor-1"), null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("envelope");
        port.Calls.Should().BeEmpty();
    }
}

public class DefaultEventOutputStreamTests
{
    [Fact]
    public async Task PumpAsync_ShouldMapAndEmit_UntilStopConditionMatches()
    {
        var mapper = new IntToStringFrameMapper();
        var stream = new DefaultEventOutputStream<int, string>(mapper);
        var emitted = new List<string>();

        await stream.PumpAsync(
            Enumerate([1, 2, 3, 4]),
            (frame, _) =>
            {
                emitted.Add(frame);
                return ValueTask.CompletedTask;
            },
            shouldStop: evt => evt == 3,
            ct: CancellationToken.None);

        emitted.Should().Equal("frame:1", "frame:2", "frame:3");
        mapper.MappedEvents.Should().Equal(1, 2, 3);
    }

    private static async IAsyncEnumerable<int> Enumerate(IEnumerable<int> values)
    {
        foreach (var value in values)
        {
            yield return value;
            await Task.Yield();
        }
    }
}

public class CqrsCoreServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCqrsCore_ShouldRegisterDefaults()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventFrameMapper<int, string>, IntToStringFrameMapper>();

        services.AddCqrsCore();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICommandContextPolicy>().Should().BeOfType<DefaultCommandContextPolicy>();
        provider.GetRequiredService<IEventOutputStream<int, string>>().Should().BeOfType<DefaultEventOutputStream<int, string>>();
        provider.GetRequiredService<ICommandObservationLifecycle<string, FakeCommandTarget, string, FakeError>>()
            .Should().BeOfType<NoOpCommandObservationLifecycle<string, FakeCommandTarget, string, FakeError>>();
    }

    [Fact]
    public void AddCqrsCore_ShouldNotOverrideCustomCommandContextPolicy()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICommandContextPolicy, CustomCommandContextPolicy>();

        services.AddCqrsCore();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICommandContextPolicy>().Should().BeOfType<CustomCommandContextPolicy>();
    }
}

internal sealed class FakeCommandTarget : ICommandDispatchTarget, ICommandDispatchCleanupAware
{
    public FakeCommandTarget(string targetId)
    {
        TargetId = targetId;
    }

    public string TargetId { get; }
    public int CleanupCalls { get; private set; }

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default)
    {
        CleanupCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeActorCommandTarget : IActorCommandDispatchTarget
{
    public FakeActorCommandTarget(string targetId)
    {
        TargetId = targetId;
        Actor = new FakeActor(targetId);
    }

    public string TargetId { get; }
    public IActor Actor { get; }
}

internal sealed class RecordingResolver : ICommandTargetResolver<string, FakeCommandTarget, FakeError>
{
    private readonly FakeCommandTarget _target;
    private readonly List<string>? _order;

    public RecordingResolver(FakeCommandTarget target, List<string>? order = null)
    {
        _target = target;
        _order = order;
    }

    public Task<CommandTargetResolution<FakeCommandTarget, FakeError>> ResolveAsync(
        string command,
        CancellationToken ct = default)
    {
        _ = command;
        ct.ThrowIfCancellationRequested();
        _order?.Add("resolve");
        return Task.FromResult(CommandTargetResolution<FakeCommandTarget, FakeError>.Success(_target));
    }
}

internal sealed class RecordingEnvelopeFactory : ICommandEnvelopeFactory<string>
{
    private readonly EventEnvelope _envelope;
    private readonly List<string>? _order;

    public RecordingEnvelopeFactory(EventEnvelope envelope, List<string>? order = null)
    {
        _envelope = envelope;
        _order = order;
    }

    public List<(string Command, CommandContext Context)> Calls { get; } = [];

    public EventEnvelope CreateEnvelope(string command, CommandContext context)
    {
        _order?.Add("envelope");
        Calls.Add((command, context));
        return _envelope;
    }
}

internal sealed class RecordingTargetEnvelopeFactory : ICommandTargetEnvelopeFactory<string, FakeCommandTarget>
{
    private readonly EventEnvelope _envelope;
    private readonly List<string>? _order;

    public RecordingTargetEnvelopeFactory(EventEnvelope envelope, List<string>? order = null)
    {
        _envelope = envelope;
        _order = order;
    }

    public List<(string Command, FakeCommandTarget Target, CommandContext Context)> Calls { get; } = [];

    public EventEnvelope CreateEnvelope(
        string command,
        FakeCommandTarget target,
        CommandContext context)
    {
        _order?.Add("target-envelope");
        Calls.Add((command, target, context));
        return _envelope;
    }
}

internal sealed class RecordingTargetDispatcher : ICommandTargetDispatcher<FakeCommandTarget>
{
    private readonly List<string>? _order;

    public RecordingTargetDispatcher(List<string>? order = null)
    {
        _order = order;
    }

    public List<(FakeCommandTarget Target, EventEnvelope Envelope)> Calls { get; } = [];

    public Task<DispatchAdmission> DispatchAsync(FakeCommandTarget target, EventEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _order?.Add("dispatch");
        Calls.Add((target, envelope));
        return Task.FromResult(DispatchAdmissionFactory.Create(target.TargetId, envelope));
    }
}

internal sealed class ThrowingTargetDispatcher : ICommandTargetDispatcher<FakeCommandTarget>
{
    public Task<DispatchAdmission> DispatchAsync(FakeCommandTarget target, EventEnvelope envelope, CancellationToken ct = default)
    {
        _ = target;
        _ = envelope;
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("dispatch failed");
    }
}

internal sealed class RejectedTargetDispatcher : ICommandTargetDispatcher<FakeCommandTarget>
{
    public Task<DispatchAdmission> DispatchAsync(
        FakeCommandTarget target,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new DispatchAdmission(
            false,
            envelope.Id,
            DateTimeOffset.UtcNow,
            target.TargetId,
            envelope.Propagation?.CorrelationId ?? envelope.Id));
    }
}

internal sealed class RecordingReceiptFactory : ICommandReceiptFactory<FakeCommandTarget, string>
{
    private readonly string _receipt;
    private readonly List<string>? _order;

    public RecordingReceiptFactory(string receipt, List<string>? order = null)
    {
        _receipt = receipt;
        _order = order;
    }

    public List<(FakeCommandTarget Target, CommandContext Context)> Calls { get; } = [];

    public string Create(FakeCommandTarget target, CommandContext context)
    {
        _order?.Add("receipt");
        Calls.Add((target, context));
        return _receipt;
    }
}

internal sealed record SeededCommand(
    string Payload,
    string? CommandId,
    string? CorrelationId,
    IReadOnlyDictionary<string, string>? Headers) : ICommandContextSeed;

internal sealed class SeededCommandResolver(FakeCommandTarget target)
    : ICommandTargetResolver<SeededCommand, FakeCommandTarget, FakeError>
{
    public Task<CommandTargetResolution<FakeCommandTarget, FakeError>> ResolveAsync(
        SeededCommand command,
        CancellationToken ct = default)
    {
        _ = command;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CommandTargetResolution<FakeCommandTarget, FakeError>.Success(target));
    }
}

internal sealed class SeededCommandEnvelopeFactory(EventEnvelope? envelope = null) : ICommandEnvelopeFactory<SeededCommand>
{
    public List<(SeededCommand Command, CommandContext Context)> Calls { get; } = [];

    public EventEnvelope CreateEnvelope(SeededCommand command, CommandContext context)
    {
        Calls.Add((command, context));
        return envelope ?? new EventEnvelope { Id = context.CommandId };
    }
}

internal sealed class SeededCommandReceiptFactory(string receipt) : ICommandReceiptFactory<FakeCommandTarget, string>
{
    public List<(FakeCommandTarget Target, CommandContext Context)> Calls { get; } = [];

    public string Create(FakeCommandTarget target, CommandContext context)
    {
        Calls.Add((target, context));
        return receipt;
    }
}

internal sealed class RecordingActorRuntime : IActorRuntime, IActorDispatchPort
{
    public List<(string ActorId, EventEnvelope Envelope)> DispatchCalls { get; } = [];

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
        throw new NotSupportedException();

    public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task DestroyAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IActor?> GetAsync(string id) =>
        throw new NotSupportedException();

    public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        DispatchCalls.Add((actorId, envelope));
        return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }

    public Task<bool> ExistsAsync(string id) =>
        throw new NotSupportedException();

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

internal sealed class RecordingDispatchPort : IActorDispatchPort
{
    public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

    public Task<DispatchAdmission> DispatchAsync(
        string actorId,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add((actorId, envelope));
        return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }
}

internal sealed class FakeActor : IActor
{
    public FakeActor(string id)
    {
        Id = id;
        Agent = new FakeAgent(id + "-agent");
    }

    public string Id { get; }
    public IAgent Agent { get; }

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
}

internal sealed class FakeAgent : IAgent
{
    public FakeAgent(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetDescriptionAsync() => Task.FromResult("fake");
    public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class IntToStringFrameMapper : IEventFrameMapper<int, string>
{
    public List<int> MappedEvents { get; } = [];

    public string Map(int evt)
    {
        MappedEvents.Add(evt);
        return $"frame:{evt}";
    }
}

internal sealed class CustomCommandContextPolicy : ICommandContextPolicy
{
    public CommandContext Create(
        string targetId,
        IReadOnlyDictionary<string, string>? headers = null,
        string? commandId = null,
        string? correlationId = null)
    {
        return new CommandContext(targetId, "custom-cmd", "custom-corr", headers ?? new Dictionary<string, string>());
    }
}

internal enum FakeError
{
    None = 0,
    Failed = 1,
}
