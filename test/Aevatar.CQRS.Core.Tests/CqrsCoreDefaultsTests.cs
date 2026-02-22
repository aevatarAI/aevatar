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
    public void Create_ShouldGenerateIds_AndCloneMetadata_WhenIdsNotProvided()
    {
        var policy = new DefaultCommandContextPolicy();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["k"] = "v",
        };

        var context = policy.Create("actor-1", metadata);

        context.TargetId.Should().Be("actor-1");
        context.CommandId.Should().NotBeNullOrWhiteSpace();
        context.CorrelationId.Should().Be(context.CommandId);
        context.Metadata.Should().ContainKey("k").WhoseValue.Should().Be("v");
        context.Metadata.Should().NotBeSameAs(metadata);
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

public class DefaultCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldDispatchEnvelopeBuiltByFactory()
    {
        var expectedEnvelope = new EventEnvelope { Id = "evt-1" };
        var factory = new FakeEnvelopeFactory(expectedEnvelope);
        var actor = new FakeActor("actor-1");
        var executor = new DefaultCommandExecutor<string>(factory);
        var context = new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>());

        await executor.ExecuteAsync(actor, "hello", context, CancellationToken.None);

        factory.Calls.Should().ContainSingle();
        factory.Calls.Single().Command.Should().Be("hello");
        factory.Calls.Single().Context.Should().Be(context);
        actor.HandledEnvelopes.Should().ContainSingle().Which.Should().BeSameAs(expectedEnvelope);
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

internal sealed class FakeEnvelopeFactory : ICommandEnvelopeFactory<string>
{
    private readonly EventEnvelope _envelope;

    public FakeEnvelopeFactory(EventEnvelope envelope)
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

internal sealed class FakeActor : IActor
{
    public FakeActor(string id)
    {
        Id = id;
        Agent = new FakeAgent(id + "-agent");
    }

    public string Id { get; }
    public IAgent Agent { get; }
    public List<EventEnvelope> HandledEnvelopes { get; } = [];

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        HandledEnvelopes.Add(envelope);
        return Task.CompletedTask;
    }

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
        IReadOnlyDictionary<string, string>? metadata = null,
        string? commandId = null,
        string? correlationId = null)
    {
        return new CommandContext(targetId, "custom-cmd", "custom-corr", metadata ?? new Dictionary<string, string>());
    }
}
