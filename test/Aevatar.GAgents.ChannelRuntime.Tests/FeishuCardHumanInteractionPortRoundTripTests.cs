using System.Text.Json;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.GAgents.Platform.Lark;
using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Channel.NyxIdRelay;
using System.Text;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// The outbound approval card is parsed back on the inbound side by
/// <c>NyxIdRelayTransport</c>, which normalizes the Lark callback <c>action.value</c> into the
/// <c>CardActionSubmission.WorkflowResume</c>. These tests pin that the composer output remains
/// Lark/Nyx JSON-compatible at the adapter edge while internal routing consumes typed payloads.
/// </summary>
public sealed class FeishuCardHumanInteractionPortRoundTripTests
{
    [Fact]
    public void Approval_card_buttons_carry_correlation_keys_in_callback_value()
    {
        var card = FeishuCardHumanInteractionPort.BuildCardJson(new HumanInteractionRequest
        {
            ActorId = "actor-A",
            RunId = "run-A",
            StepId = "step-A",
            SuspensionType = "human_approval",
            Prompt = "Approve the draft",
            Content = "draft body",
            Options = ["approve", "reject"],
        });

        using var document = JsonDocument.Parse(card);
        var formElements = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")[1]
            .GetProperty("elements");

        var approveButton = formElements
            .EnumerateArray()
            .First(e => e.GetProperty("tag").GetString() == "button" &&
                        e.GetProperty("text").GetProperty("content").GetString() == "Approve");
        approveButton.GetProperty("text").GetProperty("content").GetString().Should().Be("Approve");
        var approveValue = approveButton.GetProperty("behaviors")[0].GetProperty("value");
        approveValue.GetProperty("actor_id").GetString().Should().Be("actor-A");
        approveValue.GetProperty("run_id").GetString().Should().Be("run-A");
        approveValue.GetProperty("step_id").GetString().Should().Be("step-A");
        approveValue.GetProperty("approved").GetBoolean().Should().BeTrue();
        approveButton.GetProperty("name").GetString().Should().Be("approve");
        approveButton.GetProperty("form_action_type").GetString().Should().Be("submit");
        approveButton.TryGetProperty("value", out _).Should().BeFalse();

        var rejectButton = formElements
            .EnumerateArray()
            .First(e => e.GetProperty("tag").GetString() == "button" &&
                        e.GetProperty("text").GetProperty("content").GetString() == "Reject");
        rejectButton.GetProperty("text").GetProperty("content").GetString().Should().Be("Reject");
        var rejectValue = rejectButton.GetProperty("behaviors")[0].GetProperty("value");
        rejectValue.GetProperty("actor_id").GetString().Should().Be("actor-A");
        rejectValue.GetProperty("approved").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Approval_card_round_trips_to_typed_workflow_resume_payload()
    {
        var card = FeishuCardHumanInteractionPort.BuildCardJson(new HumanInteractionRequest
        {
            ActorId = "actor-A",
            RunId = "run-A",
            StepId = "step-A",
            SuspensionType = "human_approval",
            Prompt = "Approve the draft",
            Content = "draft body",
            Options = ["approve", "reject"],
        });

        using var document = JsonDocument.Parse(card);
        var formElements = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")[1]
            .GetProperty("elements");
        var approveButton = formElements
            .EnumerateArray()
            .First(e => e.GetProperty("tag").GetString() == "button" &&
                        e.GetProperty("text").GetProperty("content").GetString() == "Approve");
        var callbackText = JsonSerializer.Serialize(new
        {
            value = approveButton.GetProperty("behaviors")[0].GetProperty("value"),
            form_value = new Dictionary<string, string>
            {
                ["edited_content"] = "final draft",
                ["user_input"] = "looks good",
            },
        });
        var body = $$"""
            {
              "message_id": "msg-card-typed-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_1", "display_name": "User One" },
              "content": {
                "content_type": "card_action",
                "text": {{JsonSerializer.Serialize(callbackText)}}
              }
            }
            """;

        var parsed = new NyxIdRelayTransport().Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        var cardAction = parsed.Activity!.Content.CardAction;
        cardAction.WorkflowResume.ActorId.Should().Be("actor-A");
        cardAction.WorkflowResume.RunId.Should().Be("run-A");
        cardAction.WorkflowResume.StepId.Should().Be("step-A");
        cardAction.WorkflowResume.Approved.Should().BeTrue();
        cardAction.WorkflowResume.EditedContent.Should().Be("final draft");
        cardAction.WorkflowResume.UserInput.Should().Be("looks good");

        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_1",
            SenderId = "ou_1",
            SenderName = "User One",
            Text = string.Empty,
            MessageId = "evt-card-typed-1",
            ChatType = "card_action",
            CardAction = cardAction,
        };
        ChannelCardActionRouting.TryBuildWorkflowResumeCommand(inbound, out var command).Should().BeTrue();
        command!.ActorId.Should().Be("actor-A");
        command.RunId.Should().Be("run-A");
        command.StepId.Should().Be("step-A");
        command.Approved.Should().BeTrue();
        command.UserInput.Should().Be("final draft");
        command.Feedback.Should().Be("looks good");
    }

