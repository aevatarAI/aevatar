using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConversationReplyGeneratorTests
{
    [Fact]
    public async Task GenerateReplyAsync_UsesConfiguredRelayCallbackUrlInSystemPrompt()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            relayOptions: new global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                WebhookBaseUrl = "https://dev.aevatar.local/",
            });

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-1",
                Conversation = new ConversationReference
                {
                    CanonicalKey = "lark:dm:user-1",
                },
                Content = new MessageContent
                {
                    Text = "hello",
                },
            },
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        reply.Should().Be("ok");
        providerFactory.Requests.Should().ContainSingle();
        var systemPrompt = providerFactory.Requests[0].Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("https://dev.aevatar.local/api/webhooks/nyxid-relay");
        systemPrompt.Should().NotContain("https://aevatar-console-backend-api.aevatar.ai/api/webhooks/nyxid-relay");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithStreamingSinkAndPlaceholderConfigured_EmitsPlaceholderBeforeFirstDelta()
    {
        // Regression for PR#374 P2 review: the first visible Lark message must fire at the
        // outbound RTT, not at first LLM delta. Without a pre-delta placeholder, a cold-start
        // or tool-call-before-first-token makes the ≤1s target impossible to meet.
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            relayOptions: new global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingPlaceholderText = "…",
            });
        var sink = new RecordingStreamingSink();

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-placeholder",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>(),
            sink,
            CancellationToken.None);

        reply.Should().Be("ok");
        // First emit must be the placeholder, before any LLM delta.
        sink.Emissions.Should().NotBeEmpty();
        sink.Emissions[0].Should().Be("…");
        sink.Emissions.Should().Contain("ok");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithStreamingSinkButEmptyPlaceholderOption_SkipsPlaceholderEmit()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            relayOptions: new global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingPlaceholderText = string.Empty,
            });
        var sink = new RecordingStreamingSink();

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-no-placeholder",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>(),
            sink,
            CancellationToken.None);

        sink.Emissions.Should().ContainSingle().And.Contain("ok");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithoutStreamingSink_SkipsPlaceholderEmit()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            relayOptions: new global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingPlaceholderText = "…",
            });

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-no-sink",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        reply.Should().Be("ok");
    }

    [Fact]
    public async Task GenerateReplyAsync_ForLarkTurn_AutoLoadsRemoteSkillsIntoTurnPrompt()
    {
        var providerFactory = new RecordingProviderFactory();
        var discovery = new StubRemoteSkillDiscovery
        {
            Results =
            {
                new RemoteSkillSummary(
                    Name: "translate-pro",
                    Description: "Translate with glossary awareness",
                    RemoteId: "skill-1"),
            },
        };
        var fetcher = new StubRemoteSkillFetcher
        {
            ById =
            {
                ["skill-1"] = new SkillDefinition
                {
                    Name = "translate-pro",
                    Description = "Translate with glossary awareness",
                    Instructions = "Use glossary first.",
                    Source = SkillSource.Remote,
                    RemoteId = "skill-1",
                },
            },
        };
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            remoteSkillDiscoveries: [discovery],
            remoteSkillFetcher: fetcher,
            chatOptions: new NyxIdChatOptions
            {
                LarkRemoteSkillAutoLoadMaxSkills = 1,
            });

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-auto-skill",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "Translate this launch note into Chinese" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "nyx-token",
            },
            streamingSink: null,
            CancellationToken.None);

        discovery.Requests.Should().ContainSingle();
        discovery.Requests[0].AccessToken.Should().Be("nyx-token");
        discovery.Requests[0].Query.Should().Be("Translate this launch note into Chinese");
        discovery.Requests[0].Mode.Should().Be("semantic");
        fetcher.Requests.Should().ContainSingle().Which.Should().Be(("nyx-token", "skill-1"));

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().NotBeNull();
        request.Tools!.Should().Contain(tool => tool.Name == "use_skill");
        var systemPrompt = request.Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("translate-pro");
        systemPrompt.Should().Contain("Translate with glossary awareness");
        systemPrompt.Should().Contain("Use glossary first.");
        systemPrompt.Should().Contain("do not claim success until the required tool or service action has actually completed");
    }

    [Fact]
    public async Task GenerateReplyAsync_ForNetworkInventoryLarkTurn_UsesExpandedRemoteSkillSearch()
    {
        var providerFactory = new RecordingProviderFactory();
        var discovery = new StubRemoteSkillDiscovery
        {
            OnSearch = request => request.Query.Contains("network device ip address", StringComparison.OrdinalIgnoreCase)
                ? [new RemoteSkillSummary("network-inventory", "Collect network device IP addresses", "skill-network")]
                : [],
        };
        var fetcher = new StubRemoteSkillFetcher
        {
            ById =
            {
                ["skill-network"] = new SkillDefinition
                {
                    Name = "network-inventory",
                    Description = "Collect network device IP addresses",
                    Instructions = "Use the NyxID SSH-capable node to scan the office network before reporting device IPs.",
                    Source = SkillSource.Remote,
                    RemoteId = "skill-network",
                },
            },
        };
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            remoteSkillDiscoveries: [discovery],
            remoteSkillFetcher: fetcher,
            chatOptions: new NyxIdChatOptions
            {
                LarkRemoteSkillAutoLoadMaxSkills = 1,
            });

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-network-auto-skill",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "从SG Office Network拉一下所有设备的IP" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "nyx-token",
            },
            streamingSink: null,
            CancellationToken.None);

        discovery.Requests.Should().Contain(request =>
            request.Query == "从SG Office Network拉一下所有设备的IP" &&
            request.Mode == "semantic");
        discovery.Requests.Should().Contain(request =>
            request.Query.Contains("network device ip address", StringComparison.OrdinalIgnoreCase));
        fetcher.Requests.Should().ContainSingle().Which.Should().Be(("nyx-token", "skill-network"));

        var systemPrompt = providerFactory.Requests.Should().ContainSingle().Subject.Messages
            .First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("network-inventory");
        systemPrompt.Should().Contain("Use the NyxID SSH-capable node to scan the office network before reporting device IPs.");
    }

    [Fact]
    public async Task GenerateReplyAsync_ForNonLarkTurn_DoesNotAutoLoadRemoteSkills()
    {
        var providerFactory = new RecordingProviderFactory();
        var discovery = new StubRemoteSkillDiscovery
        {
            Results =
            {
                new RemoteSkillSummary("translate-pro", "Translate with glossary awareness", "skill-1"),
            },
        };
        var fetcher = new StubRemoteSkillFetcher();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            remoteSkillDiscoveries: [discovery],
            remoteSkillFetcher: fetcher);

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-non-lark-auto-skill",
                Conversation = new ConversationReference { CanonicalKey = "telegram:dm:user-1" },
                Content = new MessageContent { Text = "Translate this launch note into Chinese" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "telegram",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "nyx-token",
            },
            streamingSink: null,
            CancellationToken.None);

        discovery.Requests.Should().BeEmpty();
        fetcher.Requests.Should().BeEmpty();
        var systemPrompt = providerFactory.Requests.Should().ContainSingle().Subject.Messages
            .First(message => message.Role == "system").Content;
        systemPrompt.Should().NotContain("translate-pro");
    }

    [Fact]
    public async Task GenerateReplyAsync_AppliesSenderPrefsOverChainOwnerDefault()
    {
        // Issue #513 phase 3: when the inbound carries a sender binding-id,
        // sender prefs override the upstream-pinned bot-owner prefs field-
        // by-field. The owner's metadata is already in the input (channel
        // turn runner pins it via OwnerLlmConfigApplier in production), so
        // the generator only has to layer sender overrides where the sender
        // actually set a value.
        var providerFactory = new RecordingProviderFactory();
        var prefsStore = new ScopedStubPreferencesStore
        {
            // Sender (binding-id) has chosen a model but left route blank.
            ByBinding =
            {
                ["bnd_sender"] = new NyxIdUserLlmPreferences("sender-model", string.Empty, MaxToolRounds: 0),
            },
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, preferencesStore: prefsStore);

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-1",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>
            {
                // Owner prefs pre-pinned upstream (mirrors what
                // OwnerLlmConfigApplier writes from the registration scope).
                [LLMRequestMetadataKeys.ModelOverride] = "owner-model",
                [LLMRequestMetadataKeys.NyxIdRoutePreference] = "/api/v1/proxy/s/owner",
                [LLMRequestMetadataKeys.MaxToolRoundsOverride] = "9",
                [LLMRequestMetadataKeys.SenderBindingId] = "bnd_sender",
            },
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Metadata.Should().NotBeNull();
        var metadata = request.Metadata!;
        // Sender's model wins (non-empty).
        metadata[LLMRequestMetadataKeys.ModelOverride].Should().Be("sender-model");
        // Sender left route blank → owner's upstream-pinned route stays.
        metadata[LLMRequestMetadataKeys.NyxIdRoutePreference].Should().Be("/api/v1/proxy/s/owner");
        // Sender left max-rounds at 0 → owner's upstream-pinned value stays.
        metadata[LLMRequestMetadataKeys.MaxToolRoundsOverride].Should().Be("9");
    }

    [Fact]
    public async Task GenerateReplyAsync_LeavesOwnerPrefsIntactWhenNoSenderBinding()
    {
        // No SenderBindingId in metadata → generator does not touch the
        // upstream-pinned owner prefs. Pins the no-op behaviour so legacy
        // unbound deployments behave identically to before issue #513.
        var providerFactory = new RecordingProviderFactory();
        var prefsStore = new ScopedStubPreferencesStore();
        var generator = new NyxIdConversationReplyGenerator(providerFactory, preferencesStore: prefsStore);

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-2",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.ModelOverride] = "owner-only-model",
                [LLMRequestMetadataKeys.NyxIdRoutePreference] = "owner-route",
                [LLMRequestMetadataKeys.MaxToolRoundsOverride] = "4",
            },
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Metadata.Should().NotBeNull();
        var metadata = request.Metadata!;
        metadata[LLMRequestMetadataKeys.ModelOverride].Should().Be("owner-only-model");
        metadata[LLMRequestMetadataKeys.NyxIdRoutePreference].Should().Be("owner-route");
        metadata[LLMRequestMetadataKeys.MaxToolRoundsOverride].Should().Be("4");
        // Generator must not have touched the prefs store when no binding-id is present.
        prefsStore.Lookups.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateReplyAsync_FallsBackToOwnerPrefsWhenSenderStoreThrows()
    {
        // Pin graceful-degradation: a transient sender-config projection
        // outage must not corrupt the LLM request — the upstream-pinned
        // owner prefs survive (PR #521 review glm-5.1).
        var providerFactory = new RecordingProviderFactory();
        var prefsStore = new ScopedStubPreferencesStore { ThrowOnLookup = true };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, preferencesStore: prefsStore);

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-3",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.ModelOverride] = "owner-fallback-model",
                [LLMRequestMetadataKeys.NyxIdRoutePreference] = "owner-route",
                [LLMRequestMetadataKeys.MaxToolRoundsOverride] = "5",
                [LLMRequestMetadataKeys.SenderBindingId] = "bnd_sender",
            },
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Metadata.Should().NotBeNull();
        var metadata = request.Metadata!;
        metadata[LLMRequestMetadataKeys.ModelOverride].Should().Be("owner-fallback-model");
        metadata[LLMRequestMetadataKeys.NyxIdRoutePreference].Should().Be("owner-route");
        metadata[LLMRequestMetadataKeys.MaxToolRoundsOverride].Should().Be("5");
    }

    [Fact]
    public async Task GenerateReplyAsync_RetriesWithOwnerPrefsWhenSenderRouteFails()
    {
        var providerFactory = new RecordingProviderFactory
        {
            FailuresBeforeSuccess = 1,
        };
        var prefsStore = new ScopedStubPreferencesStore
        {
            ByBinding =
            {
                ["bnd_sender"] = new NyxIdUserLlmPreferences(
                    "sender-model",
                    "/api/v1/proxy/s/sender",
                    MaxToolRounds: 7),
            },
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, preferencesStore: prefsStore);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-sender-route-failure",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.ModelOverride] = "owner-model",
                [LLMRequestMetadataKeys.NyxIdRoutePreference] = "/api/v1/proxy/s/owner",
                [LLMRequestMetadataKeys.MaxToolRoundsOverride] = "5",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "owner-token",
                [LLMRequestMetadataKeys.NyxIdOrgToken] = "owner-token",
                [LLMRequestMetadataKeys.SenderBindingId] = "bnd_sender",
                [LLMRequestMetadataKeys.SenderNyxIdAccessToken] = "sender-token",
            },
            streamingSink: null,
            CancellationToken.None);

        reply.Should().Be("ok");
        providerFactory.Requests.Should().HaveCount(2);
        var senderMetadata = providerFactory.Requests[0].Metadata!;
        senderMetadata[LLMRequestMetadataKeys.ModelOverride].Should().Be("sender-model");
        senderMetadata[LLMRequestMetadataKeys.NyxIdRoutePreference].Should().Be("/api/v1/proxy/s/sender");
        senderMetadata[LLMRequestMetadataKeys.MaxToolRoundsOverride].Should().Be("7");
        senderMetadata[LLMRequestMetadataKeys.NyxIdAccessToken].Should().Be("sender-token");
        senderMetadata[LLMRequestMetadataKeys.NyxIdOrgToken].Should().Be("sender-token");
        senderMetadata.Should().NotContainKey(LLMRequestMetadataKeys.SenderNyxIdAccessToken);

        var ownerMetadata = providerFactory.Requests[1].Metadata!;
        ownerMetadata[LLMRequestMetadataKeys.ModelOverride].Should().Be("owner-model");
        ownerMetadata[LLMRequestMetadataKeys.NyxIdRoutePreference].Should().Be("/api/v1/proxy/s/owner");
        ownerMetadata[LLMRequestMetadataKeys.MaxToolRoundsOverride].Should().Be("5");
        ownerMetadata[LLMRequestMetadataKeys.NyxIdAccessToken].Should().Be("owner-token");
        ownerMetadata[LLMRequestMetadataKeys.NyxIdOrgToken].Should().Be("owner-token");
        ownerMetadata.Should().NotContainKey(LLMRequestMetadataKeys.SenderBindingId);
        ownerMetadata.Should().NotContainKey(LLMRequestMetadataKeys.SenderNyxIdAccessToken);
    }

    [Fact]
    public async Task GenerateReplyAsync_UsesOwnerPrefsImmediatelyWhenSenderRouteHasNoToken()
    {
        var providerFactory = new RecordingProviderFactory();
        var prefsStore = new ScopedStubPreferencesStore
        {
            ByBinding =
            {
                ["bnd_sender"] = new NyxIdUserLlmPreferences(
                    "sender-model",
                    "/api/v1/proxy/s/sender",
                    MaxToolRounds: 7),
            },
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, preferencesStore: prefsStore);

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-no-sender-token",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.ModelOverride] = "owner-model",
                [LLMRequestMetadataKeys.NyxIdRoutePreference] = "/api/v1/proxy/s/owner",
                [LLMRequestMetadataKeys.MaxToolRoundsOverride] = "5",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "owner-token",
                [LLMRequestMetadataKeys.NyxIdOrgToken] = "owner-token",
                [LLMRequestMetadataKeys.SenderBindingId] = "bnd_sender",
            },
            streamingSink: null,
            CancellationToken.None);

        var ownerMetadata = providerFactory.Requests.Should().ContainSingle().Subject.Metadata!;
        ownerMetadata[LLMRequestMetadataKeys.ModelOverride].Should().Be("owner-model");
        ownerMetadata[LLMRequestMetadataKeys.NyxIdRoutePreference].Should().Be("/api/v1/proxy/s/owner");
        ownerMetadata[LLMRequestMetadataKeys.MaxToolRoundsOverride].Should().Be("5");
        ownerMetadata[LLMRequestMetadataKeys.NyxIdAccessToken].Should().Be("owner-token");
        ownerMetadata[LLMRequestMetadataKeys.NyxIdOrgToken].Should().Be("owner-token");
        ownerMetadata.Should().NotContainKey(LLMRequestMetadataKeys.SenderBindingId);
        ownerMetadata.Should().NotContainKey(LLMRequestMetadataKeys.SenderNyxIdAccessToken);
    }

    // ─── Issue #513 phase 3 — explicit 3 binding × 3 owner-prefs override matrix ───
    //
    // The four [Fact] tests above pin specific scenarios (owner-only,
    // sender-overrides-model, sender-store-throws, route-failure-retry). This
    // [Theory] adds the explicit 3×3 matrix the issue calls out: the binding
    // axis (unbound / bound-with-empty-prefs / bound-with-model-only) is
    // crossed with the owner-prefs axis (none / partial=model-only / full).
    // Sender prefs in the bound-set row deliberately set ONLY DefaultModel so
    // we exercise the "sender supplies a subset, owner fills the rest" path
    // without crossing the route-applied + no-sender-token branch (which
    // silently swaps in the owner snapshot — orthogonal to the matrix and
    // already covered by UsesOwnerPrefsImmediatelyWhenSenderRouteHasNoToken).
    public const string MatrixUnbound = "unbound";
    public const string MatrixBoundEmpty = "bound_empty_prefs";
    public const string MatrixBoundModelOnly = "bound_model_only";
    public const string MatrixOwnerNone = "owner_none";
    public const string MatrixOwnerPartial = "owner_partial_model_only";
    public const string MatrixOwnerFull = "owner_full";

    [Theory]
    [InlineData(MatrixUnbound, MatrixOwnerNone, null, null, null)]
    [InlineData(MatrixUnbound, MatrixOwnerPartial, "owner-model", null, null)]
    [InlineData(MatrixUnbound, MatrixOwnerFull, "owner-model", "/api/v1/proxy/s/owner", "9")]
    [InlineData(MatrixBoundEmpty, MatrixOwnerNone, null, null, null)]
    [InlineData(MatrixBoundEmpty, MatrixOwnerPartial, "owner-model", null, null)]
    [InlineData(MatrixBoundEmpty, MatrixOwnerFull, "owner-model", "/api/v1/proxy/s/owner", "9")]
    [InlineData(MatrixBoundModelOnly, MatrixOwnerNone, "sender-model", null, null)]
    [InlineData(MatrixBoundModelOnly, MatrixOwnerPartial, "sender-model", null, null)]
    [InlineData(MatrixBoundModelOnly, MatrixOwnerFull, "sender-model", "/api/v1/proxy/s/owner", "9")]
    public async Task GenerateReplyAsync_OverrideMatrix_BindingTimesOwnerPrefs(
        string bindingState,
        string ownerState,
        string? expectedModel,
        string? expectedRoute,
        string? expectedRounds)
    {
        var providerFactory = new RecordingProviderFactory();
        var prefsStore = new ScopedStubPreferencesStore();

        switch (bindingState)
        {
            case MatrixBoundEmpty:
                // Lookup returns the default empty record (no entry in
                // ByBinding), so SetIfFilled writes nothing.
                break;
            case MatrixBoundModelOnly:
                prefsStore.ByBinding["bnd_sender"] = new NyxIdUserLlmPreferences(
                    DefaultModel: "sender-model",
                    PreferredRoute: string.Empty,
                    MaxToolRounds: 0);
                break;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (bindingState != MatrixUnbound)
            metadata[LLMRequestMetadataKeys.SenderBindingId] = "bnd_sender";

        switch (ownerState)
        {
            case MatrixOwnerPartial:
                metadata[LLMRequestMetadataKeys.ModelOverride] = "owner-model";
                break;
            case MatrixOwnerFull:
                metadata[LLMRequestMetadataKeys.ModelOverride] = "owner-model";
                metadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = "/api/v1/proxy/s/owner";
                metadata[LLMRequestMetadataKeys.MaxToolRoundsOverride] = "9";
                break;
        }

        var generator = new NyxIdConversationReplyGenerator(providerFactory, preferencesStore: prefsStore);
        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = $"msg-{bindingState}-{ownerState}",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            metadata,
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        var effective = request.Metadata!;

        AssertKey(effective, LLMRequestMetadataKeys.ModelOverride, expectedModel);
        AssertKey(effective, LLMRequestMetadataKeys.NyxIdRoutePreference, expectedRoute);
        AssertKey(effective, LLMRequestMetadataKeys.MaxToolRoundsOverride, expectedRounds);

        if (bindingState == MatrixUnbound)
            prefsStore.Lookups.Should().BeEmpty(
                "no binding-id in metadata → generator must not consult the prefs store");
        else
            prefsStore.Lookups.Should().ContainSingle().Which.Should().Be("bnd_sender");
    }

    private static void AssertKey(IReadOnlyDictionary<string, string> metadata, string key, string? expected)
    {
        if (expected is null)
            metadata.Should().NotContainKey(key);
        else
            metadata.Should().ContainKey(key).WhoseValue.Should().Be(expected);
    }

    private sealed class ScopedStubPreferencesStore : INyxIdUserLlmPreferencesStore
    {
        public Dictionary<string, NyxIdUserLlmPreferences> ByBinding { get; } = new(StringComparer.Ordinal);
        public List<string?> Lookups { get; } = new();
        public bool ThrowOnLookup { get; set; }

        public Task<NyxIdUserLlmPreferences> GetOwnerAsync(CancellationToken cancellationToken = default)
        {
            Lookups.Add(null);
            if (ThrowOnLookup)
                throw new InvalidOperationException("simulated projection outage");
            return Task.FromResult(new NyxIdUserLlmPreferences(string.Empty, string.Empty));
        }

        public Task<NyxIdUserLlmPreferences> GetForBindingAsync(string bindingId, CancellationToken cancellationToken = default)
        {
            Lookups.Add(bindingId);
            if (ThrowOnLookup)
                throw new InvalidOperationException("simulated projection outage");
            return Task.FromResult(ByBinding.TryGetValue(bindingId, out var prefs)
                ? prefs
                : new NyxIdUserLlmPreferences(string.Empty, string.Empty));
        }
    }

    private sealed class RecordingStreamingSink : IStreamingReplySink
    {
        public List<string> Emissions { get; } = [];

        public Task OnDeltaAsync(string accumulatedText, CancellationToken ct)
        {
            Emissions.Add(accumulatedText);
            return Task.CompletedTask;
        }
    }

    private sealed class StubRemoteSkillDiscovery : IRemoteSkillDiscovery
    {
        public List<RemoteSkillSearchRequest> Requests { get; } = [];
        public List<RemoteSkillSummary> Results { get; } = [];
        public Func<RemoteSkillSearchRequest, IReadOnlyList<RemoteSkillSummary>>? OnSearch { get; init; }

        public Task<IReadOnlyList<RemoteSkillSummary>> SearchSkillsAsync(
            RemoteSkillSearchRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(OnSearch?.Invoke(request) ?? Results.ToArray());
        }
    }

    private sealed class StubRemoteSkillFetcher : IRemoteSkillFetcher
    {
        public Dictionary<string, SkillDefinition> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Token, string NameOrId)> Requests { get; } = [];

        public Task<SkillDefinition?> FetchSkillAsync(
            string accessToken,
            string nameOrId,
            CancellationToken ct = default)
        {
            Requests.Add((accessToken, nameOrId));
            return Task.FromResult(ById.GetValueOrDefault(nameOrId));
        }
    }

    private sealed class RecordingProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "recording";

        public List<LLMRequest> Requests { get; } = [];

        public int FailuresBeforeSuccess { get; init; }

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default) =>
            Task.FromResult(new LLMResponse
            {
                Content = "non-streaming path should not be used",
            });

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Requests.Count <= FailuresBeforeSuccess)
                throw new InvalidOperationException("simulated sender route failure");

            yield return new LLMStreamChunk
            {
                DeltaContent = "ok",
            };
            await Task.CompletedTask;
            yield return new LLMStreamChunk
            {
                IsLast = true,
            };
        }
    }
}
