using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
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
    public async Task ProfiledStep_ShouldSendProfileLayersAndUseExactCatalogToolForSchemaAndExecution()
    {
        var exactCatalogTool = new CountingTool("profile_task_tool");
        var sameNameGlobalTool = new CountingTool(exactCatalogTool.Name);
        var provider = new ToolCallProvider(exactCatalogTool.Name);
        var generator = new NyxIdConversationReplyGenerator(
            new SingleProviderFactory(provider),
            new ConversationReplyGeneratorTests.StubBuiltInPromptFloorProvider("built-in floor"),
            toolSources: [new FixedToolSource(sameNameGlobalTool)]);
        var executor = new AgentRunReplyGenerationExecutor(
            Substitute.For<IActorDispatchPort>(),
            generator,
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        var catalog = new AgentProfileTurnCatalog(
            [exactCatalogTool.Name],
            new ProfileRoutingPromptLayer(
                "profile-routing-marker",
                ["always-procedure-marker"],
                new ProfileRoutingPromptProvenance("profile-alpha"),
                new PromptLayerBounds(4096, 1024)),
            new SelectedSkillPromptLayer(
                "selected-procedure-marker",
                new SelectedSkillPromptProvenance("skill-alpha"),
                new PromptLayerBounds(4096, 1024)),
            selectedIntentId: "intent-alpha",
            candidateIntentId: "intent-alpha",
            routeOwnedTools: [exactCatalogTool]);
        var request = new NeedsLlmReplyEvent
        {
            RunId = "run-profile-alpha",
            CorrelationId = "corr-profile-alpha",
            TargetActorId = "conversation-profile-alpha",
            Activity = new ChatActivity
            {
                Id = "activity-profile-alpha",
                Conversation = new ConversationReference { CanonicalKey = "conversation-profile-alpha" },
                Content = new MessageContent { Text = "run the profiled task" },
            },
        };
        var initialState = await executor.BuildInitialStepStateAsync(
            new AgentRunReplyGenerationExecutionRequest(
                request.RunId,
                "turn-actor-profile-alpha",
                Attempt: 1,
                request,
                catalog),
            CancellationToken.None);
        var llmWorkItem = new AgentRunReplyStepExecutionRequest(
            request.RunId,
            "turn-actor-profile-alpha",
            Attempt: 1,
            initialState.NextStepIndex,
            request,
            initialState,
            TurnCatalog: catalog);

        var execution = await executor.BuildLlmStepExecutionAsync(llmWorkItem, CancellationToken.None);
        var toolWorkItem = BuildToolStepWorkItem(llmWorkItem, execution.Continuation);
        await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            execution.AuthorizedToolStep,
            CancellationToken.None);

        using var assertions = new FluentAssertions.Execution.AssertionScope();
        var actualRequest = provider.Requests.Should().ContainSingle().Subject;
        var systemPrompt = actualRequest.Messages
            .Where(static message => message.Role == "system")
            .Select(static message => message.Content)
            .FirstOrDefault() ?? string.Empty;
        systemPrompt.Should().Contain("profile-routing-marker");
        systemPrompt.Should().Contain("always-procedure-marker");
        systemPrompt.Should().Contain("selected-procedure-marker");
        actualRequest.Tools.Should().ContainSingle().Which.Should().BeSameAs(exactCatalogTool);
        exactCatalogTool.ExecuteCount.Should().Be(1);
        sameNameGlobalTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task ProfiledContinuation_ShouldKeepExactCatalogAcrossRealProviderRounds()
    {
        var exactCatalogTool = new CountingTool("profile_task_tool");
        var sameNameGlobalTool = new CountingTool(exactCatalogTool.Name);
        var provider = new ToolThenFinalProvider(exactCatalogTool.Name);
        var generator = new NyxIdConversationReplyGenerator(
            new SingleProviderFactory(provider),
            new ConversationReplyGeneratorTests.StubBuiltInPromptFloorProvider("built-in floor"),
            toolSources: [new FixedToolSource(sameNameGlobalTool)]);
        var executor = new AgentRunReplyGenerationExecutor(
            Substitute.For<IActorDispatchPort>(),
            generator,
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        var catalog = new AgentProfileTurnCatalog(
            [exactCatalogTool.Name],
            new ProfileRoutingPromptLayer(
                "profile-routing-marker",
                ["always-procedure-marker"],
                new ProfileRoutingPromptProvenance("profile-alpha"),
                new PromptLayerBounds(4096, 1024)),
            new SelectedSkillPromptLayer(
                "selected-procedure-marker",
                new SelectedSkillPromptProvenance("skill-alpha"),
                new PromptLayerBounds(4096, 1024)),
            selectedIntentId: "intent-alpha",
            candidateIntentId: "intent-alpha",
            routeOwnedTools: [exactCatalogTool]);
        var request = new NeedsLlmReplyEvent
        {
            RunId = "run-profile-continuation",
            CorrelationId = "corr-profile-continuation",
            TargetActorId = "conversation-profile-continuation",
            Activity = new ChatActivity
            {
                Id = "activity-profile-continuation",
                Conversation = new ConversationReference
                {
                    CanonicalKey = "conversation-profile-continuation",
                },
                Content = new MessageContent { Text = "run the profiled task" },
            },
        };
        var initialState = await executor.BuildInitialStepStateAsync(
            new AgentRunReplyGenerationExecutionRequest(
                request.RunId,
                "turn-actor-profile-continuation",
                Attempt: 1,
                request,
                catalog),
            CancellationToken.None);
        var firstLlmWorkItem = new AgentRunReplyStepExecutionRequest(
            request.RunId,
            "turn-actor-profile-continuation",
            Attempt: 1,
            initialState.NextStepIndex,
            request,
            initialState,
            TurnCatalog: catalog);

        var firstLlmExecution = await executor.BuildLlmStepExecutionAsync(
            firstLlmWorkItem,
            CancellationToken.None);
        var toolContinuation = await executor.BuildToolStepContinuationAsync(
            BuildToolStepWorkItem(firstLlmWorkItem, firstLlmExecution.Continuation),
            firstLlmExecution.AuthorizedToolStep,
            CancellationToken.None);
        var secondLlmWorkItem = BuildContinuationLlmWorkItem(
            firstLlmWorkItem,
            firstLlmExecution.Continuation,
            toolContinuation,
            turnCatalog: catalog);

        var secondLlmExecution = await executor.BuildLlmStepExecutionAsync(
            secondLlmWorkItem,
            CancellationToken.None);

        using var assertions = new FluentAssertions.Execution.AssertionScope();
        provider.Requests.Should().HaveCount(2);
        provider.Requests.Should().AllSatisfy(actualRequest =>
        {
            var systemPrompt = actualRequest.Messages
                .Where(static message => message.Role == "system")
                .Select(static message => message.Content)
                .FirstOrDefault() ?? string.Empty;
            systemPrompt.Should().Contain("profile-routing-marker");
            systemPrompt.Should().Contain("always-procedure-marker");
            systemPrompt.Should().Contain("selected-procedure-marker");
            actualRequest.Tools.Should().ContainSingle().Which.Should().BeSameAs(exactCatalogTool);
        });
        provider.Requests
            .SelectMany(static actualRequest => actualRequest.Tools ?? [])
            .Should().NotContain(sameNameGlobalTool);
        secondLlmExecution.Continuation.LlmStepResult.Content.Should().Be("final response");
        exactCatalogTool.ExecuteCount.Should().Be(1);
        sameNameGlobalTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task ShadowProfiledStep_ShouldSendProfileAndAlwaysLayersWithoutSelectedAuthority()
    {
        var exactRecoveryTool = new CountingTool("profile_recovery_tool");
        var exactTaskTool = new CountingTool("profile_task_tool");
        var globalRecoveryTool = new CountingTool(exactRecoveryTool.Name);
        var globalTaskTool = new CountingTool(exactTaskTool.Name);
        var provider = new RecordingProvider();
        var generator = new NyxIdConversationReplyGenerator(
            new SingleProviderFactory(provider),
            new ConversationReplyGeneratorTests.StubBuiltInPromptFloorProvider("built-in floor"),
            toolSources: [new FixedToolSource(globalRecoveryTool, globalTaskTool)]);
        var executor = new AgentRunReplyGenerationExecutor(
            Substitute.For<IActorDispatchPort>(),
            generator,
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        var catalog = new AgentProfileTurnCatalog(
            [exactRecoveryTool.Name],
            new ProfileRoutingPromptLayer(
                "shadow-profile-routing-marker",
                ["shadow-always-procedure-marker"],
                new ProfileRoutingPromptProvenance("profile-shadow"),
                new PromptLayerBounds(4096, 1024)),
            selectedSkillPromptLayer: null,
            selectedIntentId: null,
            candidateIntentId: "intent-shadow",
            routeOwnedTools: [exactRecoveryTool, exactTaskTool]);
        var request = new NeedsLlmReplyEvent
        {
            RunId = "run-shadow-alpha",
            CorrelationId = "corr-shadow-alpha",
            TargetActorId = "conversation-shadow-alpha",
            Activity = new ChatActivity
            {
                Id = "activity-shadow-alpha",
                Conversation = new ConversationReference { CanonicalKey = "conversation-shadow-alpha" },
                Content = new MessageContent { Text = "run the shadow candidate" },
            },
        };
        var initialState = await executor.BuildInitialStepStateAsync(
            new AgentRunReplyGenerationExecutionRequest(
                request.RunId,
                "turn-actor-shadow-alpha",
                Attempt: 1,
                request,
                catalog),
            CancellationToken.None);

        await executor.BuildLlmStepExecutionAsync(
            new AgentRunReplyStepExecutionRequest(
                request.RunId,
                "turn-actor-shadow-alpha",
                Attempt: 1,
                initialState.NextStepIndex,
                request,
                initialState,
                TurnCatalog: catalog),
            CancellationToken.None);

        var actualRequest = provider.Requests.Should().ContainSingle().Subject;
        var systemPrompt = actualRequest.Messages
            .Where(static message => message.Role == "system")
            .Select(static message => message.Content)
            .FirstOrDefault() ?? string.Empty;
        systemPrompt.Should().Contain("shadow-profile-routing-marker");
        systemPrompt.Should().Contain("shadow-always-procedure-marker");
        systemPrompt.Should().NotContain("selected-procedure-marker");
        actualRequest.Tools.Should().ContainSingle().Which.Should().BeSameAs(exactRecoveryTool);
        actualRequest.Tools.Should().NotContain(exactTaskTool);
        actualRequest.Tools.Should().NotContain(globalRecoveryTool);
        actualRequest.Tools.Should().NotContain(globalTaskTool);
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

        await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

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
        receipt.ErrorCode.Should().Be("tool_execution_exception");
        tool.ExecuteCount.Should().Be(0);
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
            message.Content.Contains("not found", StringComparison.Ordinal));
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
            message.Content.Contains("not found", StringComparison.Ordinal));
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
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null)
    {
        var tools = new ToolManager();
        tools.Register(tool);
        var runtime = new ChatRuntime(
            () => provider,
            new ChatHistory(),
            new ToolCallLoop(tools),
            hooks: null,
            requestBuilder: _ => new LLMRequest { Messages = [], Tools = tools.GetAll() },
            llmMiddlewares: llmMiddlewares);
        var plan = new AgentRunReplyStepPlan(
            runtime.CreateStepExecutor(turnCatalog: null),
            new Dictionary<string, string>(),
            LLMControlContext.Empty,
            AgentToolExecutionContext.Empty,
            InitialMessages: [],
            MaxToolRounds: 1);
        return new AgentRunReplyGenerationExecutor(
            Substitute.For<IActorDispatchPort>(),
            new StaticStepPlanReplyGenerator(plan),
            interactiveReplyCollector: null,
            relayOptions: null,
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

    private static AgentRunReplyStepExecutionRequest BuildToolStepWorkItem(
        AgentRunReplyStepExecutionRequest llmWorkItem,
        AgentRunNextLlmStepRequestedEvent continuation)
    {
        var stepState = llmWorkItem.StepState.Clone();
        stepState.NextStepIndex = continuation.StepIndex;
        stepState.PendingToolCalls.Clear();
        stepState.PendingToolCalls.AddRange(continuation.LlmStepResult.ToolCalls.Select(static call => call.Clone()));
        return llmWorkItem with
        {
            StepIndex = continuation.StepIndex,
            StepState = stepState,
        };
    }

    private static AgentRunReplyStepExecutionRequest BuildContinuationLlmWorkItem(
        AgentRunReplyStepExecutionRequest initialWorkItem,
        AgentRunNextLlmStepRequestedEvent llmContinuation,
        AgentRunNextToolStepRequestedEvent toolContinuation,
        AgentProfileTurnCatalog? turnCatalog)
    {
        var state = initialWorkItem.StepState.Clone();
        var llmResult = llmContinuation.LlmStepResult;
        state.NextStepIndex = llmContinuation.StepIndex;
        state.AccumulatedText = llmResult.AccumulatedText;
        state.LastFinishReason = llmResult.FinishReason;
        state.HasStreamedTextContent = llmResult.HasStreamedTextContent;
        state.PendingToolCalls.Clear();
        state.PendingToolCalls.AddRange(llmResult.ToolCalls.Select(static call => call.Clone()));
        var assistant = new AgentRunChatMessage
        {
            Role = "assistant",
            Content = llmResult.Content,
            ReasoningContent = llmResult.ReasoningContent,
        };
        assistant.ToolCalls.AddRange(llmResult.ToolCalls.Select(static call => call.Clone()));
        state.Messages.Add(assistant);

        var toolResult = toolContinuation.ToolStepResult;
        state.NextStepIndex = toolContinuation.StepIndex;
        state.PendingToolCalls.Clear();
        state.Messages.AddRange(toolResult.ResultMessages.Select(static message => message.Clone()));
        state.ToolReceipts.AddRange(toolResult.ToolReceipts.Select(static receipt => receipt.Clone()));
        if (toolResult.AdvanceRound)
            state.Round++;

        return initialWorkItem with
        {
            StepIndex = toolContinuation.StepIndex,
            StepState = state,
            TurnCatalog = turnCatalog,
        };
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

    private sealed class ToolThenFinalProvider(string toolName) : ILLMProvider
    {
        public string Name => "tool-then-final-provider";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call-1",
                        Name = toolName,
                        ArgumentsJson = "{}",
                    },
                };
            }
            else
            {
                yield return new LLMStreamChunk { DeltaContent = "final response" };
            }

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

    private sealed class FixedToolSource(params IAgentTool[] tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }

    private sealed class SingleProviderFactory(ILLMProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => provider;
        public ILLMProvider GetDefault() => provider;
        public IReadOnlyList<string> GetAvailableProviders() => [provider.Name];
    }

    private sealed class EffectClassifiedTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
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

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
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
            CancellationToken ct) =>
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
