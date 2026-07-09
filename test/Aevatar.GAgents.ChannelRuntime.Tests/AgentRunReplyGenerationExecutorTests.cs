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
using ApplicationWorkflowFileRef = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileRef;
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
            new ApplicationWorkflowFileRef
            {
                FileId = "wf-file-1",
                ArtifactId = "workflow-file://wf-file-1",
                SourceKind = WorkflowFileSourceKind.ChatInput,
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

    private static AgentRunReplyGenerationExecutor CreateExecutor(
        RecordingProvider provider,
        IWorkflowFileArtifactReadPort? fileArtifactReadPort = null)
    {
        var runtime = new ChatRuntime(
            providerFactory: () => provider,
            history: new ChatHistory(),
            toolLoop: new ToolCallLoop(new ToolManager()),
            hooks: null,
            requestBuilder: static () => new LLMRequest { Messages = [] });
        var plan = new AgentRunReplyStepPlan(
            runtime.CreateStepExecutor(),
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

    private sealed class RecordingFileArtifactReadPort(ApplicationWorkflowFileRef fileRef, byte[] content)
        : IWorkflowFileArtifactReadPort
    {
        public ValueTask<ApplicationWorkflowFileRef> DescribeAsync(
            ApplicationWorkflowFileRef requestedFileRef,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(fileRef);

        public ValueTask<WorkflowFileArtifactContent> OpenReadAsync(
            ApplicationWorkflowFileRef requestedFileRef,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new WorkflowFileArtifactContent(
                fileRef,
                new MemoryStream(content, writable: false)));
    }
}
