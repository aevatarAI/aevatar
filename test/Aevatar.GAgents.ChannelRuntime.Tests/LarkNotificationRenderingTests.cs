using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Aevatar.GAgents.Platform.Lark;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LarkNotificationRenderingTests
{
    [Fact]
    public void RemoteApprovalMessageMapper_ShouldUseTypedNyxIdApprovalPayloadWithRemoteApprovalId()
    {
        var notification = BuildRemoteApprovalNotification("agent-1");

        var intent = RemoteToolApprovalMessageMapper.ToMessageContent(notification);

        intent.Cards.Should().ContainSingle();
        var card = intent.Cards[0];
        card.Actions.Should().HaveCount(2);
        card.Actions[0].NyxIdApproval.RequestId.Should().Be("nyx-remote-1");
        card.Actions[0].NyxIdApproval.Approved.Should().BeTrue();
        card.Actions[1].NyxIdApproval.RequestId.Should().Be("nyx-remote-1");
        card.Actions[1].NyxIdApproval.Approved.Should().BeFalse();
        card.Text.Should().Contain("Local request: `local-req-1`");
    }

    [Fact]
    public void RemoteApprovalMessageMapper_ShouldRenderOnlyRedactedArgumentShape()
    {
        const string path = "/prod/private";
        const string password = "password-secret";
        const string authorization = "Bearer authorization-secret";
        const string command = "rm -rf /private/data";
        const string secretPropertyName = "sk_live_property_name_secret";
        var baseNotification = BuildRemoteApprovalNotification("agent-1");
        var notification = baseNotification with
        {
            Request = baseNotification.Request with
            {
                ArgumentsJson = $$"""
                    {
                      "path": "{{path}}",
                      "credentials": { "password": "{{password}}", "authorization": "{{authorization}}" },
                      "command": "{{command}}",
                      "force": true,
                      "retries": 3,
                      "{{secretPropertyName}}": "value"
                    }
                    """,
            },
        };

        var content = RemoteToolApprovalMessageMapper.ToMessageContent(notification);

        var text = content.Cards.Should().ContainSingle().Subject.Text;
        text.Should().Contain("Arguments (values redacted):");
        text.Should().Contain("\"field_1\":\"[string redacted]\"");
        text.Should().Contain("\"field_2\":{");
        text.Should().Contain("\"field_4\":\"[boolean redacted]\"");
        text.Should().Contain("\"field_5\":\"[number redacted]\"");
        text.Should().NotContain(path);
        text.Should().NotContain(password);
        text.Should().NotContain(authorization);
        text.Should().NotContain(command);
        text.Should().NotContain(secretPropertyName);
    }

    [Fact]
    public void RemoteApprovalMessageMapper_ShouldRenderInteractiveLarkCardThroughNativeProducer()
    {
        var producer = new LarkChannelNativeMessageProducer(new LarkMessageComposer());

        var native = producer.Produce(
            RemoteToolApprovalMessageMapper.ToMessageContent(BuildRemoteApprovalNotification("agent-remote-1")),
            new ComposeContext());

        native.IsInteractive.Should().BeTrue();
        native.MessageType.Should().Be("interactive");
        var cardJson = native.CardPayload.Should().BeAssignableTo<JsonElement>().Subject.GetRawText();
        using var content = JsonDocument.Parse(cardJson);
        var elements = content.RootElement.GetProperty("body").GetProperty("elements");
        var approveValue = elements[1].GetProperty("behaviors")[0].GetProperty("value");
        approveValue.GetProperty("nyxid_approval_request_id").GetString().Should().Be("nyx-remote-1");
        approveValue.GetProperty("nyxid_approval_approved").GetBoolean().Should().BeTrue();
        var rejectValue = elements[2].GetProperty("behaviors")[0].GetProperty("value");
        rejectValue.GetProperty("nyxid_approval_request_id").GetString().Should().Be("nyx-remote-1");
        rejectValue.GetProperty("nyxid_approval_approved").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void BuildCardJson_WhenApprovalInteractionSpecPresent_ShouldIncludeWorkflowResumeIdentity()
    {
        var cardJson = LarkInteractionCardRenderer.BuildCardJson(
            new ChannelInteractionNotificationRequest
            {
                ActorId = "workflow-actor-1",
                RunId = "run-1",
                StepId = "approval-1",
                DeliveryTargetId = "agent-1",
                InteractionSpec = new InteractionSpec
                {
                    Title = "Approval required",
                    Body = "Approve?",
                    Actions =
                    {
                        new InteractionAction
                        {
                            Kind = InteractionActionKind.FormSubmit,
                            ActionId = "approve",
                            Label = "Approve",
                            ApprovalDecision = InteractionApprovalDecision.Approve,
                        },
                    },
                },
            });

        using var document = JsonDocument.Parse(cardJson);
        var approveValue = FindCallbackValue(document.RootElement, "approve");

        approveValue.GetProperty("actor_id").GetString().Should().Be("workflow-actor-1");
        approveValue.GetProperty("run_id").GetString().Should().Be("run-1");
        approveValue.GetProperty("step_id").GetString().Should().Be("approval-1");
        approveValue.GetProperty("approved").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void BuildCardJson_WhenTemplateSpecPresent_ShouldRenderLarkTemplateContent()
    {
        var template = new InteractionTemplateSpec { TemplateId = "tpl-1" };
        template.TemplateVariable["title"] = "Deploy";
        template.TemplateVariable["run"] = "run-1";

        var cardJson = LarkInteractionCardRenderer.BuildCardJson(
            new ChannelInteractionNotificationRequest
            {
                ActorId = "workflow-actor-1",
                RunId = "run-1",
                StepId = "notify-template",
                DeliveryTargetId = "agent-1",
                InteractionTemplateSpec = template,
            });

        using var document = JsonDocument.Parse(cardJson);
        document.RootElement.GetProperty("type").GetString().Should().Be("template");
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("template_id").GetString().Should().Be("tpl-1");
        data.GetProperty("template_variable").GetProperty("title").GetString().Should().Be("Deploy");
        data.GetProperty("template_variable").GetProperty("run").GetString().Should().Be("run-1");
    }

    [Fact]
    public void BuildCardJson_ShouldRejectMissingOrDoublePayload()
    {
        var empty = new ChannelInteractionNotificationRequest
        {
            ActorId = "workflow-actor-1",
            RunId = "run-1",
            StepId = "notify-1",
            DeliveryTargetId = "agent-1",
        };
        var both = empty with
        {
            InteractionSpec = new InteractionSpec { Title = "Status" },
            InteractionTemplateSpec = new InteractionTemplateSpec { TemplateId = "tpl-1" },
        };

        Action emptyAct = () => LarkInteractionCardRenderer.BuildCardJson(empty);
        Action bothAct = () => LarkInteractionCardRenderer.BuildCardJson(both);

        emptyAct.Should().Throw<InvalidOperationException>()
            .WithMessage("*exactly one typed payload*");
        bothAct.Should().Throw<InvalidOperationException>()
            .WithMessage("*exactly one typed payload*");
    }

    private static JsonElement FindCallbackValue(JsonElement element, string actionId)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type) &&
                type.GetString() == "callback" &&
                element.TryGetProperty("value", out var value) &&
                value.TryGetProperty("action_id", out var callbackActionId) &&
                callbackActionId.GetString() == actionId)
            {
                return value;
            }

            foreach (var property in element.EnumerateObject())
                if (TryFindCallbackValue(property.Value, actionId, out var match))
                    return match;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (TryFindCallbackValue(item, actionId, out var match))
                    return match;
        }

        throw new InvalidOperationException($"Callback value '{actionId}' was not found.");
    }

    private static bool TryFindCallbackValue(JsonElement element, string actionId, out JsonElement match)
    {
        try
        {
            match = FindCallbackValue(element, actionId);
            return true;
        }
        catch (InvalidOperationException)
        {
            match = default;
            return false;
        }
    }

    private static RemoteToolApprovalNotification BuildRemoteApprovalNotification(string? deliveryTargetId) =>
        new(
            new RemoteToolApprovalRequest(
                "local-req-1",
                "dangerous_tool",
                "call-1",
                """{"path":"/prod"}""",
                ToolApprovalMode.Auto,
                IsDestructive: true),
            new RemoteToolApprovalSubmission(
                "nyx-remote-1",
                DateTimeOffset.Parse("2026-06-11T10:00:00Z")),
            AgentToolExecutionContext.Empty with
            {
                Channel = new AgentToolChannelContext(
                    "lark",
                    "sender-1",
                    "scope-1",
                    "msg-1",
                    "om_1",
                    deliveryTargetId),
            });
}
