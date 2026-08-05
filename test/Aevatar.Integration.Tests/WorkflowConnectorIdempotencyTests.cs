using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowConnectorIdempotency")]
public sealed class WorkflowConnectorIdempotencyTests
{
    [Fact]
    public async Task ConnectorCallModule_ShouldForwardStepIdempotencyKeyToConnectorRequestAndPhysicalRetry()
    {
        var connector = new RecordingConnector("idempotent-connector");
        var module = new ConnectorCallModule(new FixedWorkflowConnectorResolver(connector));
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "connector-idem",
                StepType = "connector_call",
                RunId = "run-idem",
                Input = "payload",
                IdempotencyKey = "idem-connector-1",
                Parameters =
                {
                    ["connector"] = "idempotent-connector",
                    ["operation"] = "invoke",
                    ["retry"] = "1",
                },
            }),
            ctx,
            CancellationToken.None);

        connector.Requests.Should().ContainSingle();
        connector.Requests[0].IdempotencyKey.Should().Be("idem-connector-1");

        var failedAttempt = ctx.Published.Select(x => x.evt).OfType<WorkflowConnectorAttemptCompletedEvent>().Single();
        await module.HandleAsync(Envelope(failedAttempt), ctx, CancellationToken.None);

        connector.Requests.Should().HaveCount(2);
        connector.Requests.Select(x => x.IdempotencyKey).Should().OnlyContain(x => x == "idem-connector-1");
    }

    [Fact]
    public async Task ConnectorCallModule_ShouldNotRetryAfterTerminalWasInvoked()
    {
        var connector = new TerminalFailureConnector("terminal-connector");
        var module = new ConnectorCallModule(new FixedWorkflowConnectorResolver(connector));
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "connector-terminal",
                StepType = "connector_call",
                RunId = "run-terminal",
                Input = "payload",
                IdempotencyKey = "idem-terminal-1",
                Parameters =
                {
                    ["connector"] = "terminal-connector",
                    ["operation"] = "invoke",
                    ["retry"] = "1",
                },
            }),
            ctx,
            CancellationToken.None);

        var failedAttempt = ctx.Published.Select(x => x.evt).OfType<WorkflowConnectorAttemptCompletedEvent>().Single();
        await module.HandleAsync(Envelope(failedAttempt), ctx, CancellationToken.None);

        connector.Requests.Should().ContainSingle();
        var completed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Should().ContainSingle().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("terminal failed");
    }

    private static TestEventHandlerContext CreateContext()
    {
        return new TestEventHandlerContext(
            new ServiceCollection().AddAevatarWorkflow().BuildServiceProvider(),
            new TestAgent("workflow-connector-idempotency-test-agent"),
            NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IMessage evt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test-publisher", TopologyAudience.Self),
        };
    }

    private sealed class FixedWorkflowConnectorResolver(IConnector connector) : IWorkflowConnectorResolver
    {
        public ValueTask<IConnector?> ResolveAsync(
            IWorkflowExecutionContext context,
            string connectorName,
            CancellationToken ct = default)
        {
            _ = context;
            _ = connectorName;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IConnector?>(connector);
        }
    }

    private sealed class RecordingConnector(string name) : IConnector
    {
        public string Name { get; } = name;

        public string Type => "test";

        public List<ConnectorRequest> Requests { get; } = [];

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            _ = ct;
            Requests.Add(request);
            return Task.FromResult(new ConnectorResponse
            {
                Success = Requests.Count > 1,
                Output = Requests.Count > 1 ? "ok" : string.Empty,
                Error = Requests.Count > 1 ? string.Empty : "transient",
            });
        }
    }

    private sealed class TerminalFailureConnector(string name) : IConnector
    {
        public string Name { get; } = name;

        public string Type => "test";

        public List<ConnectorRequest> Requests { get; } = [];

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new ConnectorResponse
            {
                Success = false,
                Error = "terminal failed",
                TerminalInvoked = true,
                Retryable = false,
            });
        }
    }
}
