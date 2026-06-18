using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowHumanApproval")]
public sealed class WorkflowHumanApprovalModuleRoutingTests
{
    [Fact]
    public async Task HumanApprovalModule_ShouldPublishSuspensionToSelf()
    {
        var module = new HumanApprovalModule();
        var ctx = new TestEventHandlerContext(
            new ServiceCollection().AddAevatarWorkflow().BuildServiceProvider(),
            new TestAgent("workflow-human-approval-routing-test-agent"),
            NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-1",
                StepType = "human_approval",
                RunId = "run-1",
                Input = "original",
                Parameters = { ["prompt"] = "approve?" },
            }),
            ctx,
            CancellationToken.None);

        var suspendedPublication = ctx.Published.Single(x => x.evt is WorkflowSuspendedEvent);
        suspendedPublication.direction.Should().Be(TopologyAudience.Self);
    }

    private static EventEnvelope Envelope(IMessage evt) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test-publisher", TopologyAudience.Self),
        };
}
