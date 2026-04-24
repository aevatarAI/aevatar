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
}
