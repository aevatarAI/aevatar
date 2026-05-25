using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConversationReplyGeneratorTests
{
    private static LLMControlContext Control(
        string? model = null,
        string? route = null,
        int? rounds = null,
        string? token = null,
        string? senderToken = null) =>
        new(
            NyxIdAccessToken: token,
            NyxIdOrgToken: token,
            SenderNyxIdAccessToken: senderToken,
            ModelOverride: model,
            NyxIdRoutePreference: route,
            MaxToolRoundsOverride: rounds,
            UserMemoryPrompt: null);

    private static AgentToolExecutionContext? ToolContext(string? senderBindingId) =>
        string.IsNullOrWhiteSpace(senderBindingId)
            ? null
            : AgentToolExecutionContext.Empty with
            {
                SenderBinding = new AgentToolSenderBindingContext(senderBindingId),
            };

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

        reply.Text.Should().Be("ok");
        providerFactory.Requests.Should().ContainSingle();
        var systemPrompt = providerFactory.Requests[0].Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("https://dev.aevatar.local/api/webhooks/nyxid-relay");
        systemPrompt.Should().NotContain("https://aevatar-console-backend-api.aevatar.ai/api/webhooks/nyxid-relay");
        systemPrompt.Should().Contain("chrono-ai-daily");
        systemPrompt.Should().Contain("When you are following a loaded skill and you hit a missing capability");
        systemPrompt.Should().Contain("ornn_search_skills");
    }

    [Fact]
    public async Task GenerateReplyAsync_AggregatesUsageAndFinishReasonAtActorEdge()
    {
        // ADR-0021 §6 / canon §8: the actor-edge closeout returned by GenerateReplyAsync
        // MUST surface aggregated Usage and FinishReason from the underlying provider
        // stream, regardless of whether those values arrived on a mid-stream Usage chunk
        // or on the IsLast marker. Round-internal terminal markers must not leak past
        // ConversationReplyGenerator.
        var providerFactory = new UsageReportingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            relayOptions: new global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                WebhookBaseUrl = "https://dev.aevatar.local/",
            });

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-closeout",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("answer");
        reply.Usage.Should().NotBeNull();
        reply.Usage!.PromptTokens.Should().Be(7);
        reply.Usage.CompletionTokens.Should().Be(11);
        reply.Usage.TotalTokens.Should().Be(18);
        reply.FinishReason.Should().Be("stop");
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

        reply.Text.Should().Be("ok");
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

        reply.Text.Should().Be("ok");
    }

    [Fact]
    public async Task GenerateReplyAsync_CreatesApprovalMiddlewarePerTurn()
    {
        var approvalHandler = new CountingApprovalHandler();
        var generator = new NyxIdConversationReplyGenerator(
            new ToolCallingProviderFactory(),
            toolSources: [new SingleToolSource(new ApprovalRequiredTool())],
            approvalHandler: approvalHandler);

        for (var i = 0; i < 4; i++)
        {
            var reply = await generator.GenerateReplyAsync(
                new ChatActivity
                {
                    Id = $"msg-approval-{i}",
                    Conversation = new ConversationReference { CanonicalKey = $"lark:dm:user-{i}" },
                    Content = new MessageContent { Text = "run tool" },
                },
                new Dictionary<string, string>(),
                streamingSink: null,
                CancellationToken.None);

            reply.Text.Should().Be("done");
        }

        approvalHandler.RequestCount.Should().Be(4);
    }

    [Fact]
    public async Task GenerateReplyAsync_WithLocalSkillCatalog_AddsLocalSkillsWithoutRemoteFetcherWarning()
    {
        var logger = new ListLogger<NyxIdConversationReplyGenerator>();
        var localSkillCatalog = new LocalSkillCatalog();
        localSkillCatalog.Register(new SkillDefinition
        {
            Name = "local-skill",
            Description = "Local skill",
            Instructions = "Does local work",
            Source = SkillSource.Local,
        });
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            localSkillCatalog: localSkillCatalog,
            remoteSkillFetcher: null,
            logger: logger);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-local-skill",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-local-skill" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        var systemPrompt = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("local-skill");
        logger.WarningMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateReplyAsync_WithStreamingSink_EmitsPlaceholderThenFinalTextAcrossToolFollowUp()
    {
        var providerFactory = new ToolCallingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources: [new SingleToolSource(new ApprovalRequiredTool())],
            relayOptions: new global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingPlaceholderText = "…",
            });
        var sink = new RecordingStreamingSink();

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-tool-follow-up",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-tool-follow-up" },
                Content = new MessageContent { Text = "run tool" },
            },
            new Dictionary<string, string>(),
            sink,
            CancellationToken.None);

        reply.Text.Should().Be("done");
        providerFactory.Requests.Should().HaveCount(2);
        providerFactory.Requests[1].Messages.Should().Contain(message => message.Role == "tool");
        sink.Emissions.Should().Equal("…", "done");
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
            new Dictionary<string, string>(),
            Control("owner-model", "/api/v1/proxy/s/owner", 9),
            ToolContext("bnd_sender"),
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Metadata.Should().NotBeNull();
        request.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        var metadata = request.ToolContext!.ToLegacyMetadata();
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
            new Dictionary<string, string>(),
            Control("owner-only-model", "owner-route", 4),
            toolContext: null,
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Metadata.Should().NotBeNull();
        request.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        var metadata = request.ToolContext!.ToLegacyMetadata();
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
            new Dictionary<string, string>(),
            Control("owner-fallback-model", "owner-route", 5),
            ToolContext("bnd_sender"),
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Metadata.Should().NotBeNull();
        request.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        var metadata = request.ToolContext!.ToLegacyMetadata();
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
            new Dictionary<string, string>(),
            Control("owner-model", "/api/v1/proxy/s/owner", 5, "owner-token", "sender-token"),
            ToolContext("bnd_sender"),
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        providerFactory.Requests.Should().HaveCount(2);
        var senderRequest = providerFactory.Requests[0];
        senderRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        var senderMetadata = senderRequest.ToolContext!.ToLegacyMetadata();
        senderMetadata[LLMRequestMetadataKeys.ModelOverride].Should().Be("sender-model");
        senderMetadata[LLMRequestMetadataKeys.NyxIdRoutePreference].Should().Be("/api/v1/proxy/s/sender");
        senderMetadata[LLMRequestMetadataKeys.MaxToolRoundsOverride].Should().Be("7");
        senderMetadata[LLMRequestMetadataKeys.NyxIdAccessToken].Should().Be("sender-token");
        senderMetadata[LLMRequestMetadataKeys.NyxIdOrgToken].Should().Be("sender-token");
        senderMetadata[LLMRequestMetadataKeys.SenderNyxIdAccessToken].Should().Be("sender-token");

        var ownerRequest = providerFactory.Requests[1];
        ownerRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        var ownerMetadata = ownerRequest.ToolContext!.ToLegacyMetadata();
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
            new Dictionary<string, string>(),
            Control("owner-model", "/api/v1/proxy/s/owner", 5, "owner-token"),
            ToolContext("bnd_sender"),
            streamingSink: null,
            CancellationToken.None);

        var ownerRequest = providerFactory.Requests.Should().ContainSingle().Subject;
        ownerRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        var ownerMetadata = ownerRequest.ToolContext!.ToLegacyMetadata();
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
        var toolContext = bindingState == MatrixUnbound ? null : ToolContext("bnd_sender");

        LLMControlContext? control = null;
        switch (ownerState)
        {
            case MatrixOwnerPartial:
                control = Control(model: "owner-model");
                break;
            case MatrixOwnerFull:
                control = Control("owner-model", "/api/v1/proxy/s/owner", 9);
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
            control,
            toolContext,
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        var effective = request.ToolContext!.ToLegacyMetadata();

        AssertKey(effective, LLMRequestMetadataKeys.ModelOverride, expectedModel);
        AssertKey(effective, LLMRequestMetadataKeys.NyxIdRoutePreference, expectedRoute);
        AssertKey(effective, LLMRequestMetadataKeys.MaxToolRoundsOverride, expectedRounds);

        if (bindingState == MatrixUnbound)
            prefsStore.Lookups.Should().BeEmpty(
                "no typed sender binding → generator must not consult the prefs store");
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

    // ADR-0021 §6 / canon §8 contract harness: a provider that emits Usage and
    // FinishReason in mid-stream and IsLast chunks so the test asserts the
    // actor-edge closeout aggregates them instead of letting round-internal
    // markers leak past ConversationReplyGenerator.
    private sealed class UsageReportingProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "usage-reporting";
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new LLMStreamChunk { DeltaContent = "answer" };
            // Provider emits Usage in a mid-stream "bookkeeping" chunk before IsLast.
            yield return new LLMStreamChunk
            {
                Usage = new TokenUsage(PromptTokens: 7, CompletionTokens: 11, TotalTokens: 18),
                FinishReason = "stop",
            };
            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true };
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

    private sealed class ToolCallingProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "tool-calling";

        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            if (request.Messages.Any(static message => message.Role == "tool"))
            {
                yield return new LLMStreamChunk { DeltaContent = "done" };
                yield return new LLMStreamChunk { IsLast = true };
                await Task.CompletedTask;
                yield break;
            }

            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call-approval",
                    Name = ApprovalRequiredTool.ToolName,
                    ArgumentsJson = "{}",
                },
            };
            yield return new LLMStreamChunk { IsLast = true };
            await Task.CompletedTask;
        }
    }

    private sealed class SingleToolSource(IAgentTool tool) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
    }

    private sealed class ApprovalRequiredTool : IAgentTool
    {
        public const string ToolName = "approval_required_tool";

        public string Name => ToolName;

        public string Description => "Requires approval.";

        public string ParametersSchema => "{}";

        public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("""{"executed":true}""");
    }

    private sealed class CountingApprovalHandler : IToolApprovalHandler
    {
        public int RequestCount { get; private set; }

        public Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
        {
            RequestCount++;
            return Task.FromResult(ToolApprovalResult.Denied("test denial"));
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> WarningMessages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                WarningMessages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