    [Fact]
    public void Typed_interaction_spec_card_round_trips_to_workflow_resume_command()
    {
        var card = FeishuCardHumanInteractionPort.BuildCardJson(new HumanInteractionRequest
        {
            ActorId = "actor-T",
            RunId = "run-T",
            StepId = "approval-T",
            SuspensionType = "human_approval",
            Options = ["approve", "reject"],
            Prompt = "fallback",
            InteractionSpec = new InteractionSpec
            {
                Title = "Typed approval",
                Body = "Review typed output",
                Actions =
                {
                    new InteractionAction
                    {
                        Kind = InteractionActionKind.FormSubmit,
                        ActionId = "primary-review-action",
                        Label = "Approve",
                        Style = InteractionActionStyle.Primary,
                        ApprovalDecision = InteractionApprovalDecision.Approve,
                    },
                    new InteractionAction
                    {
                        Kind = InteractionActionKind.FormSubmit,
                        ActionId = "danger-review-action",
                        Label = "Reject",
                        Style = InteractionActionStyle.Danger,
                        ApprovalDecision = InteractionApprovalDecision.Reject,
                    },
                },
            },
        });

        using var document = JsonDocument.Parse(card);
        var formElements = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")[1]
            .GetProperty("elements");
        var rejectButton = formElements
            .EnumerateArray()
            .First(e => e.GetProperty("tag").GetString() == "button" &&
                        e.GetProperty("text").GetProperty("content").GetString() == "Reject");
        var callbackText = JsonSerializer.Serialize(new
        {
            value = rejectButton.GetProperty("behaviors")[0].GetProperty("value"),
            form_value = new Dictionary<string, string>
            {
                ["user_input"] = "Needs edits",
            },
        });
        var body = $$"""
            {
              "message_id": "msg-card-typed-spec-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_1", "display_name": "User One" },
              "content": {
                "content_type": "card_action",
                "text": {{JsonSerializer.Serialize(callbackText)}}
              }
            }
            """;

        var parsed = new NyxIdRelayTransport().Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        var cardAction = parsed.Activity!.Content.CardAction;
        cardAction.ActionKind.Should().Be(ActionElementKind.FormSubmit);
        cardAction.WorkflowResume.ActorId.Should().Be("actor-T");
        cardAction.WorkflowResume.RunId.Should().Be("run-T");
        cardAction.WorkflowResume.StepId.Should().Be("approval-T");
        cardAction.Arguments["action_id"].Should().Be("danger-review-action");
        cardAction.WorkflowResume.Approved.Should().BeFalse();
        cardAction.WorkflowResume.UserInput.Should().Be("Needs edits");

        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_1",
            SenderId = "ou_1",
            SenderName = "User One",
            Text = string.Empty,
            MessageId = "evt-card-typed-spec-1",
            ChatType = "card_action",
            CardAction = cardAction,
        };
        ChannelCardActionRouting.TryBuildWorkflowResumeCommand(inbound, out var command).Should().BeTrue();
        command!.ActorId.Should().Be("actor-T");
        command.RunId.Should().Be("run-T");
        command.StepId.Should().Be("approval-T");
        command.Approved.Should().BeFalse();
        command.UserInput.Should().Be("Needs edits");
        command.Feedback.Should().Be("Needs edits");
    }

    [Fact]
    public void Approval_card_inputs_render_as_form_text_inputs_with_form_field_names()
    {
        var card = FeishuCardHumanInteractionPort.BuildCardJson(new HumanInteractionRequest
        {
            ActorId = "actor-A",
            RunId = "run-A",
            StepId = "step-A",
            SuspensionType = "human_approval",
            Prompt = "Approve the draft",
            Options = ["approve", "reject"],
        });

        using var document = JsonDocument.Parse(card);
        var formElements = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")[1]
            .GetProperty("elements");

        var inputs = formElements
            .EnumerateArray()
            .Where(e => e.GetProperty("tag").GetString() == "input")
            .ToArray();
        inputs.Should().HaveCount(2);
        inputs[0].GetProperty("name").GetString().Should().Be("edited_content");
        inputs[0].TryGetProperty("label", out _).Should().BeFalse();
        inputs[1].GetProperty("name").GetString().Should().Be("user_input");
        inputs[1].TryGetProperty("label", out _).Should().BeFalse();
    }

    [Fact]
    public void Input_mode_card_renders_single_input_and_submit_button()
    {
        var card = FeishuCardHumanInteractionPort.BuildCardJson(new HumanInteractionRequest
        {
            ActorId = "actor-B",
            RunId = "run-B",
            StepId = "input-B",
            SuspensionType = "human_input",
            Prompt = "Clarify the source",
            Options = ["submit"],
        });

        using var document = JsonDocument.Parse(card);
        document.RootElement
            .GetProperty("header")
            .GetProperty("title")
            .GetProperty("content")
            .GetString()
            .Should()
            .Be("Input required.");

        var formElements = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")[1]
            .GetProperty("elements");

        var input = formElements
            .EnumerateArray()
            .Single(e => e.GetProperty("tag").GetString() == "input");
        input.GetProperty("name").GetString().Should().Be("user_input");
        input.TryGetProperty("label", out _).Should().BeFalse();
        var submitButton = formElements
            .EnumerateArray()
            .Single(e => e.GetProperty("tag").GetString() == "button");
        submitButton.GetProperty("name").GetString().Should().Be("submit");
        var submitValue = submitButton.GetProperty("behaviors")[0].GetProperty("value");
        submitValue.GetProperty("actor_id").GetString().Should().Be("actor-B");
        submitValue.GetProperty("run_id").GetString().Should().Be("run-B");
        submitValue.GetProperty("step_id").GetString().Should().Be("input-B");
        submitValue.TryGetProperty("approved", out _).Should().BeFalse();
    }

    [Fact]
    public void Approval_card_header_template_is_orange_when_any_action_is_danger()
    {
        var card = FeishuCardHumanInteractionPort.BuildCardJson(new HumanInteractionRequest
        {
            ActorId = "actor-A",
            RunId = "run-A",
            StepId = "step-A",
            SuspensionType = "human_approval",
            Prompt = "Approve",
            Options = ["approve", "reject"],
        });

        using var document = JsonDocument.Parse(card);
        document.RootElement
            .GetProperty("header")
            .GetProperty("template")
            .GetString()
            .Should()
            .Be("orange");
    }

    [Fact]
    public void Input_mode_card_header_template_is_blue()
    {
        var card = FeishuCardHumanInteractionPort.BuildCardJson(new HumanInteractionRequest
        {
            ActorId = "actor-B",
            RunId = "run-B",
            StepId = "input-B",
            SuspensionType = "human_input",
            Prompt = "Clarify",
            Options = ["submit"],
        });

        using var document = JsonDocument.Parse(card);
        document.RootElement
            .GetProperty("header")
            .GetProperty("template")
            .GetString()
            .Should()
            .Be("blue");
    }
}
