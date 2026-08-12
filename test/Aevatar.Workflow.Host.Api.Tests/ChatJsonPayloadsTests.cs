using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Google.Protobuf;
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
    public void Format_ShouldPreserveRawObservedIdentity_WhenNestedPayloadDescriptorIsUnavailable()
    {
        const string typeUrl =
            "type.googleapis.com/aevatar.gagents.nyxid_chat.NyxIdChatConversationCreationStartedEvent";
        var json = ChatJsonPayloads.Format(new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = "aevatar.raw.observed",
                Payload = Any.Pack(new WorkflowObservedEnvelopeCustomPayload
                {
                    EventId = "event-unknown",
                    PayloadTypeUrl = typeUrl,
                    PublisherActorId = "nyxid-chat-alpha",
                    CorrelationId = "correlation-alpha",
                    StateVersion = 13,
                    Payload = new Any
                    {
                        TypeUrl = typeUrl,
                        Value = ByteString.CopyFromUtf8("opaque-protobuf"),
                    },
                }),
            },
        });

        using var document = JsonDocument.Parse(json);
        var custom = document.RootElement.GetProperty("custom");
        custom.GetProperty("name").GetString().Should().Be("aevatar.raw.observed");
        var observed = custom.GetProperty("payload");
        observed.GetProperty("eventId").GetString().Should().Be("event-unknown");
        observed.GetProperty("payloadTypeUrl").GetString().Should().Be(typeUrl);
        observed.GetProperty("publisherActorId").GetString().Should().Be("nyxid-chat-alpha");
        observed.GetProperty("correlationId").GetString().Should().Be("correlation-alpha");
        observed.GetProperty("stateVersion").GetString().Should().Be("13");
        observed.TryGetProperty("payload", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("service.connect", "catalogService")]
    [InlineData("key.create", "keyCreate")]
    public void Format_ShouldSerializeTypedNyxIdActionRequestWithoutCredentialMaterial(
        string wireAction,
        string expectedParamsProperty)
    {
        var json = ChatJsonPayloads.Format(new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = "nyxid.action.request",
                Payload = Any.Pack(InteractiveActionRequest(wireAction)),
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
        payload.GetProperty("action").GetString().Should().Be(wireAction);
        var actionParams = payload.GetProperty("params");
        actionParams.EnumerateObject().Select(static property => property.Name)
            .Should().Equal(expectedParamsProperty);
        if (wireAction == "service.connect")
        {
            var catalogService = actionParams.GetProperty("catalogService");
            catalogService.GetProperty("serviceSlug").GetString().Should().Be("api-github");
            catalogService.GetProperty("requestedScopes")[0].GetString().Should().Be("repo");
        }
        else
        {
            var keyCreate = actionParams.GetProperty("keyCreate");
            keyCreate.GetProperty("name").GetString().Should().Be("agent-alpha");
            keyCreate.GetProperty("platform").GetString().Should().Be("codex");
            keyCreate.GetProperty("allowedServiceIds").EnumerateArray()
                .Select(static item => item.GetString())
                .Should().Equal("m-github", "m-lark");
        }

        EnumeratePropertyNames(payload).Should().OnlyContain(
            propertyName => !IsCredentialMaterialProperty(propertyName));
        payload.TryGetProperty("terminalContinuation", out _).Should().BeFalse();
        payload.TryGetProperty("handoffId", out _).Should().BeFalse();
    }

    private static WorkflowInteractiveActionRequestWirePayload InteractiveActionRequest(
        string wireAction)
    {
        var request = new WorkflowInteractiveActionRequestWirePayload
        {
            SchemaVersion = 4,
            ActorId = "nyxid-chat-alpha",
            OriginTurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            ActionRequestId = "action-alpha",
            Action = wireAction,
        };
        request.Params = wireAction switch
        {
            "service.connect" => new WorkflowInteractiveActionParams
            {
                CatalogService = new WorkflowInteractiveCatalogServiceActionParams
                {
                    ServiceSlug = "api-github",
                    RequestedScopes = { "repo" },
                },
            },
            "key.create" => new WorkflowInteractiveActionParams
            {
                KeyCreate = new WorkflowInteractiveKeyCreateActionParams
                {
                    Name = "agent-alpha",
                    Platform = "codex",
                    AllowedServiceIds = { "m-github", "m-lark" },
                },
            },
            _ => throw new InvalidOperationException($"Unknown wire action '{wireAction}'."),
        };
        return request;
    }

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value))
                    yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            foreach (var nested in EnumeratePropertyNames(item))
                yield return nested;
        }
    }

    private static bool IsCredentialMaterialProperty(string propertyName) =>
        propertyName.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Contains("privateKey", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Contains("keyMaterial", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Contains("apiKey", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(propertyName, "key", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(propertyName, "keyId", StringComparison.OrdinalIgnoreCase);

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
