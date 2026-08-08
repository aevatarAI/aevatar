using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ApplicationFileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;
using LlmChatFileRef = Aevatar.AI.Abstractions.LLMProviders.ChatFileRef;
using LlmChatFileSourceKind = Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentRunReplyGenerationExecutorTests
{
    [Fact]
    public async Task BuildLlmStepContinuation_WhenToolEnabledFirstRoundHasNoSuccessfulMutatingReceipt_ShouldPassMutationClaimConstraint()
    {
        var provider = new RecordingProvider();
        var executor = CreateToolEnabledExecutor(new CountingTool("submit_invoice"), provider);
        var workItem = BuildToolEnabledWorkItem(
            new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Error,
                SideEffectKind = "invoice.submit",
            });

        await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().ContainSingle().Which.Name.Should().Be("submit_invoice");
        request.Messages.Should().ContainSingle(message =>
            message.Role == "system" &&
            message.Content != null &&
            message.Content.Contains("no successful mutating tool execution", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenNonCardTurnHasBlockingReceipt_ShouldSuppressModelStreaming()
    {
        var provider = new RecordingProvider();
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        var executor = CreateToolEnabledExecutor(
            new CountingTool("submit_invoice"),
            provider,
            actorDispatchPort: dispatchPort,
            relayOptions: new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingRepliesEnabled = true,
                StreamingCardKitEnabled = false,
            });
        var workItem = BuildToolEnabledWorkItem(new AgentToolReceipt
        {
            CallId = "call-submit",
            ToolName = "submit_invoice",
            Status = AgentToolReceiptStatus.Error,
            Effect = AgentToolReceiptEffect.Mutating,
        });
        workItem.Request.Activity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "reply-1",
            CorrelationId = "corr-1",
        };

        await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        await dispatchPort.DidNotReceiveWithAnyArgs()
            .DispatchAsync(default!, default!, default);
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenNonCardTurnNeedsToolApproval_ShouldPreserveRelayReplyTokenForApprovalCard()
    {
        var tool = new ApprovalRequiredTool("use_skill");
        var provider = new TextThenToolCallProvider(tool.Name, "Preparing the workflow.");
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        var executor = CreateToolEnabledExecutor(
            tool,
            provider,
            actorDispatchPort: dispatchPort,
            relayOptions: new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingRepliesEnabled = true,
                StreamingCardKitEnabled = false,
            });
        var workItem = BuildToolEnabledWorkItem();
        workItem.Request.Activity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-message-1",
            CorrelationId = "corr-1",
        };
        workItem.Request.ReplyToken = "single-use-relay-token";
        workItem.Request.ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        await dispatchPort.DidNotReceiveWithAnyArgs()
            .DispatchAsync(default!, default!, default);
        execution.Continuation.LlmStepResult.AccumulatedText.Should().BeEmpty();
        execution.Continuation.LlmStepResult.HasStreamedTextContent.Should().BeFalse();
        execution.Continuation.LlmStepResult.ToolCalls.Should().ContainSingle()
            .Which.Name.Should().Be(tool.Name);
        execution.Continuation.LlmStepResult.ToolRequestId.Should().Be("activity-1");
        execution.AuthorizedToolCallSafeties.Should().ContainSingle()
            .Which.CallSafety.RequiresApproval.Should().BeTrue();
        execution.AuthorizedToolStep.Should().NotBeNull();
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenToolEnabledFirstRoundHasSuccessfulMutatingReceipt_ShouldKeepGroundingConstraint()
    {
        var provider = new RecordingProvider();
        var executor = CreateToolEnabledExecutor(new CountingTool("submit_invoice"), provider);
        var workItem = BuildToolEnabledWorkItem(
            new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Success,
                SideEffectKind = "invoice.submit",
                Effect = AgentToolReceiptEffect.Mutating,
            });

        await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().ContainSingle().Which.Name.Should().Be("submit_invoice");
        request.Messages.Should().NotContain(message =>
            message.Role == "system" &&
            message.Content != null &&
            message.Content.Contains("no successful mutating tool execution", StringComparison.Ordinal));
        request.Messages.Should().ContainSingle(message =>
            message.Role == "system" &&
            message.Content != null &&
            message.Content.Contains("match that exact action", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenFinalNoToolsHasNoSuccessfulMutatingReceipt_ShouldPassReceiptsToCoreConstraint()
    {
        var provider = new RecordingProvider();
        var executor = CreateExecutor(provider);
        var workItem = BuildFinalNoToolsWorkItem();

        await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().BeNull();
        request.Messages
            .Where(message => message.Role == "system" &&
                              message.Content?.Contains("no successful mutating tool execution") == true)
            .Should().ContainSingle();
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenFinalNoToolsHasSuccessfulMutatingReceipt_ShouldKeepGroundingConstraint()
    {
        var provider = new RecordingProvider();
        var executor = CreateExecutor(provider);
        var workItem = BuildFinalNoToolsWorkItem(
            new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Success,
                SideEffectKind = "definition.update",
                Effect = AgentToolReceiptEffect.Mutating,
            });

        await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().BeNull();
        request.Messages
            .Where(message => message.Role == "system" &&
                              message.Content?.Contains("no successful mutating tool execution") == true)
            .Should().BeEmpty();
        request.Messages.Should().ContainSingle(message =>
            message.Role == "system" &&
            message.Content != null &&
            message.Content.Contains("match that exact action", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenStepStateCarriesFileRef_ShouldMaterializeOnlyForProviderRequest()
    {
        var provider = new RecordingProvider();
        var artifactPort = new RecordingFileArtifactReadPort(
            new ApplicationFileArtifactRef
            {
                FileId = "wf-file-1",
                ArtifactId = "workflow-file://wf-file-1",
                SourceKind = FileArtifactSourceKind.ChatInput,
                SourceMessageId = "om-image",
                SourceResourceKey = "img-image",
                FileName = "photo.png",
                MediaType = "image/png",
                SizeBytes = 3,
            },
            [1, 2, 3]);
        var executor = CreateExecutor(provider, fileArtifactReadPort: artifactPort);
        var workItem = BuildFinalNoToolsWorkItem();
        var imagePart = new ContentPart
        {
            Kind = ContentPartKind.Image,
            MediaType = "image/png",
            Name = "photo.png",
            FileRef = new LlmChatFileRef
            {
                FileId = "wf-file-1",
                ArtifactId = "workflow-file://wf-file-1",
                SourceKind = LlmChatFileSourceKind.ChatInput,
                SourceMessageId = "om-image",
                SourceResourceKey = "img-image",
                FileName = "photo.png",
                MediaType = "image/png",
                SizeBytes = 3,
            },
        };
        workItem.StepState.Messages.Clear();
        workItem.StepState.Messages.Add(AgentRunReplyStepMappers.ToProto(ChatMessage.User([
            ContentPart.TextPart("describe"),
            imagePart,
        ])));

        await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        var providerImagePart = provider.Requests.Should().ContainSingle().Subject
            .Messages.Last(message => message.Role == "user")
            .ContentParts.Should().NotBeNull().And.Subject
            .Single(part => part.Kind == ContentPartKind.Image);
        providerImagePart.DataBase64.Should().Be(Convert.ToBase64String(new byte[] { 1, 2, 3 }));
        providerImagePart.FileRef.Should().BeNull();
        workItem.StepState.Messages.Single().ContentParts.Single(part => part.Kind == Aevatar.AI.Abstractions.ChatContentPartKind.Image)
            .DataBase64.Should().BeEmpty();
        workItem.StepState.Messages.Single().ContentParts.Single(part => part.Kind == Aevatar.AI.Abstractions.ChatContentPartKind.Image)
            .FileRef.ArtifactId.Should().Be("workflow-file://wf-file-1");
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenHistoricalFileRefHasExpired_ShouldSendUnavailableMarker()
    {
        var provider = new RecordingProvider();
        var artifactPort = Substitute.For<IFileArtifactReadPort>();
        var executor = CreateExecutor(provider, fileArtifactReadPort: artifactPort);
        var workItem = BuildFinalNoToolsWorkItem();
        var expiredPart = new ContentPart
        {
            Kind = ContentPartKind.Text,
            MediaType = "application/pdf",
            Name = "expired.pdf",
            FileRef = new LlmChatFileRef
            {
                FileId = "wf-file-expired",
                ArtifactId = "workflow-file://wf-file-expired",
                SourceKind = LlmChatFileSourceKind.ChatInput,
                FileName = "expired.pdf",
                MediaType = "application/pdf",
                ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),
            },
        };
        workItem.StepState.Messages.Clear();
        workItem.StepState.Messages.Add(AgentRunReplyStepMappers.ToProto(ChatMessage.User([
            ContentPart.TextPart("summarize the earlier attachment"),
            expiredPart,
        ])));

        await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        var providerPart = provider.Requests.Should().ContainSingle().Subject
            .Messages.Last(message => message.Role == "user")
            .ContentParts.Should().NotBeNull().And.Subject
            .Single(part => part.Text?.Contains("Attachment unavailable", StringComparison.Ordinal) == true);
        providerPart.FileRef.Should().BeNull();
        workItem.StepState.Messages.Single().ContentParts
            .Single(part => part.FileRef is not null)
            .FileRef.ArtifactId.Should().Be("workflow-file://wf-file-expired");
        artifactPort.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenFileRefMaterializationExceedsLimit_ShouldThrowGenericInvalidOperation()
    {
        var provider = new RecordingProvider();
        var oversized = new byte[10 * 1024 * 1024 + 1];
        var artifactPort = new RecordingFileArtifactReadPort(
            new ApplicationFileArtifactRef
            {
                FileId = "wf-file-large",
                ArtifactId = "workflow-file://wf-file-large",
                SourceKind = FileArtifactSourceKind.ChatInput,
                FileName = "large.png",
                MediaType = "image/png",
                SizeBytes = oversized.LongLength,
            },
            oversized);
        var executor = CreateExecutor(provider, fileArtifactReadPort: artifactPort);
        var workItem = BuildFinalNoToolsWorkItem();
        workItem.StepState.Messages.Clear();
        workItem.StepState.Messages.Add(AgentRunReplyStepMappers.ToProto(ChatMessage.User([
            ContentPart.ImageFileRefPart(
                new LlmChatFileRef
                {
                    FileId = "wf-file-large",
                    ArtifactId = "workflow-file://wf-file-large",
                    SourceKind = LlmChatFileSourceKind.ChatInput,
                    FileName = "large.png",
                    MediaType = "image/png",
                    SizeBytes = oversized.LongLength,
                }),
        ])));

        var act = async () => await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Contain("Referenced chat media exceeds the materialization size limit");
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenProviderCallsTool_ShouldReturnFactsBeforeExecutingTool()
    {
        var tool = new CountingTool("use_skill");
        var provider = new ToolCallProvider(tool.Name);
        var executor = CreateToolEnabledExecutor(tool, provider);
        var workItem = BuildToolEnabledWorkItem();

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        execution.Continuation.LlmStepResult.ToolCalls.Should().ContainSingle()
            .Which.Name.Should().Be(tool.Name);
        execution.AuthorizedToolStep.Should().NotBeNull();
        tool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WithExactSkillRecovery_ShouldSearchDespitePriorTurnUseSkill()
    {
        var useSkill = new CountingTool("use_skill");
        var searchSkills = new CountingTool("ornn_search_skills");
        var provider = new RecordingProvider();
        var recovery = new AgentSkillRecoveryContext(
            RequireInitialOrnnSearch: true,
            RequireOrnnSearchOnBlocker: true,
            CommandName: "invoice-ocr-policy-review",
            OriginalCommand: "使用精确名称为 invoice-ocr-policy-review 的 skill",
            PrimarySkillName: "invoice-ocr-policy-review",
            MaxOrnnSearchAttempts: 2,
            CommandArguments: "提取发票并运行 workflow");
        var toolContext = AgentToolExecutionContext.Empty with { SkillRecovery = recovery };
        var executor = CreateToolEnabledExecutor([useSkill, searchSkills], provider, toolContext: toolContext);
        var workItem = BuildToolEnabledWorkItem();
        workItem.StepState.ToolContext = toolContext.ToPayload();
        var priorUseSkillCall = new ToolCall
        {
            Id = "prior-use-skill",
            Name = "use_skill",
            ArgumentsJson = "{\"skill\":\"invoice-ocr-policy-review\"}",
        };
        workItem.StepState.Messages.Insert(0, AgentRunReplyStepMappers.ToProto(new ChatMessage
        {
            Role = "assistant",
            ToolCalls = [priorUseSkillCall],
        }));
        workItem.StepState.Messages.Insert(1, AgentRunReplyStepMappers.ToProto(
            ChatMessage.Tool(priorUseSkillCall.Id, "{\"status\":\"success\"}")));

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        provider.Requests.Should().BeEmpty();
        var call = execution.Continuation.LlmStepResult.ToolCalls.Should().ContainSingle().Which;
        call.Name.Should().Be("ornn_search_skills");
        call.ArgumentsJson.Should().Contain("invoice-ocr-policy-review");
        execution.AuthorizedToolStep.Should().NotBeNull();
        execution.AuthorizedToolCallSafeties.Should().ContainSingle()
            .Which.ToolName.Should().Be("ornn_search_skills");
        useSkill.ExecuteCount.Should().Be(0);
        searchSkills.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenMiddlewareRemovesRecoveryTool_ShouldNotAuthorizeDeterministicCall()
    {
        var tool = new CountingTool("use_skill");
        var provider = new RecordingProvider();
        var toolContext = AgentToolExecutionContext.Empty with
        {
            SkillRecovery = new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: true,
                RequireOrnnSearchOnBlocker: true,
                CommandName: "invoice-ocr-policy-review",
                OriginalCommand: "使用精确名称为 invoice-ocr-policy-review 的 skill",
                PrimarySkillName: "invoice-ocr-policy-review",
                MaxOrnnSearchAttempts: 2),
        };
        var executor = CreateToolEnabledExecutor(
            tool,
            provider,
            [new RemoveToolsMiddleware()],
            toolContext);
        var workItem = BuildToolEnabledWorkItem();
        workItem.StepState.ToolContext = toolContext.ToPayload();

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        provider.Requests.Should().ContainSingle().Which.Tools.Should().BeNull();
        execution.Continuation.LlmStepResult.ToolCalls.Should().BeEmpty();
        execution.AuthorizedToolStep.Should().BeNull();
        tool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenFinalAnswerReportsBlocker_ShouldRecoverWithoutPublishingFailureText()
    {
        var searchSkills = new CountingTool("ornn_search_skills");
        var provider = new RecordingProvider("无法完成：workflow backend unavailable");
        var toolContext = AgentToolExecutionContext.Empty with
        {
            SkillRecovery = new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: false,
                RequireOrnnSearchOnBlocker: true,
                CommandName: "invoice-ocr-policy-review",
                OriginalCommand: "使用精确名称为 invoice-ocr-policy-review 的 skill",
                PrimarySkillName: "invoice-ocr-policy-review",
                MaxOrnnSearchAttempts: 2),
        };
        var executor = CreateToolEnabledExecutor(
            searchSkills,
            provider,
            toolContext: toolContext);
        var workItem = BuildToolEnabledWorkItem();
        workItem.StepState.ToolContext = toolContext.ToPayload();
        var useSkillCall = new ToolCall
        {
            Id = "call-use-skill",
            Name = "use_skill",
            ArgumentsJson = "{\"skill\":\"invoice-ocr-policy-review\"}",
        };
        var useSkillMessage = AgentRunReplyStepMappers.ToProto(new ChatMessage
        {
            Role = "assistant",
            ToolCalls = [useSkillCall],
        });
        var useSkillResult = AgentRunReplyStepMappers.ToProto(
            ChatMessage.Tool(useSkillCall.Id, "{\"status\":\"success\"}"));
        workItem.StepState.Messages.Add(useSkillMessage);
        workItem.StepState.Messages.Add(useSkillResult);
        workItem.StepState.PendingHistoryMessages.Add(useSkillMessage.Clone());
        workItem.StepState.PendingHistoryMessages.Add(useSkillResult.Clone());
        var publishedChunks = new List<LLMStreamChunk>();
        workItem = workItem with
        {
            ReportChunkAsync = (chunk, _) =>
            {
                publishedChunks.Add(chunk);
                return Task.CompletedTask;
            },
        };

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        provider.Requests.Should().ContainSingle();
        publishedChunks.Should().BeEmpty();
        execution.Continuation.LlmStepResult.AccumulatedText.Should().BeEmpty();
        execution.Continuation.LlmStepResult.Content.Should().BeEmpty();
        execution.Continuation.LlmStepResult.ToolCalls.Should().ContainSingle()
            .Which.Name.Should().Be("ornn_search_skills");
        execution.AuthorizedToolStep.Should().NotBeNull();
        execution.AuthorizedToolCallSafeties.Should().ContainSingle()
            .Which.ToolName.Should().Be("ornn_search_skills");
        searchSkills.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenFinalAnswerHasNoBlocker_ShouldPublishDeferredText()
    {
        var searchSkills = new CountingTool("ornn_search_skills");
        var provider = new RecordingProvider("workflow completed");
        var toolContext = AgentToolExecutionContext.Empty with
        {
            SkillRecovery = new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: false,
                RequireOrnnSearchOnBlocker: true,
                CommandName: "invoice-ocr-policy-review",
                OriginalCommand: "使用精确名称为 invoice-ocr-policy-review 的 skill",
                PrimarySkillName: "invoice-ocr-policy-review",
                MaxOrnnSearchAttempts: 2),
        };
        var executor = CreateToolEnabledExecutor(
            searchSkills,
            provider,
            toolContext: toolContext);
        var workItem = BuildToolEnabledWorkItem();
        workItem.StepState.ToolContext = toolContext.ToPayload();
        var useSkillCall = new ToolCall
        {
            Id = "call-use-skill",
            Name = "use_skill",
            ArgumentsJson = "{\"skill\":\"invoice-ocr-policy-review\"}",
        };
        var useSkillMessage = AgentRunReplyStepMappers.ToProto(new ChatMessage
        {
            Role = "assistant",
            ToolCalls = [useSkillCall],
        });
        var useSkillResult = AgentRunReplyStepMappers.ToProto(
            ChatMessage.Tool(useSkillCall.Id, "{\"status\":\"success\"}"));
        workItem.StepState.Messages.Add(useSkillMessage);
        workItem.StepState.Messages.Add(useSkillResult);
        workItem.StepState.PendingHistoryMessages.Add(useSkillMessage.Clone());
        workItem.StepState.PendingHistoryMessages.Add(useSkillResult.Clone());
        var publishedChunks = new List<LLMStreamChunk>();
        workItem = workItem with
        {
            ReportChunkAsync = (chunk, _) =>
            {
                publishedChunks.Add(chunk);
                return Task.CompletedTask;
            },
        };

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        provider.Requests.Should().ContainSingle();
        publishedChunks.Should().ContainSingle().Which.DeltaContent.Should().Be("workflow completed");
        execution.Continuation.LlmStepResult.AccumulatedText.Should().Be("workflow completed");
        execution.Continuation.LlmStepResult.Content.Should().Be("workflow completed");
        execution.Continuation.LlmStepResult.ToolCalls.Should().BeEmpty();
        execution.AuthorizedToolStep.Should().BeNull();
        searchSkills.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WithPersistedSuccessfulSkillLoad_ShouldIgnoreFailureWordsInInstructions()
    {
        var useSkill = new CountingTool("use_skill");
        var searchSkills = new CountingTool("ornn_search_skills");
        var provider = new RecordingProvider("workflow completed");
        var recovery = new AgentSkillRecoveryContext(
            RequireInitialOrnnSearch: false,
            RequireOrnnSearchOnBlocker: true,
            CommandName: "invoice-ocr-policy-review",
            OriginalCommand: "使用精确名称为 invoice-ocr-policy-review 的 skill",
            PrimarySkillName: "invoice-ocr-policy-review",
            MaxOrnnSearchAttempts: 2,
            CommandArguments: "提取发票并运行 workflow");
        var toolContext = AgentToolExecutionContext.Empty with { SkillRecovery = recovery };
        var executor = CreateToolEnabledExecutor(
            [useSkill, searchSkills],
            provider,
            toolContext: toolContext);
        var workItem = BuildToolEnabledWorkItem();
        workItem.StepState.ToolContext = toolContext.ToPayload();
        var call = new ToolCall
        {
            Id = "call-load-invoice-skill",
            Name = "use_skill",
            ArgumentsJson = "{\"skill\":\"invoice-ocr-policy-review\"}",
        };
        var assistant = AgentRunReplyStepMappers.ToProto(new ChatMessage
        {
            Role = "assistant",
            ToolCalls = [call],
        });
        var result = AgentRunReplyStepMappers.ToProto(ToolCallLoop.BuildToolResultMessage(
            call.Id,
            call.Name,
            "# invoice-ocr-policy-review\n\nIf extraction failed, return a typed error artifact."));
        workItem.StepState.Messages.Add(assistant);
        workItem.StepState.Messages.Add(result);
        workItem.StepState.PendingHistoryMessages.Add(assistant.Clone());
        workItem.StepState.PendingHistoryMessages.Add(result.Clone());

        var roundTripped = AgentRunReplyStepMappers.FromProto(result);
        roundTripped.ToolResultView!.SkillLoad!.Status.Should().Be(ToolResultViewStatus.Success);

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        provider.Requests.Should().ContainSingle();
        execution.Continuation.LlmStepResult.Content.Should().Be("workflow completed");
        execution.Continuation.LlmStepResult.ToolCalls.Should().BeEmpty();
        execution.AuthorizedToolStep.Should().BeNull();
        searchSkills.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WithPersistedStructuredSearch_ShouldLoadMatchedSkillWithoutProvider()
    {
        var useSkill = new CountingTool("use_skill");
        var provider = new RecordingProvider();
        var recovery = new AgentSkillRecoveryContext(
            RequireInitialOrnnSearch: true,
            RequireOrnnSearchOnBlocker: true,
            CommandName: "invoice-review",
            OriginalCommand: "查找并运行发票审核 skill",
            PrimarySkillName: null,
            MaxOrnnSearchAttempts: 2,
            CommandArguments: "提取发票并运行 workflow");
        var toolContext = AgentToolExecutionContext.Empty with { SkillRecovery = recovery };
        var executor = CreateToolEnabledExecutor(useSkill, provider, toolContext: toolContext);
        var workItem = BuildToolEnabledWorkItem();
        workItem.StepState.ToolContext = toolContext.ToPayload();
        var call = new ToolCall
        {
            Id = "call-search-invoice-skill",
            Name = "ornn_search_skills",
            ArgumentsJson = "{\"query\":\"invoice\",\"scope\":\"mixed\"}",
        };
        var assistant = AgentRunReplyStepMappers.ToProto(new ChatMessage
        {
            Role = "assistant",
            ToolCalls = [call],
        });
        var result = AgentRunReplyStepMappers.ToProto(ToolCallLoop.BuildToolResultMessage(
            call.Id,
            call.Name,
            """
            {"result_type":"skill_search","status":"success","matches":[{"skill_name":"invoice-ocr-policy-review","description":"Review invoices","is_private":false,"category":"finance","tags":["invoice"]}],"http_status":200,"text":"one match"}
            """));
        workItem.StepState.Messages.Add(assistant);
        workItem.StepState.Messages.Add(result);
        workItem.StepState.PendingHistoryMessages.Add(assistant.Clone());
        workItem.StepState.PendingHistoryMessages.Add(result.Clone());

        var roundTripped = AgentRunReplyStepMappers.FromProto(result);
        var search = roundTripped.ToolResultView!.SkillSearch!;
        search.Status.Should().Be(ToolResultViewStatus.Success);
        search.HttpStatus.Should().Be(200);
        search.Matches.Should().ContainSingle().Which.SkillName.Should().Be("invoice-ocr-policy-review");

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        provider.Requests.Should().BeEmpty();
        var planned = execution.Continuation.LlmStepResult.ToolCalls.Should().ContainSingle().Which;
        planned.Name.Should().Be("use_skill");
        planned.ArgumentsJson.Should().Contain("invoice-ocr-policy-review");
        execution.AuthorizedToolStep.Should().NotBeNull();
    }

    [Fact]
    public void AgentRunChatMessage_WithTypedFailure_ShouldRoundTripFailureFacts()
    {
        var source = new ChatMessage
        {
            Role = "tool",
            ToolCallId = "call-failed",
            Content = "safe failure",
            ToolResultView = new ToolResultView(
                "use_skill",
                SkillSearch: null,
                SkillLoad: null,
                Failure: new ToolFailureResultView(
                    AgentToolReceiptStatus.AuthorizationRequired,
                    "AUTHORIZATION_REQUIRED",
                    "Authorize Ornn access.")),
        };

        var roundTripped = AgentRunReplyStepMappers.FromProto(
            AgentRunReplyStepMappers.ToProto(source));

        roundTripped.ToolResultView.Should().NotBeNull();
        roundTripped.ToolResultView!.ToolName.Should().Be("use_skill");
        roundTripped.ToolResultView.Failure.Should().BeEquivalentTo(source.ToolResultView.Failure);
    }

    [Fact]
    public async Task NyxIdChatTurnExecutor_WithExactSkillRecovery_ShouldAdvanceSearchThenUseWithoutCallingProvider()
    {
        var useSkill = new CountingTool("use_skill");
        var searchSkills = new CountingTool("ornn_search_skills");
        var provider = new RecordingProvider();
        var toolContext = AgentToolExecutionContext.Empty with
        {
            SkillRecovery = new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: true,
                RequireOrnnSearchOnBlocker: true,
                CommandName: "invoice-ocr-policy-review",
                OriginalCommand: "使用精确名称为 invoice-ocr-policy-review 的 skill",
                PrimarySkillName: "invoice-ocr-policy-review",
                MaxOrnnSearchAttempts: 2),
        };
        var generationExecutor = CreateToolEnabledExecutor(
            [useSkill, searchSkills],
            provider,
            toolContext: toolContext);
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var session = new NyxIdChatTransientExecutionSession();

        var first = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = BuildOperationKey("step-search", "operation-search"),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "提取发票并运行 workflow",
                        SessionId = "turn-1",
                        ToolContext = toolContext.ToPayload(),
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var searchCall = first.Result.Llm.ToolCalls.Should().ContainSingle().Which;
        searchCall.ToolName.Should().Be("ornn_search_skills");

        var toolResult = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = BuildOperationKey("step-search-result", "operation-search-result"),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = searchCall.CallId,
                    ToolName = searchCall.ToolName,
                    ArgumentsJson = searchCall.ArgumentsJson,
                    MayChangeExternalState = searchCall.Safety.MayChangeExternalState,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        toolResult.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);

        var second = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = BuildOperationKey("step-use", "operation-use"),
                Llm = new NyxIdChatLLMOperationInput { ContinueSession = true },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        second.Result.Llm.ToolCalls.Should().ContainSingle()
            .Which.ToolName.Should().Be("use_skill");
        provider.Requests.Should().BeEmpty();
        useSkill.ExecuteCount.Should().Be(0);
        searchSkills.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildLlmStepContinuation_ShouldSnapshotExactProviderOwnedCallSafety()
    {
        var tool = new EffectClassifiedTool("repository_update");
        var provider = new ToolCallProvider(tool.Name);
        var executor = CreateToolEnabledExecutor(tool, provider);
        var workItem = BuildToolEnabledWorkItem();

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        tool.ClassifiedArguments.Should().Be("{}");
        var snapshot = execution.AuthorizedToolCallSafeties.Should().ContainSingle().Which;
        snapshot.CallId.Should().Be("call-1");
        snapshot.ToolName.Should().Be(tool.Name);
        snapshot.ArgumentsJson.Should().Be("{}");
        snapshot.CallSafety.RequiresApproval.Should().BeFalse();
        snapshot.CallSafety.IsReadOnly.Should().BeFalse();
        snapshot.CallSafety.IsDestructive.Should().BeTrue();
        snapshot.SideEffectKind.Should().Be("repository.update");
        snapshot.Presentation.Should().NotBeNull();
        snapshot.Presentation!.NyxIdOperation.ConnectedServiceId.Should()
            .Be("connected-service-alpha");
        snapshot.Presentation.NyxIdOperation.ServiceSlug.Should().Be("service-slug-alpha");
        snapshot.Presentation.NyxIdOperation.CatalogServiceSlug.Should().Be("catalog-slug-alpha");
        snapshot.Presentation.NyxIdOperation.ReadinessCapabilityId.Should()
            .Be("readiness-capability-alpha");
    }

    [Fact]
    public async Task NyxIdCatalogTool_ThroughNyxIdChatTurnExecutor_ShouldAcceptCurrentEntriesEnvelope()
    {
        var handler = new StaticResponseHandler("""{"entries":[{"slug":"api-github"}]}""");
        using var httpClient = new HttpClient(handler);
        using var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            httpClient);
        var tool = new NyxIdCatalogTool(client);
        var generationExecutor = CreateToolEnabledExecutor(
            tool,
            new ToolCallProvider(tool.Name),
            toolContext: AgentToolExecutionContext.Empty with
            {
                Credentials = AgentToolCredentials.Empty with
                {
                    NyxIdAccessToken = "token-1",
                },
            });
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var session = new NyxIdChatTransientExecutionSession();
        var llmExecution = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = BuildOperationKey("step-llm", "operation-llm"),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "connect GitHub",
                        SessionId = "turn-1",
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var call = llmExecution.Result.Llm.ToolCalls.Should().ContainSingle().Which;

        var toolExecution = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = BuildOperationKey("step-tool", "operation-tool"),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = call.CallId,
                    ToolName = call.ToolName,
                    ArgumentsJson = call.ArgumentsJson,
                    MayChangeExternalState = call.Safety.MayChangeExternalState,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        toolExecution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Tool,
            "catalog GET is read-only and must not require an outcome receipt");
        call.Safety.IsReadOnly.Should().BeTrue();
        call.Safety.MayChangeExternalState.Should().BeFalse();
        toolExecution.Result.Tool.ResultJson.Should().Contain("api-github");
        toolExecution.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        toolExecution.Result.Tool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        handler.Method.Should().Be(HttpMethod.Get);
        handler.Path.Should().Be("/api/v1/catalog");
    }

    [Fact]
    public async Task NyxIdChatTurnExecutor_ShouldMergeDirectInputPartFileRefsIntoInitialPlanningToolContext()
    {
        var generationExecutor = new RecordingTurnGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var session = new NyxIdChatTransientExecutionSession();
        var baseContext = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-direct", "call-direct"),
            Caller = new AgentToolCallerContext("scope-direct", "owner-direct", "response-direct"),
            InputFileRefs =
            [
                new Aevatar.AI.Abstractions.ChatFileRef
                {
                    FileId = "file-existing",
                    ArtifactId = "workflow-file://file-existing",
                    SourceKind = Aevatar.AI.Abstractions.ChatFileSourceKind.Generated,
                    FileName = "existing.txt",
                    MediaType = "text/plain",
                },
            ],
        };

        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = BuildOperationKey("step-llm", "operation-llm"),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "summarize files",
                        ToolContext = baseContext.ToPayload(),
                        InputParts =
                        {
                            new ChatContentPart
                            {
                                Kind = ChatContentPartKind.Text,
                                Text = "see direct file",
                                FileRef = new Aevatar.AI.Abstractions.ChatFileRef
                                {
                                    FileId = "file-direct",
                                    ArtifactId = "workflow-file://file-direct",
                                    SourceKind = Aevatar.AI.Abstractions.ChatFileSourceKind.ChatInput,
                                    SourceMessageId = "om_direct",
                                    SourceResourceKey = "file_key_direct",
                                    FileName = "direct.pdf",
                                    MediaType = "application/pdf",
                                    SizeBytes = 123,
                                },
                            },
                            new ChatContentPart
                            {
                                Kind = ChatContentPartKind.Text,
                                Text = "duplicate",
                                FileRef = new Aevatar.AI.Abstractions.ChatFileRef
                                {
                                    FileId = "file-direct-copy",
                                    ArtifactId = "workflow-file://file-direct",
                                    SourceKind = Aevatar.AI.Abstractions.ChatFileSourceKind.ChatInput,
                                    FileName = "direct-copy.pdf",
                                    MediaType = "application/pdf",
                                },
                            },
                            new ChatContentPart
                            {
                                Kind = ChatContentPartKind.Text,
                                Text = "file id only",
                                FileRef = new Aevatar.AI.Abstractions.ChatFileRef
                                {
                                    FileId = "file-only",
                                    SourceKind = Aevatar.AI.Abstractions.ChatFileSourceKind.ChatInput,
                                    FileName = "file-only.txt",
                                    MediaType = "text/plain",
                                },
                            },
                        },
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        generationExecutor.InitialRequest.Should().NotBeNull();
        var toolContext = AgentToolExecutionContextMapper.FromPayload(generationExecutor.InitialRequest!.ToolContext);
        toolContext.Request.RequestId.Should().Be("request-direct");
        toolContext.Caller.ScopeId.Should().Be("scope-direct");
        toolContext.InputFileRefs.Select(static fileRef => fileRef.FileId)
            .Should().Equal("file-existing", "file-direct", "file-only");
        toolContext.InputFileRefs.Select(static fileRef => fileRef.ArtifactId)
            .Should().Equal("workflow-file://file-existing", "workflow-file://file-direct", string.Empty);
        toolContext.InputFileRefs[1].SourceMessageId.Should().Be("om_direct");
        toolContext.InputFileRefs[1].SourceResourceKey.Should().Be("file_key_direct");
        toolContext.InputFileRefs[1].SizeBytes.Should().Be(123);
    }

    [Fact]
    public void NyxIdCatalogTool_HttpErrorEnvelope_ShouldReturnTypedFailureReceipt()
    {
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" });
        var tool = new NyxIdCatalogTool(client);

        var receipt = ((IAgentTool)tool).CreateResultReceipt(
            "call-1",
            tool.Name,
            "{}",
            "{\"error\":true,\"status\":401,\"body\":\"secret upstream body\"}");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_CATALOG_HTTP_401");
        receipt.ResultJson.Should().NotContain("secret upstream body");
    }

    [Fact]
    public void NyxIdCatalogTool_UnrecognizedJson_ShouldLeaveOutcomeUnverified()
    {
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" });
        var tool = new NyxIdCatalogTool(client);

        var receipt = ((IAgentTool)tool).CreateResultReceipt(
            "call-1",
            tool.Name,
            "{}",
            "{}");

        receipt.Should().BeNull();
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenMiddlewareRemovesTools_ShouldRejectFabricatedToolCall()
    {
        var tool = new CountingTool("use_skill");
        var provider = new ToolCallProvider(tool.Name);
        var executor = CreateToolEnabledExecutor(tool, provider, [new RemoveToolsMiddleware()]);

        var llmWorkItem = BuildToolEnabledWorkItem();
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = BuildToolStepWorkItem(llmWorkItem, execution.Continuation);
        var continuation = await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            execution.AuthorizedToolStep,
            CancellationToken.None);

        provider.Requests.Should().ContainSingle().Which.Tools.Should().BeNull();
        continuation.ToolStepResult.Should().NotBeNull();
        var rejected = continuation.ToolStepResult.ResultMessages.Should().ContainSingle().Which;
        rejected.Content.Should().Be("{\"error\":\"The tool request failed.\"}");
        var receipt = continuation.ToolStepResult.ToolReceipts.Should().ContainSingle().Which;
        receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("tool_execution_error");
        tool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildToolStepContinuation_WithExactChatOperation_ShouldExposeTypedChatContext()
    {
        var tool = new ChatContextCapturingTool("use_skill");
        var executor = CreateToolEnabledExecutor(
            tool,
            new ToolCallProvider(tool.Name),
            toolContext: AgentToolExecutionContext.Empty with
            {
                Chat = new AgentChatInvocationContext(
                    AgentChatInvocationSurface.NyxIdAssistant,
                    "conversation-alpha",
                    "turn-alpha",
                    "task-planning",
                    null,
                    null),
            });
        var llmWorkItem = BuildToolEnabledWorkItem();
        var execution = await executor.BuildLlmStepExecutionAsync(llmWorkItem, CancellationToken.None);
        var toolWorkItem = BuildToolStepWorkItem(llmWorkItem, execution.Continuation);

        await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            execution.AuthorizedToolStep!.WithChatOperation(
                new NyxIdChatOperationKey
                {
                    ConversationActorId = "conversation-alpha",
                    TurnId = "turn-alpha",
                    TaskId = "task-alpha",
                    StepId = "step-alpha",
                    OperationId = "operation-alpha",
                    OperationGeneration = 1,
                },
                idempotencyKey: null,
                operationAdmission: null),
            CancellationToken.None);

        tool.SeenChat.Should().Be(new AgentChatInvocationContext(
            AgentChatInvocationSurface.NyxIdAssistant,
            "conversation-alpha",
            "turn-alpha",
            "task-alpha",
            "step-alpha",
            null));
        tool.SeenExternalMetadata.Keys.Should().NotContain(
            ["conversation_id", "turn_id", "task_id", "step_id"]);
    }

    [Fact]
    public async Task BuildToolStepContinuation_WithoutMatchingAuthorization_ShouldRejectAllPendingCalls()
    {
        var registeredTool = new CountingTool("use_skill");
        var executor = CreateToolEnabledExecutor(registeredTool, new RecordingProvider());
        var workItem = BuildToolEnabledWorkItem();
        workItem.StepState.NextStepIndex = 2;
        workItem.StepState.PendingToolCalls.AddRange(
        [
            new AgentRunToolCall { Id = "call-1", Name = registeredTool.Name, ArgumentsJson = "{}" },
            new AgentRunToolCall { Id = "call-2", Name = registeredTool.Name, ArgumentsJson = "{}" },
        ]);
        workItem = workItem with { StepIndex = 2 };

        var continuation = await executor.BuildToolStepContinuationAsync(
            workItem,
            authorizedToolStep: null,
            CancellationToken.None);

        continuation.StepIndex.Should().Be(3);
        var result = continuation.ToolStepResult;
        result.Should().NotBeNull();
        result.AdvanceRound.Should().BeTrue();
        result.ResultMessages.Select(static message => message.ToolCallId)
            .Should().Equal("call-1", "call-2");
        result.ResultMessages.Should().OnlyContain(static message =>
            message.Content.Contains("not authorized", StringComparison.Ordinal));
        registeredTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildToolStepContinuation_WithDurableAuthorization_ShouldExecuteWhenCatalogStillAdmitsTool()
    {
        var registeredTool = new CountingTool("use_skill");
        var executor = CreateToolEnabledExecutor(
            registeredTool,
            new ToolCallProvider(registeredTool.Name));
        var llmWorkItem = BuildToolEnabledWorkItem();
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = BuildDurablyAuthorizedToolStepWorkItem(llmWorkItem, execution.Continuation);

        var continuation = await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            authorizedToolStep: null,
            CancellationToken.None);

        continuation.ToolStepResult.Should().NotBeNull();
        continuation.ToolStepResult.ResultMessages.Should().ContainSingle();
        continuation.ToolStepResult.AuthorizationOutcome.Should().Be(
            AgentRunToolAuthorizationOutcome.DurableMatched);
        registeredTool.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildApprovedToolStepContinuation_WhenCallbackActivityDiffers_ShouldUseOriginalToolRequestIdentity()
    {
        var registeredTool = new CountingTool("use_skill");
        var executor = CreateToolEnabledExecutor(
            registeredTool,
            new ToolCallProvider(registeredTool.Name));
        var llmWorkItem = BuildToolEnabledWorkItem();
        var originalToolRequestId = llmWorkItem.Request.Activity.Id;
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = BuildDurablyAuthorizedToolStepWorkItem(llmWorkItem, execution.Continuation);
        var callbackRequest = toolWorkItem.Request.Clone();
        callbackRequest.Activity.Id = "approval-callback-1";
        toolWorkItem = toolWorkItem with { Request = callbackRequest };
        var pendingCall = toolWorkItem.StepState.PendingToolCalls.Should().ContainSingle().Subject;
        var pendingApproval = new AgentRunPendingToolApprovalState
        {
            RunId = toolWorkItem.RunId,
            CorrelationId = callbackRequest.CorrelationId,
            Attempt = toolWorkItem.Attempt,
            StepIndex = toolWorkItem.StepIndex,
            ApprovalRequestId = "tool-approval-1",
            ToolRequestId = originalToolRequestId,
            ToolCallId = pendingCall.Id,
            ToolName = pendingCall.Name,
            ArgumentsSha256 = AgentToolArgumentsDigest.ComputeSha256(pendingCall.ArgumentsJson),
            Decision = AgentRunToolApprovalDecision.Approved,
        };

        var continuation = await executor.BuildApprovedToolStepContinuationAsync(
            toolWorkItem,
            pendingApproval,
            CancellationToken.None);

        continuation.ToolStepResult.Should().NotBeNull();
        continuation.ToolStepResult.ResultMessages.Should().ContainSingle();
        continuation.ToolStepResult.AuthorizationOutcome.Should().Be(
            AgentRunToolAuthorizationOutcome.DurableMatched);
        registeredTool.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildApprovedToolStepContinuation_GenericSentinelApproval_ShouldNotAuthorizeConnectedEffect()
    {
        var connectedEffect = new EffectClassifiedTool("svc-lark__approval_create");
        var executor = CreateToolEnabledExecutor(
            connectedEffect,
            new ToolCallProvider(connectedEffect.Name));
        var llmWorkItem = BuildToolEnabledWorkItem();
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = BuildDurablyAuthorizedToolStepWorkItem(
            llmWorkItem,
            execution.Continuation);
        var genericApproval = new AgentRunPendingToolApprovalState
        {
            RunId = toolWorkItem.RunId,
            CorrelationId = toolWorkItem.Request.CorrelationId,
            Attempt = toolWorkItem.Attempt,
            StepIndex = toolWorkItem.StepIndex,
            ApprovalRequestId = "sentinel-request-uuid",
            ToolRequestId = llmWorkItem.Request.Activity.Id,
            ToolCallId = "generic-call",
            ToolName = "generic-tool",
            ArgumentsSha256 = AgentToolArgumentsDigest.ComputeSha256("{}"),
            SubjectKind = "nyxid.approval-service",
            SubjectId = "tool_approval",
            Decision = AgentRunToolApprovalDecision.Approved,
        };

        var act = () => executor.BuildApprovedToolStepContinuationAsync(
            toolWorkItem,
            genericApproval,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no longer matches the suspended call*");
        connectedEffect.ExecuteCount.Should().Be(0,
            "a sentinel request UUID is scoped to its exact generic tool call, not svc-lark");
    }

    [Fact]
    public async Task BuildToolStepContinuation_WithDurableAuthorization_ShouldMatchSafetyInsideToolContextScope()
    {
        var registeredTool = new ContextClassifiedTool("use_skill");
        var executor = CreateToolEnabledExecutor(
            registeredTool,
            new ToolCallProvider(registeredTool.Name),
            toolContext: BuildSafetyModeToolContext(SafetyMode.ReadOnly));
        var llmWorkItem = BuildToolEnabledWorkItem();
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = BuildDurablyAuthorizedToolStepWorkItem(llmWorkItem, execution.Continuation);

        var continuation = await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            authorizedToolStep: null,
            CancellationToken.None);

        continuation.ToolStepResult.Should().NotBeNull();
        continuation.ToolStepResult.ResultMessages.Should().ContainSingle();
        registeredTool.ExecuteCount.Should().Be(1);
    }

    [Theory]
    [InlineData(DefinitionDrift.Description)]
    [InlineData(DefinitionDrift.ParametersSchema)]
    [InlineData(DefinitionDrift.ApprovalMode)]
    public async Task BuildToolStepContinuation_WhenToolDefinitionDrifts_ShouldRejectBeforeToolExecution(
        DefinitionDrift drift)
    {
        var registeredTool = new DriftableDefinitionTool("use_skill");
        var executor = CreateToolEnabledExecutor(
            registeredTool,
            new ToolCallProvider(registeredTool.Name));
        var llmWorkItem = BuildToolEnabledWorkItem();
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = BuildDurablyAuthorizedToolStepWorkItem(llmWorkItem, execution.Continuation);
        if (drift == DefinitionDrift.ApprovalMode)
        {
            var authorization = toolWorkItem.StepState.PendingToolAuthorizations
                .Should().ContainSingle().Subject;
            authorization.HasRequiresApproval.Should().BeTrue(
                "durable authorization stores the final approval decision, not a nullable provider hint");
            authorization.RequiresApproval.Should().BeFalse();
        }
        registeredTool.Apply(drift);

        var continuation = await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            authorizedToolStep: null,
            CancellationToken.None);

        continuation.ToolStepResult.Should().NotBeNull();
        continuation.ToolStepResult.ResultMessages.Should().OnlyContain(static message =>
            message.Content.Contains("not authorized", StringComparison.Ordinal));
        registeredTool.ExecuteCount.Should().Be(0);
    }

    [Theory]
    [InlineData(SafetyMode.ApprovalUnspecified)]
    [InlineData(SafetyMode.ApprovalRequired)]
    [InlineData(SafetyMode.Destructive)]
    [InlineData(SafetyMode.ReadOnlyChanged)]
    [InlineData(SafetyMode.SideEffectChanged)]
    public async Task BuildToolStepContinuation_WhenCurrentSafetyDrifts_ShouldRejectBeforeToolExecution(
        SafetyMode currentSafety)
    {
        var registeredTool = new ContextClassifiedTool("use_skill");
        var executor = CreateToolEnabledExecutor(
            registeredTool,
            new ToolCallProvider(registeredTool.Name),
            toolContext: BuildSafetyModeToolContext(SafetyMode.ReadOnly));
        var llmWorkItem = BuildToolEnabledWorkItem();
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = BuildDurablyAuthorizedToolStepWorkItem(llmWorkItem, execution.Continuation);
        registeredTool.SafetyOverride = currentSafety;

        var continuation = await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            authorizedToolStep: null,
            CancellationToken.None);

        continuation.ToolStepResult.Should().NotBeNull();
        continuation.ToolStepResult.ResultMessages.Should().OnlyContain(static message =>
            message.Content.Contains("not authorized", StringComparison.Ordinal));
        registeredTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildToolStepContinuation_WhenDurableAuthorizationWasConsumed_ShouldRejectBeforeToolExecution()
    {
        var registeredTool = new CountingTool("use_skill");
        var executor = CreateToolEnabledExecutor(
            registeredTool,
            new ToolCallProvider(registeredTool.Name));
        var llmWorkItem = BuildToolEnabledWorkItem();
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = BuildToolStepWorkItem(llmWorkItem, execution.Continuation);
        var consumedStepState = toolWorkItem.StepState.Clone();
        consumedStepState.PendingToolAuthorizationConsumed = true;
        toolWorkItem = toolWorkItem with { StepState = consumedStepState };

        var continuation = await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            authorizedToolStep: null,
            CancellationToken.None);

        continuation.ToolStepResult.Should().NotBeNull();
        continuation.ToolStepResult.ResultMessages.Should().OnlyContain(static message =>
            message.Content.Contains("not authorized", StringComparison.Ordinal));
        registeredTool.ExecuteCount.Should().Be(0);
    }

    [Theory]
    [InlineData(AuthorizedToolStepMutation.ToolCallId)]
    [InlineData(AuthorizedToolStepMutation.ToolName)]
    [InlineData(AuthorizedToolStepMutation.Arguments)]
    public async Task BuildToolStepContinuation_WhenDurableAuthorizationIsMismatched_ShouldRejectBeforeToolExecution(
        AuthorizedToolStepMutation mutation)
    {
        var registeredTool = new CountingTool("use_skill");
        var executor = CreateToolEnabledExecutor(
            registeredTool,
            new ToolCallProvider(registeredTool.Name));
        var llmWorkItem = BuildToolEnabledWorkItem();
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = MutateToolStepWorkItem(
            BuildDurablyAuthorizedToolStepWorkItem(llmWorkItem, execution.Continuation),
            mutation);

        var continuation = await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            authorizedToolStep: null,
            CancellationToken.None);

        continuation.ToolStepResult.Should().NotBeNull();
        continuation.ToolStepResult.ResultMessages.Should().OnlyContain(static message =>
            message.Content.Contains("not authorized", StringComparison.Ordinal));
        registeredTool.ExecuteCount.Should().Be(0);
    }

    [Theory]
    [InlineData(AuthorizedToolStepMutation.RunId)]
    [InlineData(AuthorizedToolStepMutation.CorrelationId)]
    [InlineData(AuthorizedToolStepMutation.Attempt)]
    [InlineData(AuthorizedToolStepMutation.StepIndex)]
    [InlineData(AuthorizedToolStepMutation.ToolCallCount)]
    [InlineData(AuthorizedToolStepMutation.ToolCallId)]
    [InlineData(AuthorizedToolStepMutation.ToolName)]
    [InlineData(AuthorizedToolStepMutation.Arguments)]
    public async Task BuildToolStepContinuation_WhenAuthorizationIsTampered_ShouldRejectBeforeToolExecution(
        AuthorizedToolStepMutation mutation)
    {
        var registeredTool = new CountingTool("use_skill");
        var executor = CreateToolEnabledExecutor(
            registeredTool,
            new ToolCallProvider(registeredTool.Name));
        var llmWorkItem = BuildToolEnabledWorkItem();
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = MutateToolStepWorkItem(
            BuildToolStepWorkItem(llmWorkItem, execution.Continuation),
            mutation);

        var continuation = await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            execution.AuthorizedToolStep,
            CancellationToken.None);

        execution.AuthorizedToolStep.Should().NotBeNull();
        var result = continuation.ToolStepResult;
        result.Should().NotBeNull();
        result!.ResultMessages.Should().HaveCount(toolWorkItem.StepState.PendingToolCalls.Count);
        result.ResultMessages.Should().OnlyContain(static message =>
            message.Content.Contains("not authorized", StringComparison.Ordinal));
        registeredTool.ExecuteCount.Should().Be(0);
    }

    private static AgentRunReplyGenerationExecutor CreateExecutor(
        RecordingProvider provider,
        IFileArtifactReadPort? fileArtifactReadPort = null)
    {
        var runtime = new ChatRuntime(
            providerFactory: () => provider,
            history: new ChatHistory(),
            toolLoop: new ToolCallLoop(new ToolManager()),
            hooks: null,
            requestBuilder: static _ => new LLMRequest { Messages = [] });
        var plan = new AgentRunReplyStepPlan(
            runtime.CreateStepExecutor(turnCatalog: null),
            new Dictionary<string, string>(),
            LLMControlContext.Empty,
            AgentToolExecutionContext.Empty,
            InitialMessages: [],
            MaxToolRounds: 1,
            DisableTools: true);

        return new AgentRunReplyGenerationExecutor(
            Substitute.For<IActorDispatchPort>(),
            new StaticStepPlanReplyGenerator(plan),
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance,
            fileArtifactReadPort: fileArtifactReadPort);
    }

    private static AgentRunReplyGenerationExecutor CreateToolEnabledExecutor(
        IAgentTool tool,
        ILLMProvider provider,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null,
        AgentToolExecutionContext? toolContext = null,
        IActorDispatchPort? actorDispatchPort = null,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions = null)
        => CreateToolEnabledExecutor(
            [tool],
            provider,
            llmMiddlewares,
            toolContext,
            actorDispatchPort,
            relayOptions);

    private static AgentRunReplyGenerationExecutor CreateToolEnabledExecutor(
        IReadOnlyList<IAgentTool> registeredTools,
        ILLMProvider provider,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null,
        AgentToolExecutionContext? toolContext = null,
        IActorDispatchPort? actorDispatchPort = null,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions = null)
    {
        var tools = new ToolManager();
        tools.Register(registeredTools);
        var runtime = new ChatRuntime(
            () => provider,
            new ChatHistory(),
            new ToolCallLoop(
                tools,
                toolExecutionPort: new ChannelConversationTurnRunnerTests.TestAgentToolExecutionPort()),
            hooks: null,
            requestBuilder: _ => new LLMRequest { Messages = [], Tools = tools.GetAll() },
            llmMiddlewares: llmMiddlewares);
        var plan = new AgentRunReplyStepPlan(
            runtime.CreateStepExecutor(turnCatalog: null),
            new Dictionary<string, string>(),
            LLMControlContext.Empty,
            toolContext ?? AgentToolExecutionContext.Empty,
            InitialMessages: [],
            MaxToolRounds: 1);
        return new AgentRunReplyGenerationExecutor(
            actorDispatchPort ?? Substitute.For<IActorDispatchPort>(),
            new StaticStepPlanReplyGenerator(plan),
            interactiveReplyCollector: null,
            relayOptions,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
    }

    private static AgentRunReplyStepExecutionRequest BuildFinalNoToolsWorkItem(params AgentToolReceipt[] receipts)
    {
        var request = new NeedsLlmReplyEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-1",
            TargetActorId = "conversation-actor",
            Activity = new ChatActivity
            {
                Id = "activity-1",
                Content = new MessageContent { Text = "finish" },
            },
        };
        var stepState = new AgentRunReplyStepState
        {
            RunId = "run-1",
            CorrelationId = "corr-1",
            TargetActorId = "conversation-actor",
            Attempt = 1,
            NextStepIndex = 2,
            Round = 1,
            MaxToolRounds = 1,
            FinalNoToolsStep = true,
        };
        stepState.Messages.Add(AgentRunReplyStepMappers.ToProto(ChatMessage.User("hello")));
        stepState.Messages.Add(AgentRunReplyStepMappers.ToProto(ChatMessage.Assistant("partial")));
        stepState.ToolReceipts.AddRange(receipts.Select(receipt => receipt.Clone()));

        return new AgentRunReplyStepExecutionRequest(
            "run-1",
            "channel-agent-run:run-1",
            Attempt: 1,
            StepIndex: 2,
            request,
            stepState);
    }

    private static AgentRunReplyStepExecutionRequest BuildToolEnabledWorkItem(params AgentToolReceipt[] receipts)
    {
        var request = new NeedsLlmReplyEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-1",
            TargetActorId = "conversation-actor",
            Activity = new ChatActivity
            {
                Id = "activity-1",
                Content = new MessageContent { Text = "run" },
            },
        };
        var stepState = new AgentRunReplyStepState
        {
            RunId = "run-1",
            CorrelationId = "corr-1",
            TargetActorId = "conversation-actor",
            Attempt = 1,
            NextStepIndex = 1,
            MaxToolRounds = 1,
        };
        var userMessage = AgentRunReplyStepMappers.ToProto(ChatMessage.User("run"));
        stepState.Messages.Add(userMessage);
        stepState.PendingHistoryMessages.Add(userMessage.Clone());
        stepState.ToolReceipts.AddRange(receipts.Select(receipt => receipt.Clone()));
        return new AgentRunReplyStepExecutionRequest(
            "run-1",
            "channel-agent-run:run-1",
            Attempt: 1,
            StepIndex: 1,
            request,
            stepState);
    }

    private static AgentToolExecutionContext BuildSafetyModeToolContext(SafetyMode mode) =>
        AgentToolExecutionContext.Empty with
        {
            ExternalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tool_safety_mode"] = mode.ToString(),
            },
        };

    private static NyxIdChatOperationKey BuildOperationKey(string stepId, string operationId) =>
        new()
        {
            ConversationActorId = "conversation-1",
            TurnId = "turn-1",
            TaskId = "task-1",
            StepId = stepId,
            OperationId = operationId,
            OperationGeneration = 1,
        };

    private static AgentRunReplyStepExecutionRequest BuildToolStepWorkItem(
        AgentRunReplyStepExecutionRequest llmWorkItem,
        AgentRunNextLlmStepRequestedEvent continuation)
    {
        var stepState = llmWorkItem.StepState.Clone();
        stepState.NextStepIndex = continuation.StepIndex;
        stepState.PendingToolCalls.Clear();
        stepState.PendingToolCalls.AddRange(continuation.LlmStepResult.ToolCalls.Select(static call => call.Clone()));
        stepState.PendingToolAuthorizations.Clear();
        stepState.PendingToolAuthorizations.AddRange(
            continuation.LlmStepResult.PendingToolAuthorizations.Select(static authorization => authorization.Clone()));
        return llmWorkItem with
        {
            StepIndex = continuation.StepIndex,
            StepState = stepState,
        };
    }

    private static AgentRunReplyStepExecutionRequest BuildDurablyAuthorizedToolStepWorkItem(
        AgentRunReplyStepExecutionRequest llmWorkItem,
        AgentRunNextLlmStepRequestedEvent continuation)
    {
        var workItem = BuildToolStepWorkItem(llmWorkItem, continuation);
        workItem.StepState.PendingToolAuthorizationConsumed = true;
        return workItem with { AllowDurableToolAuthorization = true };
    }

    private static AgentRunReplyStepExecutionRequest MutateToolStepWorkItem(
        AgentRunReplyStepExecutionRequest workItem,
        AuthorizedToolStepMutation mutation)
    {
        if (mutation == AuthorizedToolStepMutation.RunId)
            return workItem with { RunId = "run-tampered" };
        if (mutation == AuthorizedToolStepMutation.CorrelationId)
        {
            var request = workItem.Request.Clone();
            request.CorrelationId = "corr-tampered";
            return workItem with { Request = request };
        }
        if (mutation == AuthorizedToolStepMutation.Attempt)
            return workItem with { Attempt = workItem.Attempt + 1 };
        if (mutation == AuthorizedToolStepMutation.StepIndex)
            return workItem with { StepIndex = workItem.StepIndex + 1 };

        var stepState = workItem.StepState.Clone();
        if (mutation == AuthorizedToolStepMutation.ToolCallCount)
        {
            stepState.PendingToolCalls.Add(new AgentRunToolCall
            {
                Id = "call-2",
                Name = "use_skill",
                ArgumentsJson = "{}",
            });
        }
        else
        {
            var toolCall = stepState.PendingToolCalls.Should().ContainSingle().Subject;
            switch (mutation)
            {
                case AuthorizedToolStepMutation.ToolCallId:
                    toolCall.Id = "call-tampered";
                    break;
                case AuthorizedToolStepMutation.ToolName:
                    toolCall.Name = "tampered_tool";
                    break;
                case AuthorizedToolStepMutation.Arguments:
                    toolCall.ArgumentsJson = "{\"tampered\":true}";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
        }

        return workItem with { StepState = stepState };
    }

    private sealed class RecordingTurnGenerationExecutor : IAgentRunReplyGenerationExecutorPort
    {
        public NeedsLlmReplyEvent? InitialRequest { get; private set; }

        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct)
        {
            InitialRequest = request.Request.Clone();
            return Task.FromResult(new AgentRunReplyStepState
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                NextStepIndex = 1,
                MaxToolRounds = 1,
                ToolContext = request.Request.ToolContext?.Clone(),
                LlmControl = request.Request.LlmControl?.Clone(),
            });
        }

        public Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            Task.FromResult(new AgentRunLlmStepExecution(
                new AgentRunNextLlmStepRequestedEvent
                {
                    RunId = request.RunId,
                    CorrelationId = request.Request.CorrelationId,
                    TargetActorId = request.Request.TargetActorId,
                    Attempt = request.Attempt,
                    StepIndex = request.StepIndex + 1,
                    LlmStepResult = new AgentRunLlmStepResult
                    {
                        FinishReason = "stop",
                    },
                },
                AuthorizedToolStep: null));

        public Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingProvider(string content = "final") : ILLMProvider
    {
        public string Name => "recording-provider";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            yield return new LLMStreamChunk { DeltaContent = content };
            await Task.Yield();
        }
    }

    private sealed class ToolCallProvider(string toolName) : ILLMProvider
    {
        public string Name => "tool-call-provider";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call-1",
                    Name = toolName,
                    ArgumentsJson = "{}",
                },
            };
            await Task.Yield();
        }
    }

    private sealed class TextThenToolCallProvider(string toolName, string content) : ILLMProvider
    {
        public string Name => "text-then-tool-call-provider";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new LLMStreamChunk { DeltaContent = content };
            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call-approval-1",
                    Name = toolName,
                    ArgumentsJson = "{}",
                },
            };
            await Task.Yield();
        }
    }

    private sealed class RemoveToolsMiddleware : ILLMCallMiddleware
    {
        public Task InvokeAsync(LLMCallContext context, Func<Task> next)
        {
            var request = context.Request;
            context.Request = new LLMRequest
            {
                Messages = request.Messages,
                RequestId = request.RequestId,
                Metadata = request.Metadata,
                CallerContext = request.CallerContext,
                ToolContext = request.ToolContext,
                RoutingContext = request.RoutingContext,
                LlmControl = request.LlmControl,
                Tools = null,
                Model = request.Model,
                Temperature = request.Temperature,
                MaxTokens = request.MaxTokens,
                ResponseFormat = request.ResponseFormat,
            };
            return next();
        }
    }

    private sealed class CountingTool(string name) : IAgentTool
    {
        public int ExecuteCount { get; private set; }
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("{}");
        }
    }

    private sealed class ApprovalRequiredTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
        public AgentToolCallSafety GetCallSafety(string argumentsJson) => new(
            RequiresApproval: true,
            IsReadOnly: false,
            IsDestructive: false);

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    public enum DefinitionDrift
    {
        Description,
        ParametersSchema,
        ApprovalMode,
    }

    private sealed class DriftableDefinitionTool : IAgentTool
    {
        private readonly string _name;

        public DriftableDefinitionTool(string name)
        {
            _name = name;
            Description = name;
        }

        public int ExecuteCount { get; private set; }
        public string Name => _name;
        public string Description { get; private set; }
        public string ParametersSchema { get; private set; } = "{}";
        public ToolApprovalMode ApprovalMode { get; private set; } = ToolApprovalMode.NeverRequire;

        public AgentToolCallSafety GetCallSafety(string argumentsJson) => new(
            RequiresApproval: null,
            IsReadOnly: true,
            IsDestructive: false);

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("{}");
        }

        public void Apply(DefinitionDrift drift)
        {
            if (drift == DefinitionDrift.Description)
            {
                Description = "changed definition";
                return;
            }

            if (drift == DefinitionDrift.ApprovalMode)
            {
                ApprovalMode = ToolApprovalMode.Auto;
                return;
            }

            ParametersSchema = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}}}";
        }
    }

    public enum SafetyMode
    {
        ReadOnly,
        ApprovalUnspecified,
        ApprovalRequired,
        Destructive,
        ReadOnlyChanged,
        SideEffectChanged,
    }

    private sealed class ContextClassifiedTool(string name) : IAgentTool
    {
        public int ExecuteCount { get; private set; }
        public SafetyMode? SafetyOverride { get; set; }
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public string SideEffectKind => ResolveSafetyMode() == SafetyMode.SideEffectChanged
            ? "context.changed"
            : "context.classified";

        public AgentToolCallSafety GetCallSafety(string argumentsJson)
        {
            return ResolveSafetyMode() switch
            {
                SafetyMode.ApprovalUnspecified => new AgentToolCallSafety(
                    RequiresApproval: null,
                    IsReadOnly: true,
                    IsDestructive: false),
                SafetyMode.ApprovalRequired => new AgentToolCallSafety(
                    RequiresApproval: true,
                    IsReadOnly: true,
                    IsDestructive: false),
                SafetyMode.Destructive => new AgentToolCallSafety(
                    RequiresApproval: false,
                    IsReadOnly: false,
                    IsDestructive: true),
                SafetyMode.ReadOnlyChanged => new AgentToolCallSafety(
                    RequiresApproval: false,
                    IsReadOnly: false,
                    IsDestructive: false),
                _ => new AgentToolCallSafety(
                    RequiresApproval: false,
                    IsReadOnly: true,
                    IsDestructive: false),
            };
        }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("{}");
        }

        private SafetyMode ResolveSafetyMode()
        {
            if (SafetyOverride is { } safetyOverride)
                return safetyOverride;

            if (AgentToolRequestContext.Current?.ExternalMetadata.TryGetValue("tool_safety_mode", out var mode) == true &&
                Enum.TryParse<SafetyMode>(mode, ignoreCase: false, out var parsed))
            {
                return parsed;
            }

            return SafetyMode.Destructive;
        }
    }

    private sealed class ChatContextCapturingTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public AgentChatInvocationContext SeenChat { get; private set; } = AgentChatInvocationContext.Empty;
        public IReadOnlyDictionary<string, string> SeenExternalMetadata { get; private set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            SeenChat = AgentToolRequestContext.Current?.Chat ?? AgentChatInvocationContext.Empty;
            SeenExternalMetadata = AgentToolRequestContext.Current?.ExternalMetadata
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            return Task.FromResult("{}");
        }
    }

    private sealed class EffectClassifiedTool(string name) : IAgentTool
    {
        public int ExecuteCount { get; private set; }
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public ToolPresentationDescriptor Presentation => new()
        {
            InvocationName = name,
            DisplayName = name,
            Kind = ToolPresentationKind.NyxIdOperation,
            Availability = ToolAvailability.Available,
            NyxIdOperation = new NyxIdOperationRef
            {
                ConnectedServiceId = "connected-service-alpha",
                ServiceSlug = "service-slug-alpha",
                CatalogServiceSlug = "catalog-slug-alpha",
                ReadinessCapabilityId = "readiness-capability-alpha",
            },
        };
        public bool IsDestructive => true;
        public string SideEffectKind => "repository.update";
        public string? ClassifiedArguments { get; private set; }

        public AgentToolCallSafety GetCallSafety(string argumentsJson)
        {
            ClassifiedArguments = argumentsJson;
            return new AgentToolCallSafety(
                RequiresApproval: false,
                IsReadOnly: false,
                IsDestructive: true);
        }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("{}");
        }
    }

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    public enum AuthorizedToolStepMutation
    {
        RunId,
        CorrelationId,
        Attempt,
        StepIndex,
        ToolCallCount,
        ToolCallId,
        ToolName,
        Arguments,
    }

    private sealed class StaticStepPlanReplyGenerator(AgentRunReplyStepPlan plan) : IAgentRunStepConversationReplyGenerator
    {
        public Task<AgentRunReplyStepPlan> BuildStepPlanAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IReadOnlyList<ConversationHistoryEntry>? priorHistory,
            ChatAttachmentInputContext? attachmentContext,
            bool forceDisableTools,
            CancellationToken ct,
            AgentProfileTurnCatalog? turnCatalog = null) =>
            Task.FromResult(plan);

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Per-step tests drive BuildLlmStepExecutionAsync only.");

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Per-step tests drive BuildLlmStepExecutionAsync only.");
    }

    private sealed class RecordingFileArtifactReadPort(ApplicationFileArtifactRef fileRef, byte[] content)
        : IFileArtifactReadPort
    {
        public ValueTask<ApplicationFileArtifactRef> DescribeAsync(
            ApplicationFileArtifactRef requestedFileRef,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(fileRef);

        public ValueTask<FileArtifactContent> OpenReadAsync(
            ApplicationFileArtifactRef requestedFileRef,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FileArtifactContent(
                fileRef,
                new MemoryStream(content, writable: false)));
    }
}
