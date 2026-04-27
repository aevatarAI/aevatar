using System.Text.Json;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.GAgents.Platform.Lark;
using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Authoring;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// The outbound approval card is parsed back on the inbound side by
/// <c>NyxIdRelayTransport</c>, which normalizes the Lark callback <c>action.value</c> into the
/// strongly typed <c>CardActionSubmission.Arguments</c> map and <c>action.form_value</c> into
/// <c>CardActionSubmission.FormFields</c>. These tests pin that the composer output still carries
/// the correlation keys in the exact locations the transport reads, so <c>ChannelCardActionRouting</c>
/// can rebuild the workflow resume command end-to-end.
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
