using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class WaitSignalModuleTests
{
    [Fact]
    public async Task HandleAsync_WhenSameRunAndSignalHaveMultipleWaiters_ShouldRequireStepIdForPreciseResume()
    {
        var module = new WaitSignalModule();
        var context = new RecordingEventHandlerContext(
            new EmptyServiceProvider(),
            new StubAgent("workflow-1"),
            NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-a",
                StepType = "wait_signal",
                RunId = "run-shared",
                Input = "fallback-a",
                Parameters = { ["signal_name"] = "approval" },
            }),
            context,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-b",
                StepType = "wait_signal",
                RunId = "run-shared",
                Input = "fallback-b",
                Parameters = { ["signal_name"] = "approval" },
            }),
            context,
            CancellationToken.None);
        context.Published.Clear();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                RunId = "run-shared",
                SignalName = "approval",
                Payload = "ambiguous",
            }),
            context,
            CancellationToken.None);
        context.Published.Should().BeEmpty();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                RunId = "run-shared",
                SignalName = "approval",
                StepId = "wait-b",
                Payload = "resolved-b",
            }),
            context,
            CancellationToken.None);

        var completion = context.Published.Select(item => item.Event).OfType<StepCompletedEvent>().Single();
        completion.StepId.Should().Be("wait-b");
        completion.RunId.Should().Be("run-shared");
        completion.Output.Should().Be("resolved-b");
    }

    private static EventEnvelope Envelope(IMessage evt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = "test",
            Direction = EventDirection.Self,
        };
    }

    private sealed class RecordingEventHandlerContext : IEventHandlerContext
    {
        public RecordingEventHandlerContext(IServiceProvider services, IAgent agent, ILogger logger)
        {
            Services = services;
            Agent = agent;
            Logger = logger;
            InboundEnvelope = new EventEnvelope();
        }

        public List<(IMessage Event, EventDirection Direction)> Published { get; } = [];
        public EventEnvelope InboundEnvelope { get; }
        public string AgentId => Agent.Id;
        public IAgent Agent { get; }
        public IServiceProvider Services { get; }
        public ILogger Logger { get; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
