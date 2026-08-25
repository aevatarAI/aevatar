using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using ApplicationFileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;
using LlmChatFileRef = Aevatar.AI.Abstractions.LLMProviders.ChatFileRef;
using LlmChatFileSourceKind = Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

[Collection(ChannelRuntimeTestCollections.NyxIdInventoryRequestContext)]
public sealed class ConversationReplyGeneratorTests
{
    private static readonly IBuiltInPromptFloorProvider BuiltInPromptFloorProvider =
        new StubBuiltInPromptFloorProvider("built-in prompt floor");

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

    // A channel-relay tool context carries the typed channel identity (platform/sender/message) that
    // survives metadata stripping and every LLM round — the signal the human-only tool gate keys on.
    private static AgentToolExecutionContext RelayToolContext(string senderBindingId, string messageId = "msg-relay") =>
        AgentToolExecutionContext.Empty with
        {
            SenderBinding = new AgentToolSenderBindingContext(senderBindingId),
            Channel = new AgentToolChannelContext("lark", "ou_user_1", "scope-1", messageId, null),
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

    private static byte[] BuildSimplePdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(text, 12, new PdfPoint(50, 750), font);
        return builder.Build();
    }

    [Fact]
    public async Task GenerateReplyAsync_WithPriorConversationHistory_BuildsSecondTurnRequestWithPreviousUserAndAssistant()
    {
        var providerFactory = new SequentialResponseProviderFactory("first assistant", "second assistant", "isolated assistant");
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider);

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
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider);

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
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider);

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
        var fileArtifacts = new RecordingWorkflowFileArtifactPort();
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: lark,
            fileIngressPort: fileArtifacts,
            fileArtifactReadPort: fileArtifacts);

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
        imagePart.FileRef.Should().BeNull();
        userMessage.ContentParts!.Should().NotContain(part =>
            part.Text != null &&
            part.Text.Contains("Attachment visibility warning", StringComparison.Ordinal));
        lark.Downloads.Should().ContainSingle().Which.Should().Be((
            "user-token",
            "om_current",
            "img_current",
            LarkMessageResourceKind.Image));
        fileArtifacts.IngressRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task GenerateReplyAsync_WithLarkFileCardImageAttachment_BuildsImageContentPart()
    {
        var imageBytes = new byte[] { 9, 10, 11, 12 };
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, imageBytes, "image/jpeg", "IMG_20260708_091630.jpg"));
        var fileArtifacts = new RecordingWorkflowFileArtifactPort();
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: lark,
            fileIngressPort: fileArtifacts,
            fileArtifactReadPort: fileArtifacts);
        var activity = CreateLarkActivity(
            "msg-file-card-image",
            "describe it",
            "om_file_card_image",
            token: "user-token");
        activity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = "file_img_key",
            Kind = AttachmentKind.File,
            ContentType = "image/jpeg",
            Name = "IMG_20260708_091630.jpg",
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
        userMessage.ContentParts!.Should().Contain(part =>
            part.Kind == ContentPartKind.Text &&
            part.Text == "describe it");
        var imagePart = userMessage.ContentParts!.Single(part => part.Kind == ContentPartKind.Image);
        imagePart.DataBase64.Should().Be(Convert.ToBase64String(imageBytes));
        imagePart.MediaType.Should().Be("image/jpeg");
        imagePart.Name.Should().Be("IMG_20260708_091630.jpg");
        imagePart.FileRef.Should().BeNull();
        userMessage.ContentParts!.Should().NotContain(part =>
            part.Text != null &&
            part.Text.Contains("Attachment visibility warning", StringComparison.Ordinal));
        lark.Downloads.Should().ContainSingle().Which.Should().Be((
            "user-token",
            "om_file_card_image",
            "file_img_key",
            LarkMessageResourceKind.File));
        fileArtifacts.IngressRequests.Should().ContainSingle().Which.Should().Match<FileArtifactIngressRequest>(request =>
            request.Content.ToArray().SequenceEqual(imageBytes) &&
            request.SourceKind == FileArtifactSourceKind.ChatInput &&
            request.SourceMessageId == "om_file_card_image" &&
            request.SourceResourceKey == "file_img_key" &&
            request.FileName == "IMG_20260708_091630.jpg" &&
            request.MediaType == "image/jpeg");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithCurrentLarkImageAttachment_ShouldUseInboundProviderSlugClient()
    {
        var imageBytes = new byte[] { 5, 6, 7, 8 };
        var defaultLark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(false, [], Detail: "wrong-client"));
        var inboundLark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, imageBytes, "image/png", "photo.png"));
        var fileArtifacts = new RecordingWorkflowFileArtifactPort();
        var outboundFactory = Substitute.For<ILarkOutboundClientFactory>();
        outboundFactory.ResolveNyxClient("api-lark-bot-4").Returns(inboundLark);
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: defaultLark,
            fileIngressPort: fileArtifacts,
            fileArtifactReadPort: fileArtifacts,
            larkOutboundClientFactory: outboundFactory);
        var activity = CreateLarkImageActivity(
            "msg-image-current-inbound-provider",
            "describe it",
            "om_current",
            "img_current",
            token: "user-token");
        activity.TransportExtras!.NyxProviderSlug = " api-lark-bot-4 ";

        await generator.GenerateReplyAsync(
            activity,
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        var userMessage = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.Last(message => message.Role == "user");
        var imagePart = userMessage.ContentParts.Should().NotBeNull().And.Subject
            .Single(part => part.Kind == ContentPartKind.Image);
        imagePart.DataBase64.Should().Be(Convert.ToBase64String(imageBytes));
        imagePart.FileRef.Should().BeNull();
        outboundFactory.Received(1).ResolveNyxClient("api-lark-bot-4");
        inboundLark.Downloads.Should().ContainSingle().Which.Should().Be((
            "user-token",
            "om_current",
            "img_current",
            LarkMessageResourceKind.Image));
        defaultLark.Downloads.Should().BeEmpty();
        fileArtifacts.IngressRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task GenerateReplyAsync_WithOversizedLarkImageAttachment_ContinuesWithAttachmentVisibilityWarning()
    {
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, [1], "image/png", "large.png"));
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: lark,
            fileIngressPort: new RecordingWorkflowFileArtifactPort(),
            fileArtifactReadPort: new RecordingWorkflowFileArtifactPort());
        var activity = CreateLarkImageActivity(
            "msg-image-large",
            "describe it",
            "om_large",
            "img_large",
            token: "user-token");
        activity.Content.Attachments[0].SizeBytes = 10 * 1024 * 1024 + 1;
        var reply = await generator.GenerateReplyAsync(
            activity,
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        providerFactory.Requests.Should().ContainSingle();
        var request = providerFactory.Requests[0];
        request.Messages.Single(message => message.Role == "system").Content.Should()
            .Contain("Attachment visibility warning")
            .And.Contain("one or more attachments could not be converted to LLM input");
        request.Messages.Single(message => message.Role == "user").ContentParts.Should()
            .ContainSingle(part => part.Kind == ContentPartKind.Text && part.Text == "describe it");
        lark.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateReplyAsync_WithUnsupportedLarkImageDownload_ContinuesWithAttachmentVisibilityWarning()
    {
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, [1, 2, 3], "image/tiff", "scan.tiff"));
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: lark,
            fileIngressPort: new RecordingWorkflowFileArtifactPort(),
            fileArtifactReadPort: new RecordingWorkflowFileArtifactPort());

        var reply = await generator.GenerateReplyAsync(
            CreateLarkImageActivity(
                "msg-image-unsupported",
                "describe it",
                "om_unsupported",
                "img_unsupported",
                token: "user-token"),
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        providerFactory.Requests.Should().ContainSingle();
        providerFactory.Requests[0].Messages.Single(message => message.Role == "system").Content.Should()
            .Contain("Attachment visibility warning")
            .And.Contain("one or more attachments could not be converted to LLM input");
        lark.Downloads.Should().ContainSingle();
    }

    [Fact]
    public async Task GenerateReplyAsync_WhenFileIngressRejectsAttachment_ContinuesWithAttachmentVisibilityWarning()
    {
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, [1, 2, 3], "image/png", "large.png"));
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: lark,
            fileIngressPort: new RejectingWorkflowFileIngressPort(
                new InvalidOperationException("ingress policy rejected attachment")),
            fileArtifactReadPort: new RecordingWorkflowFileArtifactPort());

        var reply = await generator.GenerateReplyAsync(
            CreateLarkImageActivity(
                "msg-image-ingress-large",
                "describe it",
                "om_ingress_large",
                "img_ingress_large",
                token: "user-token"),
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        providerFactory.Requests.Should().ContainSingle();
        providerFactory.Requests[0].Messages.Single(message => message.Role == "system").Content.Should()
            .Contain("Attachment visibility warning")
            .And.Contain("one or more attachments could not be converted to LLM input");
        lark.Downloads.Should().ContainSingle();
    }

    [Fact]
    public async Task BuildStepPlanAsync_WithRecentLarkImageAttachment_PersistsFileRefWithoutDataBase64()
    {
        var imageBytes = new byte[] { 9, 8, 7 };
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, imageBytes, "image/jpeg", "recent.jpg"));
        var fileArtifacts = new RecordingWorkflowFileArtifactPort();
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: lark,
            fileIngressPort: fileArtifacts,
            fileArtifactReadPort: fileArtifacts);
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
        imagePart.DataBase64.Should().BeNull();
        imagePart.FileRef.Should().NotBeNull();
        imagePart.FileRef!.ArtifactId.Should().Be("workflow-file://wf-file-1");
        imagePart.FileRef.SourceKind.Should().Be(LlmChatFileSourceKind.ChatInput);
        imagePart.FileRef.SourceMessageId.Should().Be("om_recent");
        imagePart.FileRef.SourceResourceKey.Should().Be("img_recent");
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
        fileArtifacts.IngressRequests.Should().ContainSingle().Which.Should().Match<FileArtifactIngressRequest>(
            request => request.Content.ToArray().SequenceEqual(imageBytes) &&
                       request.SourceKind == FileArtifactSourceKind.ChatInput &&
                       request.SourceMessageId == "om_recent" &&
                       request.SourceResourceKey == "img_recent" &&
                       request.FileName == "recent.jpg" &&
                       request.MediaType == "image/jpeg");
    }

    [Fact]
    public async Task BuildStepPlanAsync_WithTypedLarkCallbackAttachmentWithoutRawPayload_ExposesFileRefToTools()
    {
        var callbackBody = """
            {
              "message_id": "msg-lark-typed-image-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_group_1", "type": "group" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": {
                "type": "image",
                "text": "/invoice-approval",
                "attachments": [
                  {
                    "content_type": "image",
                    "url": "https://open.larksuite.com/open-apis/im/v1/messages/om_typed_image_1/resources/img_typed_1?type=image",
                    "platform_message_id": "om_typed_image_1",
                    "image_key": "img_typed_1",
                    "filename": "invoice.png",
                    "mime_type": "image/png",
                    "size_bytes": 3
                  }
                ]
              }
            }
            """;
        var parsed = new NyxIdRelayTransport().Parse(Encoding.UTF8.GetBytes(callbackBody));
        parsed.Success.Should().BeTrue();

        var imageBytes = new byte[] { 9, 8, 7 };
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, imageBytes, "image/png", "invoice.png"));
        var fileArtifacts = new RecordingWorkflowFileArtifactPort();
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: lark,
            fileIngressPort: fileArtifacts,
            fileArtifactReadPort: fileArtifacts);

        var plan = await generator.BuildStepPlanAsync(
            parsed.Activity!,
            new Dictionary<string, string>(),
            llmControl: null,
            toolContext: AgentToolExecutionContext.Empty,
            priorHistory: null,
            new ChatAttachmentInputContext([], "user-token"),
            forceDisableTools: false,
            CancellationToken.None);

        var fileRef = plan.ToolContext.InputFileRefs.Should().ContainSingle().Subject;
        fileRef.FileId.Should().Be("wf-file-1");
        fileRef.ArtifactId.Should().Be("workflow-file://wf-file-1");
        fileRef.SourceKind.Should().Be(Aevatar.AI.Abstractions.ChatFileSourceKind.ChatInput);
        fileRef.SourceMessageId.Should().Be("om_typed_image_1");
        fileRef.SourceResourceKey.Should().Be("img_typed_1");
        fileRef.FileName.Should().Be("invoice.png");
        fileRef.MediaType.Should().Be("image/png");
        lark.Downloads.Should().ContainSingle().Which.Should().Be((
            "user-token",
            "om_typed_image_1",
            "img_typed_1",
            LarkMessageResourceKind.Image));
    }

    [Fact]
    public async Task BuildStepPlanAsync_WithRecentLarkPdfAttachment_PersistsFileRefWithoutExtractedText()
    {
        var pdfBytes = BuildSimplePdf("confidential extracted document text");
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, pdfBytes, "application/pdf", "recent.pdf"));
        var fileArtifacts = new RecordingWorkflowFileArtifactPort();
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = LLMProviderCapabilities.TextOnly,
        };
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: lark,
            fileIngressPort: fileArtifacts,
            fileArtifactReadPort: fileArtifacts);
        var recentActivity = CreateLarkActivity(
            "msg-pdf-recent",
            "earlier pdf",
            "om_recent_pdf",
            token: null);
        recentActivity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = "pdf_recent",
            Kind = AttachmentKind.File,
            ContentType = "application/pdf",
            Name = "recent.pdf",
            SizeBytes = pdfBytes.Length,
        });
        var currentActivity = new ChatActivity
        {
            Id = "msg-follow-up-pdf",
            ChannelId = ChannelId.From("lark"),
            Conversation = new ConversationReference { CanonicalKey = "lark:scope-a:chat-1" },
            Content = new MessageContent { Text = "what was in the pdf?" },
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
        var documentPart = userMessage.ContentParts.Should().NotBeNull().And.Subject
            .Single(part => part.Kind == ContentPartKind.Text && part.FileRef is not null);
        documentPart.Text.Should().BeNull();
        documentPart.FileRef!.ArtifactId.Should().Be("workflow-file://wf-file-1");
        documentPart.FileRef.SourceKind.Should().Be(LlmChatFileSourceKind.ChatInput);
        documentPart.FileRef.SourceMessageId.Should().Be("om_recent_pdf");
        documentPart.FileRef.SourceResourceKey.Should().Be("pdf_recent");
        documentPart.MediaType.Should().Be("application/pdf");
        documentPart.Name.Should().Be("recent.pdf");
        var systemPrompt = plan.InitialMessages.Single(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("Current input files");
        systemPrompt.Should().Contain("input_parts[].file_ref");
        systemPrompt.Should().Contain("workflow-file://wf-file-1");
        systemPrompt.Should().Contain("\"source_kind\": 1");
        systemPrompt.Should().NotContain("source_message_id");
        systemPrompt.Should().NotContain("source_resource_key");
        systemPrompt.Should().NotContain("recent.pdf");
        systemPrompt.Should().NotContain("attachment_ref");
        userMessage.ContentParts!.Should().NotContain(part =>
            part.Text != null &&
            part.Text.Contains("confidential extracted document text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildStepPlanAsync_WithRecentLarkImageAttachment_ShouldUseAttachmentActivityProviderSlugClient()
    {
        var imageBytes = new byte[] { 3, 4, 5 };
        var defaultLark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(false, [], Detail: "wrong-client"));
        var inboundLark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, imageBytes, "image/jpeg", "recent.jpg"));
        var fileArtifacts = new RecordingWorkflowFileArtifactPort();
        var outboundFactory = Substitute.For<ILarkOutboundClientFactory>();
        outboundFactory.ResolveNyxClient("api-lark-bot-4").Returns(inboundLark);
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: defaultLark,
            fileIngressPort: fileArtifacts,
            fileArtifactReadPort: fileArtifacts,
            larkOutboundClientFactory: outboundFactory);
        var recentActivity = CreateLarkImageActivity(
            "msg-image-recent-provider",
            "earlier image",
            "om_recent",
            "img_recent",
            token: null);
        recentActivity.TransportExtras!.NyxProviderSlug = " api-lark-bot-4 ";
        var currentActivity = new ChatActivity
        {
            Id = "msg-follow-up-provider",
            ChannelId = ChannelId.From("lark"),
            Conversation = new ConversationReference { CanonicalKey = "lark:scope-a:chat-1" },
            Content = new MessageContent { Text = "/project-summary" },
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
        imagePart.DataBase64.Should().BeNull();
        imagePart.FileRef.Should().NotBeNull();
        imagePart.FileRef!.ArtifactId.Should().Be("workflow-file://wf-file-1");
        imagePart.FileRef.SourceMessageId.Should().Be("om_recent");
        imagePart.FileRef.SourceResourceKey.Should().Be("img_recent");
        outboundFactory.Received(1).ResolveNyxClient("api-lark-bot-4");
        inboundLark.Downloads.Should().ContainSingle().Which.Should().Be((
            "recent-token",
            "om_recent",
            "img_recent",
            LarkMessageResourceKind.Image));
        defaultLark.Downloads.Should().BeEmpty();
        fileArtifacts.IngressRequests.Should().ContainSingle().Which.Should().Match<FileArtifactIngressRequest>(
            request => request.Content.ToArray().SequenceEqual(imageBytes) &&
                       request.SourceMessageId == "om_recent" &&
                       request.SourceResourceKey == "img_recent");
    }

    [Fact]
    public async Task BuildStepPlanAsync_InChannelRelayTurn_GatesOutHumanSessionTools()
    {
        // Issue #2580 Item 2: in a channel-relay turn the effective credential is bot-class, so a
        // tool declaring RequiresHumanSession is filtered out (never offered), while a delegated tool
        // stays. A console/studio (non-channel) turn keeps the full set.
        var providerFactory = new RecordingProviderFactory { Capabilities = MultimodalCapabilities };
        var toolSource = new StubToolSource(
            new HumanSessionStubTool("human_only_tool"),
            new StubTool("delegated_tool"));
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            toolSources: [toolSource]);
        var activity = CreateLarkActivity("msg-gate", "hi", "om_gate", token: "runtime-token");
        var channelMetadata = new Dictionary<string, string>
        {
            [ChannelMetadataKeys.Platform] = "lark",
            [ChannelMetadataKeys.SenderId] = "ou_user_1",
            [ChannelMetadataKeys.MessageId] = "msg-gate",
        };

        var channelPlan = await generator.BuildStepPlanAsync(
            activity, channelMetadata, Control(token: "runtime-token"), RelayToolContext("bnd-1", "msg-gate"),
            priorHistory: null, attachmentContext: null, forceDisableTools: false, CancellationToken.None);
        var channelToolNames = OfferedToolNames(channelPlan);

        channelToolNames.Should().Contain("delegated_tool");
        channelToolNames.Should().NotContain("human_only_tool",
            "the human-session tool would be rejected by the broker in a relay turn, so it must not be offered");

        // A non-channel (console/studio) human-session turn (no typed channel context) keeps the full set.
        var consolePlan = await generator.BuildStepPlanAsync(
            activity, new Dictionary<string, string>(), Control(token: "runtime-token"), ToolContext("bnd-1"),
            priorHistory: null, attachmentContext: null, forceDisableTools: false, CancellationToken.None);
        var consoleToolNames = OfferedToolNames(consolePlan);

        consoleToolNames.Should().Contain("delegated_tool");
        consoleToolNames.Should().Contain("human_only_tool");
    }

    [Fact]
    public async Task BuildStepPlanAsync_InLaterRelayRoundWithStrippedMetadata_StillGatesHumanSessionTools()
    {
        // Issue #2580 Item 2 regression (PR #2583 review): from the second LLM round the per-step
        // metadata no longer carries channel.platform / sender_id / message_id (owned control keys
        // stripped by AgentToolExecutionContextMapper.StripOwnedControlKeys), so a metadata-based gate
        // would re-offer the human-only tools. With the typed channel context still present, the gate
        // must stay on — otherwise a relay turn could call a relay-safe tool, advance a round, and
        // regain nyxid_api_keys / nyxid_services under a bot-class token.
        var providerFactory = new RecordingProviderFactory { Capabilities = MultimodalCapabilities };
        var toolSource = new StubToolSource(
            new HumanSessionStubTool("human_only_tool"),
            new StubTool("delegated_tool"));
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            toolSources: [toolSource]);
        var activity = CreateLarkActivity("msg-round2", "next round", "om_round2", token: "runtime-token");

        // Round 2+ shape: channel.* metadata keys already stripped, typed channel context retained.
        var plan = await generator.BuildStepPlanAsync(
            activity, new Dictionary<string, string>(), Control(token: "runtime-token"), RelayToolContext("bnd-1", "msg-round2"),
            priorHistory: null, attachmentContext: null, forceDisableTools: false, CancellationToken.None);
        var toolNames = OfferedToolNames(plan);

        toolNames.Should().Contain("delegated_tool");
        toolNames.Should().NotContain("human_only_tool",
            "the durable typed channel context must keep the human-only gate on after the channel metadata is stripped");
    }

    [Fact]
    public async Task BuildStepPlanAsync_InNyxIdChatTurn_UsesPinnedSourceAndAllowsHumanSessionReads()
    {
        var registeredSource = new StubToolSource(
            new StubTool("newly_registered_tool"));
        var pinnedSource = new StubToolSource(
            new HumanSessionStubTool("nyxid_services"),
            new HumanSessionStubTool("nyxid_api_keys"),
            new StubTool("nyxid_require_service"));
        var inputSource = new StubToolSource(new StubTool("ask_user"));
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            new RecordingProviderFactory { Capabilities = MultimodalCapabilities },
            BuiltInPromptFloorProvider,
            toolSources: [registeredSource],
            nyxIdChatToolSources: [pinnedSource, inputSource]);
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "runtime-token",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                null,
                "scope-alpha",
                null,
                null),
        };

        var plan = await generator.BuildStepPlanAsync(
            new ChatActivity
            {
                Id = "turn-alpha",
                Conversation = new ConversationReference { CanonicalKey = "nyxid-chat-alpha" },
                Content = new MessageContent { Text = "我要查看 aws 账单" },
            },
            new Dictionary<string, string>(),
            Control(token: "runtime-token"),
            toolContext,
            priorHistory: null,
            attachmentContext: null,
            forceDisableTools: false,
            CancellationToken.None);

        var toolNames = OfferedToolNames(plan);
        toolNames.Should().BeEquivalentTo(
            "nyxid_services",
            "nyxid_api_keys",
            "nyxid_require_service",
            "ask_user");
        toolNames.Should().NotContain("newly_registered_tool");
    }

    [Fact]
    public async Task BuildStepPlanAsync_InNyxIdChatTurnWithoutHumanSession_HidesPinnedReads()
    {
        var pinnedSource = new StubToolSource(
            new HumanSessionStubTool("nyxid_status"),
            new StubTool("nyxid_require_service"));
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            new RecordingProviderFactory { Capabilities = MultimodalCapabilities },
            BuiltInPromptFloorProvider,
            toolSources: [new StubToolSource(new StubTool("newly_registered_tool"))],
            nyxIdChatToolSources: [pinnedSource]);
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                null,
                "scope-alpha",
                null,
                null),
        };

        var plan = await generator.BuildStepPlanAsync(
            new ChatActivity
            {
                Id = "turn-no-human-session",
                Conversation = new ConversationReference { CanonicalKey = "nyxid-chat-alpha" },
                Content = new MessageContent { Text = "查看状态" },
            },
            new Dictionary<string, string>(),
            Control(),
            toolContext,
            priorHistory: null,
            attachmentContext: null,
            forceDisableTools: false,
            CancellationToken.None);

        OfferedToolNames(plan).Should().ContainSingle().Which.Should().Be("nyxid_require_service");
    }

    [Fact]
    public async Task BuildStepPlanAsync_InUnprofiledNyxIdChatTurnWithHumanSession_ShouldExposeExactPinnedProductionCatalog()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        using var apiClient = new NyxIdApiClient(options, new HttpClient());
        var localSkillCatalog = new LocalSkillCatalog();
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            new RecordingProviderFactory { Capabilities = MultimodalCapabilities },
            BuiltInPromptFloorProvider,
            localSkillCatalog: localSkillCatalog,
            nyxIdChatToolSources:
            [
                new NyxIdAssistantToolSource(options, apiClient),
                new StubToolSource(new StubTool("ask_user")),
                new SkillsAgentToolSource(
                    new SkillsOptions(),
                    new SkillDiscovery(),
                    localSkillCatalog),
            ]);
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "source-readable-bearer",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                null,
                "scope-alpha",
                null,
                null),
        };

        var plan = await generator.BuildStepPlanAsync(
            new ChatActivity
            {
                Id = "turn-production-human",
                Conversation = new ConversationReference { CanonicalKey = "nyxid-chat-production" },
                Content = new MessageContent { Text = "查看 NyxID 状态" },
            },
            new Dictionary<string, string>(),
            Control(token: "source-readable-bearer"),
            toolContext,
            priorHistory: null,
            attachmentContext: null,
            forceDisableTools: false,
            CancellationToken.None);

        OfferedToolNames(plan).Should().BeEquivalentTo(
            "ask_user",
            "nyxid_account",
            "nyxid_api_keys",
            "nyxid_approvals",
            "nyxid_catalog",
            "nyxid_developer_apps",
            "nyxid_durable_grants",
            "nyxid_endpoints",
            "nyxid_external_keys",
            "nyxid_llm_status",
            "nyxid_mfa",
            "nyxid_node_credentials",
            "nyxid_nodes",
            "nyxid_notifications",
            "nyxid_oauth_bindings",
            "nyxid_orgs",
            "nyxid_profile",
            "nyxid_providers",
            "nyxid_require_service",
            "nyxid_request_key_create",
            "nyxid_request_key_rotate",
            "nyxid_service_pools",
            "nyxid_services",
            "nyxid_sessions",
            "nyxid_status",
            "use_skill");
    }

    [Fact]
    public async Task BuildStepPlanAsync_InNyxIdChatTurnWithGenericNyxIdSource_ShouldExcludeChannelEventMutation()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        using var apiClient = new NyxIdApiClient(options, new HttpClient());
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            new RecordingProviderFactory { Capabilities = MultimodalCapabilities },
            BuiltInPromptFloorProvider,
            nyxIdChatToolSources: [new NyxIdAgentToolSource(options, apiClient)]);
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "source-readable-bearer",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                null,
                "scope-alpha",
                null,
                null),
        };

        var plan = await generator.BuildStepPlanAsync(
            new ChatActivity
            {
                Id = "turn-generic-source",
                Conversation = new ConversationReference { CanonicalKey = "nyxid-chat-generic-source" },
                Content = new MessageContent { Text = "push a channel event" },
            },
            new Dictionary<string, string>(),
            Control(token: "source-readable-bearer"),
            toolContext,
            priorHistory: null,
            attachmentContext: null,
            forceDisableTools: false,
            CancellationToken.None);

        OfferedToolNames(plan).Should().Contain("nyxid_catalog");
        OfferedToolNames(plan).Should().NotContain("nyxid_channel_events");
    }

    [Fact]
    public async Task BuildStepPlanAsync_InUnprofiledNyxIdChatTurnWithoutHumanSession_ShouldExposeExactPinnedNonHumanCatalog()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        using var apiClient = new NyxIdApiClient(options, new HttpClient());
        var localSkillCatalog = new LocalSkillCatalog();
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            new RecordingProviderFactory { Capabilities = MultimodalCapabilities },
            BuiltInPromptFloorProvider,
            localSkillCatalog: localSkillCatalog,
            nyxIdChatToolSources:
            [
                new NyxIdAssistantToolSource(options, apiClient),
                new StubToolSource(new StubTool("ask_user")),
                new SkillsAgentToolSource(
                    new SkillsOptions(),
                    new SkillDiscovery(),
                    localSkillCatalog),
            ]);
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                null,
                "scope-alpha",
                null,
                null),
        };

        var plan = await generator.BuildStepPlanAsync(
            new ChatActivity
            {
                Id = "turn-production-non-human",
                Conversation = new ConversationReference { CanonicalKey = "nyxid-chat-production" },
                Content = new MessageContent { Text = "查看 NyxID 状态" },
            },
            new Dictionary<string, string>(),
            Control(),
            toolContext,
            priorHistory: null,
            attachmentContext: null,
            forceDisableTools: false,
            CancellationToken.None);

        OfferedToolNames(plan).Should().BeEquivalentTo(
            "ask_user",
            "nyxid_catalog",
            "nyxid_llm_status",
            "nyxid_require_service",
            "use_skill");
    }

    [Fact]
    public async Task BuildStepPlanAsync_InUnprofiledNyxIdChatTurnWithDuplicatePinnedToolNames_ShouldFailClosed()
    {
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            new RecordingProviderFactory { Capabilities = MultimodalCapabilities },
            BuiltInPromptFloorProvider,
            nyxIdChatToolSources:
            [
                new StubToolSource(new StubTool("duplicate_tool")),
                new StubToolSource(new StubTool("DUPLICATE_TOOL")),
            ]);
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                null,
                "scope-alpha",
                null,
                null),
        };

        Func<Task> act = async () => await generator.BuildStepPlanAsync(
            new ChatActivity
            {
                Id = "turn-duplicate",
                Conversation = new ConversationReference { CanonicalKey = "nyxid-chat-duplicate" },
                Content = new MessageContent { Text = "test" },
            },
            new Dictionary<string, string>(),
            Control(),
            toolContext,
            priorHistory: null,
            attachmentContext: null,
            forceDisableTools: false,
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AgentToolDiscoveryException>();
        exception.Which.Failure.Code.Should().Be(AgentToolDiscoveryFailureCode.ToolNameCollision);
        exception.Which.Failure.ToolName.Should().Be("duplicate_tool");
    }

    [Fact]
    public async Task BuildStepPlanAsync_InNyxIdChatTurnWithUnknownRouteToolSet_ShouldExposeNoTools()
    {
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            new RecordingProviderFactory { Capabilities = MultimodalCapabilities },
            BuiltInPromptFloorProvider,
            localSkillCatalog: new LocalSkillCatalog(),
            nyxIdChatToolSources: []);
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                null,
                "scope-alpha",
                null,
                null),
        };

        var plan = await generator.BuildStepPlanAsync(
            new ChatActivity
            {
                Id = "turn-unknown-route-set",
                Conversation = new ConversationReference { CanonicalKey = "nyxid-chat-unknown-route" },
                Content = new MessageContent { Text = "test" },
            },
            new Dictionary<string, string>(),
            Control(),
            toolContext,
            priorHistory: null,
            attachmentContext: null,
            forceDisableTools: false,
            CancellationToken.None);

        OfferedToolNames(plan).Should().BeEmpty();
    }

    [Fact]
    public async Task BuildStepPlanAsync_InNyxIdChatTurn_HidesRawProxyOnlyOnThatSurface()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var rawProxy = new NyxIdProxyTool(new NyxIdApiClient(options, new HttpClient()));
        var requireService = new StubTool("nyxid_require_service");
        var typedInventory = new StubTool("nyxid_service_inventory");
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            new RecordingProviderFactory { Capabilities = MultimodalCapabilities },
            BuiltInPromptFloorProvider,
            toolSources: [new StubToolSource(rawProxy)],
            nyxIdChatToolSources: [new StubToolSource(rawProxy, requireService, typedInventory)]);
        var nyxIdChatContext = AgentToolExecutionContext.Empty with
        {
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                null,
                "scope-alpha",
                null,
                null),
        };

        var nyxIdChatPlan = await generator.BuildStepPlanAsync(
            new ChatActivity
            {
                Id = "turn-nyxid-chat",
                Conversation = new ConversationReference { CanonicalKey = "nyxid-chat-alpha" },
                Content = new MessageContent { Text = "我要连接 github" },
            },
            new Dictionary<string, string>(),
            Control(token: "runtime-token"),
            nyxIdChatContext,
            priorHistory: null,
            attachmentContext: null,
            forceDisableTools: false,
            CancellationToken.None);

        OfferedToolNames(nyxIdChatPlan).Should()
            .BeEquivalentTo("nyxid_require_service", "nyxid_service_inventory");

        var larkPlan = await generator.BuildStepPlanAsync(
            CreateLarkActivity("turn-lark", "读取 github", "om_lark", token: "runtime-token"),
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.SenderId] = "ou_user_1",
                [ChannelMetadataKeys.MessageId] = "turn-lark",
            },
            Control(token: "runtime-token"),
            RelayToolContext("bnd-user-1", "turn-lark"),
            priorHistory: null,
            attachmentContext: null,
            forceDisableTools: false,
            CancellationToken.None);

        OfferedToolNames(larkPlan).Should().Contain("nyxid_proxy");
    }

    [Fact]
    public async Task BuildStepPlanAsync_WithTurnCatalog_ShouldApplyProfileToolsAndPrompt()
    {
        var allowed = new StubTool("nyxid_require_service");
        var denied = new StubTool("nyxid_catalog");
        var generator = (IAgentRunStepConversationReplyGenerator)new NyxIdConversationReplyGenerator(
            new RecordingProviderFactory { Capabilities = MultimodalCapabilities },
            BuiltInPromptFloorProvider,
            toolSources: [new StubToolSource(allowed, denied)]);
        var catalog = new AgentTurnToolCatalog(
            [allowed.Name],
            new ProfileRoutingPromptLayer(
                "profile-route-sentinel",
                new ProfileRoutingPromptProvenance("profile-alpha"),
                new PromptLayerBounds(1024, 256)),
            new SelectedSkillPromptLayer(
                "selected-skill-sentinel",
                new SelectedSkillPromptProvenance("skill-alpha"),
                new PromptLayerBounds(1024, 256)),
            selectedIntentId: "service_connect",
            candidateIntentId: "service_connect",
            exactTools: [allowed]);

        var plan = await generator.BuildStepPlanAsync(
            new ChatActivity
            {
                Id = "turn-alpha",
                Conversation = new ConversationReference { CanonicalKey = "nyxid-chat-alpha" },
                Content = new MessageContent { Text = "我要连一下 github" },
            },
            new Dictionary<string, string>(),
            Control(token: "runtime-token"),
            AgentToolExecutionContext.Empty,
            priorHistory: null,
            attachmentContext: null,
            forceDisableTools: false,
            ct: CancellationToken.None,
            turnCatalog: catalog);

        var request = plan.StepExecutor.BuildLlmStepRequest(
            plan.InitialMessages,
            "turn-alpha",
            plan.Metadata,
            plan.ToolContext,
            plan.LlmControl,
            round: 0,
            finalNoTools: false);
        request.Tools.Should().ContainSingle().Which.Should().BeSameAs(allowed);
        request.Messages.Single(message => message.Role == "system").Content.Should()
            .Contain("profile-route-sentinel")
            .And.Contain("selected-skill-sentinel");
    }

    [Fact]
    public async Task BuildStepPlanAsync_ForBoundLarkRelayTurn_DiscoversRequestToolsWithSenderCredentialContext()
    {
        var requestScopedSource = new RequestScopedToolSource(
            new FixedResultTool("nyxid_service_inventory", """{"instances":[]}"""));
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            new RecordingProviderFactory(),
            BuiltInPromptFloorProvider,
            toolSources: [requestScopedSource]);

        var plan = await generator.BuildStepPlanAsync(
            CreateLarkActivity(
                "msg-bound-step-inventory",
                "我在 NyxID 上连接了哪些服务",
                "om_bound_step_inventory",
                token: "sender-token"),
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.SenderId] = "ou_user_1",
                [ChannelMetadataKeys.MessageId] = "msg-bound-step-inventory",
            },
            Control("sender-model", "sender-route", 4, token: "owner-token", senderToken: "sender-token"),
            RelayToolContext("bnd-user-1", "msg-bound-step-inventory"),
            priorHistory: null,
            attachmentContext: null,
            forceDisableTools: false,
            CancellationToken.None);

        requestScopedSource.CapturedAccessTokens.Should().ContainSingle()
            .Which.Should().Be("sender-token");
        plan.ToolContext.Credentials.NyxIdAccessToken.Should().Be("sender-token");
        OfferedToolNames(plan).Should().ContainSingle(name => name == "nyxid_service_inventory");
    }

    private static IReadOnlyList<string> OfferedToolNames(AgentRunReplyStepPlan plan)
    {
        var llmRequest = plan.StepExecutor.BuildLlmStepRequest(
            [ChatMessage.User("hi")],
            requestId: "req",
            plan.Metadata,
            plan.ToolContext,
            plan.LlmControl,
            round: 0,
            finalNoTools: false);
        return (llmRequest.Tools ?? []).Select(tool => tool.Name).ToArray();
    }

    private sealed class StubToolSource(params IAgentTool[] tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }

    private class StubTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => """{"type":"object","properties":{}}""";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private sealed class HumanSessionStubTool(string name) : StubTool(name), IAgentToolCapabilityDescriptor
    {
        public IReadOnlyCollection<string> Capabilities => [AgentToolCapabilities.RequiresHumanSession];
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
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider, larkClient: lark);

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
    public async Task GenerateReplyAsync_WithoutChannelResourceDownloader_AddsProviderNeutralVisibilityWarning()
    {
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider);

        await generator.GenerateReplyAsync(
            CreateLarkImageActivity(
                "msg-image-no-downloader",
                "describe it",
                "om_no_downloader",
                "img_no_downloader",
                token: "user-token"),
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        var systemMessage = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.First(message => message.Role == "system");
        systemMessage.Content.Should().Contain("channel resource download is not available in this runtime");
        systemMessage.Content.Should().NotContain("Lark");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithoutChannelUserCredential_AddsProviderNeutralVisibilityWarning()
    {
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, [1], "image/png", "photo.png"));
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider, larkClient: lark);

        await generator.GenerateReplyAsync(
            CreateLarkImageActivity(
                "msg-image-no-token",
                "describe it",
                "om_no_token",
                "img_no_token",
                token: null),
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        var systemMessage = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.First(message => message.Role == "system");
        systemMessage.Content.Should().Contain("channel user credential needed to download the attachment is unavailable");
        systemMessage.Content.Should().NotContain("Lark");
        lark.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateReplyAsync_WithLarkPdfFileAttachment_AddsExtractedTextPart()
    {
        var pdfBytes = BuildSimplePdf("Document value 42.00 USD");
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, pdfBytes, "application/pdf", "report.pdf"));
        var fileArtifacts = new RecordingWorkflowFileArtifactPort();
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = LLMProviderCapabilities.TextOnly,
        };
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: lark,
            fileIngressPort: fileArtifacts,
            fileArtifactReadPort: fileArtifacts);
        var activity = CreateLarkActivity(
            "msg-file-pdf",
            "read this",
            "om_file_pdf",
            token: "user-token");
        activity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = "https://open.larksuite.com/open-apis/im/v1/messages/om_file_pdf/resources/file_key?type=file",
            ExternalUrl = "https://open.larksuite.com/open-apis/im/v1/messages/om_file_pdf/resources/file_key?type=file",
            Kind = AttachmentKind.File,
            ContentType = "application/pdf",
            Name = "report.pdf",
            SizeBytes = pdfBytes.Length,
        });

        var result = await generator.GenerateReplyAsync(
            activity,
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        var userMessage = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.Last(message => message.Role == "user");
        userMessage.ContentParts.Should().NotBeNull();
        userMessage.ContentParts!.Should().NotContain(part => part.Kind == ContentPartKind.Image);
        userMessage.ContentParts!.Should().Contain(part =>
            part.Kind == ContentPartKind.Text &&
            part.Text == "read this");
        userMessage.ContentParts!.Should().Contain(part =>
            part.Kind == ContentPartKind.Text &&
            part.Text != null &&
            part.Text.Contains("PDF attachment 'report.pdf' extracted text", StringComparison.Ordinal) &&
            part.Text.Contains("Document value 42.00 USD", StringComparison.Ordinal));
        providerFactory.Requests[0].Messages.First(message => message.Role == "system").Content.Should()
            .NotContain("Attachment visibility warning");
        lark.Downloads.Should().ContainSingle().Which.Should().Be((
            "user-token",
            "om_file_pdf",
            "file_key",
            LarkMessageResourceKind.File));
        var ingress = fileArtifacts.IngressRequests.Should().ContainSingle().Subject;
        ingress.Content.ToArray().Should().Equal(pdfBytes);
        ingress.SourceKind.Should().Be(FileArtifactSourceKind.ChatInput);
        ingress.SourceMessageId.Should().Be("om_file_pdf");
        ingress.SourceResourceKey.Should().Be("file_key");
        ingress.FileName.Should().Be("report.pdf");
        ingress.MediaType.Should().Be("application/pdf");
        result.AppendedHistory.Should().NotContain(entry =>
            entry.ContentParts.Any(part =>
                part.Text.Contains("Document value 42.00 USD", StringComparison.Ordinal)));
        result.AppendedHistory.SelectMany(entry => entry.ContentParts)
            .Should().Contain(part =>
                part.Kind == Aevatar.AI.Abstractions.ChatContentPartKind.Text &&
                part.Text.Length == 0 &&
                part.FileRef != null &&
                part.FileRef.ArtifactId == "workflow-file://wf-file-1");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithLongLarkPdfFileAttachment_MarksExtractedTextAsTruncated()
    {
        var pdfBytes = BuildSimplePdf(new string('A', 21_000));
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, pdfBytes, "application/pdf", "long-report.pdf"));
        var fileArtifacts = new RecordingWorkflowFileArtifactPort();
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = LLMProviderCapabilities.TextOnly,
        };
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: lark,
            fileIngressPort: fileArtifacts,
            fileArtifactReadPort: fileArtifacts);
        var activity = CreateLarkActivity(
            "msg-file-long-pdf",
            "read this",
            "om_file_long_pdf",
            token: "user-token");
        activity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = "file_key",
            Kind = AttachmentKind.File,
            ContentType = "application/pdf",
            Name = "long-report.pdf",
            SizeBytes = pdfBytes.Length,
        });

        await generator.GenerateReplyAsync(
            activity,
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        var userMessage = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.Last(message => message.Role == "user");
        userMessage.ContentParts.Should().NotBeNull();
        userMessage.ContentParts!.Should().Contain(part =>
            part.Kind == ContentPartKind.Text &&
            part.Text != null &&
            part.Text.Contains("PDF attachment 'long-report.pdf' extracted text", StringComparison.Ordinal) &&
            part.Text.Contains("truncated to first 20000 characters", StringComparison.Ordinal));
        providerFactory.Requests[0].Messages.First(message => message.Role == "system").Content.Should()
            .NotContain("Attachment visibility warning");
    }

    [Theory]
    [InlineData("text/plain", "notes.txt", "hello from notes")]
    [InlineData("application/json", "config.json", "{\"enabled\":true}")]
    [InlineData("application/octet-stream", "config.yaml", "enabled: true")]
    public async Task GenerateReplyAsync_WithLarkTextFileAttachment_AddsTextContentPart(
        string contentType,
        string fileName,
        string fileContent)
    {
        var fileBytes = Encoding.UTF8.GetBytes(fileContent);
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, fileBytes, contentType, fileName));
        var fileArtifacts = new RecordingWorkflowFileArtifactPort();
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = LLMProviderCapabilities.TextOnly,
        };
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: lark,
            fileIngressPort: fileArtifacts,
            fileArtifactReadPort: fileArtifacts);
        var activity = CreateLarkActivity(
            $"msg-file-{fileName}",
            "read this",
            $"om_file_{fileName}",
            token: "user-token");
        activity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = "file_key",
            Kind = AttachmentKind.File,
            ContentType = contentType,
            Name = fileName,
            SizeBytes = fileBytes.Length,
        });

        var result = await generator.GenerateReplyAsync(
            activity,
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        var userMessage = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.Last(message => message.Role == "user");
        userMessage.ContentParts.Should().NotBeNull();
        userMessage.ContentParts!.Should().Contain(part =>
            part.Kind == ContentPartKind.Text &&
            part.Text == "read this");
        userMessage.ContentParts!.Should().Contain(part =>
            part.Kind == ContentPartKind.Text &&
            part.Text != null &&
            part.Text.Contains($"Text attachment '{fileName}' content", StringComparison.Ordinal) &&
            part.Text.Contains(fileContent, StringComparison.Ordinal));
        providerFactory.Requests[0].Messages.First(message => message.Role == "system").Content.Should()
            .NotContain("Attachment visibility warning");
        lark.Downloads.Should().ContainSingle().Which.Should().Be((
            "user-token",
            $"om_file_{fileName}",
            "file_key",
            LarkMessageResourceKind.File));
        var ingress = fileArtifacts.IngressRequests.Should().ContainSingle().Subject;
        ingress.Content.ToArray().Should().Equal(fileBytes);
        ingress.SourceKind.Should().Be(FileArtifactSourceKind.ChatInput);
        ingress.SourceMessageId.Should().Be($"om_file_{fileName}");
        ingress.SourceResourceKey.Should().Be("file_key");
        ingress.FileName.Should().Be(fileName);
        ingress.MediaType.Should().Be(fileName.EndsWith(".yaml", StringComparison.Ordinal)
            ? "application/yaml"
            : contentType);
        result.AppendedHistory.Should().NotContain(entry =>
            entry.ContentParts.Any(part =>
                part.Text.Contains(fileContent, StringComparison.Ordinal)));
        result.AppendedHistory.SelectMany(entry => entry.ContentParts)
            .Should().Contain(part =>
                part.Kind == Aevatar.AI.Abstractions.ChatContentPartKind.Text &&
                part.Text.Length == 0 &&
                part.FileRef != null &&
                part.FileRef.ArtifactId == "workflow-file://wf-file-1");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithUnsupportedFileAttachment_AddsHonestVisibilityWarning()
    {
        var lark = new RecordingLarkNyxClient(
            new LarkMessageResourceDownloadResult(true, [1], "application/zip", "archive.zip"));
        var providerFactory = new RecordingProviderFactory
        {
            Capabilities = MultimodalCapabilities,
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider, larkClient: lark);
        var activity = CreateLarkActivity(
            "msg-file-zip",
            "read this",
            "om_file_zip",
            token: "user-token");
        activity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = "file_key",
            Kind = AttachmentKind.File,
            ContentType = "application/zip",
            Name = "archive.zip",
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
        userMessage.ContentParts!.Should().ContainSingle(part =>
            part.Kind == ContentPartKind.Text &&
            part.Text == "read this");
        var systemMessage = providerFactory.Requests[0].Messages.First(message => message.Role == "system");
        systemMessage.Content.Should().Contain("Attachment visibility warning");
        systemMessage.Content.Should().Contain("one or more attachments could not be converted to LLM input");
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
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider, larkClient: lark);

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
        systemMessage.Content.Should().Contain("could not be converted to LLM input");
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
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider, larkClient: lark);

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
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider);
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
            BuiltInPromptFloorProvider,
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
        // Kernel invariant still present alongside the configured relay callback URL. (Skill-discovery
        // how-to moved from the kernel into the System Skill Overlay in #2468.)
        systemPrompt.Should().Contain("## Execution Phases");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithChannelContextMiddleware_RendersOperatorIdsWithProviderNeutralLabels()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
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
            },
            llmControl: null,
            toolContext: AgentToolExecutionContext.Empty with
            {
                Channel = AgentToolChannelContext.Empty with
                {
                    IdentityHints =
                    [
                        new AgentToolChannelIdentityHint("sender", "global", "on_sender_1"),
                        new AgentToolChannelIdentityHint("conversation", "platform", "oc_provider_1"),
                        new AgentToolChannelIdentityHint("operator", "account", "provider-user-1"),
                        new AgentToolChannelIdentityHint("operator", "platform", "provider-operator-1"),
                    ],
                },
            },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        var systemPrompt = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("identity_hints:");
        systemPrompt.Should().Contain("- subject: \"sender\", kind: \"global\", value: \"on_sender_1\"");
        systemPrompt.Should().Contain("- subject: \"conversation\", kind: \"platform\", value: \"oc_provider_1\"");
        systemPrompt.Should().Contain("- subject: \"operator\", kind: \"account\", value: \"provider-user-1\"");
        systemPrompt.Should().Contain("- subject: \"operator\", kind: \"platform\", value: \"provider-operator-1\"");
        systemPrompt.Should().NotContain("operator_user_id:");
        systemPrompt.Should().NotContain("operator_open_id:");
        systemPrompt.Should().NotContain("operator_union_id:");
        systemPrompt.Should().NotContain("lark_union_id:");
        systemPrompt.Should().NotContain("lark_chat_id:");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithSystemSkillOverlayProvider_IncludesOverlayAfterKernelBeforeChannelContext()
    {
        const string overlayMarkdown = "## Runtime system skills\n- prefer the committed overlay";
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            new StubBuiltInPromptFloorProvider("MANDATORY FLOOR"),
            overlayProvider: new StubSystemSkillOverlayProvider(overlayMarkdown));

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-overlay-channel",
                ChannelId = ChannelId.From("lark"),
                Conversation = new ConversationReference { CanonicalKey = "lark:group:oc_1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.ChatType] = "group",
                [ChannelMetadataKeys.SenderId] = "ou_sender_1",
                [ChannelMetadataKeys.MessageId] = "om_overlay",
                [ChannelMetadataKeys.ConversationId] = "oc_1",
            },
            streamingSink: null,
            CancellationToken.None);

        var systemPrompt = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain(overlayMarkdown);
        systemPrompt.Should().Contain("MANDATORY FLOOR");
        systemPrompt.Should().Contain("<channel-context>");
        // Kernel anchor: a stable invariant heading the slimmed kernel still carries, asserting the
        // overlay is appended AFTER the kernel. (Capability how-to like skill-discovery moved out of
        // the kernel into the overlay in #2468, so it is no longer a valid kernel anchor.)
        systemPrompt.Should().Contain("## Execution Phases");
        systemPrompt!.IndexOf("## Execution Phases", StringComparison.Ordinal)
            .Should()
            .BeLessThan(systemPrompt.IndexOf("MANDATORY FLOOR", StringComparison.Ordinal));
        systemPrompt.IndexOf("MANDATORY FLOOR", StringComparison.Ordinal)
            .Should()
            .BeLessThan(systemPrompt.IndexOf(overlayMarkdown, StringComparison.Ordinal));
        // Anchor on the INJECTED channel-context runtime block (its rendered sender id), not the
        // kernel's documentation of `<channel-context>`, to assert the overlay sits before the channel
        // runtime facts.
        systemPrompt.IndexOf(overlayMarkdown, StringComparison.Ordinal)
            .Should()
            .BeLessThan(systemPrompt.IndexOf("ou_sender_1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateReplyAsync_ThreadsChannelPlatformIntoOverlayRequest()
    {
        // Context-aware injection (issue #2498): the channel seam must resolve the overlay for the
        // turn's channel platform so a lark turn gets lark-scoped members and other platforms do not.
        var overlayProvider = new StubSystemSkillOverlayProvider("## overlay\n- context-aware");
        var generator = new NyxIdConversationReplyGenerator(
            new RecordingProviderFactory(),
            BuiltInPromptFloorProvider,
            overlayProvider: overlayProvider);

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-overlay-platform",
                ChannelId = ChannelId.From("lark"),
                Conversation = new ConversationReference { CanonicalKey = "lark:group:oc_2" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.ChatType] = "group",
                [ChannelMetadataKeys.SenderId] = "ou_sender_2",
                [ChannelMetadataKeys.ConversationId] = "oc_2",
            },
            streamingSink: null,
            CancellationToken.None);

        overlayProvider.LastRequest.Platform.Should().Be("lark");
    }

    [Fact]
    public async Task BuildStepPlanAsync_ResolvesOverlayPlatformFromTypedChannelContext()
    {
        // The per-step plan path strips owned control keys (channel.platform included) from the
        // external metadata it hands to prompt construction, so the overlay platform must come from
        // the typed channel context — reading metadata alone would silently degrade platform-scoped
        // overlay members to global-only on every AgentRun turn (issue #2498).
        var overlayProvider = new StubSystemSkillOverlayProvider("## overlay\n- per-step context-aware");
        IAgentRunStepConversationReplyGenerator generator = new NyxIdConversationReplyGenerator(
            new RecordingProviderFactory(),
            BuiltInPromptFloorProvider,
            overlayProvider: overlayProvider);
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Channel = AgentToolChannelContext.Empty with { Platform = "lark" },
        };

        var plan = await generator.BuildStepPlanAsync(
            new ChatActivity
            {
                Id = "msg-overlay-step-platform",
                ChannelId = ChannelId.From("lark"),
                Conversation = new ConversationReference { CanonicalKey = "lark:group:oc_3" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.SenderId] = "ou_sender_3",
            },
            llmControl: null,
            toolContext: toolContext,
            priorHistory: null,
            attachmentContext: null,
            forceDisableTools: false,
            CancellationToken.None);

        overlayProvider.LastRequest.Platform.Should().Be("lark");
        plan.InitialMessages.First(message => message.Role == "system").Content
            .Should().Contain("per-step context-aware");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GenerateReplyAsync_WithEmptyOrMissingSystemSkillOverlayProvider_DoesNotInjectOverlay(string? overlayMarkdown)
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            new StubBuiltInPromptFloorProvider("MANDATORY FLOOR"),
            overlayProvider: overlayMarkdown is null ? null : new StubSystemSkillOverlayProvider(overlayMarkdown));

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = $"msg-overlay-empty-{overlayMarkdown?.Length ?? 0}",
                ChannelId = ChannelId.From("lark"),
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        var systemPrompt = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("MANDATORY FLOOR");
        systemPrompt.Should().NotContain("Runtime system skills");
        systemPrompt.Should().NotContain("prefer the committed overlay");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithChannelContextMiddleware_RendersSubjectIdsSeparatelyFromOperatorIds()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
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
            },
            llmControl: null,
            toolContext: AgentToolExecutionContext.Empty with
            {
                Channel = AgentToolChannelContext.Empty with
                {
                    IdentityHints =
                    [
                        new AgentToolChannelIdentityHint("subject", "account", "provider-subject-user-1"),
                        new AgentToolChannelIdentityHint("subject", "directory", "directory-1"),
                    ],
                },
            },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Be("ok");
        var systemPrompt = providerFactory.Requests.Should().ContainSingle().Subject
            .Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("identity_hints:");
        systemPrompt.Should().Contain("- subject: \"subject\", kind: \"account\", value: \"provider-subject-user-1\"");
        systemPrompt.Should().Contain("- subject: \"subject\", kind: \"directory\", value: \"directory-1\"");
        systemPrompt.Should().NotContain("operator_account_id:");
        systemPrompt.Should().NotContain("operator_platform_id:");
        systemPrompt.Should().NotContain("subject_user_id:");
        systemPrompt.Should().NotContain("subject_employee_id:");
        systemPrompt.Should().NotContain("operator_user_id: \"lark-subject-user-1\"");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithChannelContextMiddleware_IncludesResolvedMentionsLine()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
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
            BuiltInPromptFloorProvider,
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
            BuiltInPromptFloorProvider,
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
            BuiltInPromptFloorProvider,
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
            BuiltInPromptFloorProvider,
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
            BuiltInPromptFloorProvider,
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
            BuiltInPromptFloorProvider,
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
            BuiltInPromptFloorProvider,
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
    // delivers its scheduled runs to /admin#/observatory, never a chat/bot) self-declare the
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
            BuiltInPromptFloorProvider,
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
            BuiltInPromptFloorProvider,
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
    public async Task GenerateReplyAsync_WhenUseSkillPreviewsWorkflowMount_ShouldRunReadOnlyWithoutApprovalDenial()
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
        var mountPort = new RecordingSkillWorkflowMountPort();
        var providerFactory = new UseSkillMountWorkflowProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            toolSources:
            [
                new SingleToolSource(new UseSkillTool(catalog, workflowMountPort: mountPort)),
            ],
            localSkillCatalog: catalog,
            toolExecutionPort: new ChannelConversationTurnRunnerTests.TestAgentToolExecutionPort());

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
                Caller = new AgentToolCallerContext("scope-alpha", "owner-alpha", null),
                Credentials = new AgentToolCredentials(
                    "token-alpha",
                    null,
                    null,
                    AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
                NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                    "nyxid",
                    "tenant-alpha",
                    "nyx-user-alpha"),
            },
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Contain("## Mounted Workflows");
        reply.Text.Should().Contain("\"status\": \"confirmation_required\"");
        reply.Text.Should().NotContain("approval-gated tools cannot run here");
        reply.Text.Should().NotContain("Workflow mounting is not available in this host");
        mountPort.Requests.Should().ContainSingle()
            .Which.Should().Match<SkillWorkflowMountRequest>(request =>
                request.ScopeId == "scope-alpha" &&
                request.CallerId == "nyx-user-alpha" &&
                request.Workflows.Count == 1 &&
                request.Workflows[0].WorkflowId == "demo_dinner");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithStreamingSink_EmitsPlaceholderThenFinalTextAcrossToolFollowUp()
    {
        var providerFactory = new ToolCallingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
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
    public async Task GenerateReplyAsync_ForNyxIdInventory_UsesSkillThenTypedToolAndStreamsFinalAnswer()
    {
        var executionEvents = new List<string>();
        var providerFactory = new NyxIdInventorySkillStreamingProviderFactory();
        var remoteSkillFetcher = new RecordingNyxIdRemoteSkillFetcher(executionEvents);
        var skillCapabilityIssuer = new RecordingNyxIdSkillCapabilityIssuer("sender-skill-token");
        var inventoryCapabilityIssuer = new RecordingNyxIdInventoryCapabilityIssuer(
            "sender-inventory-token",
            executionEvents);
        var inventoryHandler = new RecordingNyxIdInventoryHandler(executionEvents);
        var nyxIdOptions = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var toolExecutionPort = new ChannelConversationTurnRunnerTests.TestAgentToolExecutionPort();
        var inventorySource = new ChannelNyxIdConnectedServiceInventoryToolSource(
            toolExecutionPort,
            nyxIdOptions,
            new FixedNyxIdApiClientFactory(new NyxIdApiClient(
                nyxIdOptions,
                new HttpClient(inventoryHandler))),
            inventoryCapabilityIssuer,
            NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance);
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            toolSources: [inventorySource],
            localSkillCatalog: new LocalSkillCatalog(),
            remoteSkillFetcher: remoteSkillFetcher,
            relayOptions: new global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingPlaceholderText = "…",
            },
            remoteSkillAccessTokenResolver: new ChannelRemoteSkillAccessTokenResolver(
                skillCapabilityIssuer,
                NullLogger<ChannelRemoteSkillAccessTokenResolver>.Instance),
            toolExecutionPort: toolExecutionPort);
        var sink = new RecordingStreamingSink();
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Channel = new AgentToolChannelContext(
                "lark",
                "ou-channel-alpha",
                "scope-channel-alpha",
                "message-inventory-alpha",
                null),
            SenderBinding = new AgentToolSenderBindingContext(
                "bnd-skill-inventory-alpha",
                NyxUserId: "nyx-user-channel-alpha",
                SenderTenant: "tenant-channel-alpha"),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "lark",
                "tenant-authority-alpha",
                "ou-authority-alpha"),
        };

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "message-inventory-alpha",
                ChannelId = ChannelId.From("lark"),
                Conversation = new ConversationReference
                {
                    CanonicalKey = "lark:dm:ou-channel-alpha",
                },
                Content = new MessageContent
                {
                    Text = "我在 NyxID 上连接了哪些服务",
                },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.SenderId] = "ou-channel-alpha",
                [ChannelMetadataKeys.MessageId] = "message-inventory-alpha",
            },
            Control(
                model: "sender-model",
                route: "sender-route",
                rounds: 4,
                token: "ambient-owner-token",
                senderToken: null),
            toolContext,
            sink,
            CancellationToken.None);

        providerFactory.ChatStreamCallCount.Should().Be(3);
        providerFactory.Requests.Should().HaveCount(3);
        providerFactory.ObservedToolCalls.Should().Equal(
            "use_skill",
            "nyxid_service_inventory");
        executionEvents.Should().Equal(
            "use_skill",
            "nyxid_service_inventory",
            "/api/v1/keys");

        remoteSkillFetcher.Requests.Should().ContainSingle().Which.Should().Be((
            "sender-skill-token",
            "nyxid"));
        remoteSkillFetcher.Requests.Should().NotContain(request =>
            request.AccessToken == "ambient-owner-token");
        skillCapabilityIssuer.BindingIds.Should().ContainSingle()
            .Which.Should().Be("bnd-skill-inventory-alpha");
        inventoryCapabilityIssuer.BindingIds.Should().ContainSingle()
            .Which.Should().Be("bnd-skill-inventory-alpha");
        skillCapabilityIssuer.Subjects.Should().ContainSingle().Which.Should().Be((
            "lark",
            "tenant-authority-alpha",
            "ou-authority-alpha"));
        inventoryCapabilityIssuer.Subjects.Should().ContainSingle().Which.Should().Be((
            "lark",
            "tenant-authority-alpha",
            "ou-authority-alpha"));
        inventoryHandler.Authorization.Should().Be("Bearer sender-inventory-token");
        inventoryHandler.RequestPath.Should().Be("/api/v1/keys");

        var useSkillResult = providerFactory.Requests[1].Messages
            .Should().ContainSingle(message =>
                message.Role == "tool" &&
                message.ToolCallId == "call-use-nyxid")
            .Which.Content;
        var inventoryResult = providerFactory.Requests[2].Messages
            .Should().ContainSingle(message =>
                message.Role == "tool" &&
                message.ToolCallId == "call-nyxid-inventory")
            .Which.Content;
        useSkillResult.Should().Contain("nyxid_service_inventory");
        inventoryResult.Should().Contain("GitHub");

        providerFactory.Requests
            .SelectMany(request => request.Tools ?? [])
            .Should().NotContain(tool => tool.Name == "code_execute");
        reply.Text.Should().Be("你已连接 GitHub。");
        sink.Emissions.Should().NotBeEmpty();
        sink.Emissions.Last().Should().Be("你已连接 GitHub。");

        var visibleAndToolOutput = string.Join(
            "\n",
            new[] { reply.Text, useSkillResult, inventoryResult }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        visibleAndToolOutput.Should().NotContain("UNAUTHENTICATED");
        visibleAndToolOutput.Should().NotContain("nyxid service list");
        visibleAndToolOutput.Should().NotContain("/init");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithToolCallPreamble_DoesNotStreamProcessNarration()
    {
        var providerFactory = new ToolCallingPreambleProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
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
            BuiltInPromptFloorProvider,
            toolSources:
            [
                new SingleToolSource(new FixedResultTool("ornn_search_skills", "Found 1 skills:\n- **project-summary**")),
                new SingleToolSource(new FixedResultTool("use_skill", "# project-summary\n## Instructions\nBuild the project summary.")),
            ],
            relayOptions: new global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingPlaceholderText = "…",
            },
            toolExecutionPort: new ChannelConversationTurnRunnerTests.TestAgentToolExecutionPort());
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
            BuiltInPromptFloorProvider,
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
            BuiltInPromptFloorProvider,
            toolSources:
            [
                new SingleToolSource(new FixedResultTool("ornn_search_skills", "Found 1 skills:\n- **project-summary**")),
                new SingleToolSource(new FixedResultTool("use_skill", "# project-summary\n## Instructions\nFetch project data.")),
                new SingleToolSource(new FixedResultTool(
                    "chrono_storage_query",
                    "Error: Invalid URI: The hostname could not be parsed.",
                    AgentToolReceiptStatus.Error)),
            ],
            toolExecutionPort: new ChannelConversationTurnRunnerTests.TestAgentToolExecutionPort());
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
            BuiltInPromptFloorProvider,
            toolSources:
            [
                new SingleToolSource(new FixedResultTool("ornn_search_skills", "Found 1 skills:\n- **goal**")),
                new SingleToolSource(new FixedResultTool("use_skill", "# goal\n## Instructions\nExecute the goal command.")),
            ],
            toolExecutionPort: new ChannelConversationTurnRunnerTests.TestAgentToolExecutionPort());
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
            BuiltInPromptFloorProvider,
            toolSources:
            [
                new SingleToolSource(new FixedResultTool("ornn_search_skills", "Found 1 skills:\n* project-summary")),
            ],
            toolExecutionPort: new ChannelConversationTurnRunnerTests.TestAgentToolExecutionPort());
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
    public async Task GenerateReplyAsync_AppliesCompleteSenderSelectionOverOwnerSelection()
    {
        // Issue #513 phase 3: when the inbound carries a sender binding-id,
        // sender prefs override the upstream-pinned bot-owner selection as one fact. The owner's metadata is already in the input (channel
        // turn runner pins it via OwnerLlmConfigApplier in production), so
        // the generator only has to layer sender overrides where the sender
        // actually set a value.
        var providerFactory = new RecordingProviderFactory();
        var prefsStore = new ScopedStubPreferencesStore
        {
            ByBinding =
            {
                ["bnd_sender"] = SenderPreferences(),
            },
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider, preferencesStore: prefsStore);

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-1",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>(),
            Control("owner-model", "/api/v1/proxy/s/owner", 9, "owner-token", "sender-token"),
            ToolContext("bnd_sender"),
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Metadata.Should().NotBeNull();
        request.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        var toolContext = request.ToolContext!;
        toolContext.Routing.ModelOverride.Should().Be("sender-model");
        toolContext.Routing.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/sender");
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
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider, preferencesStore: prefsStore);

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
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider);

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
            BuiltInPromptFloorProvider,
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

    // The unbound-sender gate detaches every tool while the kernel prompt still documents
    // them; the system prompt must carry the honest override so the model reports "tools
    // disabled this turn, bind to enable" instead of denying the capability exists
    // (2026-07 incident: the bot answered "no scheduling tool entry point" to a bound-
    // looking user whose turn ran in the unbound degrade).
    [Fact]
    public async Task GenerateReplyAsync_ForUnboundChannelTurn_TellsModelToolsAreDisabledAndBindable()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            toolSources: [new SingleToolSource(new FixedResultTool("any_tool", """{"ok":true}"""))]);

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-unbound-tools-notice",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "remind me in five minutes" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.SenderId] = "ou_user_1",
                [ChannelMetadataKeys.MessageId] = "msg-unbound-tools-notice",
            },
            Control("owner-only-model", "owner-route", 4),
            toolContext: null,
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().BeNull();
        var systemMessage = request.Messages.Should().Contain(message => message.Role == "system").Which;
        systemMessage.Content.Should().Contain("Tools disabled for this turn");
        systemMessage.Content.Should().Contain("/init");
    }

    // The production DI pool hands the channel reply generator EVERY registered tool
    // source, including the Lark authoring source carrying the scheduling tool. Pin the
    // full bound-sender relay path — real AgentBuilderToolSource → discovery → channel
    // filters → LLM request — so a future change that drops the source or gates its tools
    // out of channel turns fails here instead of shipping. Concrete tool names appear as
    // test fixtures only (CLAUDE.md testfile exception).
    [Fact]
    public async Task GenerateReplyAsync_ForBoundLarkRelayTurn_InjectsAgentBuilderToolsIntoLlmRequest()
    {
        var providerFactory = new RecordingProviderFactory();
        var nyxClientFactory = Substitute.For<INyxIdApiClientFactory>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var issuer = new ScheduledAgentApiKeyIssuer(nyxClientFactory);
        var agentBuilderSource = new AgentBuilderToolSource(
            Substitute.For<IUserAgentCatalogQueryPort>(),
            Substitute.For<IScheduledDispatchApplicationService>(),
            Substitute.For<IScheduledWorkflowAgentCreationPort>(),
            catalogCommandPort,
            Substitute.For<ICallerScopeResolver>(),
            new ScheduledAgentCreateRequestMapper(),
            new ScheduledAgentCredentialLifecycle(new InMemorySecretVault(), catalogCommandPort, issuer),
            Substitute.For<IScheduledInvocationAuthorizationPlanner>(),
            Substitute.For<IScheduledInvocationAuthorizationRevalidator>());
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            toolSources: [agentBuilderSource]);

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-bound-channel-tools",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "remind me in five minutes" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.SenderId] = "ou_user_1",
                [ChannelMetadataKeys.MessageId] = "msg-bound-channel-tools",
            },
            Control("sender-model", "sender-route", 4),
            RelayToolContext("bnd-user-1", "msg-bound-channel-tools"),
            streamingSink: null,
            CancellationToken.None);

        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().NotBeNull();
        request.Tools!.Select(static tool => tool.Name).Should().Contain(
        [
            "scheduled_agent_creator",
            "agent_builder",
        ]);
        request.Messages.Should().Contain(message => message.Role == "system")
            .Which.Content.Should().NotContain("Tools disabled for this turn");
    }

    [Fact]
    public async Task GenerateReplyAsync_ForBoundLarkRelayTurn_DiscoversRequestToolsWithSenderCredentialContext()
    {
        var providerFactory = new RecordingProviderFactory();
        var requestScopedSource = new RequestScopedToolSource(
            new FixedResultTool("nyxid_service_inventory", """{"instances":[]}"""));
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            toolSources: [requestScopedSource]);

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-bound-channel-inventory",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "我在 NyxID 上连接了哪些服务" },
            },
            new Dictionary<string, string>
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.SenderId] = "ou_user_1",
                [ChannelMetadataKeys.MessageId] = "msg-bound-channel-inventory",
            },
            Control("sender-model", "sender-route", 4, token: "owner-token", senderToken: "sender-token"),
            RelayToolContext("bnd-user-1", "msg-bound-channel-inventory"),
            streamingSink: null,
            CancellationToken.None);

        requestScopedSource.CapturedAccessTokens.Should().ContainSingle()
            .Which.Should().Be("sender-token");
        var request = providerFactory.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().ContainSingle(tool => tool.Name == "nyxid_service_inventory");
        request.ToolContext!.Credentials.NyxIdAccessToken.Should().Be("sender-token");
    }

    [Fact]
    public async Task GenerateReplyAsync_FallsBackToOwnerPrefsWhenSenderStoreThrows()
    {
        // Pin graceful-degradation: a transient sender-config projection
        // outage must not corrupt the LLM request — the upstream-pinned
        // owner prefs survive (PR #521 review glm-5.1).
        var providerFactory = new RecordingProviderFactory();
        var prefsStore = new ScopedStubPreferencesStore { ThrowOnLookup = true };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider, preferencesStore: prefsStore);

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
                ["bnd_sender"] = SenderPreferences(7),
            },
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider, preferencesStore: prefsStore);

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
                ["bnd_sender"] = SenderPreferences(7),
            },
        };
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
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
            BuiltInPromptFloorProvider,
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
                ["bnd_sender"] = SenderPreferences(7),
            },
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider, preferencesStore: prefsStore);

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
        requestToolContext.Routing.ModelOverride.Should().Be("owner-model");
        requestToolContext.Routing.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/owner");
        requestToolContext.Routing.MaxToolRoundsOverride.Should().Be(5);
        requestToolContext.Credentials.NyxIdAccessToken.Should().Be("owner-token");
        requestToolContext.Credentials.NyxIdOrgToken.Should().Be("owner-token");
        requestToolContext.SenderBinding.BindingId.Should().Be("bnd_sender");
        requestToolContext.Credentials.SenderNyxIdAccessToken.Should().BeNull();
    }

    [Fact]
    public async Task GenerateReplyAsync_WithSenderSelection_ShouldPromoteSenderTokenForTools()
    {
        var providerFactory = new RecordingProviderFactory();
        var prefsStore = new ScopedStubPreferencesStore
        {
            ByBinding =
            {
                ["bnd_sender"] = SenderPreferences(),
            },
        };
        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider, preferencesStore: prefsStore);

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
        toolContext.Routing.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/sender");
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
    public const string MatrixBoundSelection = "bound_selection";
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
    [InlineData(MatrixBoundSelection, MatrixOwnerNone, "sender-model", "/api/v1/proxy/s/sender", null)]
    [InlineData(MatrixBoundSelection, MatrixOwnerPartial, "sender-model", "/api/v1/proxy/s/sender", null)]
    [InlineData(MatrixBoundSelection, MatrixOwnerFull, "sender-model", "/api/v1/proxy/s/sender", "9")]
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
            case MatrixBoundSelection:
                prefsStore.ByBinding["bnd_sender"] = SenderPreferences();
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
        if (bindingState == MatrixBoundSelection)
            control = (control ?? LLMControlContext.Empty) with { SenderNyxIdAccessToken = "sender-token" };

        var generator = new NyxIdConversationReplyGenerator(providerFactory, BuiltInPromptFloorProvider, preferencesStore: prefsStore);
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
            return Task.FromResult(NyxIdUserLlmPreferences.Empty);
        }

        public Task<NyxIdUserLlmPreferences> GetForBindingAsync(string bindingId, CancellationToken cancellationToken = default)
        {
            Lookups.Add(bindingId);
            if (ThrowOnLookup)
                throw new InvalidOperationException("simulated projection outage");
            return Task.FromResult(ByBinding.TryGetValue(bindingId, out var prefs)
                ? prefs
                : NyxIdUserLlmPreferences.Empty);
        }
    }

    private static NyxIdUserLlmPreferences SenderPreferences(int maxToolRounds = 0) => new(
        new LLMSelection
        {
            RouteKind = LLMRouteKind.NyxIdUserService,
            RouteValue = "/api/v1/proxy/s/sender",
            NyxIdUserServiceId = "us-sender",
            ServiceSlugSnapshot = "sender",
            ModelSelection = new LLMModelSelection
            {
                Kind = LLMModelSelectionKind.ExplicitModel,
                ModelId = "sender-model",
            },
        },
        LLMSelectionPersistenceStatus.Ready,
        maxToolRounds);

    private sealed class StubSystemSkillOverlayProvider(string? overlayMarkdown) : ISystemSkillOverlayProvider
    {
        public SystemSkillOverlayRequest LastRequest { get; private set; }

        public GlobalSystemSkillPromptLayer? GetCurrent(SystemSkillOverlayRequest request)
        {
            LastRequest = request;
            return overlayMarkdown is null
                ? null
                : new GlobalSystemSkillPromptLayer(
                    overlayMarkdown,
                    new GlobalSystemSkillPromptProvenance("test-global"),
                    new PromptLayerBounds(32 * 1024, 8192));
        }
    }

    internal sealed class StubBuiltInPromptFloorProvider(string content) : IBuiltInPromptFloorProvider
    {
        public BuiltInPromptFloorLayer GetFloor() =>
            new(content, new BuiltInPromptFloorProvenance("test-floor"));
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

    private sealed class RecordingWorkflowFileArtifactPort : IFileArtifactIngressPort, IFileArtifactReadPort
    {
        private readonly Dictionary<string, (ApplicationFileArtifactRef FileRef, byte[] Content)> _files = new(StringComparer.Ordinal);
        private int _nextId;

        public List<FileArtifactIngressRequest> IngressRequests { get; } = [];

        public ValueTask<FileArtifactIngressResult> IngestAsync(
            FileArtifactIngressRequest request,
            CancellationToken cancellationToken = default)
        {
            IngressRequests.Add(request);
            var content = request.Content.ToArray();
            var fileId = $"wf-file-{++_nextId}";
            var fileRef = new ApplicationFileArtifactRef
            {
                FileId = fileId,
                ArtifactId = $"workflow-file://{fileId}",
                SourceKind = request.SourceKind,
                SourceMessageId = request.SourceMessageId,
                SourceResourceKey = request.SourceResourceKey,
                FileName = request.FileName,
                MediaType = request.MediaType,
                SizeBytes = content.LongLength,
                Sha256 = $"sha-{fileId}",
                CreatedAtUnixMs = new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero)
                    .ToUnixTimeMilliseconds(),
                ExpiresAtUnixMs = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    .ToUnixTimeMilliseconds(),
                OwnerRunId = request.OwnerRunId,
                OwnerScopeId = request.OwnerScopeId,
            };
            _files[fileRef.ArtifactId!] = (fileRef, content);
            return ValueTask.FromResult(new FileArtifactIngressResult(fileRef));
        }

        public ValueTask<ApplicationFileArtifactRef> DescribeAsync(
            ApplicationFileArtifactRef fileRef,
            CancellationToken cancellationToken = default)
        {
            var stored = Resolve(fileRef);
            return ValueTask.FromResult(stored.FileRef);
        }

        public ValueTask<FileArtifactContent> OpenReadAsync(
            ApplicationFileArtifactRef fileRef,
            CancellationToken cancellationToken = default)
        {
            var stored = Resolve(fileRef);
            return ValueTask.FromResult(new FileArtifactContent(
                stored.FileRef,
                new MemoryStream(stored.Content, writable: false)));
        }

        private (ApplicationFileArtifactRef FileRef, byte[] Content) Resolve(ApplicationFileArtifactRef fileRef)
        {
            var key = fileRef.ArtifactId ?? $"workflow-file://{fileRef.FileId}";
            if (!_files.TryGetValue(key, out var stored))
                throw new FileNotFoundException("Test workflow file artifact was not found.", key);
            return stored;
        }
    }

    private sealed class RejectingWorkflowFileIngressPort(Exception exception) : IFileArtifactIngressPort
    {
        public ValueTask<FileArtifactIngressResult> IngestAsync(
            FileArtifactIngressRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<FileArtifactIngressResult>(exception);
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

    private sealed class NyxIdInventorySkillStreamingProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "nyxid-inventory-skill-streaming";

        public int ChatStreamCallCount { get; private set; }

        public List<LLMRequest> Requests { get; } = [];

        public List<string> ObservedToolCalls { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ChatStreamCallCount++;
            Requests.Add(request);

            if (!HasToolCall(request, "use_skill"))
            {
                ObservedToolCalls.Add("use_skill");
                yield return ToolChunk(
                    "call-use-nyxid",
                    "use_skill",
                    """{"skill":"nyxid"}""");
                yield return new LLMStreamChunk { IsLast = true };
                await Task.CompletedTask;
                yield break;
            }

            if (!HasToolCall(request, "nyxid_service_inventory"))
            {
                ObservedToolCalls.Add("nyxid_service_inventory");
                yield return ToolChunk(
                    "call-nyxid-inventory",
                    "nyxid_service_inventory",
                    "{}");
                yield return new LLMStreamChunk { IsLast = true };
                await Task.CompletedTask;
                yield break;
            }

            yield return new LLMStreamChunk { DeltaContent = "你已连接 " };
            yield return new LLMStreamChunk { DeltaContent = "GitHub。" };
            yield return new LLMStreamChunk { IsLast = true };
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingNyxIdRemoteSkillFetcher(List<string> executionEvents)
        : IRemoteSkillFetcher
    {
        public List<(string AccessToken, string NameOrId)> Requests { get; } = [];

        public Task<SkillDefinition?> FetchSkillAsync(
            string accessToken,
            string nameOrId,
            CancellationToken ct = default)
        {
            Requests.Add((accessToken, nameOrId));
            executionEvents.Add("use_skill");
            return Task.FromResult<SkillDefinition?>(new SkillDefinition
            {
                Name = "nyxid",
                Description = "Use the caller-scoped NyxID tools.",
                Instructions =
                    "Use `nyxid_service_inventory` for the sender-scoped connected-service inventory.",
                Source = SkillSource.Remote,
                RemoteId = "skill-nyxid-alpha",
            });
        }
    }

    private sealed class RecordingNyxIdSkillCapabilityIssuer(string accessToken)
        : INyxIdSkillCapabilityIssuer
    {
        public List<string> BindingIds { get; } = [];

        public List<(string Platform, string Tenant, string ExternalUserId)> Subjects { get; } = [];

        public Task<CapabilityHandle> IssueByBindingIdAsync(
            ExternalSubjectRef externalSubject,
            string bindingId,
            CancellationToken ct = default)
        {
            BindingIds.Add(bindingId);
            Subjects.Add((
                externalSubject.Platform,
                externalSubject.Tenant,
                externalSubject.ExternalUserId));
            return Task.FromResult(new CapabilityHandle
            {
                AccessToken = accessToken,
                Scope = "proxy",
            });
        }
    }

    private sealed class RecordingNyxIdInventoryCapabilityIssuer(
        string accessToken,
        List<string> executionEvents)
        : INyxIdConnectedServiceInventoryCapabilityIssuer
    {
        public List<string> BindingIds { get; } = [];

        public List<(string Platform, string Tenant, string ExternalUserId)> Subjects { get; } = [];

        public Task<CapabilityHandle> IssueByBindingIdAsync(
            ExternalSubjectRef externalSubject,
            string bindingId,
            CancellationToken ct = default)
        {
            executionEvents.Add("nyxid_service_inventory");
            BindingIds.Add(bindingId);
            Subjects.Add((
                externalSubject.Platform,
                externalSubject.Tenant,
                externalSubject.ExternalUserId));
            return Task.FromResult(new CapabilityHandle
            {
                AccessToken = accessToken,
                Scope = "proxy",
            });
        }
    }

    private sealed class RecordingNyxIdInventoryHandler(List<string> executionEvents) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        public string? RequestPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            RequestPath = request.RequestUri?.AbsolutePath;
            executionEvents.Add(RequestPath ?? "unknown-http-path");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "keys": [
                        {
                          "id": "user-service-github-alpha",
                          "slug": "github",
                          "service_id": "catalog-github-alpha",
                          "label": "GitHub",
                          "is_active": true,
                          "connected": true,
                          "status": "active",
                          "credential_source": { "type": "personal" }
                        }
                      ]
                    }
                    """),
            });
        }
    }

    private sealed class FixedNyxIdApiClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
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

    private sealed class RequestScopedToolSource(IAgentTool tool) : IAgentToolSource
    {
        public List<string?> CapturedAccessTokens { get; } = [];

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            var accessToken = AgentToolRequestContext.NyxIdAccessToken;
            CapturedAccessTokens.Add(accessToken);
            return Task.FromResult<IReadOnlyList<IAgentTool>>(
                string.IsNullOrWhiteSpace(accessToken) ? [] : [tool]);
        }
    }

    private sealed class FixedResultTool(
        string name,
        string result,
        AgentToolReceiptStatus status = AgentToolReceiptStatus.Success) : IAgentTool
    {
        public string Name => name;

        public string Description => "Returns a fixed test result.";

        public string ParametersSchema => "{}";

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = status,
                ResultJson = resultJson,
                ErrorCode = status == AgentToolReceiptStatus.Error ? "test_tool_error" : string.Empty,
            };

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

    private sealed class RecordingSkillWorkflowMountPort : ISkillWorkflowMountPort
    {
        public List<SkillWorkflowMountRequest> Requests { get; } = [];

        public Task<SkillWorkflowMountResult> MountAsync(
            SkillWorkflowMountRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            var confirmation = new SkillWorkflowMountConfirmation(
                "demo_dinner",
                "rev-demo-dinner",
                "sha256:demo-dinner",
                []);
            return Task.FromResult(new SkillWorkflowMountResult(
                "confirmation_required",
                false,
                [],
                "Review before mounting.",
                [
                    new SkillWorkflowMountPreview(
                        confirmation.WorkflowId,
                        confirmation.RevisionId,
                        confirmation.WorkflowBundleDigest,
                        [],
                        confirmation),
                ]));
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
