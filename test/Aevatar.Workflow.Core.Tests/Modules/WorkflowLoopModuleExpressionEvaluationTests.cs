using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Async;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public class WorkflowLoopModuleExpressionEvaluationTests
{
    [Fact]
    public async Task DispatchStep_ShouldEvaluateExpressionsInParameters()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "first",
                    Type = "transform",
                    Parameters = new Dictionary<string, string>
                    {
                        ["value"] = "${concat('v=', input)}",
                    },
                },
                new StepDefinition
                {
                    Id = "second",
                    Type = "transform",
                    Parameters = new Dictionary<string, string>
                    {
                        ["value"] = "${concat('prev=', first, ', input=', input)}",
                    },
                },
            ],
        };

        var module = new WorkflowLoopModule();
        module.SetWorkflow(workflow);

        var ctx = new CapturingContext();
        const string runId = "run-1";

        await module.HandleAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "wf",
            RunId = runId,
            Input = "hello",
        }), ctx, CancellationToken.None);

        var firstReq = ctx.Published.Single(x => x.Event is StepRequestEvent).Event as StepRequestEvent;
        firstReq.Should().NotBeNull();
        firstReq!.StepId.Should().Be("first");
        firstReq.Input.Should().Be("hello");
        firstReq.Parameters["value"].Should().Be("v=hello");

        ctx.Published.Clear();

        await module.HandleAsync(Wrap(new StepCompletedEvent
        {
            StepId = "first",
            RunId = runId,
            Success = true,
            Output = "out1",
        }), ctx, CancellationToken.None);

        var secondReq = ctx.Published.Single(x => x.Event is StepRequestEvent).Event as StepRequestEvent;
        secondReq.Should().NotBeNull();
        secondReq!.StepId.Should().Be("second");
        secondReq.Input.Should().Be("out1");
        secondReq.Parameters["value"].Should().Be("prev=out1, input=out1");
    }

    private static EventEnvelope Wrap(IMessage evt) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        PublisherId = "test",
        Direction = EventDirection.Self,
    };

    private sealed class CapturingContext : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";
        public IAgent Agent { get; } = new StubAgent();
        public IServiceProvider Services { get; } = new NullServiceProvider();
        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, EventDirection Direction)> Published { get; } = [];

        public Task PublishAsync<TEvent>(TEvent evt, EventDirection direction = EventDirection.Down, CancellationToken ct = default)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            _ = callbackId;
            _ = dueTime;
            _ = evt;
            _ = metadata;
            _ = ct;
            throw new NotSupportedException("This test context does not support scheduling.");
        }

        public Task<RuntimeCallbackLease> ScheduleSelfTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            _ = callbackId;
            _ = dueTime;
            _ = period;
            _ = evt;
            _ = metadata;
            _ = ct;
            throw new NotSupportedException("This test context does not support scheduling.");
        }

        public Task CancelScheduledCallbackAsync(
            string callbackId,
            long? expectedGeneration = null,
            CancellationToken ct = default)
        {
            _ = callbackId;
            _ = expectedGeneration;
            _ = ct;
            throw new NotSupportedException("This test context does not support scheduling.");
        }
    }

    private sealed class StubAgent : IAgent
    {
        public string Id => "agent-1";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
