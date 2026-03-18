using System.Text.Json;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatJsonPayloadsTests
{
    [Fact]
    public void Format_ShouldSerializeWorkflowExecutionStartedPayload()
    {
        var frame = BuildFrame();

        var json = ChatJsonPayloads.Format(frame);
        using var document = JsonDocument.Parse(json);

        var payload = document.RootElement
            .GetProperty("custom")
            .GetProperty("payload");

        payload.GetProperty("@type").GetString()
            .Should().Be("type.googleapis.com/aevatar.workflow.WorkflowRunExecutionStartedEvent");
        payload.GetProperty("runId").GetString().Should().Be("run-1");
        payload.GetProperty("workflowName").GetString().Should().Be("review");
        payload.GetProperty("input").GetString().Should().Be("hello");
        payload.GetProperty("definitionActorId").GetString().Should().Be("wf:def");
    }

    [Fact]
    public void ToJsonElement_ShouldSerializeWorkflowExecutionStartedPayload()
    {
        var payload = ChatJsonPayloads.ToJsonElement(BuildFrame())
            .GetProperty("custom")
            .GetProperty("payload");

        payload.GetProperty("@type").GetString()
            .Should().Be("type.googleapis.com/aevatar.workflow.WorkflowRunExecutionStartedEvent");
        payload.GetProperty("runId").GetString().Should().Be("run-1");
    }

    private static WorkflowRunEventEnvelope BuildFrame() =>
        new()
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = "aevatar.workflow.execution.started",
                Payload = Any.Pack(new WorkflowRunExecutionStartedEvent
                {
                    RunId = "run-1",
                    WorkflowName = "review",
                    Input = "hello",
                    DefinitionActorId = "wf:def",
                }),
            },
        };
}
