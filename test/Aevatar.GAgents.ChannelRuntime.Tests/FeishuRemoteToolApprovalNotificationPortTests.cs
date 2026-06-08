using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class FeishuRemoteToolApprovalNotificationPortTests
{
    [Fact]
    public void BuildCardJson_ShouldRenderTypedApproveAndDenyActions_WithoutCredentials()
    {
        var cardJson = FeishuRemoteToolApprovalNotificationPort.BuildCardJson(
            BuildNotification(argumentsJson: """{"amount":42,"memo":"ok"}"""));

        using var document = JsonDocument.Parse(cardJson);
        document.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Tool approval required.");
        document.RootElement.GetProperty("header").GetProperty("template").GetString()
            .Should().Be("orange");

        var bodyElements = document.RootElement.GetProperty("body").GetProperty("elements");
        var markdown = bodyElements.EnumerateArray()
            .Single(element => element.GetProperty("tag").GetString() == "markdown")
            .GetProperty("content")
            .GetString();
        markdown.Should().Contain("Tool: `delete-file`");
        markdown.Should().Contain("Request: `req-1`");
        markdown.Should().Contain("This tool call is marked destructive.");
        markdown.Should().Contain("Expires:");
        markdown.Should().Contain("\"amount\": 42");

        var approveButton = FindButton(bodyElements, "Approve");
        approveButton.GetProperty("type").GetString().Should().Be("primary");
        var approveValue = approveButton.GetProperty("behaviors")[0].GetProperty("value");
        approveValue.GetProperty("action_id").GetString().Should().Be("nyxid_approval_approve");
        approveValue.GetProperty("action_kind").GetString().Should().Be("button");
        approveValue.GetProperty("nyxid_approval_request_id").GetString().Should().Be("req-1");
        approveValue.GetProperty("nyxid_remote_approval_id").GetString().Should().Be("remote-1");
        approveValue.GetProperty("approved").GetBoolean().Should().BeTrue();

        var denyButton = FindButton(bodyElements, "Deny");
        denyButton.GetProperty("type").GetString().Should().Be("danger");
        var denyValue = denyButton.GetProperty("behaviors")[0].GetProperty("value");
        denyValue.GetProperty("action_id").GetString().Should().Be("nyxid_approval_deny");
        denyValue.GetProperty("nyxid_approval_request_id").GetString().Should().Be("req-1");
        denyValue.GetProperty("nyxid_remote_approval_id").GetString().Should().Be("remote-1");
        denyValue.GetProperty("approved").GetBoolean().Should().BeFalse();

        cardJson.Should().NotContain("nyx-access-token-secret");
        cardJson.Should().NotContain("nyx-org-token-secret");
        cardJson.Should().NotContain("sender-token-secret");
        cardJson.Should().NotContain("nyx-api-key");
    }

    [Fact]
    public void BuildCardJson_ShouldHandleInvalidAndLongArguments()
    {
        var invalidCardJson = FeishuRemoteToolApprovalNotificationPort.BuildCardJson(
            BuildNotification(argumentsJson: "{not-json"));
        using var invalidDocument = JsonDocument.Parse(invalidCardJson);
        var invalidMarkdown = ExtractFirstMarkdown(invalidDocument);

        invalidMarkdown.Should().Contain("```json");
        invalidMarkdown.Should().Contain("{not-json");

        var longArguments = "{\"payload\":\"" + new string('a', 1_700) + "\"}";
        var longCardJson = FeishuRemoteToolApprovalNotificationPort.BuildCardJson(
            BuildNotification(argumentsJson: longArguments));
        using var longDocument = JsonDocument.Parse(longCardJson);
        var longMarkdown = ExtractFirstMarkdown(longDocument);

        longMarkdown.Should().Contain("...[truncated]");
        longMarkdown!.Length.Should().BeLessThan(longArguments.Length + 200);
    }

    [Theory]
    [MemberData(nameof(DeliveryTargetIdCases))]
    public async Task NotifyAsync_ShouldUseDeliveryTargetIdPrecedence(
        string? messageId,
        string? platformMessageId,
        string? callerResponseId,
        IReadOnlyDictionary<string, string> externalMetadata,
        string expectedDeliveryTargetId)
    {
        var reader = new RecordingDeliveryTargetReader();
        var dispatcher = new RecordingLarkOutboundDispatcher();
        var port = CreatePort(reader, dispatcher);

        await port.NotifyAsync(
            BuildNotification(
                messageId: messageId,
                platformMessageId: platformMessageId,
                callerResponseId: callerResponseId,
                externalMetadata: externalMetadata),
            CancellationToken.None);

        reader.RequestedIds.Should().ContainSingle().Which.Should().Be(expectedDeliveryTargetId);
        dispatcher.Requests.Should().ContainSingle();
        var request = dispatcher.Requests[0];
        request.MessageType.Should().Be("interactive");
        request.PrimaryTarget.ReceiveId.Should().Be("oc_chat_1");
        request.PrimaryTarget.ReceiveIdType.Should().Be("chat_id");
        request.ContentJson.Should().Contain("nyxid_approval_request_id");
        request.ContentJson.Should().NotContain("nyx-access-token-secret");
        request.ContentJson.Should().NotContain("nyx-org-token-secret");
        request.ContentJson.Should().NotContain("sender-token-secret");
    }

    [Fact]
    public async Task NotifyAsync_ShouldFailBeforeLookup_WhenNoDeliveryTargetIdExists()
    {
        var reader = new RecordingDeliveryTargetReader();
        var dispatcher = new RecordingLarkOutboundDispatcher();
        var port = CreatePort(reader, dispatcher);

        Func<Task> act = () => port.NotifyAsync(
            BuildNotification(
                messageId: null,
                platformMessageId: null,
                callerResponseId: null,
                externalMetadata: new Dictionary<string, string>(StringComparer.Ordinal)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires an actor/catalog-owned delivery target id*");
        reader.RequestedIds.Should().BeEmpty();
        dispatcher.Requests.Should().BeEmpty();
    }

    public static TheoryData<string?, string?, string?, IReadOnlyDictionary<string, string>, string> DeliveryTargetIdCases()
    {
        var cases = new TheoryData<string?, string?, string?, IReadOnlyDictionary<string, string>, string>();

        cases.Add(
            "msg-target",
            "platform-target",
            "response-target",
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.MessageId] = "metadata-message-target",
                [ChannelMetadataKeys.PlatformMessageId] = "metadata-platform-target",
            },
            "msg-target");
        cases.Add(
            null,
            "platform-target",
            "response-target",
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.MessageId] = "metadata-message-target",
                [ChannelMetadataKeys.PlatformMessageId] = "metadata-platform-target",
            },
            "platform-target");
        cases.Add(
            null,
            null,
            "response-target",
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.MessageId] = "metadata-message-target",
                [ChannelMetadataKeys.PlatformMessageId] = "metadata-platform-target",
            },
            "response-target");
        cases.Add(
            null,
            null,
            null,
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.MessageId] = "metadata-message-target",
                [ChannelMetadataKeys.PlatformMessageId] = "metadata-platform-target",
            },
            "metadata-message-target");
        cases.Add(
            null,
            null,
            null,
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.PlatformMessageId] = "metadata-platform-target",
            },
            "metadata-platform-target");

        return cases;
    }

    private static FeishuRemoteToolApprovalNotificationPort CreatePort(
        IUserAgentDeliveryTargetReader reader,
        ILarkOutboundDispatcher dispatcher) =>
        new(
            reader,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            new LarkMessageComposer(),
            NullLogger<FeishuRemoteToolApprovalNotificationPort>.Instance,
            dispatcher);

    private static RemoteToolApprovalNotification BuildNotification(
        string argumentsJson = """{"name":"value"}""",
        string? messageId = "delivery-target-1",
        string? platformMessageId = null,
        string? callerResponseId = null,
        IReadOnlyDictionary<string, string>? externalMetadata = null) =>
        new(
            RequestId: "req-1",
            RemoteApprovalId: "remote-1",
            ToolName: "delete-file",
            ToolCallId: "tool-call-1",
            ArgumentsJson: argumentsJson,
            ApprovalMode: ToolApprovalMode.Auto,
            IsDestructive: true,
            SessionId: "session-1",
            ExpiresAt: new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero),
            ToolContext: AgentToolExecutionContext.Empty with
            {
                Credentials = new AgentToolCredentials(
                    NyxIdAccessToken: "nyx-access-token-secret",
                    NyxIdOrgToken: "nyx-org-token-secret",
                    SenderNyxIdAccessToken: "sender-token-secret"),
                Caller = new AgentToolCallerContext(
                    ScopeId: "scope-1",
                    OwnerSubject: "owner-1",
                    ResponseId: callerResponseId),
                Channel = new AgentToolChannelContext(
                    Platform: "lark",
                    SenderId: "sender-1",
                    RegistrationScopeId: "registration-1",
                    MessageId: messageId,
                    PlatformMessageId: platformMessageId),
                ExternalMetadata = externalMetadata ?? new Dictionary<string, string>(StringComparer.Ordinal),
            });

    private static JsonElement FindButton(JsonElement bodyElements, string label) =>
        bodyElements
            .EnumerateArray()
            .Single(element =>
                element.GetProperty("tag").GetString() == "button" &&
                element.GetProperty("text").GetProperty("content").GetString() == label);

    private static string? ExtractFirstMarkdown(JsonDocument document) =>
        document.RootElement
            .GetProperty("body")
            .GetProperty("elements")
            .EnumerateArray()
            .First(element => element.GetProperty("tag").GetString() == "markdown")
            .GetProperty("content")
            .GetString();

    private sealed class RecordingDeliveryTargetReader : IUserAgentDeliveryTargetReader
    {
        public List<string> RequestedIds { get; } = [];

        public Task<UserAgentDeliveryTarget?> GetAsync(string agentId, CancellationToken ct = default)
        {
            RequestedIds.Add(agentId);
            return Task.FromResult<UserAgentDeliveryTarget?>(new UserAgentDeliveryTarget(
                AgentId: agentId,
                Platform: "lark",
                ConversationId: "oc_chat_1",
                NyxProviderSlug: "api-lark-bot",
                NyxApiKey: "nyx-api-key",
                LarkReceiveId: "oc_chat_1",
                LarkReceiveIdType: "chat_id",
                LarkReceiveIdFallback: string.Empty,
                LarkReceiveIdTypeFallback: string.Empty,
                OutputFormat: SkillRunnerOutputFormat.Auto,
                TemplateName: "social_media",
                AgentType: string.Empty));
        }
    }

    private sealed class RecordingLarkOutboundDispatcher : ILarkOutboundDispatcher
    {
        public List<LarkSendNewMessageRequest> Requests { get; } = [];

        public Task<LarkSendNewMessageResult> SendNewMessageAsync(
            LarkSendNewMessageRequest request,
            CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(LarkSendNewMessageResult.Sent(
                "om_remote_approval_1",
                request.PrimaryTarget,
                usedFallback: false));
        }
    }
}
