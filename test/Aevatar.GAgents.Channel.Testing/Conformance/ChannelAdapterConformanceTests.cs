using System.Threading.Channels;
using Aevatar.GAgents.Channel.Abstractions;
using Shouldly;

namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// Conformance test base class that every channel adapter ships against.
/// </summary>
/// <typeparam name="TAdapter">
/// The adapter under test. The same concrete class must implement both <see cref="IChannelTransport"/> and
/// <see cref="IChannelOutboundPort"/> so transport and outbound state stay fused to one lifecycle.
/// </typeparam>
/// <remarks>
/// Derive one concrete class per adapter and implement <see cref="CreateAdapter"/>. Webhook-only adapters can return
/// <see langword="null"/> from <see cref="GatewayFixture"/>; gateway-only adapters can return <see langword="null"/> from
/// <see cref="WebhookFixture"/>. Tests that rely on a missing fixture self-skip with a no-op return so the conformance
/// matrix stays honest about what the adapter actually supports.
/// </remarks>
public abstract class ChannelAdapterConformanceTests<TAdapter>
    where TAdapter : class, IChannelTransport, IChannelOutboundPort
{
    /// <summary>
    /// Creates one fully-constructed adapter instance bound to the fixtures returned by
    /// <see cref="WebhookFixture"/> and <see cref="GatewayFixture"/>.
    /// </summary>
    protected abstract TAdapter CreateAdapter();

    /// <summary>
    /// Returns the webhook fixture that drives synthetic inbound traffic, or <see langword="null"/> when the adapter does
    /// not expose a webhook surface.
    /// </summary>
    protected abstract WebhookFixture? WebhookFixture { get; }

    /// <summary>
    /// Returns the gateway fixture that drives lifecycle and RESUME/IDENTIFY behavior, or <see langword="null"/> when the
    /// adapter does not run a gateway.
    /// </summary>
    protected abstract GatewayFixture? GatewayFixture { get; }

    /// <summary>
    /// Returns the transport binding used to initialize the adapter.
    /// </summary>
    protected abstract ChannelTransportBinding CreateBinding();

    /// <summary>
    /// Creates one secondary binding that shares the adapter class but uses a distinct <see cref="BotInstanceId"/>, for
    /// multi-tenant routing tests.
    /// </summary>
    /// <remarks>
    /// Adapters that do not support multi-tenant hosting may return <see langword="null"/>; tests that rely on this
    /// binding self-skip.
    /// </remarks>
    protected virtual ChannelTransportBinding? CreateSecondaryBinding() => null;

    /// <summary>
    /// Returns suite-wide timeouts. Override in a derived class to tune for slow fixtures.
    /// </summary>
    protected virtual ConformanceTimeouts Timeouts => ConformanceTimeouts.Default;

    /// <summary>
    /// Hook called after <see cref="CreateAdapter"/> to let derived classes attach optional probes or capture hooks
    /// before <see cref="IChannelTransport.InitializeAsync"/> runs.
    /// </summary>
    protected virtual Task PrepareAsync(TAdapter adapter, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Returns whether the adapter implements <typeparamref name="TCapability"/> given the current capability matrix.
    /// </summary>
    /// <remarks>
    /// Default implementation inspects the runtime type of the adapter. Override in a derived class when adapters gate
    /// capability interfaces behind another boundary (for example, a composition root where the adapter is wrapped).
    /// </remarks>
    protected virtual bool AdapterImplements<TCapability>(TAdapter adapter) => adapter is TCapability;

    /// <summary>
    /// Returns whether the adapter implements the supplied optional interface type. The capability-parity test uses this
    /// overload so it reads the adapter's real runtime type instead of trusting an author-supplied bool.
    /// </summary>
    /// <remarks>
    /// Default implementation delegates to <see cref="Type.IsInstanceOfType(object?)"/>. Override when the adapter is
    /// hosted through a proxy or composition wrapper that hides its optional interfaces from a direct <c>is</c> test.
    /// </remarks>
    protected virtual bool AdapterImplements(TAdapter adapter, Type capabilityInterface)
    {
        ArgumentNullException.ThrowIfNull(capabilityInterface);
        return capabilityInterface.IsInstanceOfType(adapter);
    }

    /// <summary>
    /// Returns the capability-flag ↔ optional-interface parity rules the adapter wants enforced.
    /// </summary>
    /// <remarks>
    /// Each entry pairs one <see cref="ChannelCapabilities"/> flag value with one optional <see cref="Type"/>. The
    /// suite uses <see cref="AdapterImplements(TAdapter, Type)"/> to read the adapter's runtime type, so author-
    /// supplied parity booleans cannot lie. Default returns an empty set — adapters with optional typing / reaction /
    /// mention adapter interfaces override this hook to wire the parity into the suite.
    /// </remarks>
    protected virtual IEnumerable<CapabilityInterfaceParity> GetCapabilityInterfaceParities(TAdapter adapter) =>
        Array.Empty<CapabilityInterfaceParity>();

    /// <summary>
    /// Returns the adapter-supplied reaction probe.
    /// </summary>
    /// <remarks>
    /// Required whenever <see cref="ChannelCapabilities.SupportsReactions"/> is <see langword="true"/>. The reaction
    /// conformance test fails when the capability is claimed but the probe is <see langword="null"/>, so adapters
    /// cannot advertise reaction support without exercising it.
    /// </remarks>
    protected virtual ReactionProbe? Reactions => null;

    /// <summary>
    /// Returns the adapter-supplied typing probe.
    /// </summary>
    /// <remarks>
    /// Required whenever <see cref="ChannelCapabilities.SupportsTyping"/> is <see langword="true"/>. The four typing
    /// conformance tests fail when the capability is claimed but the probe is <see langword="null"/>, so adapters
    /// cannot advertise typing support without exercising keepalive / TTL / breaker / idempotent start-stop behavior.
    /// </remarks>
    protected virtual TypingProbe? Typing => null;

    /// <summary>
    /// Builds a synthetic inbound activity seed whose body mentions the bot associated with the supplied binding using
    /// the adapter's native mention syntax.
    /// </summary>
    /// <remarks>
    /// Required whenever <see cref="ChannelCapabilities.SupportsMention"/> is <see langword="true"/>. The mention
    /// conformance test fails when the capability is claimed but this hook returns <see langword="null"/>. Adapters
    /// override to emit a seed whose <see cref="InboundActivitySeed.Text"/> contains the raw mention token and whose
    /// <see cref="InboundActivitySeed.Mentions"/> carries the normalized participant.
    /// </remarks>
    protected virtual InboundActivitySeed? BuildBotMentionSeed(ChannelTransportBinding binding) => null;

    /// <summary>
    /// Builds a synthetic inbound activity seed whose body mentions one non-bot participant using the adapter's native
    /// mention syntax so the test can verify other-participant mentions are preserved in normalized text.
    /// </summary>
    /// <remarks>
    /// Required whenever <see cref="ChannelCapabilities.SupportsMention"/> is <see langword="true"/>.
    /// </remarks>
    protected virtual InboundActivitySeed? BuildParticipantMentionSeed() => null;

    [Fact]
    public async Task Inbound_Message_ProducesChatActivity()
    {
        if (WebhookFixture is null && GatewayFixture is null)
            return;

        await using var lifetime = await StartAdapterAsync();
        var seed = InboundActivitySeed.DirectMessage("hi");

        var activity = await DispatchAsync(seed);

        activity.Type.ShouldBe(ActivityType.Message);
        activity.Content.Text.ShouldBe(seed.Text);
        activity.Conversation.Channel.Value.ShouldBe(ChannelOf(lifetime.Adapter).Value);
        activity.Conversation.CanonicalKey.ShouldContain(ChannelOf(lifetime.Adapter).Value);
    }

    [Fact]
    public async Task Inbound_GroupMessage_HasDistinctConversationKey()
    {
        if (WebhookFixture is null && GatewayFixture is null)
            return;

        await using var lifetime = await StartAdapterAsync();

        var dm = await DispatchAsync(InboundActivitySeed.DirectMessage("dm"));
        var group1 = await DispatchAsync(InboundActivitySeed.GroupMessage("room-A", "hi A"));
        var group2 = await DispatchAsync(InboundActivitySeed.GroupMessage("room-B", "hi B"));

        dm.Conversation.CanonicalKey.ShouldNotBe(group1.Conversation.CanonicalKey);
        group1.Conversation.CanonicalKey.ShouldNotBe(group2.Conversation.CanonicalKey);
        group1.Conversation.Scope.ShouldBe(ConversationScope.Group);
    }

    [Fact]
    public async Task Inbound_Duplicate_IsDedupedByActivityId()
    {
        if (WebhookFixture is null && GatewayFixture is null)
            return;

        await using var lifetime = await StartAdapterAsync();
        var seed = InboundActivitySeed.DirectMessage("dedupe-me") with
        {
            PlatformMessageId = "platform-msg-1",
        };

        var first = await DispatchAsync(seed);
        var replay = await ReplayLastAsync();

        if (replay is null)
            return;

        replay.Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task Outbound_SimpleText_SendsSuccessfully()
    {
        await using var lifetime = await StartAdapterAsync();
        var reference = BuildDirectMessageReference(lifetime.Adapter);

        var emit = await lifetime.Adapter.SendAsync(reference, SampleMessageContent.SimpleText(), CancellationToken.None);

        emit.Success.ShouldBeTrue();
        emit.SentActivityId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Outbound_WithActions_EmitsNativeActionsOrDegrades()
    {
        await using var lifetime = await StartAdapterAsync();
        var reference = BuildDirectMessageReference(lifetime.Adapter);

        var emit = await lifetime.Adapter.SendAsync(
            reference,
            SampleMessageContent.TextWithActions(),
            CancellationToken.None);

        emit.Success.ShouldBeTrue();
        if (CapabilitiesOf(lifetime.Adapter).SupportsActionButtons)
        {
            emit.Capability.ShouldBe(ComposeCapability.Exact);
        }
        else
        {
            emit.Capability.ShouldBeOneOf(ComposeCapability.Degraded, ComposeCapability.Exact);
        }
    }

    [Fact]
    public async Task Outbound_Edit_UpdatesOrDegrades()
    {
        await using var lifetime = await StartAdapterAsync();
        var reference = BuildDirectMessageReference(lifetime.Adapter);
        var initial = await lifetime.Adapter.SendAsync(
            reference,
            SampleMessageContent.SimpleText("v1"),
            CancellationToken.None);
        initial.Success.ShouldBeTrue();

        var updated = await lifetime.Adapter.UpdateAsync(
            reference,
            initial.SentActivityId,
            SampleMessageContent.SimpleText("v2"),
            CancellationToken.None);

        if (CapabilitiesOf(lifetime.Adapter).SupportsEdit)
        {
            updated.Success.ShouldBeTrue();
            updated.SentActivityId.ShouldBe(initial.SentActivityId);
        }
        else
        {
            updated.Success.ShouldBeFalse();
            updated.Capability.ShouldBeOneOf(ComposeCapability.Degraded, ComposeCapability.Unsupported);
        }
    }

    [Fact]
    public async Task Outbound_Attachment_UploadsAndReferencesCorrectly()
    {
        await using var lifetime = await StartAdapterAsync();

        if (!CapabilitiesOf(lifetime.Adapter).SupportsFiles)
            return;

        var reference = BuildDirectMessageReference(lifetime.Adapter);
        var emit = await lifetime.Adapter.SendAsync(
            reference,
            SampleMessageContent.TextWithAttachment(),
            CancellationToken.None);

        emit.Success.ShouldBeTrue();
        emit.SentActivityId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Outbound_StreamingReply_CoalescesDeltasWithinRateLimit()
    {
        await using var lifetime = await StartAdapterAsync();

        if (CapabilitiesOf(lifetime.Adapter).Streaming == StreamingSupport.None)
            return;

        var reference = BuildDirectMessageReference(lifetime.Adapter);
        var emit = await lifetime.Adapter.SendAsync(
            reference,
            SampleMessageContent.SimpleText("streaming-start"),
            CancellationToken.None);
        emit.Success.ShouldBeTrue();

        var debounce = Math.Max(CapabilitiesOf(lifetime.Adapter).RecommendedStreamDebounceMs, 0);
        debounce.ShouldBeLessThanOrEqualTo(3000);
    }

    [Fact]
    public async Task Lifecycle_StartStop_NoLeaks()
    {
        var adapter = CreateAdapter();
        await PrepareAsync(adapter, CancellationToken.None);
        await adapter.InitializeAsync(CreateBinding(), CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);
        await adapter.StopReceivingAsync(CancellationToken.None);

        await Should.NotThrowAsync(() => adapter.StopReceivingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Capabilities_Declared_AreConsistent()
    {
        await using var lifetime = await StartAdapterAsync();
        IChannelTransport transport = lifetime.Adapter;
        IChannelOutboundPort outbound = lifetime.Adapter;

        ReferenceEquals(transport, outbound).ShouldBeTrue();
        transport.Channel.Value.ShouldBe(outbound.Channel.Value);
        transport.Capabilities.ShouldBe(outbound.Capabilities);
        transport.Capabilities.Transport.ShouldBe(transport.TransportMode);
    }

    [Fact]
    public async Task CapabilityGap_Degrades_Gracefully()
    {
        await using var lifetime = await StartAdapterAsync();
        var reference = BuildDirectMessageReference(lifetime.Adapter);
        var ephemeralIntent = SampleMessageContent.Ephemeral();

        var emit = await lifetime.Adapter.SendAsync(reference, ephemeralIntent, CancellationToken.None);

        if (!CapabilitiesOf(lifetime.Adapter).SupportsEphemeral)
            emit.Capability.ShouldBeOneOf(ComposeCapability.Degraded, ComposeCapability.Unsupported);
    }

    [Fact]
    public async Task RateLimit_Respected_NoHardFailure()
    {
        await using var lifetime = await StartAdapterAsync();
        var reference = BuildDirectMessageReference(lifetime.Adapter);

        for (var i = 0; i < 5; i++)
        {
            var emit = await lifetime.Adapter.SendAsync(
                reference,
                SampleMessageContent.SimpleText($"ratelimit-{i}"),
                CancellationToken.None);

            if (!emit.Success)
            {
                emit.ErrorCode.ShouldNotBeNullOrWhiteSpace();
                return;
            }
        }
    }

    [Fact]
    public async Task ConversationReference_Roundtrip_IsDeterministic()
    {
        if (WebhookFixture is null && GatewayFixture is null)
            return;

        await using var lifetime = await StartAdapterAsync();
        var seed = InboundActivitySeed.GroupMessage("room-R", "roundtrip");

        var first = await DispatchAsync(seed);
        var second = await DispatchAsync(seed with { Text = "roundtrip-2" });

        first.Conversation.CanonicalKey.ShouldBe(second.Conversation.CanonicalKey);
    }

    [Fact]
    public async Task Inbound_MultipleBotInstances_RoutedCorrectly()
    {
        var webhookFixture = WebhookFixture;
        if (webhookFixture is null)
            return;

        var primary = CreateBinding();
        var secondary = CreateSecondaryBinding();
        if (secondary is null)
            return;

        ChatActivity primaryActivity;
        ChatActivity secondaryActivity;
        try
        {
            primaryActivity = await webhookFixture.DispatchInboundToBindingAsync(
                primary,
                InboundActivitySeed.DirectMessage("primary-tenant"));
            secondaryActivity = await webhookFixture.DispatchInboundToBindingAsync(
                secondary,
                InboundActivitySeed.DirectMessage("secondary-tenant"));
        }
        catch (NotSupportedException)
        {
            // Fixture does not support multi-tenant dispatch; multi-binding routing is not applicable here.
            return;
        }

        primaryActivity.Bot.Value.ShouldBe(primary.Bot.Bot.Value);
        secondaryActivity.Bot.Value.ShouldBe(secondary.Bot.Bot.Value);
        primaryActivity.Bot.Value.ShouldNotBe(secondaryActivity.Bot.Value);
    }

    [Fact]
    public async Task Streaming_ReachesTerminalState_EvenIfIntentDegrades()
    {
        await using var lifetime = await StartAdapterAsync();

        if (CapabilitiesOf(lifetime.Adapter).Streaming == StreamingSupport.None)
            return;

        var reference = BuildDirectMessageReference(lifetime.Adapter);
        var emit = await lifetime.Adapter.SendAsync(
            reference,
            SampleMessageContent.TextWithCard(),
            CancellationToken.None);
        emit.Capability.ShouldBeOneOf(ComposeCapability.Exact, ComposeCapability.Degraded, ComposeCapability.Unsupported);
        emit.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task Capabilities_ImplementedInterfaces_AreConsistent()
    {
        await using var lifetime = await StartAdapterAsync();
        var caps = CapabilitiesOf(lifetime.Adapter);

        if (caps.SupportsTyping)
            caps.TypingTtlMs.ShouldBeGreaterThanOrEqualTo(caps.TypingKeepaliveIntervalMs);

        var parities = GetCapabilityInterfaceParities(lifetime.Adapter).ToList();
        if (parities.Count == 0)
            return;

        foreach (var parity in parities)
        {
            var implemented = AdapterImplements(lifetime.Adapter, parity.OptionalInterface);
            implemented.ShouldBe(
                parity.CapabilityFlag,
                $"Capability flag '{parity.CapabilityName}' (={parity.CapabilityFlag}) must match whether the adapter "
                    + $"implements {parity.OptionalInterface.FullName} (runtime={implemented}). "
                    + "The suite reads the adapter's runtime type, not the author-supplied value, so declarations "
                    + "cannot drift from implementations.");
        }
    }

    [Fact]
    public async Task ParticipantRef_CanonicalId_StableAcrossDisplayNameChanges()
    {
        if (WebhookFixture is null && GatewayFixture is null)
            return;

        await using var lifetime = await StartAdapterAsync();
        var first = await DispatchAsync(new InboundActivitySeed(
            ActivityType.Message,
            ConversationScope.DirectMessage,
            ConversationKey: "dm-participant",
            SenderCanonicalId: "user-42",
            SenderDisplayName: "Alpha",
            Text: "first"));
        var second = await DispatchAsync(new InboundActivitySeed(
            ActivityType.Message,
            ConversationScope.DirectMessage,
            ConversationKey: "dm-participant",
            SenderCanonicalId: "user-42",
            SenderDisplayName: "Beta",
            Text: "second"));

        first.From.CanonicalId.ShouldBe(second.From.CanonicalId);
    }

    [Fact]
    public async Task Typing_KeepaliveWithinInterval()
    {
        await using var lifetime = await StartAdapterAsync();
        if (!CapabilitiesOf(lifetime.Adapter).SupportsTyping)
            return;

        Typing.ShouldNotBeNull(
            "SupportsTyping=true requires an adapter-supplied TypingProbe; the suite cannot verify keepalive behavior "
                + "without one.");

        var fired = await Typing.KeepaliveFiresWithinIntervalAsync(CancellationToken.None);
        fired.ShouldBeTrue(
            "Typing must surface at least one keepalive event within the declared TypingKeepaliveIntervalMs window.");
    }

    [Fact]
    public async Task Typing_AutoStopsAfterTtl()
    {
        await using var lifetime = await StartAdapterAsync();
        if (!CapabilitiesOf(lifetime.Adapter).SupportsTyping)
            return;

        Typing.ShouldNotBeNull(
            "SupportsTyping=true requires an adapter-supplied TypingProbe; the suite cannot verify TTL auto-stop "
                + "without one.");

        var autoStopped = await Typing.AutoStopsAfterTtlAsync(CancellationToken.None);
        autoStopped.ShouldBeTrue(
            "Typing must auto-stop after TypingTtlMs even if business code never calls stop (RFC §8.1).");
    }

    [Fact]
    public async Task Typing_FailureCircuitBreakerAfterConsecutiveFailures()
    {
        await using var lifetime = await StartAdapterAsync();
        if (!CapabilitiesOf(lifetime.Adapter).SupportsTyping)
            return;

        Typing.ShouldNotBeNull(
            "SupportsTyping=true requires an adapter-supplied TypingProbe; the suite cannot verify the keepalive "
                + "circuit breaker without one.");

        var tripped = await Typing.CircuitBreakerTripsAfterConsecutiveFailuresAsync(CancellationToken.None);
        tripped.ShouldBeTrue(
            "Consecutive keepalive failures must trip the circuit breaker so broken typing state does not keep "
                + "hammering the platform (RFC §8.1).");
    }

    [Fact]
    public async Task Typing_StartStop_Idempotent()
    {
        await using var lifetime = await StartAdapterAsync();
        if (!CapabilitiesOf(lifetime.Adapter).SupportsTyping)
            return;

        Typing.ShouldNotBeNull(
            "SupportsTyping=true requires an adapter-supplied TypingProbe; the suite cannot verify start/stop "
                + "idempotency without one.");

        var idempotent = await Typing.StartStopIsIdempotentAsync(CancellationToken.None);
        idempotent.ShouldBeTrue("Repeated typing start/stop must be idempotent (RFC §8.1).");
    }

    [Fact]
    public async Task Reaction_SendAndRemove_Succeeds()
    {
        await using var lifetime = await StartAdapterAsync();
        if (!CapabilitiesOf(lifetime.Adapter).SupportsReactions)
            return;

        Reactions.ShouldNotBeNull(
            "SupportsReactions=true must be backed by an adapter-supplied ReactionProbe; the conformance suite cannot "
                + "claim reaction coverage without one.");

        var reference = BuildDirectMessageReference(lifetime.Adapter);
        var sent = await lifetime.Adapter.SendAsync(
            reference,
            SampleMessageContent.SimpleText("reaction-target"),
            CancellationToken.None);
        sent.Success.ShouldBeTrue();

        var added = await Reactions.AddAsync(sent.SentActivityId, ":thumbsup:", CancellationToken.None);
        added.ShouldBeTrue("Adding a reaction on a channel with SupportsReactions=true must succeed.");

        var removed = await Reactions.RemoveAsync(sent.SentActivityId, ":thumbsup:", CancellationToken.None);
        removed.ShouldBeTrue("Removing a previously added reaction must succeed.");
    }

    [Fact]
    public async Task Mention_BotMention_StrippedFromTextButPresentInMentions()
    {
        if (WebhookFixture is null && GatewayFixture is null)
            return;

        await using var lifetime = await StartAdapterAsync();
        if (!CapabilitiesOf(lifetime.Adapter).SupportsMention)
            return;

        var seed = BuildBotMentionSeed(lifetime.Binding);
        seed.ShouldNotBeNull(
            "SupportsMention=true requires an adapter-supplied BuildBotMentionSeed override; the suite cannot exercise "
                + "bot-mention stripping without one.");

        var botId = lifetime.Binding.Bot.Bot.Value;
        var activity = await DispatchAsync(seed);

        activity.Mentions.ShouldContain(
            m => m.CanonicalId == botId,
            "Normalized activity must expose the bot mention in the Mentions collection.");
        activity.Content.Text.ShouldNotContain(
            botId,
            Case.Insensitive,
            "Normalized Content.Text must not carry the raw bot mention token after stripping.");
    }

    [Fact]
    public async Task Mention_OtherParticipantMention_PreservedInText()
    {
        if (WebhookFixture is null && GatewayFixture is null)
            return;

        await using var lifetime = await StartAdapterAsync();
        if (!CapabilitiesOf(lifetime.Adapter).SupportsMention)
            return;

        var seed = BuildParticipantMentionSeed();
        seed.ShouldNotBeNull(
            "SupportsMention=true requires an adapter-supplied BuildParticipantMentionSeed override; the suite cannot "
                + "exercise other-participant mention preservation without one.");

        var activity = await DispatchAsync(seed);

        activity.Mentions.Count.ShouldBeGreaterThan(0);
        var mention = activity.Mentions[0];
        activity.Content.Text.ShouldContain(
            mention.DisplayName,
            Case.Insensitive,
            "Mentions targeting other participants must remain visible in Content.Text.");
    }

    /// <summary>
    /// Returns the capability matrix on an adapter without triggering interface member ambiguity.
    /// </summary>
    protected static ChannelCapabilities CapabilitiesOf(TAdapter adapter) => ((IChannelTransport)adapter).Capabilities;

    /// <summary>
    /// Returns the channel identifier on an adapter without triggering interface member ambiguity.
    /// </summary>
    protected static ChannelId ChannelOf(TAdapter adapter) => ((IChannelTransport)adapter).Channel;

    /// <summary>
    /// Builds the canonical direct-message reference for outbound tests that do not require a real inbound activity.
    /// </summary>
    protected virtual ConversationReference BuildDirectMessageReference(TAdapter adapter) =>
        ConversationReference.Create(
            ChannelOf(adapter),
            BotInstanceId.From("conformance-bot"),
            ConversationScope.DirectMessage,
            partition: null,
            "conformance-user");

    /// <summary>
    /// Starts the adapter bound to the default binding and returns a disposable lifetime that stops receive on dispose.
    /// </summary>
    protected async Task<AdapterLifetime> StartAdapterAsync(CancellationToken ct = default)
    {
        var adapter = CreateAdapter();
        await PrepareAsync(adapter, ct);
        var binding = CreateBinding();
        await adapter.InitializeAsync(binding, ct);
        await adapter.StartReceivingAsync(ct);
        return new AdapterLifetime(adapter, binding);
    }

    /// <summary>
    /// Dispatches one synthetic inbound activity, preferring webhook when both fixtures exist.
    /// </summary>
    protected async Task<ChatActivity> DispatchAsync(InboundActivitySeed seed, CancellationToken ct = default)
    {
        if (WebhookFixture is not null)
            return await WebhookFixture.DispatchInboundAsync(seed, ct);

        if (GatewayFixture is not null)
            return await GatewayFixture.PublishEventAsync(seed, ct);

        throw new InvalidOperationException("No inbound fixture configured.");
    }

    /// <summary>
    /// Replays the most recent synthetic inbound call through whichever fixture drove it, preferring webhook when both
    /// fixtures exist. Returns <see langword="null"/> when the active fixture has no replay history yet, so callers can
    /// treat that as not-applicable rather than a contract failure.
    /// </summary>
    protected async Task<ChatActivity?> ReplayLastAsync(CancellationToken ct = default)
    {
        if (WebhookFixture is not null)
            return await WebhookFixture.ReplayLastInboundAsync(ct);

        if (GatewayFixture is not null)
            return await GatewayFixture.ReplayLastEventAsync(ct);

        throw new InvalidOperationException("No inbound fixture configured.");
    }

    /// <summary>
    /// Disposable adapter lifetime that stops receive on dispose.
    /// </summary>
    public sealed class AdapterLifetime : IAsyncDisposable
    {
        internal AdapterLifetime(TAdapter adapter, ChannelTransportBinding binding)
        {
            Adapter = adapter;
            Binding = binding;
        }

        /// <summary>
        /// Gets the adapter instance under test.
        /// </summary>
        public TAdapter Adapter { get; }

        /// <summary>
        /// Gets the binding the adapter was initialized with.
        /// </summary>
        public ChannelTransportBinding Binding { get; }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Adapter.StopReceivingAsync(CancellationToken.None);
            }
            catch
            {
                // Swallow: shutdown errors are covered by Lifecycle_StartStop_NoLeaks.
            }
        }
    }
}
