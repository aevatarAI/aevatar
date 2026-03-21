using System.Text.Json;
using Aevatar.AI.Abstractions;
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

    [Fact]
    public void Format_ShouldSerializeAiPayload_WhenCustomPayloadCarriesAiEvent()
    {
        var json = ChatJsonPayloads.Format(new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = "aevatar.raw.observed",
                Payload = Any.Pack(new MediaContentEvent
                {
                    SessionId = "session-1",
                    AgentId = "agent-1",
                    Part = new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Image,
                        Uri = "https://example.com/cat.png",
                        MediaType = "image/png",
                        Name = "cat",
                    },
                }),
            },
        });

        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement
            .GetProperty("custom")
            .GetProperty("payload");

        payload.GetProperty("@type").GetString()
            .Should().Be("type.googleapis.com/aevatar.ai.MediaContentEvent");
        payload.GetProperty("sessionId").GetString().Should().Be("session-1");
        payload.GetProperty("agentId").GetString().Should().Be("agent-1");
        payload.GetProperty("part").GetProperty("uri").GetString().Should().Be("https://example.com/cat.png");
        payload.GetProperty("part").GetProperty("mediaType").GetString().Should().Be("image/png");
        payload.GetProperty("part").GetProperty("name").GetString().Should().Be("cat");
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
