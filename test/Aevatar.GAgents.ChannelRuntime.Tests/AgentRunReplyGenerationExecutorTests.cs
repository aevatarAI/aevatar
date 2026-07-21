using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.Foundation.Abstractions;
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
    public async Task BuildLlmStepContinuation_WhenFinalNoToolsHasNoSuccessfulMutatingReceipt_ShouldPassReceiptsToCoreConstraint()
    {
        var provider = new RecordingProvider();
        var executor = CreateExecutor(provider);
        var workItem = BuildFinalNoToolsWorkItem();

        await executor.BuildLlmStepContinuationAsync(workItem, CancellationToken.None);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().BeNull();
        request.Messages
            .Where(message => message.Role == "system" &&
                              message.Content?.Contains("no successful mutating tool execution") == true)
            .Should().ContainSingle();
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenFinalNoToolsHasSuccessfulMutatingReceipt_ShouldSuppressCoreConstraint()
    {
        var provider = new RecordingProvider();
        var executor = CreateExecutor(provider);
        var workItem = BuildFinalNoToolsWorkItem(
            new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Success,
                SideEffectKind = "definition.update",
            });

        await executor.BuildLlmStepContinuationAsync(workItem, CancellationToken.None);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().BeNull();
        request.Messages
            .Where(message => message.Role == "system" &&
                              message.Content?.Contains("no successful mutating tool execution") == true)
            .Should().BeEmpty();
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

        await executor.BuildLlmStepContinuationAsync(workItem, CancellationToken.None);

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

        var act = async () => await executor.BuildLlmStepContinuationAsync(workItem, CancellationToken.None);

        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Contain("Referenced chat media exceeds the materialization size limit");
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolStep_WhenMiddlewareRemovedToolInPriorLlmStep_ShouldRejectForgedCall()
    {
        var tool = new CountingTool("visible");
        var tools = new ToolManager();
        tools.Register(tool);
        var provider = new ForgedToolProvider("visible");
        var runtime = new ChatRuntime(
            () => provider,
            new ChatHistory(),
            new ToolCallLoop(tools),
            hooks: null,
            requestBuilder: _ => new LLMRequest { Messages = [], Tools = tools.GetAll() },
            llmMiddlewares: [new RemovingToolsMiddleware()]);
        var plan = new AgentRunReplyStepPlan(
            runtime.CreateStepExecutor(turnCatalog: null),
            new Dictionary<string, string>(),
            LLMControlContext.Empty,
            AgentToolExecutionContext.Empty,
            InitialMessages: [],
            MaxToolRounds: 1);
        var executor = new AgentRunReplyGenerationExecutor(
            Substitute.For<IActorDispatchPort>(),
            new StaticStepPlanReplyGenerator(plan),
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        var llmWorkItem = BuildToolEnabledWorkItem();

        var llmContinuation = await executor.BuildLlmStepContinuationAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolStepState = llmWorkItem.StepState.Clone();
        toolStepState.PendingToolCalls.AddRange(
            llmContinuation.LlmStepResult.ToolCalls.Select(static call => call.Clone()));
        var toolContinuation = await executor.BuildToolStepContinuationAsync(
            llmWorkItem with
            {
                StepIndex = 2,
                StepState = toolStepState,
            },
            CancellationToken.None);

        provider.Requests.Should().ContainSingle().Which.Tools.Should().BeNull();
        toolContinuation.ToolStepResult.ResultMessages.Should().ContainSingle()
            .Which.Content.Should().Contain("not found");
        tool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task ToolStep_WhenPlainToolContractMatchesAcrossRediscovery_ShouldExecuteRediscoveredTool()
    {
        var authorizedTool = new CountingTool("use_skill");
        var rediscoveredTool = new CountingTool("use_skill");

        var result = await ExecuteAcrossContinuationAsync(authorizedTool, rediscoveredTool);

        result.LlmContinuation.LlmStepResult.AuthorizedTools.Should().ContainSingle();
        result.ToolContinuation.ToolStepResult.ResultMessages.Should().ContainSingle()
            .Which.Content.Should().NotContain("not found");
        rediscoveredTool.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public async Task ToolStep_WhenPlainToolContractChangesAcrossRediscovery_ShouldRejectTool()
    {
        var authorizedTool = new CountingTool("use_skill", "original contract");
        var rediscoveredTool = new CountingTool("use_skill", "changed contract");

        var result = await ExecuteAcrossContinuationAsync(authorizedTool, rediscoveredTool);

        result.ToolContinuation.ToolStepResult.ResultMessages.Should().ContainSingle()
            .Which.Content.Should().Contain("not found");
        rediscoveredTool.ExecuteCount.Should().Be(0);
    }

    private static async Task<ContinuationExecutionResult> ExecuteAcrossContinuationAsync(
        IAgentTool authorizedTool,
        IAgentTool rediscoveredTool)
    {
        var provider = new ForgedToolProvider(authorizedTool.Name);
        var generator = new SequencedStepPlanReplyGenerator(
            BuildToolPlan(provider, authorizedTool),
            BuildToolPlan(provider, rediscoveredTool));
        var executor = new AgentRunReplyGenerationExecutor(
            Substitute.For<IActorDispatchPort>(),
            generator,
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        var llmWorkItem = BuildToolEnabledWorkItem();

        var llmContinuation = await executor.BuildLlmStepContinuationAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolStepState = llmWorkItem.StepState.Clone();
        toolStepState.PendingToolCalls.AddRange(
            llmContinuation.LlmStepResult.ToolCalls.Select(static call => call.Clone()));
        toolStepState.AuthorizedTools.AddRange(
            llmContinuation.LlmStepResult.AuthorizedTools.Select(static tool => tool.Clone()));
        var toolContinuation = await executor.BuildToolStepContinuationAsync(
            llmWorkItem with
            {
                StepIndex = 2,
                StepState = toolStepState,
            },
            CancellationToken.None);

        return new ContinuationExecutionResult(llmContinuation, toolContinuation);
    }

    private static AgentRunReplyStepPlan BuildToolPlan(ILLMProvider provider, IAgentTool tool)
    {
        var tools = new ToolManager();
        tools.Register(tool);
        var runtime = new ChatRuntime(
            () => provider,
            new ChatHistory(),
            new ToolCallLoop(tools),
            hooks: null,
            requestBuilder: _ => new LLMRequest { Messages = [], Tools = tools.GetAll() });
        return new AgentRunReplyStepPlan(
            runtime.CreateStepExecutor(turnCatalog: null),
            new Dictionary<string, string>(),
            LLMControlContext.Empty,
            AgentToolExecutionContext.Empty,
            InitialMessages: [],
            MaxToolRounds: 1);
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

    private static AgentRunReplyStepExecutionRequest BuildToolEnabledWorkItem()
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
        stepState.Messages.Add(AgentRunReplyStepMappers.ToProto(ChatMessage.User("run")));
        return new AgentRunReplyStepExecutionRequest(
            "run-1",
            "channel-agent-run:run-1",
            Attempt: 1,
            StepIndex: 1,
            request,
            stepState);
    }

    private sealed class RecordingProvider : ILLMProvider
    {
        public string Name => "recording-provider";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            yield return new LLMStreamChunk { DeltaContent = "final" };
            await Task.Yield();
        }
    }

    private sealed class ForgedToolProvider(string toolName) : ILLMProvider
    {
        public string Name => "forged-tool-provider";
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
                    Id = "forged-call",
                    Name = toolName,
                    ArgumentsJson = "{}",
                },
            };
            await Task.Yield();
        }
    }

    private sealed class RemovingToolsMiddleware : Aevatar.AI.Abstractions.Middleware.ILLMCallMiddleware
    {
        public async Task InvokeAsync(
            Aevatar.AI.Abstractions.Middleware.LLMCallContext context,
            Func<Task> next)
        {
            context.Request = new LLMRequest
            {
                Messages = context.Request.Messages,
                RequestId = context.Request.RequestId,
                Metadata = context.Request.Metadata,
                CallerContext = context.Request.CallerContext,
                ToolContext = context.Request.ToolContext,
                RoutingContext = context.Request.RoutingContext,
                LlmControl = context.Request.LlmControl,
                Tools = null,
                Model = context.Request.Model,
                Temperature = context.Request.Temperature,
                MaxTokens = context.Request.MaxTokens,
                ResponseFormat = context.Request.ResponseFormat,
            };
            await next();
        }
    }

    private sealed class CountingTool(string name, string? description = null) : IAgentTool
    {
        public int ExecuteCount { get; private set; }
        public string Name => name;
        public string Description => description ?? name;
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("{}");
        }
    }

    private sealed class SequencedStepPlanReplyGenerator(params AgentRunReplyStepPlan[] plans)
        : IAgentRunStepConversationReplyGenerator
    {
        private readonly Queue<AgentRunReplyStepPlan> _plans = new(plans);

        public Task<AgentRunReplyStepPlan> BuildStepPlanAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IReadOnlyList<ConversationHistoryEntry>? priorHistory,
            ChatAttachmentInputContext? attachmentContext,
            bool forceDisableTools,
            CancellationToken ct) =>
            Task.FromResult(_plans.Dequeue());

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed record ContinuationExecutionResult(
        AgentRunNextLlmStepRequestedEvent LlmContinuation,
        AgentRunNextToolStepRequestedEvent ToolContinuation);

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
            CancellationToken ct) =>
            Task.FromResult(plan);

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Per-step tests drive BuildLlmStepContinuationAsync only.");

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Per-step tests drive BuildLlmStepContinuationAsync only.");
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
