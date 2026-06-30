using Aevatar.AI.ToolProviders.Lark;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelCardConversationTurnRunnerTests
{
    [Fact]
    public async Task Source_ShouldUseLarkStreamingCardShellHelper_ForCloseStreamingSettings()
    {
        var sourcePath = Path.Combine(
            GetRepositoryRoot(),
            "agents",
            "Aevatar.GAgents.NyxidChat",
            "ChannelCardConversationTurnRunner.cs");
        var source = await File.ReadAllTextAsync(sourcePath);

        Assert.Contains("LarkStreamingCardShell.BuildCloseStreamingSettingsJson()", source);
        Assert.DoesNotContain("""{"streaming_mode": false}""", source);
        Assert.DoesNotContain("""{"streaming_mode":false}""", source);
        Assert.DoesNotContain("""{"config":{"streaming_mode":false}}""", source);
    }

    [Fact]
    public async Task RunCardCreateAsync_ShouldUseRuntimeToken_WhenActivityIsSanitized()
    {
        var cardKit = new RecordingCardKitClient();
        var lark = new RecordingLarkNyxClient();
        var runner = new ChannelCardConversationTurnRunner(
            cardKit,
            lark,
            NullLogger<ChannelCardConversationTurnRunner>.Instance);

        var result = await runner.RunCardCreateAsync(
            BuildChunk("corr-card-create-runtime-1"),
            "streaming_main",
            RuntimeContext("runtime-card-token-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ErrorCode.Should().BeEmpty();
        cardKit.CreateCalls.Should().ContainSingle();
        cardKit.CreateCalls[0].Token.Should().Be("runtime-card-token-1");
        lark.ReplyCalls.Should().ContainSingle();
        lark.ReplyCalls[0].Token.Should().Be("runtime-card-token-1");
        cardKit.StreamCalls.Should().ContainSingle();
        cardKit.StreamCalls[0].Token.Should().Be("runtime-card-token-1");
    }

    [Fact]
    public async Task RunCardCreateAsync_ShouldReplyInThread_ForGroupActivityWithInboundPlatformMessageId()
    {
        var cardKit = new RecordingCardKitClient();
        var lark = new RecordingLarkNyxClient();
        var runner = new ChannelCardConversationTurnRunner(
            cardKit,
            lark,
            NullLogger<ChannelCardConversationTurnRunner>.Instance);

        var result = await runner.RunCardCreateAsync(
            BuildChunk("corr-card-reply-1"),
            "streaming_main",
            RuntimeContext("runtime-card-token-reply"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.CardMessageId.Should().Be("om_card_reply_1");
        lark.ReplyCalls.Should().ContainSingle();
        lark.SendCalls.Should().BeEmpty();

        var reply = lark.ReplyCalls[0];
        reply.Token.Should().Be("runtime-card-token-reply");
        reply.Request.MessageId.Should().Be("om_inbound_1");
        reply.Request.MessageType.Should().Be("interactive");
        reply.Request.ReplyInThread.Should().BeTrue();
        reply.Request.IdempotencyKey.Should().Be("corr-card-reply-1");
        reply.Request.ContentJson.Should().Contain("card-runtime-1");
        cardKit.StreamCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task RunCardCreateAsync_ShouldReturnCardSendFailed_WhenThreadedReplyFails()
    {
        var cardKit = new RecordingCardKitClient();
        var lark = new RecordingLarkNyxClient
        {
            ReplyResponse = """{"code":230001,"msg":"reply rejected"}""",
        };
        var runner = new ChannelCardConversationTurnRunner(
            cardKit,
            lark,
            NullLogger<ChannelCardConversationTurnRunner>.Instance);

        var result = await runner.RunCardCreateAsync(
            BuildChunk("corr-card-reply-fail-1"),
            "streaming_main",
            RuntimeContext("runtime-card-token-reply-fail"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("card_send_failed");
        result.ErrorSummary.Should().Contain("lark_code=230001");
        lark.ReplyCalls.Should().ContainSingle();
        lark.SendCalls.Should().BeEmpty();
        cardKit.StreamCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunCardCreateAsync_ShouldSendToChatId_WhenGroupActivityHasNoInboundPlatformMessageId()
    {
        var cardKit = new RecordingCardKitClient();
        var lark = new RecordingLarkNyxClient();
        var runner = new ChannelCardConversationTurnRunner(
            cardKit,
            lark,
            NullLogger<ChannelCardConversationTurnRunner>.Instance);

        var result = await runner.RunCardCreateAsync(
            BuildChunk(
                "corr-card-group-send-1",
                BuildSanitizedActivity(platformMessageId: string.Empty)),
            "streaming_main",
            RuntimeContext("runtime-card-token-group-send"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        lark.ReplyCalls.Should().BeEmpty();
        lark.SendCalls.Should().ContainSingle();
        lark.SendCalls[0].Request.TargetType.Should().Be("chat_id");
        lark.SendCalls[0].Request.TargetId.Should().Be("oc_group_chat_1");
        lark.SendCalls[0].Request.MessageType.Should().Be("interactive");
    }

    [Fact]
    public async Task RunCardCreateAsync_ShouldSendToChatId_WhenGroupActivityPlatformMessageIdIsNotLarkMessageId()
    {
        var cardKit = new RecordingCardKitClient();
        var lark = new RecordingLarkNyxClient();
        var runner = new ChannelCardConversationTurnRunner(
            cardKit,
            lark,
            NullLogger<ChannelCardConversationTurnRunner>.Instance);

        var result = await runner.RunCardCreateAsync(
            BuildChunk(
                "corr-card-group-route-id-1",
                BuildSanitizedActivity(platformMessageId: "route-uuid")),
            "streaming_main",
            RuntimeContext("runtime-card-token-group-route-id"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        lark.ReplyCalls.Should().BeEmpty();
        lark.SendCalls.Should().ContainSingle();
        lark.SendCalls[0].Request.TargetType.Should().Be("chat_id");
        lark.SendCalls[0].Request.TargetId.Should().Be("oc_group_chat_1");
        lark.SendCalls[0].Request.MessageType.Should().Be("interactive");
    }

    [Fact]
    public async Task RunCardCreateAsync_ShouldSendToUnionId_ForDirectMessageActivity()
    {
        var cardKit = new RecordingCardKitClient();
        var lark = new RecordingLarkNyxClient();
        var runner = new ChannelCardConversationTurnRunner(
            cardKit,
            lark,
            NullLogger<ChannelCardConversationTurnRunner>.Instance);

        var result = await runner.RunCardCreateAsync(
            BuildChunk(
                "corr-card-dm-send-1",
                BuildSanitizedActivity(
                    scope: ConversationScope.DirectMessage,
                    partition: "route-dm-1",
                    scopeSegment: "dm",
                    conversationIdentity: "ou_user_1",
                    platformMessageId: "om_dm_1")),
            "streaming_main",
            RuntimeContext("runtime-card-token-dm-send"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        lark.ReplyCalls.Should().BeEmpty();
        lark.SendCalls.Should().ContainSingle();
        lark.SendCalls[0].Request.TargetType.Should().Be("union_id");
        lark.SendCalls[0].Request.TargetId.Should().Be("on_union_1");
        lark.SendCalls[0].Request.MessageType.Should().Be("interactive");
    }

    [Fact]
    public async Task RunCardStreamAndFinalizeAsync_ShouldUseRuntimeToken_WhenActivitiesAreSanitized()
    {
        var cardKit = new RecordingCardKitClient();
        var lark = new RecordingLarkNyxClient();
        var runner = new ChannelCardConversationTurnRunner(
            cardKit,
            lark,
            NullLogger<ChannelCardConversationTurnRunner>.Instance);
        var runtimeContext = RuntimeContext("runtime-card-token-2");

        var streamResult = await runner.RunCardStreamAsync(
            BuildChunk("corr-card-stream-runtime-1"),
            "card-runtime-1",
            "streaming_main",
            sequence: 4,
            runtimeContext,
            CancellationToken.None);

        var finalizeResult = await runner.RunCardFinalizeAsync(
            BuildSanitizedActivity(),
            "card-runtime-1",
            "streaming_main",
            "final text",
            finalTextDiffersFromLastFlushed: true,
            sequence: 5,
            runtimeContext,
            CancellationToken.None);

        streamResult.Success.Should().BeTrue();
        finalizeResult.Success.Should().BeTrue();
        cardKit.StreamCalls.Should().HaveCount(2);
        cardKit.StreamCalls.Should().OnlyContain(call => call.Token == "runtime-card-token-2");
        cardKit.SettingsCalls.Should().ContainSingle();
        cardKit.SettingsCalls[0].Token.Should().Be("runtime-card-token-2");
        cardKit.SettingsCalls[0].Request.SettingsJson.Should().Be("""{"config":{"streaming_mode":false}}""");
    }

    [Fact]
    public async Task RunCardCreateAsync_ShouldProxyThroughInboundBotSlug_WhenActivityCarriesProviderSlug()
    {
        // The inbound DM was received by the bot whose NyxID proxy slug is api-lark-bot-4. The
        // streamed card reply must proxy through THAT slug, not the process-wide default — otherwise
        // a sibling bot under the same account answers the DM (the cross-talk bug).
        var defaultCardKit = new RecordingCardKitClient();
        var defaultLark = new RecordingLarkNyxClient();
        var perBotCardKit = new RecordingCardKitClient();
        var perBotLark = new RecordingLarkNyxClient();
        var factory = new RecordingOutboundClientFactory(
            defaultCardKit,
            defaultLark,
            new Dictionary<string, (RecordingCardKitClient, RecordingLarkNyxClient)>
            {
                ["api-lark-bot-4"] = (perBotCardKit, perBotLark),
            });
        var runner = new ChannelCardConversationTurnRunner(
            defaultCardKit,
            defaultLark,
            factory,
            NullLogger<ChannelCardConversationTurnRunner>.Instance);

        var result = await runner.RunCardCreateAsync(
            BuildChunk(
                "corr-card-slug-1",
                BuildSanitizedActivity(
                    scope: ConversationScope.DirectMessage,
                    partition: "route-dm-1",
                    scopeSegment: "dm",
                    conversationIdentity: "ou_user_1",
                    platformMessageId: "om_dm_1",
                    providerSlug: "api-lark-bot-4")),
            "streaming_main",
            RuntimeContext("runtime-card-token-slug"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        // The factory was asked for the inbound bot's slug, and the per-bot clients carried the calls.
        factory.RequestedCardKitSlugs.Should().Contain("api-lark-bot-4");
        factory.RequestedNyxSlugs.Should().Contain("api-lark-bot-4");
        perBotCardKit.CreateCalls.Should().ContainSingle();
        perBotCardKit.StreamCalls.Should().ContainSingle();
        perBotLark.SendCalls.Should().ContainSingle();
        // The default-slug singleton clients were never used for this reply.
        defaultCardKit.CreateCalls.Should().BeEmpty();
        defaultCardKit.StreamCalls.Should().BeEmpty();
        defaultLark.SendCalls.Should().BeEmpty();
        defaultLark.ReplyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunCardCreateAsync_ShouldUseDefaultClients_WhenActivityHasNoProviderSlug()
    {
        // No per-bot slug on the activity → fall back to the configured-default clients. The factory
        // must not be consulted for a per-bot client in that case.
        var defaultCardKit = new RecordingCardKitClient();
        var defaultLark = new RecordingLarkNyxClient();
        var factory = new RecordingOutboundClientFactory(
            defaultCardKit,
            defaultLark,
            new Dictionary<string, (RecordingCardKitClient, RecordingLarkNyxClient)>());
        var runner = new ChannelCardConversationTurnRunner(
            defaultCardKit,
            defaultLark,
            factory,
            NullLogger<ChannelCardConversationTurnRunner>.Instance);

        var result = await runner.RunCardCreateAsync(
            BuildChunk(
                "corr-card-no-slug-1",
                BuildSanitizedActivity(
                    scope: ConversationScope.DirectMessage,
                    partition: "route-dm-1",
                    scopeSegment: "dm",
                    conversationIdentity: "ou_user_1",
                    platformMessageId: "om_dm_1",
                    providerSlug: string.Empty)),
            "streaming_main",
            RuntimeContext("runtime-card-token-no-slug"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        factory.RequestedCardKitSlugs.Should().BeEmpty();
        factory.RequestedNyxSlugs.Should().BeEmpty();
        defaultCardKit.CreateCalls.Should().ContainSingle();
        defaultCardKit.StreamCalls.Should().ContainSingle();
        defaultLark.SendCalls.Should().ContainSingle();
    }

    private static ConversationTurnRuntimeContext RuntimeContext(string token) =>
        new(NyxRelayReplyToken: null, NyxUserAccessToken: token);

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static LlmReplyCardStreamChunkEvent BuildChunk(string correlationId, ChatActivity? activity = null) =>
        new()
        {
            CorrelationId = correlationId,
            RegistrationId = "reg-1",
            Activity = activity ?? BuildSanitizedActivity(),
            AccumulatedText = "hello card",
            ChunkAtUnixMs = 42,
        };

    private static ChatActivity BuildSanitizedActivity(
        ConversationScope scope = ConversationScope.Group,
        string partition = "oc_group_chat_1",
        string scopeSegment = "group",
        string conversationIdentity = "oc_group_chat_1",
        string platformMessageId = "om_inbound_1",
        string providerSlug = "") =>
        new()
        {
            Id = "msg-card-runtime-token-1",
            Type = ActivityType.Message,
            ChannelId = ChannelId.From("lark"),
            Bot = BotInstanceId.From("reg-1"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                scope,
                partition,
                scopeSegment,
                conversationIdentity),
            From = new ParticipantRef
            {
                CanonicalId = "ou_user_1",
                DisplayName = "User One",
            },
            Content = new MessageContent { Text = "hello" },
            TransportExtras = new TransportExtras
            {
                NyxPlatform = "lark",
                NyxUserAccessToken = string.Empty,
                NyxPlatformMessageId = platformMessageId,
                NyxLarkChatId = "oc_group_chat_1",
                NyxLarkUnionId = "on_union_1",
                NyxProviderSlug = providerSlug,
            },
        };

    private sealed class RecordingCardKitClient : ILarkCardKitClient
    {
        public List<(string Token, LarkCardKitCreateRequest Request)> CreateCalls { get; } = [];
        public List<(string Token, LarkCardKitStreamElementContentRequest Request)> StreamCalls { get; } = [];
        public List<(string Token, LarkCardKitSettingsRequest Request)> SettingsCalls { get; } = [];

        public Task<string> CreateCardAsync(string token, LarkCardKitCreateRequest request, CancellationToken ct)
        {
            CreateCalls.Add((token, request));
            return Task.FromResult("""{"code":0,"data":{"card_id":"card-runtime-1"}}""");
        }

        public Task<string> StreamElementContentAsync(
            string token,
            LarkCardKitStreamElementContentRequest request,
            CancellationToken ct)
        {
            StreamCalls.Add((token, request));
            return Task.FromResult("""{"code":0,"data":{}}""");
        }

        public Task<string> SetCardSettingsAsync(string token, LarkCardKitSettingsRequest request, CancellationToken ct)
        {
            SettingsCalls.Add((token, request));
            return Task.FromResult("""{"code":0,"data":{}}""");
        }

        public Task<string> UpdateCardAsync(string token, LarkCardKitUpdateRequest request, CancellationToken ct) =>
            Task.FromResult("""{"code":0,"data":{}}""");
    }

    private sealed class RecordingLarkNyxClient : ILarkNyxClient
    {
        public List<(string Token, LarkSendMessageRequest Request)> SendCalls { get; } = [];
        public List<(string Token, LarkReplyMessageRequest Request)> ReplyCalls { get; } = [];

        public string SendResponse { get; set; } = """{"code":0,"data":{"message_id":"om_card_runtime_1"}}""";
        public string ReplyResponse { get; set; } = """{"code":0,"data":{"message_id":"om_card_reply_1"}}""";

        public Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct)
        {
            SendCalls.Add((token, request));
            return Task.FromResult(SendResponse);
        }

        public Task<string> ReplyToMessageAsync(string token, LarkReplyMessageRequest request, CancellationToken ct)
        {
            ReplyCalls.Add((token, request));
            return Task.FromResult(ReplyResponse);
        }

        public Task<string> CreateMessageReactionAsync(string token, LarkMessageReactionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ListMessageReactionsAsync(string token, LarkMessageReactionListRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> DeleteMessageReactionAsync(string token, LarkMessageReactionDeleteRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> BatchGetMessagesAsync(string token, LarkMessagesBatchGetRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<LarkMessageResourceDownloadResult> DownloadMessageResourceAsync(
            string token,
            LarkMessageResourceDownloadRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> SearchChatsAsync(string token, LarkChatSearchRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> AppendSheetRowsAsync(string token, LarkSheetAppendRowsRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ListApprovalTasksAsync(string token, LarkApprovalTaskQueryRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> GetApprovalInstanceAsync(string token, LarkApprovalInstanceGetRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ActOnApprovalTaskAsync(string token, LarkApprovalTaskActionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> CreateDocxDocumentAsync(string token, LarkDocxCreateRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> AppendDocxTextBlocksAsync(string token, LarkDocxAppendBlocksRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> SetDrivePermissionAsync(string token, LarkDrivePermissionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> CreateBitableAppAsync(string token, LarkBitableCreateRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> GrantResourceMemberAsync(string token, LarkResourceMemberGrantRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> UploadDriveMediaAsync(string token, LarkDriveMediaUploadRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> UploadApprovalFileAsync(string token, LarkApprovalFileUploadRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingOutboundClientFactory : ILarkOutboundClientFactory
    {
        private readonly ILarkCardKitClient _defaultCardKit;
        private readonly ILarkNyxClient _defaultLark;
        private readonly IReadOnlyDictionary<string, (RecordingCardKitClient CardKit, RecordingLarkNyxClient Lark)> _perSlug;

        public RecordingOutboundClientFactory(
            ILarkCardKitClient defaultCardKit,
            ILarkNyxClient defaultLark,
            IReadOnlyDictionary<string, (RecordingCardKitClient, RecordingLarkNyxClient)> perSlug)
        {
            _defaultCardKit = defaultCardKit;
            _defaultLark = defaultLark;
            _perSlug = perSlug;
        }

        public List<string> RequestedCardKitSlugs { get; } = [];
        public List<string> RequestedNyxSlugs { get; } = [];

        public ILarkCardKitClient ResolveCardKitClient(string? providerSlug)
        {
            var slug = providerSlug?.Trim() ?? string.Empty;
            RequestedCardKitSlugs.Add(slug);
            return _perSlug.TryGetValue(slug, out var clients) ? clients.CardKit : _defaultCardKit;
        }

        public ILarkNyxClient ResolveNyxClient(string? providerSlug)
        {
            var slug = providerSlug?.Trim() ?? string.Empty;
            RequestedNyxSlugs.Add(slug);
            return _perSlug.TryGetValue(slug, out var clients) ? clients.Lark : _defaultLark;
        }
    }
}
