using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Channel.Testing;
using Shouldly;

namespace Aevatar.GAgents.Platform.Lark.Tests;

public sealed class LarkMessageComposerTests : MessageComposerUnitTests<LarkMessageComposer>
{
    protected override LarkMessageComposer CreateComposer() => new();

    protected override ChannelCapabilities CreateCapabilities() => LarkMessageComposer.DefaultCapabilities.Clone();

    protected override void AssertSimpleTextPayload(object payload, MessageContent intent, ComposeContext context)
    {
        var native = payload.ShouldBeOfType<LarkOutboundMessage>();
        native.MessageType.ShouldBe("text");
        using var document = JsonDocument.Parse(native.ContentJson);
        document.RootElement.GetProperty("text").GetString().ShouldBe(intent.Text);
    }

    protected override void AssertActionsPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability)
    {
        var native = payload.ShouldBeOfType<LarkOutboundMessage>();
        native.MessageType.ShouldBe("interactive");
        native.ContentJson.ShouldContain("Confirm");
        native.ContentJson.ShouldContain("Cancel");
    }

    protected override void AssertCardPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability)
    {
        var native = payload.ShouldBeOfType<LarkOutboundMessage>();
        native.MessageType.ShouldBe("interactive");
        native.ContentJson.ShouldContain("Hero");
        native.ContentJson.ShouldContain("Hero body");
    }

    protected override void AssertOverflowTruncation(object payload, int maxLength)
    {
        var native = payload.ShouldBeOfType<LarkOutboundMessage>();
        native.PlainText.Length.ShouldBeLessThanOrEqualTo(maxLength);
    }

    [Fact]
    public void Compose_WhenTextContainsSurrogatePair_DoesNotSplitTextElement()
    {
        var payload = CreateComposer().Compose(
            new MessageContent
            {
                Text = "A🙂B",
            },
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = new ChannelCapabilities
                {
                    MaxMessageLength = 2,
                },
            });

        payload.PlainText.ShouldBe("A🙂");
    }

    [Fact]
    public void Compose_WhenRenderingInteractiveCard_UsesLarkV2BodyElements()
    {
        var intent = new MessageContent
        {
            Text = "Choose an agent",
        };
        intent.Cards.Add(new CardBlock
        {
            Title = "Agents",
            Text = "skill-runner",
        });
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "status",
            Label = "Status",
        });

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        payload.MessageType.ShouldBe("interactive");
        using var document = JsonDocument.Parse(payload.ContentJson);
        document.RootElement.GetProperty("schema").GetString().ShouldBe("2.0");
        document.RootElement.TryGetProperty("elements", out _).ShouldBeFalse();
        var bodyElements = document.RootElement.GetProperty("body").GetProperty("elements");
        bodyElements.GetArrayLength().ShouldBe(3);
        bodyElements[0].GetProperty("content").GetString().ShouldBe("Choose an agent");
        var cardMarkdown = bodyElements[1].GetProperty("content").GetString();
        cardMarkdown.ShouldNotBeNull();
        cardMarkdown.ShouldContain("skill-runner");
        var button = bodyElements[2];
        button.GetProperty("tag").GetString().ShouldBe("button");
        button.TryGetProperty("value", out _).ShouldBeFalse();
        var behavior = button.GetProperty("behaviors")[0];
        behavior.GetProperty("type").GetString().ShouldBe("callback");
        behavior.GetProperty("value").GetProperty("action_id").GetString().ShouldBe("status");
    }

    [Fact]
    public void Compose_WhenFormInputCarriesValue_RendersLarkDefaultValue()
    {
        var intent = new MessageContent();
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.TextInput,
            ActionId = "github_username",
            Label = "GitHub Username",
            Placeholder = "octocat",
            Value = "eanzhao",
        });
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.FormSubmit,
            ActionId = "submit",
            Label = "Submit",
            IsPrimary = true,
        });

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        payload.MessageType.ShouldBe("interactive");
        using var document = JsonDocument.Parse(payload.ContentJson);
        var formElement = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "form");
        var inputElement = formElement
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "input");
        inputElement.TryGetProperty("label", out _).ShouldBeFalse();
        inputElement.GetProperty("default_value").GetString().ShouldBe("eanzhao");
    }

    [Fact]
    public void Compose_WhenFormInputHasNoValue_OmitsLarkDefaultValue()
    {
        var intent = new MessageContent();
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.TextInput,
            ActionId = "github_username",
            Label = "GitHub Username",
            Placeholder = "octocat",
        });
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.FormSubmit,
            ActionId = "submit",
            Label = "Submit",
            IsPrimary = true,
        });

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        using var document = JsonDocument.Parse(payload.ContentJson);
        var formElement = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "form");
        var inputElement = formElement
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "input");
        inputElement.TryGetProperty("default_value", out _).ShouldBeFalse();
    }

    [Fact]
    public void Compose_WhenRenderingFormSubmit_UsesLarkV2CallbackBehavior()
    {
        var intent = new MessageContent();
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.TextInput,
            ActionId = "github_username",
            Label = "GitHub Username",
            Placeholder = "octocat",
        });
        var submit = new ActionElement
        {
            Kind = ActionElementKind.FormSubmit,
            ActionId = "submit_daily_report",
            Label = "Create",
            IsPrimary = true,
        };
        submit.Arguments["agent_builder_action"] = "create_daily_report";
        intent.Actions.Add(submit);

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        using var document = JsonDocument.Parse(payload.ContentJson);
        var formElement = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "form");
        var submitButton = formElement
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "button");

        submitButton.GetProperty("name").GetString().ShouldBe("submit_daily_report");
        submitButton.GetProperty("form_action_type").GetString().ShouldBe("submit");
        submitButton.TryGetProperty("value", out _).ShouldBeFalse();
        var behavior = submitButton.GetProperty("behaviors")[0];
        behavior.GetProperty("type").GetString().ShouldBe("callback");
        var value = behavior.GetProperty("value");
        value.GetProperty("action_id").GetString().ShouldBe("submit_daily_report");
        value.GetProperty("agent_builder_action").GetString().ShouldBe("create_daily_report");
    }
}
