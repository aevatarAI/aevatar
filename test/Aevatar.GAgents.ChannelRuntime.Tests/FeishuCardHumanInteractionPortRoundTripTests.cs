using System.Text.Json;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.GAgents.Platform.Lark;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// The outbound approval card is parsed back on the inbound side by
/// <c>NyxIdRelayWorkflowCards</c>, which flattens <c>action.value</c> and <c>action.form_value</c>
/// into a single scalar map keyed by property name. These tests pin that the migrated
/// composer output still carries the correlation keys in the exact locations the parser reads,
/// so the approval flow keeps working end-to-end.
/// </summary>
public sealed class FeishuCardHumanInteractionPortRoundTripTests
{
    [Fact]
    public void Approval_card_buttons_carry_correlation_keys_in_value()
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

        var approveButton = formElements[2];
        approveButton.GetProperty("text").GetProperty("content").GetString().Should().Be("Approve");
        var approveValue = approveButton.GetProperty("value");
        approveValue.GetProperty("actor_id").GetString().Should().Be("actor-A");
        approveValue.GetProperty("run_id").GetString().Should().Be("run-A");
        approveValue.GetProperty("step_id").GetString().Should().Be("step-A");
        approveValue.GetProperty("approved").GetBoolean().Should().BeTrue();
        approveButton.GetProperty("name").GetString().Should().Be("approve");
        approveButton.GetProperty("form_action_type").GetString().Should().Be("submit");

        var rejectButton = formElements[3];
        rejectButton.GetProperty("text").GetProperty("content").GetString().Should().Be("Reject");
        var rejectValue = rejectButton.GetProperty("value");
        rejectValue.GetProperty("actor_id").GetString().Should().Be("actor-A");
        rejectValue.GetProperty("approved").GetBoolean().Should().BeFalse();
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

        formElements[0].GetProperty("tag").GetString().Should().Be("input");
        formElements[0].GetProperty("name").GetString().Should().Be("edited_content");
        formElements[1].GetProperty("tag").GetString().Should().Be("input");
        formElements[1].GetProperty("name").GetString().Should().Be("user_input");
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

        formElements.GetArrayLength().Should().Be(2);
        formElements[0].GetProperty("tag").GetString().Should().Be("input");
        formElements[0].GetProperty("name").GetString().Should().Be("user_input");
        formElements[1].GetProperty("tag").GetString().Should().Be("button");
        formElements[1].GetProperty("name").GetString().Should().Be("submit");
        var submitValue = formElements[1].GetProperty("value");
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
