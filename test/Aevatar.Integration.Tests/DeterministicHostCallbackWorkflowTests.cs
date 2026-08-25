using Aevatar.Bootstrap.Connectors;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Connectors;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "DeterministicHostCallback")]
// Implement (issue #3526):
//   Behavior: Execute a versioned inline algorithm and pass its stable output to the next workflow step.
//   Why this shape: The integration test crosses connector execution, annotations, and workflow continuation without polling.
public sealed class DeterministicHostCallbackWorkflowTests
{
    [Fact]
    public async Task HandleAsync_WhenDeterministicHostCallbackRuns_ShouldExposeVersionAndFeedNextStep()
    {
        var registry = new ConfiguredConnectorRegistry();
        var handler = new SHA256DeterministicComputeHandler();
        await registry.RegisterAsync(ConnectorRegistration.External(new HostCallbackConnector(
            "deterministic-hash",
            handler.Name,
            handler,
            [SHA256DeterministicComputeHandler.OperationId],
            ["text"])));
        var module = new ConnectorCallModule(new RegistryBackedWorkflowConnectorResolver(registry));
        var ctx = CreateContext();
        ctx.SetNextElapsedTime(TimeSpan.FromMilliseconds(88));
        var request = new StepRequestEvent
        {
            StepId = "hash",
            RunId = "run-deterministic-hash",
            StepType = "connector_call",
            Input = """{"text":"abc"}""",
            Parameters =
            {
                ["connector"] = "deterministic-hash",
                ["operation"] = SHA256DeterministicComputeHandler.OperationId,
            },
        };

        await HandleAndDrainAsync(module, Envelope(request), ctx);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be(
            """{"sha256":"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"}""");
        completed.Annotations["host_callback.algorithm_version"].Should().Be("1");
        completed.Annotations["host_callback.result.sha256"].Should().Be(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        completed.Annotations["connector.type"].Should().Be("host_callback");
        completed.Annotations["connector.duration_ms"].Should().Be("88.00");

        var loop = new WorkflowLoopModule();
        loop.SetWorkflow(new WorkflowDefinition
        {
            Name = "deterministic-workflow",
            Roles = [],
            Steps =
            [
                new StepDefinition { Id = "hash", Type = "connector_call" },
                new StepDefinition { Id = "consume", Type = "transform" },
            ],
        });
        var loopContext = CreateContext();
        await loop.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = request.RunId,
                Input = request.Input,
            }),
            loopContext,
            CancellationToken.None);
        loopContext.Published.Clear();

        await loop.HandleAsync(Envelope(completed), loopContext, CancellationToken.None);

        var consumer = loopContext.Published.Should().ContainSingle().Subject.evt
            .Should().BeOfType<StepRequestEvent>().Subject;
        consumer.StepId.Should().Be("consume");
        consumer.Input.Should().Be(completed.Output);
    }

    private static TestEventHandlerContext CreateContext()
    {
        return new TestEventHandlerContext(
            new ServiceCollection().BuildServiceProvider(),
            new TestAgent("deterministic-host-callback-test-agent"),
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
            Propagation = new EnvelopePropagation(),
        };
    }

    private static async Task HandleAndDrainAsync(
        ConnectorCallModule module,
        EventEnvelope envelope,
        TestEventHandlerContext ctx)
    {
        await module.HandleAsync(envelope, ctx, CancellationToken.None);
        for (var index = 0; index < ctx.Published.Count; index++)
        {
            if (ctx.Published[index].evt is not WorkflowConnectorAttemptCompletedEvent completed)
                continue;

            ctx.Published.RemoveAt(index);
            index--;
            await module.HandleAsync(Envelope(completed), ctx, CancellationToken.None);
        }
    }
}
