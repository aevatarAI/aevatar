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
    public void Format_ShouldSerializeWorkflowPayload_WhenCustomPayloadCarriesWorkflowEvent()
    {
        var json = ChatJsonPayloads.Format(new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = "aevatar.raw.observed",
                Payload = Any.Pack(new WorkflowLlmInvocationCompletedEvent
                {
                    SessionId = "session-1",
                    RunId = "run-1",
                    StepId = "step-1",
                    Content = "reply",
                    Success = true,
                }),
            },
        });

        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement
            .GetProperty("custom")
            .GetProperty("payload");

        payload.GetProperty("@type").GetString()
            .Should().Be("type.googleapis.com/aevatar.workflow.WorkflowLlmInvocationCompletedEvent");
        payload.GetProperty("sessionId").GetString().Should().Be("session-1");
        payload.GetProperty("runId").GetString().Should().Be("run-1");
        payload.GetProperty("stepId").GetString().Should().Be("step-1");
        payload.GetProperty("content").GetString().Should().Be("reply");
        payload.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Format_ShouldSerializeInitializeRoleAgentEvent_WhenCustomPayloadCarriesAiMessage()
    {
        var json = ChatJsonPayloads.Format(new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = "aevatar.raw.observed",
                Payload = Any.Pack(new InitializeRoleAgentEvent
                {
                    RoleId = "reviewer",
                    RoleName = "Reviewer",
                    ProviderName = "openai",
                    Model = "gpt-4.1",
                    SystemPrompt = "Review the plan.",
                    MaxTokens = 2048,
                    MaxToolRounds = 4,
                    MaxHistoryMessages = 12,
                    EventModules = "module-a",
                    EventRoutes = "route-a",
                }),
            },
        });

        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement
            .GetProperty("custom")
            .GetProperty("payload");

        payload.GetProperty("@type").GetString()
            .Should().Be("type.googleapis.com/aevatar.ai.InitializeRoleAgentEvent");
        payload.GetProperty("roleId").GetString().Should().Be("reviewer");
        payload.GetProperty("roleName").GetString().Should().Be("Reviewer");
        payload.GetProperty("providerName").GetString().Should().Be("openai");
        payload.GetProperty("model").GetString().Should().Be("gpt-4.1");
        payload.GetProperty("systemPrompt").GetString().Should().Be("Review the plan.");
        payload.GetProperty("maxTokens").GetInt32().Should().Be(2048);
        payload.GetProperty("maxToolRounds").GetInt32().Should().Be(4);
        payload.GetProperty("maxHistoryMessages").GetInt32().Should().Be(12);
        payload.GetProperty("eventModules").GetString().Should().Be("module-a");
        payload.GetProperty("eventRoutes").GetString().Should().Be("route-a");
    }

    [Fact]
    public void Format_ShouldSerializeNyxIdActionRequestWithSchemaV4CamelCaseFields()
    {
        var json = ChatJsonPayloads.Format(new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = "nyxid.action.request",
                Payload = Any.Pack(new WorkflowInteractiveActionRequestWirePayload
                {
                    SchemaVersion = 4,
                    ActorId = "nyxid-chat-alpha",
                    OriginTurnId = "turn-alpha",
                    TaskId = "task-alpha",
                    StepId = "step-alpha",
                    ActionRequestId = "action-alpha",
                    Action = "service.connect",
                    Params = new WorkflowInteractiveActionParams
                    {
                        CatalogService = new WorkflowInteractiveCatalogServiceActionParams
                        {
                            ServiceSlug = "api-github",
                            RequestedScopes = { "repo" },
                        },
                    },
                }),
            },
        });

        using var document = JsonDocument.Parse(json);
        var custom = document.RootElement.GetProperty("custom");
        custom.GetProperty("name").GetString().Should().Be("nyxid.action.request");
        var payload = custom.GetProperty("payload");
        payload.GetProperty("schemaVersion").GetInt32().Should().Be(4);
        payload.GetProperty("actorId").GetString().Should().Be("nyxid-chat-alpha");
        payload.GetProperty("originTurnId").GetString().Should().Be("turn-alpha");
        payload.GetProperty("taskId").GetString().Should().Be("task-alpha");
        payload.GetProperty("stepId").GetString().Should().Be("step-alpha");
        payload.GetProperty("actionRequestId").GetString().Should().Be("action-alpha");
        payload.GetProperty("action").GetString().Should().Be("service.connect");
        var catalogService = payload.GetProperty("params").GetProperty("catalogService");
        catalogService.GetProperty("serviceSlug").GetString().Should().Be("api-github");
        catalogService.GetProperty("requestedScopes")[0].GetString().Should().Be("repo");
        payload.TryGetProperty("terminalContinuation", out _).Should().BeFalse();
        payload.TryGetProperty("handoffId", out _).Should().BeFalse();
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
