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
        lark.SendCalls.Should().ContainSingle();
        lark.SendCalls[0].Token.Should().Be("runtime-card-token-1");
        cardKit.StreamCalls.Should().ContainSingle();
        cardKit.StreamCalls[0].Token.Should().Be("runtime-card-token-1");
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

    private static LlmReplyCardStreamChunkEvent BuildChunk(string correlationId) =>
        new()
        {
            CorrelationId = correlationId,
            RegistrationId = "reg-1",
            Activity = BuildSanitizedActivity(),
            AccumulatedText = "hello card",
            ChunkAtUnixMs = 42,
        };

    private static ChatActivity BuildSanitizedActivity() =>
        new()
        {
            Id = "msg-card-runtime-token-1",
            Type = ActivityType.Message,
            ChannelId = ChannelId.From("lark"),
            Bot = BotInstanceId.From("reg-1"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.Group,
                "oc_group_chat_1",
                "group",
                "oc_group_chat_1"),
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
                NyxLarkChatId = "oc_group_chat_1",
                NyxLarkUnionId = "on_union_1",
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

        public Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct)
        {
            SendCalls.Add((token, request));
            return Task.FromResult("""{"code":0,"data":{"message_id":"om_card_runtime_1"}}""");
        }

        public Task<string> ReplyToMessageAsync(string token, LarkReplyMessageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> CreateMessageReactionAsync(string token, LarkMessageReactionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ListMessageReactionsAsync(string token, LarkMessageReactionListRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> DeleteMessageReactionAsync(string token, LarkMessageReactionDeleteRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> SearchMessagesAsync(string token, LarkMessageSearchRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> BatchGetMessagesAsync(string token, LarkMessagesBatchGetRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> SearchChatsAsync(string token, LarkChatSearchRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> AppendSheetRowsAsync(string token, LarkSheetAppendRowsRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ListApprovalTasksAsync(string token, LarkApprovalTaskQueryRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ActOnApprovalTaskAsync(string token, LarkApprovalTaskActionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
