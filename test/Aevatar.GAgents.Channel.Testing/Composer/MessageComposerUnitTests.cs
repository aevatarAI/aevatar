using Aevatar.GAgents.Channel.Abstractions;
using Shouldly;

namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// Composer unit test base class shared by every channel adapter.
/// </summary>
/// <typeparam name="TComposer">The composer under test.</typeparam>
/// <remarks>
/// These tests validate the pure <c>intent → native payload</c> translation expected of <see cref="IMessageComposer"/> and
/// <see cref="IMessageComposer{TNativePayload}"/>. Integration concerns (transport, auth) are out of scope and covered by
/// <see cref="ChannelAdapterConformanceTests{TAdapter}"/>.
/// </remarks>
public abstract class MessageComposerUnitTests<TComposer>
    where TComposer : IMessageComposer
{
    /// <summary>
    /// Creates the composer under test.
    /// </summary>
    protected abstract TComposer CreateComposer();

    /// <summary>
    /// Returns the capability matrix the composer expects at runtime.
    /// </summary>
    protected abstract ChannelCapabilities CreateCapabilities();

    /// <summary>
    /// Asserts that the supplied native payload matches the expected shape for <see cref="Compose_SimpleText_MatchesExpectedPayload"/>.
    /// </summary>
    /// <remarks>
    /// Each adapter has its own native payload shape (Slack Block Kit, Telegram keyboard, Lark card). Concrete tests
    /// override this hook to assert shape equivalence instead of hardcoding one native structure in the base class.
    /// </remarks>
    protected abstract void AssertSimpleTextPayload(object payload, MessageContent intent, ComposeContext context);

    /// <summary>
    /// Asserts the native payload for <see cref="Compose_WithActions_ProducesNativeActionsOrDegrades"/>.
    /// </summary>
    protected abstract void AssertActionsPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability);

    /// <summary>
    /// Asserts the native payload for <see cref="Compose_WithCards_EmitsNativeCardLayout"/>.
    /// </summary>
    protected abstract void AssertCardPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability);

    /// <summary>
    /// Asserts that the native payload honored the supplied <c>maxLength</c> cap.
    /// </summary>
    protected abstract void AssertOverflowTruncation(object payload, int maxLength);

    [Fact]
    public void Compose_SimpleText_MatchesExpectedPayload()
    {
        var composer = CreateComposer();
        var context = CreateContext();
        var intent = SampleMessageContent.SimpleText("hello world");

        var payload = composer.Compose(intent, context);

        payload.ShouldNotBeNull();
        AssertSimpleTextPayload(payload, intent, context);
    }

    [Fact]
    public void Compose_WithActions_ProducesNativeActionsOrDegrades()
    {
        var composer = CreateComposer();
        var context = CreateContext();
        var intent = SampleMessageContent.TextWithActions();
        var capability = composer.Evaluate(intent, context);

        capability.ShouldNotBe(ComposeCapability.Unspecified);

        if (capability == ComposeCapability.Unsupported)
            return;

        var payload = composer.Compose(intent, context);
        payload.ShouldNotBeNull();
        AssertActionsPayload(payload, intent, context, capability);
    }

    [Fact]
    public void Compose_WithCards_EmitsNativeCardLayout()
    {
        var composer = CreateComposer();
        var context = CreateContext();
        var intent = SampleMessageContent.TextWithCard();
        var capability = composer.Evaluate(intent, context);

        if (capability == ComposeCapability.Unsupported)
            return;

        var payload = composer.Compose(intent, context);
        payload.ShouldNotBeNull();
        AssertCardPayload(payload, intent, context, capability);
    }

    [Fact]
    public void Compose_OverflowsMaxLength_Truncates()
    {
        var composer = CreateComposer();
        var capabilities = CreateCapabilities();
        var max = capabilities.MaxMessageLength;

        if (max <= 0)
            return;

        var context = CreateContext(capabilities);
        var intent = SampleMessageContent.Overflowing(max);

        var payload = composer.Compose(intent, context);

        payload.ShouldNotBeNull();
        AssertOverflowTruncation(payload, max);
    }

    [Fact]
    public void Evaluate_ReturnsCorrectCapability()
    {
        var composer = CreateComposer();
        var context = CreateContext();
        var plain = composer.Evaluate(SampleMessageContent.SimpleText(), context);

        plain.ShouldBe(ComposeCapability.Exact);
    }

    [Fact]
    public void Evaluate_EphemeralOnNonSupportedChannel_ReturnsDegraded()
    {
        var capabilities = CreateCapabilities();
        if (capabilities.SupportsEphemeral)
            return;

        var composer = CreateComposer();
        var context = CreateContext(capabilities);
        var result = composer.Evaluate(SampleMessageContent.Ephemeral(), context);

        result.ShouldBeOneOf(ComposeCapability.Degraded, ComposeCapability.Unsupported);
    }

    /// <summary>
    /// Builds the <see cref="ComposeContext"/> used by composer tests.
    /// </summary>
    protected virtual ComposeContext CreateContext(ChannelCapabilities? capabilities = null)
    {
        var composer = CreateComposer();
        var reference = ConversationReference.Create(
            composer.Channel,
            BotInstanceId.From("composer-bot"),
            ConversationScope.DirectMessage,
            partition: null,
            "composer-user");

        return new ComposeContext
        {
            Conversation = reference,
            Capabilities = capabilities ?? CreateCapabilities(),
        };
    }
}
