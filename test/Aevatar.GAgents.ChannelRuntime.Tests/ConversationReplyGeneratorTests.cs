using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConversationReplyGeneratorTests
{
    private static readonly LLMProviderCapabilities MultimodalCapabilities = new()
    {
        SupportedInputModalities = new HashSet<ContentPartKind>
        {
            ContentPartKind.Text,
            ContentPartKind.Image,
        },
        SupportedOutputModalities = new HashSet<ContentPartKind>
        {
            ContentPartKind.Text,
        },
        SupportsStreaming = true,
        SupportsToolCalls = true,
    };

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

    private static ChatActivity CreateLarkImageActivity(
        string id,
        string text,
        string platformMessageId,
        string imageKey,
        string? token) =>
        AddImageAttachment(CreateLarkActivity(id, text, platformMessageId, token), imageKey);

    private static ChatActivity AddImageAttachment(ChatActivity activity, string imageKey)
    {
        activity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = imageKey,
            Kind = AttachmentKind.Image,
            ContentType = "image/png",
            Name = "photo.png",
            SizeBytes = 512,
        });
        return activity;
    }

    private static ChatActivity CreateLarkActivity(
        string id,
        string text,
        string platformMessageId,
        string? token) =>
        new()
        {
            Id = id,
            ChannelId = ChannelId.From("lark"),
            Conversation = new ConversationReference { CanonicalKey = "lark:scope-a:chat-1" },
            Content = new MessageContent { Text = text },
            TransportExtras = new TransportExtras
            {
                NyxPlatform = "lark",
                NyxPlatformMessageId = platformMessageId,
                NyxUserAccessToken = token ?? string.Empty,
            },
        };

    [Fact]
    public async Task GenerateReplyAsync_WithPriorConversationHistory_BuildsSecondTurnRequestWithPreviousUserAndAssistant()
    {
        var providerFactory = new SequentialResponseProviderFactory("first assistant", "second assistant", "isolated assistant");
        var generator = new NyxIdConversationReplyGenerator(providerFactory);

        var first = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "lark-msg-1",
                ChannelId = new ChannelId { Value = "lark" },
                Conversation = new ConversationReference { CanonicalKey = "lark:scope-a:chat-1" },
                Content = new MessageContent { Text = "first user" },
            },
            new Dictionary<string, string>(),
            llmControl: null,
            toolContext: null,
            priorHistory: null,
            streamingSink: null,
            CancellationToken.None);

        var second = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "lark-msg-2",
                ChannelId = new ChannelId { Value = "lark" },
                Conversation = new ConversationReference { CanonicalKey = "lark:scope-a:chat-1" },
                Content = new MessageContent { Text = "second user" },
            },
            new Dictionary<string, string>(),
            llmControl: null,
            toolContext: null,
            priorHistory: first.AppendedHistory,
            streamingSink: null,
            CancellationToken.None);

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "lark-msg-other",
                ChannelId = new ChannelId { Value = "lark" },
                Conversation = new ConversationReference { CanonicalKey = "lark:scope-b:chat-2" },
                Content = new MessageContent { Text = "other user" },
            },
            new Dictionary<string, string>(),
            llmControl: null,
            toolContext: null,
            priorHistory: null,
            streamingSink: null,
            CancellationToken.None);

        first.AppendedHistory.Should().NotBeNull();
        first.AppendedHistory!.Select(message => (message.Role, message.Content))
            .Should()
            .ContainInOrder(("user", "first user"), ("assistant", "first assistant"));
        second.AppendedHistory.Should().NotBeNull();
        second.AppendedHistory!.Select(message => (message.Role, message.Content))
            .Should()
            .ContainInOrder(("user", "second user"), ("assistant", "second assistant"));

        providerFactory.Requests.Should().HaveCount(3);
        providerFactory.Requests[1].Messages
            .Where(message => message.Role is "user" or "assistant")
            .Select(message => (message.Role, message.Content))
            .Should()
            .ContainInOrder(
                ("user", "first user"),
                ("assistant", "first assistant"),
                ("user", "second user"));

        providerFactory.Requests[2].Messages
            .Should()
            .NotContain(message => message.Content == "first user" || message.Content == "first assistant");
    }

    // Conversations poisoned before AgentRunGAgent stopped persisting reasoning-only
    // turns still carry assistant entries with no wire-visible content. Replay must
    // skip them: providers drop bare reasoning on assistant history messages, so such
    // entries degenerate into empty assistant turns that corrupt every later request.
    [Fact]
    public async Task GenerateReplyAsync_WithEmptyAssistantHistoryEntries_SkipsThemOnReplay()
    {
        var providerFactory = new SequentialResponseProviderFactory("recovered assistant");
        var generator = new NyxIdConversationReplyGenerator(providerFactory);

        var poisonedHistory = new[]
        {
            new ConversationHistoryEntry { Role = "user", Content = "建一个定时任务" },
            new ConversationHistoryEntry { Role = "assistant", ReasoningContent = "reasoning only, no answer" },
            new ConversationHistoryEntry { Role = "user", Content = "怎么没反应" },
            new ConversationHistoryEntry { Role = "assistant", Content = "我已经收到了" },
        };

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "lark-msg-poisoned",
                ChannelId = new ChannelId { Value = "lark" },
                Conversation = new ConversationReference { CanonicalKey = "lark:scope-a:chat-poisoned" },
                Content = new MessageContent { Text = "帮我建一个定时任务，每天早上9点提醒我喝水" },
            },
            new Dictionary<string, string>(),
            llmControl: null,
            toolContext: null,
            priorHistory: poisonedHistory,
            streamingSink: null,
            CancellationToken.None);

        providerFactory.Requests.Should().NotBeEmpty();
        var messages = providerFactory.Requests[0].Messages;
        messages.Should().NotContain(
            message => message.Role == "assistant" &&
                       string.IsNullOrEmpty(message.Content) &&
                       (message.ToolCalls == null || message.ToolCalls.Count == 0),
            "assistant history entries without wire-visible content must be skipped on replay");
        messages.Should().Contain(message => message.Role == "assistant" && message.Content == "我已经收到了");
        messages.Should().Contain(message => message.Role == "user" && message.Content == "建一个定时任务");
        messages.Should().Contain(message => message.Role == "user" && message.Content == "怎么没反应");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithReasoningBearingPriorHistory_StripsReasoningFromLlmInput()
    {
        // Regression for the 2026-06-12 prod incident: prior turns' persisted
        // reasoning_content was rehydrated verbatim into the next turn's LLM request.
        // Replayed reasoning violates the reasoning-model contract (DeepSeek rejects it
        // by spec; through the NyxID proxy it silently derails generation until every
        // turn in the conversation completes empty). The rehydration boundary must strip
        // reasoning while preserving the visible content.
        var providerFactory = new SequentialResponseProviderFactory("next assistant");
        var generator = new NyxIdConversationReplyGenerator(providerFactory);

        var priorHistory = new List<ConversationHistoryEntry>
        {
            new()
            {
                Role = "user",
                Content = "first user",
            },
            new()
            {
                Role = "assistant",
                Content = "first assistant",
                ReasoningContent = "prior-turn chain of thought that must never be replayed",
            },
        };

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "lark-msg-reasoning",
                ChannelId = new ChannelId { Value = "lark" },
                Conversation = new ConversationReference { CanonicalKey = "lark:scope-a:chat-reasoning" },
                Content = new MessageContent { Text = "second user" },
            },
            new Dictionary<string, string>(),
            llmControl: null,
            toolContext: null,
            priorHistory: priorHistory,
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        var rehydratedAssistant = request.Messages.Should()
            .ContainSingle(message => message.Role == "assistant" && message.Content == "first assistant")
            .Subject;
        rehydratedAssistant.ReasoningContent.Should().BeNull(
            "prior-turn reasoning_content must never be replayed into provider input");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithCurrentLarkImageAttachment_BuildsImageContentPart()
    {
        var imageBytes = new byte[] { 1, 2, 3, 4 };
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, imageBytes, "image/png", "photo.png"));
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(providerFactory, larkClient: lark);

        await generator.GenerateReplyAsync(
            CreateLarkImageActivity(
                "msg-image-current",
                "describe it",
                "om_current",
                "img_current",
                token: "user-token"),
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        var userMessage = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.Last(message => message.Role == "user");
        userMessage.ContentParts.Should().NotBeNull();
        userMessage.ContentParts!.Should().Contain(part =>
            part.Kind == ContentPartKind.Text &&
            part.Text == "describe it");
        var imagePart = userMessage.ContentParts!.Single(part => part.Kind == ContentPartKind.Image);
        imagePart.DataBase64.Should().Be(Convert.ToBase64String(imageBytes));
        imagePart.MediaType.Should().Be("image/png");
        imagePart.Name.Should().Be("photo.png");
        userMessage.ContentParts!.Should().NotContain(part =>
            part.Text != null &&
            part.Text.Contains("Attachment visibility warning", StringComparison.Ordinal));
        lark.Downloads.Should().ContainSingle().Which.Should().Be((
            "user-token",
            "om_current",
            "img_current",
            LarkMessageResourceKind.Image));
    }

    [Fact]
    public async Task BuildStepPlanAsync_WithRecentLarkImageAttachment_BuildsImageContentPart()
    {
        var imageBytes = new byte[] { 9, 8, 7 };
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, imageBytes, "image/jpeg", "recent.jpg"));
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            larkClient: lark);
        var recentActivity = CreateLarkImageActivity(
            "msg-image-recent",
            "earlier image",
            "om_recent",
            "img_recent",
            token: null);
        var currentActivity = new ChatActivity
        {
            Id = "msg-follow-up",
            ChannelId = ChannelId.From("lark"),
            Conversation = new ConversationReference { CanonicalKey = "lark:scope-a:chat-1" },
            Content = new MessageContent { Text = "what was in the image?" },
        };
        var attachmentContext = new ChatAttachmentInputContext(
            [
                new RecentConversationAttachmentActivity
                {
                    ActivityId = recentActivity.Id,
                    AcceptedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Activity = recentActivity.Clone(),
                },
            ],
            "recent-token");

        var plan = await generator.BuildStepPlanAsync(
            currentActivity,
            new Dictionary<string, string>(),
            llmControl: null,
            toolContext: null,
            priorHistory: null,
            attachmentContext,
            forceDisableTools: false,
            CancellationToken.None);

        var userMessage = plan.InitialMessages.Last(message => message.Role == "user");
        var imagePart = userMessage.ContentParts.Should().NotBeNull().And.Subject
            .Single(part => part.Kind == ContentPartKind.Image);
        imagePart.DataBase64.Should().Be(Convert.ToBase64String(imageBytes));
        imagePart.MediaType.Should().Be("image/jpeg");
        imagePart.Name.Should().Be("recent.jpg");
        userMessage.ContentParts!.Should().NotContain(part =>
            part.Text != null &&
            part.Text.Contains("Attachment visibility warning", StringComparison.Ordinal));
        lark.Downloads.Should().ContainSingle().Which.Should().Be((
            "recent-token",
            "om_recent",
            "img_recent",
            LarkMessageResourceKind.Image));
    }

    [Fact]
    public async Task GenerateReplyAsync_WithTextOnlyProviderAndImageAttachment_AddsHonestVisibilityWarning()
    {
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, [1], "image/png", "photo.png"));
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = LLMProviderCapabilities.TextOnly,
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, larkClient: lark);

        await generator.GenerateReplyAsync(
            CreateLarkImageActivity(
                "msg-image-text-only",
                "describe it",
                "om_text_only",
                "img_text_only",
                token: "user-token"),
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        var userMessage = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.Last(message => message.Role == "user");
        userMessage.ContentParts.Should().NotBeNull();
        userMessage.ContentParts!.Should().NotContain(part => part.Kind == ContentPartKind.Image);
        userMessage.ContentParts!.Should().ContainSingle(part =>
            part.Kind == ContentPartKind.Text &&
            part.Text == "describe it");
        var systemMessage = providerFactory.Requests[0].Messages.First(message => message.Role == "system");
        systemMessage.Content.Should().Contain("Attachment visibility warning");
        systemMessage.Content.Should().Contain("selected LLM route does not support image input");
        systemMessage.Content.Should().Contain("do not describe, infer, or pretend to have seen");
        lark.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateReplyAsync_WithNonImageAttachment_AddsHonestVisibilityWarning()
    {
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, [1], "image/png", "photo.png"));
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, larkClient: lark);
        var activity = CreateLarkActivity(
            "msg-file",
            "read this",
            "om_file",
            token: "user-token");
        activity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = "file_key",
            Kind = AttachmentKind.File,
            ContentType = "application/pdf",
            Name = "report.pdf",
            SizeBytes = 512,
        });

        await generator.GenerateReplyAsync(
            activity,
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        var userMessage = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.Last(message => message.Role == "user");
        userMessage.ContentParts.Should().NotBeNull();
        userMessage.ContentParts!.Should().NotContain(part => part.Kind == ContentPartKind.Image);
        userMessage.ContentParts!.Should().ContainSingle(part =>
            part.Kind == ContentPartKind.Text &&
            part.Text == "read this");
        var systemMessage = providerFactory.Requests[0].Messages.First(message => message.Role == "system");
        systemMessage.Content.Should().Contain("Attachment visibility warning");
        systemMessage.Content.Should().Contain("could not be converted to LLM image input");
        lark.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateReplyAsync_WhenLarkImageDownloadFails_AddsHonestVisibilityWarning()
    {
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(false, [], "image/png", "photo.png", "not found", 404));
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, larkClient: lark);

        await generator.GenerateReplyAsync(
            CreateLarkImageActivity(
                "msg-image-download-failure",
                "describe it",
                "om_download_fail",
                "img_download_fail",
                token: "user-token"),
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        var userMessage = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.Last(message => message.Role == "user");
        userMessage.ContentParts.Should().NotBeNull();
        userMessage.ContentParts!.Should().NotContain(part => part.Kind == ContentPartKind.Image);
        userMessage.ContentParts!.Should().ContainSingle(part =>
            part.Kind == ContentPartKind.Text &&
            part.Text == "describe it");
        var systemMessage = providerFactory.Requests[0].Messages.First(message => message.Role == "system");
        systemMessage.Content.Should().Contain("Attachment visibility warning");
        systemMessage.Content.Should().Contain("could not be converted to LLM image input");
        lark.Downloads.Should().ContainSingle();
    }

    [Fact]
    public async Task GenerateReplyAsync_WithoutAttachments_DoesNotAddVisibilityWarning()
    {
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, [1], "image/png", "photo.png"));
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = LLMProviderCapabilities.TextOnly,
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, larkClient: lark);

        await generator.GenerateReplyAsync(
            CreateLarkActivity(
                "msg-no-attachment",
                "hello",
                "om_no_attachment",
                token: "user-token"),
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        var userMessage = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.Last(message => message.Role == "user");
        userMessage.ContentParts.Should().NotBeNull();
        userMessage.ContentParts!.Should().ContainSingle(part => part.Kind == ContentPartKind.Text && part.Text == "hello");
        userMessage.ContentParts!.Should().NotContain(part =>
            part.Text != null &&
            part.Text.Contains("Attachment visibility warning", StringComparison.Ordinal));
        lark.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateReplyAsync_CapsPriorHistoryToTenMostRecent_AndStillExportsCurrentTurnHistory()
    {
        var providerFactory = new SequentialResponseProviderFactory("window assistant");
        var generator = new NyxIdConversationReplyGenerator(providerFactory);
        var priorHistory = Enumerable.Range(0, 100)
            .Select(index => new ConversationHistoryEntry
            {
                Role = index % 2 == 0 ? "user" : "assistant",
                Content = $"prior {index}",
            })
            .ToArray();

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "lark-msg-window",
                ChannelId = new ChannelId { Value = "lark" },
                Conversation = new ConversationReference { CanonicalKey = "lark:scope-a:chat-window" },
                Content = new MessageContent { Text = "current user" },
            },
            new Dictionary<string, string>(),
            llmControl: null,
            toolContext: null,
            priorHistory,
            streamingSink: null,
            CancellationToken.None);

        providerFactory.Requests.Should().HaveCount(1);
        var promptHistory = providerFactory.Requests[0].Messages
            .Where(message => message.Role is "user" or "assistant")
            .Select(message => (message.Role, message.Content))
            .ToList();
        // R1: never send the full group history. Only the 10 MOST RECENT prior entries (prior 90..99)
        // reach the prompt, in order, followed by the current turn. The oldest (prior 0..89) are dropped.
        promptHistory.Should().NotContain(("user", "prior 0"), "the oldest prior history must be dropped");
        promptHistory.Should().NotContain(("user", "prior 88"), "only the 10 most recent prior entries are kept");
        promptHistory.Should().ContainInOrder(
            ("user", "prior 90"), ("assistant", "prior 91"), ("assistant", "prior 99"), ("user", "current user"));
        promptHistory.Count(entry => entry.Content.StartsWith("prior ", StringComparison.Ordinal))
            .Should().BeLessThanOrEqualTo(10, "prior history sent to the prompt is capped at the 10 most recent entries");
        reply.AppendedHistory.Should().NotBeNull();
        reply.AppendedHistory!.Select(message => (message.Role, message.Content))
            .Should()
            .ContainInOrder(("user", "current user"), ("assistant", "window assistant"));
    }

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
        systemPrompt.Should().NotContain("chrono-ai-daily");
        systemPrompt.Should().Contain("When you are following a loaded skill and you hit a missing capability");
        systemPrompt.Should().Contain("ornn_search_skills");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithChannelContextMiddleware_IncludesLarkApprovalOperatorUserIdInSystemPrompt()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            llmMiddlewares: [new ChannelContextMiddleware(NullLogger<ChannelContextMiddleware>.Instance)]);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-lark-operator-context",
                ChannelId = ChannelId.From("lark"),
                Conversation = new ConversationReference { CanonicalKey = "lark:group:oc_1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.ChatType] = "group",
                [ChannelMetadataKeys.SenderId] = "ou_sender_1",
                [ChannelMetadataKeys.ConversationId] = "oc_1",
                [ChannelMetadataKeys.LarkOperatorUserId] = "lark-user-1",
                [ChannelMetadataKeys.LarkOperatorOpenId] = "ou_operator_1",
            },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        var systemPrompt = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("operator_user_id: \"lark-user-1\"");
        systemPrompt.Should().Contain("operator_open_id: \"ou_operator_1\"");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithChannelContextMiddleware_IncludesLarkSubjectIdsSeparatelyFromOperatorIds()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            llmMiddlewares: [new ChannelContextMiddleware(NullLogger<ChannelContextMiddleware>.Instance)]);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-lark-subject-context",
                ChannelId = ChannelId.From("lark"),
                Conversation = new ConversationReference { CanonicalKey = "lark:group:oc_1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.ChatType] = "group",
                [ChannelMetadataKeys.SenderId] = "ou_sender_1",
                [ChannelMetadataKeys.ConversationId] = "oc_1",
                [ChannelMetadataKeys.LarkSubjectUserId] = "lark-subject-user-1",
                [ChannelMetadataKeys.LarkSubjectEmployeeId] = "employee-1",
            },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        var systemPrompt = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("subject_user_id: \"lark-subject-user-1\"");
        systemPrompt.Should().Contain("subject_employee_id: \"employee-1\"");
        systemPrompt.Should().Contain("operator_user_id: \"\"");
        systemPrompt.Should().Contain("operator_open_id: \"\"");
        systemPrompt.Should().NotContain("operator_user_id: \"lark-subject-user-1\"");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithChannelContextMiddleware_IncludesResolvedMentionsLine()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            llmMiddlewares: [new ChannelContextMiddleware(NullLogger<ChannelContextMiddleware>.Instance)]);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-lark-mentions-context",
                ChannelId = ChannelId.From("lark"),
                Conversation = new ConversationReference { CanonicalKey = "lark:group:oc_1" },
                Content = new MessageContent { Text = "@_user_1 给 @_user_2 加一下权限" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.ChatType] = "group",
                [ChannelMetadataKeys.SenderId] = "ou_sender_1",
                [ChannelMetadataKeys.ConversationId] = "oc_1",
                [ChannelMetadataKeys.Mentions] = "Aevatar <ou_bot_1>; 张三 <ou_zhangsan>",
            },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        var systemPrompt = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.First(message => message.Role == "system").Content;
        // Emitted raw (not JSON-escaped) so the open_id delimiters and CJK display name stay readable.
        systemPrompt.Should().Contain("mentions: Aevatar <ou_bot_1>; 张三 <ou_zhangsan>");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithChannelContextMiddleware_OmitsMentionsLineWhenNoneMentioned()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            llmMiddlewares: [new ChannelContextMiddleware(NullLogger<ChannelContextMiddleware>.Instance)]);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-lark-no-mentions-context",
                ChannelId = ChannelId.From("lark"),
                Conversation = new ConversationReference { CanonicalKey = "lark:group:oc_1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.ChatType] = "group",
                [ChannelMetadataKeys.SenderId] = "ou_sender_1",
                [ChannelMetadataKeys.ConversationId] = "oc_1",
            },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        var systemPrompt = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().NotContain("mentions: ");
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
    public async Task GenerateReplyAsync_WhenApprovalHandlerMissingAndToolRequiresApproval_ShouldDenyWithoutExecutingTool()
    {
        var tool = new ApprovalRequiredTool();
        var generator = new NyxIdConversationReplyGenerator(
            new ToolResultEchoingProviderFactory(),
            toolSources: [new SingleToolSource(tool)]);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-no-handler-approval",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-no-handler" },
                Content = new MessageContent { Text = "run tool" },
            },
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Contain("approval-gated tools cannot run here");
        reply.Text.Should().NotContain("An approval request has been sent.");
        reply.Text.Should().NotContain("\"approval_required\":true");
        tool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task GenerateReplyAsync_WhenSenderBoundMutationHasNoSenderToken_ShouldDenyBeforeApprovalAndExecution()
    {
        var tool = new ApprovalRequiredTool();
        var approvalHandler = new CountingApprovalHandler();
        var providerFactory = new ToolResultEchoingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources: [new SingleToolSource(tool)],
            approvalHandler: approvalHandler);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-sender-bound-no-token-write",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-sender-bound-no-token" },
                Content = new MessageContent { Text = "run tool" },
            },
            new Dictionary<string, string>(),
            Control(token: "owner-token"),
            ToolContext("bnd_sender"),
            streamingSink: null,
            CancellationToken.None);

        providerFactory.Requests.Should().HaveCount(2);
        var toolResult = providerFactory.Requests[1].Messages
            .Should()
            .ContainSingle(message => message.Role == "tool")
            .Subject
            .Content;
        toolResult.Should().Contain("credential_denied");
        toolResult.Should().Contain("Owner credentials were not used");
        toolResult.Should().Contain("/init");
        reply.Text.Should().Contain("credential_denied");
        reply.Text.Should().Contain("Owner credentials were not used");
        reply.Text.Should().Contain("/init");
        approvalHandler.RequestCount.Should().Be(0);
        tool.ExecuteCount.Should().Be(0);
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
    public async Task GenerateReplyAsync_ForLarkRelayTurn_InjectsAevatarWorkflowToolsIntoLlmRequest()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources:
            [
                new SingleToolSource(new FixedResultTool("aevatar_invoke_gagent", """{"ok":true}""")),
                new SingleToolSource(new FixedResultTool("aevatar_invoke_team", """{"ok":true}""")),
                new SingleToolSource(new FixedResultTool("aevatar_start_workflow", """{"run_id":"run-1"}""")),
                new SingleToolSource(new FixedResultTool("aevatar_observe_run", """{"status":"running"}""")),
            ]);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "lark-relay-msg-workflow-tools",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-workflow-tools" },
                Content = new MessageContent { Text = "start the workflow" },
                TransportExtras = new TransportExtras
                {
                    NyxPlatform = "lark",
                    NyxPlatformMessageId = "om_workflow_tools",
                },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.PlatformMessageId] = "om_workflow_tools",
            },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().NotBeNull();
        request.Tools!.Select(static tool => tool.Name).Should().Contain(
        [
            "aevatar_invoke_gagent",
            "aevatar_invoke_team",
            "aevatar_start_workflow",
            "aevatar_observe_run",
        ]);
        request.Tools!.Select(static tool => tool.Name).Should().NotContain("aevatar_invoke_workflow");
    }

    // Tools whose outcome lands off-chat (e.g. aevatar_provision_workflow_schedule, which
    // delivers its scheduled runs to /workflow/observatory, never a chat/bot) self-declare the
    // generic AgentToolCapabilities.ExcludeFromDirectChannelChat marker. The channel/Lark
    // conversation agent must hide ANY tool carrying that capability — keyed off the capability,
    // not the tool name — otherwise it could route a Lark user's request away from their chat.
    // Ordinary channel workflow tools (no such capability) are unaffected.
    [Fact]
    public async Task GenerateReplyAsync_ForLarkRelayTurn_ExcludesChannelHiddenCapabilityToolFromLlmRequest()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources:
            [
                new SingleToolSource(new CapabilityFixedResultTool(
                    "aevatar_provision_workflow_schedule",
                    """{"ok":true}""",
                    AgentToolCapabilities.ExcludeFromDirectChannelChat)),
                new SingleToolSource(new FixedResultTool("aevatar_start_workflow", """{"run_id":"run-1"}""")),
            ]);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "lark-relay-msg-excluded-tool",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-excluded-tool" },
                Content = new MessageContent { Text = "schedule it" },
                TransportExtras = new TransportExtras
                {
                    NyxPlatform = "lark",
                    NyxPlatformMessageId = "om_excluded_tool",
                },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.PlatformMessageId] = "om_excluded_tool",
            },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().NotBeNull();
        // The capability-marked (Observatory-only) scheduling tool is hidden from the channel surface...
        request.Tools!.Select(static tool => tool.Name).Should().NotContain("aevatar_provision_workflow_schedule");
        // ...but ordinary channel workflow tools still flow through unchanged.
        request.Tools!.Select(static tool => tool.Name).Should().Contain("aevatar_start_workflow");
    }

    // The exclusion is keyed off the GENERIC capability marker, not the tool name. A tool with the
    // very same name but WITHOUT the capability stays on the channel surface; a differently-named
    // tool that DOES declare the capability is hidden. This pins the no-hardcoded-tool-name contract
    // (CLAUDE.md "不得对特定 skill/命令/模板名硬编码").
    [Fact]
    public async Task GenerateReplyAsync_ForLarkRelayTurn_KeysChannelExclusionOnCapabilityNotToolName()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources:
            [
                // Same name as the Observatory tool, but no exclusion capability → stays visible.
                new SingleToolSource(new FixedResultTool("aevatar_provision_workflow_schedule", """{"ok":true}""")),
                // Arbitrary name, but declares the exclusion capability → hidden.
                new SingleToolSource(new CapabilityFixedResultTool(
                    "some_other_off_chat_tool",
                    """{"ok":true}""",
                    AgentToolCapabilities.ExcludeFromDirectChannelChat)),
            ]);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "lark-relay-msg-capability-keyed",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-capability-keyed" },
                Content = new MessageContent { Text = "do it" },
                TransportExtras = new TransportExtras
                {
                    NyxPlatform = "lark",
                    NyxPlatformMessageId = "om_capability_keyed",
                },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.PlatformMessageId] = "om_capability_keyed",
            },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().NotBeNull();
        var toolNames = request.Tools!.Select(static tool => tool.Name).ToArray();
        // Name alone never triggers exclusion — only the capability does.
        toolNames.Should().Contain("aevatar_provision_workflow_schedule");
        toolNames.Should().NotContain("some_other_off_chat_tool");
    }

    [Fact]
    public async Task GenerateReplyAsync_WhenUseSkillMountsWorkflows_ShouldUseRegisteredScopedToolWithoutApprovalDenial()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "demo-dinner-workflow-skill",
            Description = "Dinner workflow demo",
            Instructions = "Run the dinner workflow.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "demo_dinner",
                    WorkflowYamls = ["name: demo_dinner\nsteps: []\n"],
                },
            ],
        });
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var providerFactory = new UseSkillMountWorkflowProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources:
            [
                new SingleToolSource(new UseSkillTool(catalog, scopeWorkflowCommandPort: commandPort)),
            ],
            localSkillCatalog: catalog);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-use-skill-mount-workflow",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-workflow-mount" },
                Content = new MessageContent { Text = "跑一下demo-dinner-workflow-skill这个skill" },
            },
            new Dictionary<string, string>(),
            Control(),
            AgentToolExecutionContext.Empty with
            {
                Caller = new AgentToolCallerContext("scope-1", "scope-1", null),
            },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Contain("## Mounted Workflows");
        reply.Text.Should().Contain("\"accepted\": true");
        reply.Text.Should().NotContain("approval-gated tools cannot run here");
        reply.Text.Should().NotContain("scope workflow command port is not available in this host");
        commandPort.Requests.Should().ContainSingle()
            .Which.Should().Match<ScopeWorkflowUpsertRequest>(request =>
                request.ScopeId == "scope-1" &&
                request.WorkflowId == "demo_dinner");
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
    public async Task GenerateReplyAsync_WithToolCallPreamble_DoesNotStreamProcessNarration()
    {
        var providerFactory = new ToolCallingPreambleProviderFactory();
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
                Id = "msg-tool-preamble",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-tool-preamble" },
                Content = new MessageContent { Text = "/approval eanzhao" },
            },
            new Dictionary<string, string>(),
            sink,
            CancellationToken.None);

        reply.Text.Should().Be("最终结果");
        sink.Emissions.Should().Equal("…", "最终结果");
        sink.Emissions.Should().NotContain(text => text.Contains("开始执行", StringComparison.Ordinal));
        sink.Emissions.Should().NotContain(text => text.Contains("先查目录", StringComparison.Ordinal));
        providerFactory.Requests.Should().HaveCount(2);
        providerFactory.Requests[1].Messages.Any(message =>
            message.Role == "assistant" &&
            message.Content == "开始执行 approval-flow，先查目录结构。" &&
            message.ToolCalls is { Count: 1 }).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateReplyAsync_WhenPrimarySkillSkipsOrnnDiscovery_ForcesSearchThenUseSkillBeforeFinal()
    {
        var providerFactory = new PrimarySkillRecoveryProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources:
            [
                new SingleToolSource(new FixedResultTool("ornn_search_skills", "Found 1 skills:\n- **project-summary**")),
                new SingleToolSource(new FixedResultTool("use_skill", "# project-summary\n## Instructions\nBuild the project summary.")),
            ],
            relayOptions: new global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingPlaceholderText = "…",
            });
        var skillRecovery = new AgentSkillRecoveryContext(
            RequireInitialOrnnSearch: true,
            RequireOrnnSearchOnBlocker: true,
            CommandName: "summary",
            OriginalCommand: "/summary",
            PrimarySkillName: "project-summary",
            MaxOrnnSearchAttempts: 2);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-summary-primary-skill",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-summary-primary-skill" },
                Content = new MessageContent { Text = "/summary" },
            },
            new Dictionary<string, string>(),
            Control(),
            AgentToolExecutionContext.Empty with { SkillRecovery = skillRecovery },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("summary report from loaded skill");
        providerFactory.Requests.Should().ContainSingle();
        providerFactory.ObservedToolCalls.Should().Contain("ornn_search_skills");
        providerFactory.ObservedToolCalls.Should().Contain("use_skill");
        providerFactory.Requests.Any(request =>
            request.Messages.Any(message =>
                message.Role == "assistant" &&
                message.ToolCalls?.Any(call =>
                    call.Name == "ornn_search_skills" &&
                    call.ArgumentsJson.Contains("project-summary", StringComparison.Ordinal)) == true)).Should().BeTrue();
        providerFactory.Requests.Any(request =>
            request.Messages.Any(message =>
                message.Role == "assistant" &&
                message.ToolCalls?.Any(call =>
                    call.Name == "use_skill" &&
                    call.ArgumentsJson.Contains("project-summary", StringComparison.Ordinal)) == true)).Should().BeTrue();
        reply.Text.Should().NotContain("generic summary answer");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithSkillRecoveryStreamingStatus_DoesNotPolluteFinalReplyText()
    {
        var providerFactory = new PrimarySkillRecoveryProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources:
            [
                new SingleToolSource(new FixedResultTool("ornn_search_skills", "Found 1 skills:\n- **project-summary**")),
                new SingleToolSource(new FixedResultTool("use_skill", "# project-summary\n## Instructions\nBuild the project summary.")),
            ]);
        var sink = new RecordingStreamingSink();
        var skillRecovery = new AgentSkillRecoveryContext(
            RequireInitialOrnnSearch: true,
            RequireOrnnSearchOnBlocker: true,
            CommandName: "summary",
            OriginalCommand: "/summary",
            PrimarySkillName: "project-summary",
            MaxOrnnSearchAttempts: 2);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-summary-streaming-status",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-summary-streaming-status" },
                Content = new MessageContent { Text = "/summary" },
            },
            new Dictionary<string, string>(),
            Control(),
            AgentToolExecutionContext.Empty with { SkillRecovery = skillRecovery },
            sink,
            CancellationToken.None);

        reply.Text.Should().Be("summary report from loaded skill");
        sink.Emissions.Should().HaveCount(2);
        sink.Emissions[0].Should().Contain("正在处理 `/summary`");
        sink.Emissions[0].Should().NotBe("…");
        sink.Emissions[1].Should().Be("summary report from loaded skill");
    }

    [Fact]
    public async Task GenerateReplyAsync_WhenLoadedSkillHitsToolBlocker_ForcesOrnnRecoveryBeforeFinalFailure()
    {
        var providerFactory = new BlockerRecoveryProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources:
            [
                new SingleToolSource(new FixedResultTool("ornn_search_skills", "Found 1 skills:\n- **project-summary**")),
                new SingleToolSource(new FixedResultTool("use_skill", "# project-summary\n## Instructions\nFetch project data.")),
                new SingleToolSource(new FixedResultTool("chrono_storage_query", "Error: Invalid URI: The hostname could not be parsed.")),
            ]);
        var skillRecovery = new AgentSkillRecoveryContext(
            RequireInitialOrnnSearch: true,
            RequireOrnnSearchOnBlocker: true,
            CommandName: "summary",
            OriginalCommand: "/summary",
            PrimarySkillName: "project-summary",
            MaxOrnnSearchAttempts: 2);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-summary-recovery",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-summary-recovery" },
                Content = new MessageContent { Text = "/summary" },
            },
            new Dictionary<string, string>(),
            Control(),
            AgentToolExecutionContext.Empty with { SkillRecovery = skillRecovery },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("recovered summary report");
        providerFactory.Requests.Should().HaveCount(3);
        providerFactory.Requests.Any(request =>
            request.Messages.Any(message =>
                message.Role == "assistant" &&
                message.ToolCalls?.Any(call =>
                    call.Name == "ornn_search_skills" &&
                    call.ArgumentsJson.Contains("project-summary", StringComparison.Ordinal)) == true)).Should().BeTrue();
        providerFactory.Requests.Any(request =>
            request.Messages.Any(message =>
                message.Role == "assistant" &&
                message.ToolCalls?.Any(call =>
                    call.Name == "ornn_search_skills" &&
                    call.ArgumentsJson.Contains("Invalid URI", StringComparison.Ordinal)) == true)).Should().BeTrue();
        providerFactory.ObservedToolCalls.Count(call => call == "ornn_search_skills").Should().BeGreaterThanOrEqualTo(2);
        reply.Text.Contains("chrono storage backend unavailable", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateReplyAsync_WhenUnknownSlashSkipsInitialOrnnSearch_ForcesSearchBeforeFinal()
    {
        var providerFactory = new SlashInitialSearchRecoveryProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources:
            [
                new SingleToolSource(new FixedResultTool("ornn_search_skills", "Found 1 skills:\n- **goal**")),
                new SingleToolSource(new FixedResultTool("use_skill", "# goal\n## Instructions\nExecute the goal command.")),
            ]);
        var skillRecovery = new AgentSkillRecoveryContext(
            RequireInitialOrnnSearch: true,
            RequireOrnnSearchOnBlocker: true,
            CommandName: "goal",
            OriginalCommand: "/goal ship command fix",
            PrimarySkillName: null,
            MaxOrnnSearchAttempts: 2);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-goal-recovery",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-goal-recovery" },
                Content = new MessageContent { Text = "/goal ship command fix" },
            },
            new Dictionary<string, string>(),
            Control(),
            AgentToolExecutionContext.Empty with { SkillRecovery = skillRecovery },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("goal command from loaded skill");
        providerFactory.Requests.Should().ContainSingle();
        providerFactory.ObservedToolCalls.Should().Contain("ornn_search_skills");
        providerFactory.ObservedToolCalls.Should().Contain("use_skill");
        providerFactory.Requests.Any(request =>
            request.Messages.Any(message =>
                message.Role == "assistant" &&
                message.ToolCalls?.Any(call => call.Name == "ornn_search_skills") == true)).Should().BeTrue();
        providerFactory.Requests.Any(request =>
            request.Messages.Any(message =>
                message.Role == "assistant" &&
                message.ToolCalls?.Any(call =>
                    call.Name == "use_skill" &&
                    call.ArgumentsJson.Contains("goal", StringComparison.Ordinal)) == true)).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateReplyAsync_WhenSearchMatchCannotBeParsed_BoundsNudgeOnlyRecovery()
    {
        var providerFactory = new UnparseableSearchMatchRecoveryProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources:
            [
                new SingleToolSource(new FixedResultTool("ornn_search_skills", "Found 1 skills:\n* project-summary")),
            ]);
        var skillRecovery = new AgentSkillRecoveryContext(
            RequireInitialOrnnSearch: true,
            RequireOrnnSearchOnBlocker: true,
            CommandName: "summary",
            OriginalCommand: "/summary",
            PrimarySkillName: null,
            MaxOrnnSearchAttempts: 2);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-unparseable-search-match",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-unparseable-search-match" },
                Content = new MessageContent { Text = "/summary" },
            },
            new Dictionary<string, string>(),
            Control(),
            AgentToolExecutionContext.Empty with { SkillRecovery = skillRecovery },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("fallback after bounded recovery");
        providerFactory.Requests.Count.Should().BeLessThan(40);
        providerFactory.Requests.Count(request =>
            request.Messages.Any(message =>
                message.Role == "user" &&
                message.Content?.Contains("no skill has been loaded", StringComparison.OrdinalIgnoreCase) == true))
            .Should().Be(1);
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
        var toolContext = request.ToolContext!;
        // Sender's model wins (non-empty).
        toolContext.Routing.ModelOverride.Should().Be("sender-model");
        // Sender left route blank → owner's upstream-pinned route stays.
        toolContext.Routing.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/owner");
        // Sender left max-rounds at 0 → owner's upstream-pinned value stays.
        toolContext.Routing.MaxToolRoundsOverride.Should().Be(9);
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
        var toolContext = request.ToolContext!;
        toolContext.Routing.ModelOverride.Should().Be("owner-only-model");
        toolContext.Routing.NyxIdRoutePreference.Should().Be("owner-route");
        toolContext.Routing.MaxToolRoundsOverride.Should().Be(4);
        // Generator must not have touched the prefs store when no binding-id is present.
        prefsStore.Lookups.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateReplyAsync_ShouldNotPromoteMetadataOwnedKeysIntoToolContext()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(providerFactory);

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-owned-metadata",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-owned-metadata" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.ScopeId] = "metadata-scope",
                ["scope_id"] = "metadata-scope-alias",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "metadata-access-token",
                [LLMRequestMetadataKeys.NyxIdOrgToken] = "metadata-org-token",
                [LLMRequestMetadataKeys.SenderNyxIdAccessToken] = "metadata-sender-token",
                [LLMRequestMetadataKeys.NyxIdRoutePreference] = "metadata-route",
                [LLMRequestMetadataKeys.ModelOverride] = "metadata-model",
                [LLMRequestMetadataKeys.MaxToolRoundsOverride] = "9",
                ["channel.platform"] = "lark",
                ["channel.sender_id"] = "ou_metadata_sender",
                ["channel.message_id"] = "metadata-message",
                ["trace-id"] = "trace-1",
            },
            Control(model: "typed-model", route: "typed-route", rounds: 6, token: "typed-token"),
            toolContext: null,
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Metadata.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("trace-id", "trace-1"));

        var toolContext = request.ToolContext!;
        toolContext.Caller.ScopeId.Should().BeNull();
        toolContext.Channel.Platform.Should().BeNull();
        toolContext.Channel.SenderId.Should().BeNull();
        toolContext.Channel.MessageId.Should().BeNull();
        toolContext.Credentials.NyxIdAccessToken.Should().Be("typed-token");
        toolContext.Credentials.NyxIdOrgToken.Should().Be("typed-token");
        toolContext.Routing.ModelOverride.Should().Be("typed-model");
        toolContext.Routing.NyxIdRoutePreference.Should().Be("typed-route");
        toolContext.Routing.MaxToolRoundsOverride.Should().Be(6);
        toolContext.ExternalMetadata.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("trace-id", "trace-1"));
    }

    // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
    // slash silently consumed.
    // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
    // non-slash text path unchanged (owner-LLM chat fallback).
    [Fact]
    public async Task GenerateReplyAsync_DisablesTools_WhenChannelTurnHasNoSenderBinding()
    {
        var providerFactory = new RecordingProviderFactory();
        var toolSource = new CountingToolSource(new ApprovalRequiredTool());
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources: [toolSource],
            localSkillCatalog: new LocalSkillCatalog());

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-unbound-channel-tools",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.SenderId] = "ou_user_1",
                [ChannelMetadataKeys.MessageId] = "msg-unbound-channel-tools",
            },
            Control("owner-only-model", "owner-route", 4),
            toolContext: null,
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().BeNull();
        toolSource.DiscoverCount.Should().Be(0);
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
        var toolContext = request.ToolContext!;
        toolContext.Routing.ModelOverride.Should().Be("owner-fallback-model");
        toolContext.Routing.NyxIdRoutePreference.Should().Be("owner-route");
        toolContext.Routing.MaxToolRoundsOverride.Should().Be(5);
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
        var senderToolContext = senderRequest.ToolContext!;
        senderToolContext.Routing.ModelOverride.Should().Be("sender-model");
        senderToolContext.Routing.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/sender");
        senderToolContext.Routing.MaxToolRoundsOverride.Should().Be(7);
        senderToolContext.Credentials.NyxIdAccessToken.Should().Be("sender-token");
        senderToolContext.Credentials.NyxIdOrgToken.Should().Be("sender-token");
        senderToolContext.Credentials.SenderNyxIdAccessToken.Should().Be("sender-token");

        var ownerRequest = providerFactory.Requests[1];
        ownerRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        var ownerToolContext = ownerRequest.ToolContext!;
        ownerToolContext.Routing.ModelOverride.Should().Be("owner-model");
        ownerToolContext.Routing.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/owner");
        ownerToolContext.Routing.MaxToolRoundsOverride.Should().Be(5);
        ownerToolContext.Credentials.NyxIdAccessToken.Should().Be("owner-token");
        ownerToolContext.Credentials.NyxIdOrgToken.Should().Be("owner-token");
        ownerToolContext.SenderBinding.BindingId.Should().BeNull();
        ownerToolContext.Credentials.SenderNyxIdAccessToken.Should().BeNull();
    }

    [Fact]
    public async Task GenerateReplyAsync_RetriesWithOwnerPrefsAndNoToolsWhenToolSchemaIsRejected()
    {
        var providerFactory = new RecordingProviderFactory
        {
            FailureBeforeSuccess = new InvalidOperationException(
                "Invalid schema for function 'aevatar_observe_run': schema must have type 'object' and not have 'oneOf' at the top level (HTTP 400)."),
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
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources: [new SingleToolSource(new FixedResultTool("aevatar_observe_run", """{"status":"running"}"""))],
            preferencesStore: prefsStore);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-schema-fallback",
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
        providerFactory.Requests[0].Tools.Should().NotBeNull();
        providerFactory.Requests[0].ToolContext!.Routing.ModelOverride.Should().Be("sender-model");
        providerFactory.Requests[0].ToolContext!.Credentials.SenderNyxIdAccessToken.Should().Be("sender-token");

        providerFactory.Requests[1].Tools.Should().BeNull();
        var ownerToolContext = providerFactory.Requests[1].ToolContext!;
        ownerToolContext.Routing.ModelOverride.Should().Be("owner-model");
        ownerToolContext.Routing.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/owner");
        ownerToolContext.Credentials.NyxIdAccessToken.Should().Be("owner-token");
        ownerToolContext.SenderBinding.BindingId.Should().BeNull();
        ownerToolContext.Credentials.SenderNyxIdAccessToken.Should().BeNull();
    }

    [Fact]
    public async Task GenerateReplyAsync_RetriesWithOwnerNoToolsWhenBoundSenderHasNoLlmPrefs()
    {
        var providerFactory = new RecordingProviderFactory
        {
            FailureBeforeSuccess = new InvalidOperationException(
                "Invalid schema for function 'aevatar_observe_run': schema must have type 'object' and not have 'oneOf' at the top level (HTTP 400)."),
        };
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            toolSources: [new SingleToolSource(new FixedResultTool("aevatar_observe_run", """{"status":"running"}"""))]);

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-schema-fallback-no-prefs",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>(),
            Control("owner-model", "/api/v1/proxy/s/owner", 5, "owner-token"),
            ToolContext("bnd_sender"),
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        providerFactory.Requests.Should().HaveCount(2);
        providerFactory.Requests[0].Tools.Should().NotBeNull();
        providerFactory.Requests[0].ToolContext!.SenderBinding.BindingId.Should().Be("bnd_sender");
        providerFactory.Requests[0].ToolContext!.Routing.ModelOverride.Should().Be("owner-model");

        providerFactory.Requests[1].Tools.Should().BeNull();
        var ownerToolContext = providerFactory.Requests[1].ToolContext!;
        ownerToolContext.Routing.ModelOverride.Should().Be("owner-model");
        ownerToolContext.Routing.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/owner");
        ownerToolContext.Credentials.NyxIdAccessToken.Should().Be("owner-token");
        ownerToolContext.SenderBinding.BindingId.Should().BeNull();
    }

    [Fact]
    public async Task GenerateReplyAsync_WhenSenderRouteHasNoToken_ShouldKeepSenderBindingAndFallbackOnlyLlmRoute()
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

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        var requestToolContext = request.ToolContext!;
        requestToolContext.Routing.ModelOverride.Should().Be("sender-model");
        requestToolContext.Routing.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/owner");
        requestToolContext.Routing.MaxToolRoundsOverride.Should().Be(7);
        requestToolContext.Credentials.NyxIdAccessToken.Should().Be("owner-token");
        requestToolContext.Credentials.NyxIdOrgToken.Should().Be("owner-token");
        requestToolContext.SenderBinding.BindingId.Should().Be("bnd_sender");
        requestToolContext.Credentials.SenderNyxIdAccessToken.Should().BeNull();
    }

    [Fact]
    public async Task GenerateReplyAsync_WhenSenderHasNoRoutePreference_ShouldStillPromoteSenderTokenForTools()
    {
        var providerFactory = new RecordingProviderFactory();
        var prefsStore = new ScopedStubPreferencesStore
        {
            ByBinding =
            {
                ["bnd_sender"] = new NyxIdUserLlmPreferences("sender-model", string.Empty, MaxToolRounds: 0),
            },
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, preferencesStore: prefsStore);

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-sender-token-no-route-pref",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>(),
            Control("owner-model", "/api/v1/proxy/s/owner", 5, "owner-token", " sender-token "),
            ToolContext("bnd_sender"),
            streamingSink: null,
            CancellationToken.None);

        var toolContext = providerFactory.Requests.Should().ContainSingle().Subject.ToolContext!;
        toolContext.Routing.ModelOverride.Should().Be("sender-model");
        toolContext.Routing.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/owner");
        toolContext.Credentials.NyxIdAccessToken.Should().Be("sender-token");
        toolContext.Credentials.NyxIdOrgToken.Should().Be("sender-token");
        toolContext.Credentials.SenderNyxIdAccessToken.Should().Be("sender-token");
        toolContext.SenderBinding.BindingId.Should().Be("bnd_sender");
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
    // without crossing the route-applied + no-sender-token branch, which
    // now falls back only the LLM route while preserving sender binding.
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
        var effective = request.ToolContext!;

        effective.Routing.ModelOverride.Should().Be(expectedModel);
        effective.Routing.NyxIdRoutePreference.Should().Be(expectedRoute);
        effective.Routing.MaxToolRoundsOverride?.ToString().Should().Be(expectedRounds);

        if (bindingState == MatrixUnbound)
            prefsStore.Lookups.Should().BeEmpty(
                "no typed sender binding → generator must not consult the prefs store");
        else
            prefsStore.Lookups.Should().ContainSingle().Which.Should().Be("bnd_sender");
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

        public LLMProviderCapabilities Capabilities { get; init; } = LLMProviderCapabilities.TextOnly;

        public int FailuresBeforeSuccess { get; init; }

        public Exception? FailureBeforeSuccess { get; init; }

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
            if (FailureBeforeSuccess is not null && Requests.Count == 1)
                throw FailureBeforeSuccess;

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

    private sealed class RecordingLarkNyxClient(LarkMessageResourceDownloadResult downloadResult) : ILarkNyxClient
    {
        public List<(string Token, string MessageId, string ResourceKey, LarkMessageResourceKind Kind)> Downloads { get; } = [];

        public Task<LarkMessageResourceDownloadResult> DownloadMessageResourceAsync(
            string token,
            LarkMessageResourceDownloadRequest request,
            CancellationToken ct)
        {
            Downloads.Add((token, request.MessageId, request.ResourceKey, request.Kind));
            return Task.FromResult(downloadResult);
        }

        public Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ReplyToMessageAsync(string token, LarkReplyMessageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> CreateMessageReactionAsync(string token, LarkMessageReactionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ListMessageReactionsAsync(string token, LarkMessageReactionListRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> DeleteMessageReactionAsync(string token, LarkMessageReactionDeleteRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> BatchGetMessagesAsync(string token, LarkMessagesBatchGetRequest request, CancellationToken ct) =>
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

    private sealed class SequentialResponseProviderFactory(params string[] responses) : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "sequential";

        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            var response = Requests.Count <= responses.Length ? responses[Requests.Count - 1] : "ok";
            yield return new LLMStreamChunk { DeltaContent = response };
            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true };
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

    private sealed class ToolResultEchoingProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "tool-result-echoing";

        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            var toolResult = request.Messages.LastOrDefault(static message => message.Role == "tool")?.Content;
            if (toolResult is not null)
            {
                yield return new LLMStreamChunk { DeltaContent = toolResult };
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

    private sealed class UseSkillMountWorkflowProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "use-skill-mount-workflow";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var toolResult = request.Messages.LastOrDefault(static message => message.Role == "tool")?.Content;
            if (toolResult is not null)
            {
                yield return new LLMStreamChunk { DeltaContent = toolResult };
                yield return new LLMStreamChunk { IsLast = true };
                await Task.CompletedTask;
                yield break;
            }

            yield return ToolChunk(
                "call-use-skill",
                "use_skill",
                """{"skill":"demo-dinner-workflow-skill","mount_workflows":true}""");
            yield return new LLMStreamChunk { IsLast = true };
            await Task.CompletedTask;
        }
    }

    private sealed class ToolCallingPreambleProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "tool-calling-preamble";

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
                yield return new LLMStreamChunk { DeltaContent = "最终结果" };
                yield return new LLMStreamChunk { IsLast = true };
                await Task.CompletedTask;
                yield break;
            }

            yield return new LLMStreamChunk { DeltaContent = "开始执行 approval-flow，先查目录结构。" };
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

    private sealed class PrimarySkillRecoveryProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "primary-skill-recovery";

        public List<LLMRequest> Requests { get; } = [];
        public List<string> ObservedToolCalls { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            foreach (var call in request.Messages.SelectMany(static message => message.ToolCalls ?? []))
                ObservedToolCalls.Add(call.Name);

            yield return new LLMStreamChunk
            {
                DeltaContent = HasToolCall(request, "use_skill")
                    ? "summary report from loaded skill"
                    : "generic summary answer",
            };
            yield return new LLMStreamChunk { IsLast = true };
            await Task.CompletedTask;
        }
    }

    private sealed class BlockerRecoveryProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "blocker-recovery";

        public List<LLMRequest> Requests { get; } = [];
        public List<string> ObservedToolCalls { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            foreach (var call in request.Messages.SelectMany(static message => message.ToolCalls ?? []))
                ObservedToolCalls.Add(call.Name);

            if (!HasToolCall(request, "use_skill"))
            {
                yield return ToolChunk("call-use-summary", "use_skill", """{"skill":"project-summary","args":""}""");
                yield return new LLMStreamChunk { IsLast = true };
                await Task.CompletedTask;
                yield break;
            }

            if (!HasToolCall(request, "chrono_storage_query"))
            {
                yield return ToolChunk("call-storage", "chrono_storage_query", "{}");
                yield return new LLMStreamChunk { IsLast = true };
                await Task.CompletedTask;
                yield break;
            }

            var ornnSearchCount = CountToolCalls(request, "ornn_search_skills");
            if (ornnSearchCount < 2)
            {
                yield return new LLMStreamChunk { DeltaContent = "chrono storage backend unavailable: Invalid URI." };
                yield return new LLMStreamChunk { IsLast = true };
                await Task.CompletedTask;
                yield break;
            }

            yield return new LLMStreamChunk { DeltaContent = "recovered summary report" };
            yield return new LLMStreamChunk { IsLast = true };
            await Task.CompletedTask;
        }
    }

    private sealed class SlashInitialSearchRecoveryProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "slash-initial-search-recovery";

        public List<LLMRequest> Requests { get; } = [];
        public List<string> ObservedToolCalls { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            foreach (var call in request.Messages.SelectMany(static message => message.ToolCalls ?? []))
                ObservedToolCalls.Add(call.Name);

            yield return new LLMStreamChunk
            {
                DeltaContent = HasToolCall(request, "use_skill")
                    ? "goal command from loaded skill"
                    : HasToolCall(request, "ornn_search_skills")
                        ? "goal skill selected without loading"
                        : "generic answer",
            };
            yield return new LLMStreamChunk { IsLast = true };
            await Task.CompletedTask;
        }
    }

    private sealed class UnparseableSearchMatchRecoveryProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "unparseable-search-match-recovery";

        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            yield return new LLMStreamChunk
            {
                DeltaContent = "fallback after bounded recovery",
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

    private sealed class FixedResultTool(string name, string result) : IAgentTool
    {
        public string Name => name;

        public string Description => "Returns a fixed test result.";

        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    // A tool that self-declares an arbitrary set of generic capability tokens via
    // IAgentToolCapabilityDescriptor. Used to prove the channel-discovery exclusion keys off
    // the GENERIC capability marker (not the tool name): an excluded tool can have any name,
    // and a same-named tool WITHOUT the capability is not excluded.
    private sealed class CapabilityFixedResultTool(string name, string result, params string[] capabilities)
        : IAgentTool, IAgentToolCapabilityDescriptor
    {
        public string Name => name;

        public string Description => "Returns a fixed test result.";

        public string ParametersSchema => "{}";

        public IReadOnlyCollection<string> Capabilities { get; } = capabilities;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private sealed class RecordingScopeWorkflowCommandPort : IScopeWorkflowCommandPort
    {
        public List<ScopeWorkflowUpsertRequest> Requests { get; } = [];

        public Task<ScopeWorkflowUpsertResult> UpsertAsync(
            ScopeWorkflowUpsertRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ScopeWorkflowUpsertResult(
                request.ScopeId,
                request.WorkflowId,
                $"service-key-{request.WorkflowId}",
                $"revision-{request.WorkflowId}",
                "definition-prefix",
                $"actor-{request.WorkflowId}",
                $"deployment-{request.WorkflowId}",
                DateTimeOffset.UnixEpoch,
                [new ScopeWorkflowCommandAcceptedHandle("create_revision", "target-actor", "cmd-1", "corr-1")],
                $"/api/scopes/{request.ScopeId}/workflows/{request.WorkflowId}"));
        }
    }

    private sealed class CountingToolSource(IAgentTool tool) : IAgentToolSource
    {
        public int DiscoverCount { get; private set; }

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            DiscoverCount++;
            return Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
        }
    }

    private sealed class ApprovalRequiredTool : IAgentTool
    {
        public const string ToolName = "approval_required_tool";

        public int ExecuteCount { get; private set; }

        public string Name => ToolName;

        public string Description => "Requires approval.";

        public string ParametersSchema => "{}";

        public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

        public bool IsDestructive => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("""{"executed":true}""");
        }
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

    private static bool HasToolCall(LLMRequest request, string toolName) =>
        request.Messages.Any(message =>
            message.ToolCalls?.Any(call => string.Equals(call.Name, toolName, StringComparison.OrdinalIgnoreCase)) == true);

    private static int CountToolCalls(LLMRequest request, string toolName) =>
        request.Messages.Sum(message =>
            message.ToolCalls?.Count(call => string.Equals(call.Name, toolName, StringComparison.OrdinalIgnoreCase)) ?? 0);

    private static LLMStreamChunk ToolChunk(string id, string name, string argumentsJson) =>
        new()
        {
            DeltaToolCall = new ToolCall
            {
                Id = id,
                Name = name,
                ArgumentsJson = argumentsJson,
            },
        };

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
