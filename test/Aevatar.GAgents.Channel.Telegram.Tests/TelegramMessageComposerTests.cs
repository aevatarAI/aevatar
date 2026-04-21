using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Telegram;
using Aevatar.GAgents.Channel.Testing;
using Shouldly;

namespace Aevatar.GAgents.Channel.Telegram.Tests;

public sealed class TelegramMessageComposerTests : MessageComposerUnitTests<TelegramMessageComposer>
{
    protected override TelegramMessageComposer CreateComposer() => new();

    protected override ChannelCapabilities CreateCapabilities() => new()
    {
        SupportsEphemeral = false,
        SupportsEdit = true,
        SupportsDelete = true,
        SupportsThread = false,
        Streaming = StreamingSupport.EditLoopRateLimited,
        SupportsFiles = true,
        MaxMessageLength = 4096,
        SupportsActionButtons = true,
        SupportsConfirmDialog = false,
        SupportsModal = false,
        SupportsMention = false,
        SupportsTyping = false,
        SupportsReactions = false,
        RecommendedStreamDebounceMs = 3000,
        Transport = TransportMode.Webhook,
    };

    protected override void AssertSimpleTextPayload(object payload, MessageContent intent, ComposeContext context)
    {
        var native = payload.ShouldBeOfType<TelegramNativePayload>();
        native.Text.ShouldBe(intent.Text);
        native.Attachment.ShouldBeNull();
    }

    protected override void AssertActionsPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability)
    {
        var native = payload.ShouldBeOfType<TelegramNativePayload>();
        if (capability == ComposeCapability.Exact)
            native.ReplyMarkup.ShouldNotBeNull();
    }

    protected override void AssertCardPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability)
    {
        var native = payload.ShouldBeOfType<TelegramNativePayload>();
        native.Text.ShouldContain("Hero");
        capability.ShouldBe(ComposeCapability.Degraded);
    }

    protected override void AssertOverflowTruncation(object payload, int maxLength)
    {
        payload.ShouldBeOfType<TelegramNativePayload>().Text.Length.ShouldBeLessThanOrEqualTo(maxLength);
    }

    [Fact]
    public void Compose_WhitespaceOnlyIntent_DoesNotInventPlaceholderText()
    {
        var composer = CreateComposer();
        var payload = composer.Compose(new MessageContent
        {
            Text = "   ",
            Disposition = MessageDisposition.Normal,
        }, CreateContext());

        payload.Text.ShouldBeEmpty();
    }

    [Fact]
    public void Options_DefaultLongPollingTimeout_UsesTelegramFriendlyDefault()
    {
        new TelegramChannelAdapterOptions().LongPollingTimeoutSeconds.ShouldBe(30);
    }
}
