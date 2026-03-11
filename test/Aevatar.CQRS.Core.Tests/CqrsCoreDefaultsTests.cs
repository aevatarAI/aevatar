using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.DependencyInjection;
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
    public async Task DispatchAsync_ShouldResolveBindDispatchAndCreateReceipt()
    {
        var target = new FakeCommandTarget("actor-1");
        var resolver = new RecordingResolver(target);
        var binder = new RecordingBinder();
        var envelopeFactory = new RecordingEnvelopeFactory(new EventEnvelope { Id = "evt-1" });
        var dispatcher = new RecordingTargetDispatcher();
        var receiptFactory = new RecordingReceiptFactory("receipt-1");
        var pipeline = new DefaultCommandDispatchPipeline<string, FakeCommandTarget, string, FakeError>(
            resolver,
            new DefaultCommandContextPolicy(),
            binder,
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
        binder.Calls.Should().ContainSingle(x => x.Command == "hello" && x.Target == target);
        dispatcher.Calls.Should().ContainSingle(x => x.Target == target && x.Envelope.Id == "evt-1");
        receiptFactory.Calls.Should().ContainSingle(x => x.Target == target);
    }

    [Fact]
    public async Task DispatchAsync_ShouldCleanupTarget_WhenDispatcherFails()
    {
        var target = new FakeCommandTarget("actor-1");
        var pipeline = new DefaultCommandDispatchPipeline<string, FakeCommandTarget, string, FakeError>(
            new RecordingResolver(target),
            new DefaultCommandContextPolicy(),
            new RecordingBinder(),
            new RecordingEnvelopeFactory(new EventEnvelope { Id = "evt-1" }),
            new ThrowingTargetDispatcher(),
            new RecordingReceiptFactory("unused"));

        var act = () => pipeline.DispatchAsync("hello");

        await act.Should().ThrowAsync<InvalidOperationException>();
        target.CleanupCalls.Should().Be(1);
    }

    [Fact]
    public async Task DispatchService_ShouldMapSuccessfulPipelineExecutionToReceipt()
    {
        var target = new FakeCommandTarget("actor-1");
        var pipeline = new DefaultCommandDispatchPipeline<string, FakeCommandTarget, string, FakeError>(
            new RecordingResolver(target),
            new DefaultCommandContextPolicy(),
            new RecordingBinder(),
            new RecordingEnvelopeFactory(new EventEnvelope { Id = "evt-1" }),
            new RecordingTargetDispatcher(),
            new RecordingReceiptFactory("receipt-1"));
        var service = new DefaultCommandDispatchService<string, FakeCommandTarget, string, FakeError>(pipeline);

        var result = await service.DispatchAsync("hello");

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be("receipt-1");
    }

    [Fact]
    public async Task NoOpTargetBinder_ShouldAlwaysSucceed()
    {
        var binder = new NoOpCommandTargetBinder<string, FakeCommandTarget, FakeError>();

        var result = await binder.BindAsync(
            "hello",
            new FakeCommandTarget("actor-1"),
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
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
        provider.GetRequiredService<ICommandTargetBinder<string, FakeCommandTarget, FakeError>>()
            .Should().BeOfType<NoOpCommandTargetBinder<string, FakeCommandTarget, FakeError>>();
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

    public RecordingResolver(FakeCommandTarget target)
    {
        _target = target;
    }

    public Task<CommandTargetResolution<FakeCommandTarget, FakeError>> ResolveAsync(
        string command,
        CancellationToken ct = default)
    {
        _ = command;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CommandTargetResolution<FakeCommandTarget, FakeError>.Success(_target));
    }
}

internal sealed class RecordingBinder : ICommandTargetBinder<string, FakeCommandTarget, FakeError>
{
    public List<(string Command, FakeCommandTarget Target, CommandContext Context)> Calls { get; } = [];

    public Task<CommandTargetBindingResult<FakeError>> BindAsync(
        string command,
        FakeCommandTarget target,
        CommandContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add((command, target, context));
        return Task.FromResult(CommandTargetBindingResult<FakeError>.Success());
    }
}

internal sealed class RecordingEnvelopeFactory : ICommandEnvelopeFactory<string>
{
    private readonly EventEnvelope _envelope;

    public RecordingEnvelopeFactory(EventEnvelope envelope)
    {
        _envelope = envelope;
    }

    public List<(string Command, CommandContext Context)> Calls { get; } = [];

    public EventEnvelope CreateEnvelope(string command, CommandContext context)
    {
        Calls.Add((command, context));
        return _envelope;
    }
}

internal sealed class RecordingTargetDispatcher : ICommandTargetDispatcher<FakeCommandTarget>
{
    public List<(FakeCommandTarget Target, EventEnvelope Envelope)> Calls { get; } = [];

    public Task DispatchAsync(FakeCommandTarget target, EventEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add((target, envelope));
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingTargetDispatcher : ICommandTargetDispatcher<FakeCommandTarget>
{
    public Task DispatchAsync(FakeCommandTarget target, EventEnvelope envelope, CancellationToken ct = default)
    {
        _ = target;
        _ = envelope;
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("dispatch failed");
    }
}

internal sealed class RecordingReceiptFactory : ICommandReceiptFactory<FakeCommandTarget, string>
{
    private readonly string _receipt;

    public RecordingReceiptFactory(string receipt)
    {
        _receipt = receipt;
    }

    public List<(FakeCommandTarget Target, CommandContext Context)> Calls { get; } = [];

    public string Create(FakeCommandTarget target, CommandContext context)
    {
        Calls.Add((target, context));
        return _receipt;
    }
}

internal sealed class RecordingActorRuntime : IActorRuntime, IActorDispatchPort
{
    public List<(string ActorId, EventEnvelope Envelope)> DispatchCalls { get; } = [];

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
        throw new NotSupportedException();

    public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task DestroyAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IActor?> GetAsync(string id) =>
        throw new NotSupportedException();

    public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        DispatchCalls.Add((actorId, envelope));
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string id) =>
        throw new NotSupportedException();

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
        throw new NotSupportedException();
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
    public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
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
