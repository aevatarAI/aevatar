using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
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
    public async Task BuildInitialStepState_WhenChannelProfileIsSelected_ShouldPinAndReplayExactCatalog()
    {
        var fixture = CreateProfiledChannelExecutor();

        var state = await fixture.Executor.BuildInitialStepStateAsync(
            new AgentRunReplyGenerationExecutionRequest(
                "run-1",
                "channel-agent-run:run-1",
                1,
                fixture.Request.Clone()),
            CancellationToken.None);

        state.AgentProfileSnapshot.Should().NotBeNull();
        AgentProfileSnapshotCodec.ByteEquivalent(state.AgentProfileSnapshot, fixture.Profile).Should().BeTrue();
        state.AgentProfileTurnAuthority.Should().Be(fixture.Authority);
        state.ToolCatalogPolicyVersion.Should().Be(AgentRunReplyGenerationExecutor.ToolCatalogPolicyVersion);
        state.ToolCatalogProof.Should().Be(fixture.Catalog.Proof.ToPayload());
        fixture.Generator.ReceivedCatalog.Should().BeSameAs(fixture.Catalog);

        var execution = await fixture.Executor.BuildLlmStepExecutionAsync(
            new AgentRunReplyStepExecutionRequest(
                "run-1",
                "channel-agent-run:run-1",
                1,
                state.NextStepIndex,
                fixture.Request.Clone(),
                state.Clone()),
            CancellationToken.None);

        var providerRequest = fixture.Provider.Requests.Should().ContainSingle().Subject;
        providerRequest.Tools.Should().ContainSingle().Which.Should().BeSameAs(fixture.Tool);
        execution.Continuation.LlmStepResult!.ToolCatalogCaptured.Should().BeTrue();
        execution.Continuation.LlmStepResult.AvailableToolNames.Should().Equal(fixture.Tool.Name);
        providerRequest.ToolCatalogProof.Should().NotBeNull();
        providerRequest.ToolCatalogProof!.ToPayload().Should().Be(state.ToolCatalogProof);
        await fixture.ProfilePlanner.Received(2).MaterializeCommittedAsync(
            Arg.Any<AgentProfileSnapshot>(),
            Arg.Any<AgentProfileTurnAuthorityState>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<IAgentTool>>(),
            Arg.Any<AgentToolExecutionContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenPinnedChannelProofIsTampered_ShouldFailClosed()
    {
        var fixture = CreateProfiledChannelExecutor();
        var state = await fixture.Executor.BuildInitialStepStateAsync(
            new AgentRunReplyGenerationExecutionRequest(
                "run-1",
                "channel-agent-run:run-1",
                1,
                fixture.Request.Clone()),
            CancellationToken.None);
        state.ToolCatalogProof.CatalogDigest = "sha256:tampered";

        var act = () => fixture.Executor.BuildLlmStepExecutionAsync(
            new AgentRunReplyStepExecutionRequest(
                "run-1",
                "channel-agent-run:run-1",
                1,
                state.NextStepIndex,
                fixture.Request.Clone(),
                state),
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AgentTurnToolCatalogException>();
        exception.Which.Failure.Code.Should().Be(AgentTurnToolCatalogFailureCode.CatalogProofMismatch);
        fixture.Provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildInitialStepState_WhenConversationCarriesPinnedProfile_ShouldNotResolveCurrentBinding()
    {
        var fixture = CreateProfiledChannelExecutor();
        var request = fixture.Request.Clone();
        request.AgentProfile = fixture.Profile.Clone();
        fixture.ProfileResolver.ResolveAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ChatRouteAgentProfileKind>(),
                Arg.Any<ChatRouteAgentProfileRef?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AgentProfileTurnSnapshotResolution>(
                new InvalidOperationException("The current binding must not replace a conversation pin.")));

        var state = await fixture.Executor.BuildInitialStepStateAsync(
            new AgentRunReplyGenerationExecutionRequest(
                "run-1",
                "channel-agent-run:run-1",
                1,
                request),
            CancellationToken.None);

        AgentProfileSnapshotCodec.ByteEquivalent(state.AgentProfileSnapshot, fixture.Profile).Should().BeTrue();
        state.ToolCatalogProof.Should().Be(fixture.Catalog.Proof.ToPayload());
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenTurnCatalogIsMissing_ShouldUseRestrictedEmptyCatalogProof()
    {
        var provider = new RecordingProvider();
        var generator = new CatalogAwareStepPlanReplyGenerator(
            new CountingTool("legacy_discovered_tool"),
            provider);
        var executor = new AgentRunReplyGenerationExecutor(
            Substitute.For<IActorDispatchPort>(),
            generator,
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);

        await executor.BuildLlmStepExecutionAsync(
            BuildToolEnabledWorkItem() with { TurnCatalog = null },
            CancellationToken.None);

        generator.ReceivedCatalog.Should().NotBeNull();
        generator.ReceivedCatalog!.FinalAllowedToolNames.Should().BeEmpty();
        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().BeNull();
        request.ToolCatalogProof.Should().NotBeNull();
        request.ToolCatalogProof!.ToolDescriptors.Should().BeEmpty();
        request.ToolCatalogProof.ToolCount.Should().Be(0);
        request.ToolCatalogProof.SchemaBytes.Should().Be(0);
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WithRequiredProfileReadiness_ShouldBypassProviderAndAuthorizeExactTool()
    {
        var provider = new RecordingProvider("must-not-be-used");
        var tool = new CountingTool("nyxid_require_service");
        var executor = CreateToolEnabledExecutor(tool, provider);
        var invocation = new AgentProfileRequiredToolInvocation(
            tool.Name,
            "{\"service_slug\":\"api-github\",\"requested_scopes\":[\"repo\"]}");
        var catalog = new AgentTurnToolCatalog(
            [tool.Name],
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: "github-via-nyxid",
            candidateIntentId: "github-via-nyxid",
            exactTools: [tool],
            hasUnresolvedConnectedServiceSelectors: true,
            requiredToolInvocation: invocation);
        var workItem = BuildToolEnabledWorkItem() with { TurnCatalog = catalog };

        var execution = await executor.BuildLlmStepExecutionAsync(
            workItem,
            CancellationToken.None);

        provider.Requests.Should().BeEmpty();
        var call = execution.Continuation.LlmStepResult!.ToolCalls.Should().ContainSingle().Subject;
        call.Name.Should().Be(tool.Name);
        call.ArgumentsJson.Should().Be(invocation.ArgumentsJson);
        execution.Continuation.LlmStepResult.AvailableToolNames.Should().BeEmpty(
            "the server-authored required call bypassed the provider and was not loaded into a model round");
        execution.Continuation.LlmStepResult.ToolCatalogCaptured.Should().BeFalse();
        execution.AuthorizedToolStep.Should().NotBeNull();
        execution.AuthorizedToolCallSafeties.Should().ContainSingle()
            .Which.ToolName.Should().Be(tool.Name);
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenMiddlewareShortCircuits_ShouldNotClaimModelLoadedTools()
    {
        var provider = new RecordingProvider("must-not-be-used");
        var tool = new CountingTool("cached_lookup");
        var executor = CreateToolEnabledExecutor(
            tool,
            provider,
            [new ShortCircuitLlmMiddleware()]);

        var execution = await executor.BuildLlmStepExecutionAsync(
            BuildToolEnabledWorkItem(),
            CancellationToken.None);

        provider.Requests.Should().BeEmpty();
        execution.Continuation.LlmStepResult!.Content.Should().Be("middleware-answer");
        execution.Continuation.LlmStepResult.AvailableToolNames.Should().BeEmpty(
            "no provider model invocation started");
        execution.Continuation.LlmStepResult.ToolCatalogCaptured.Should().BeFalse();
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenToolEnabledFirstRoundHasNoSuccessfulMutatingReceipt_ShouldPassMutationClaimConstraint()
    {
        var provider = new RecordingProvider();
        var executor = CreateToolEnabledExecutor(new CountingTool("submit_record"), provider);
        var workItem = BuildToolEnabledWorkItem(
            new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Error,
                SideEffectKind = "record.submit",
            });

        await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().ContainSingle().Which.Name.Should().Be("submit_record");
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
            new CountingTool("submit_record"),
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
            ToolName = "submit_record",
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
    public async Task BuildLlmStepContinuation_WhenCardKitTurnEmitsToolCall_ShouldNotExposeToolPreamble()
    {
        var tool = new DriftableDefinitionTool("scope_workflows_get");
        var provider = new TextThenToolCallProvider(
            tool.Name,
            """Starting workflow with prompt `{"submit":false}`.""");
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        var executor = CreateToolEnabledExecutor(
            tool,
            provider,
            actorDispatchPort: dispatchPort,
            relayOptions: new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingRepliesEnabled = true,
                StreamingCardKitEnabled = true,
            });
        var workItem = BuildToolEnabledWorkItem();
        workItem.Request.Activity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-message-1",
            CorrelationId = "corr-1",
        };
        workItem.Request.ReplyToken = "relay-token";
        workItem.Request.ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        await dispatchPort.DidNotReceiveWithAnyArgs()
            .DispatchAsync(default!, default!, default);
        execution.Continuation.LlmStepResult.AccumulatedText.Should().BeEmpty();
        execution.Continuation.LlmStepResult.HasStreamedTextContent.Should().BeFalse();
        execution.Continuation.LlmStepResult.ToolCalls.Should().ContainSingle()
            .Which.Name.Should().Be(tool.Name);
        execution.Continuation.LlmStepResult.Content.Should()
            .Be("""Starting workflow with prompt `{"submit":false}`.""");
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenToolCallTextIsDeferred_ShouldStillReportModelLifecycle()
    {
        var tool = new CountingTool("scope_workflows_get");
        var provider = new TextThenToolCallProvider(tool.Name, "Hidden tool preamble.");
        var executor = CreateToolEnabledExecutor(tool, provider);
        var chunks = new List<LLMStreamChunk>();
        var workItem = BuildToolEnabledWorkItem() with
        {
            ReportChunkAsync = (chunk, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                chunks.Add(chunk);
                return Task.CompletedTask;
            },
        };

        var execution = await executor.BuildLlmStepExecutionAsync(
            workItem,
            CancellationToken.None);

        chunks.Should().NotContain(chunk => chunk.DeltaContent == "Hidden tool preamble.");
        var started = chunks.Should().ContainSingle(chunk => chunk.LLMInvocationStarted != null)
            .Which.LLMInvocationStarted!;
        started.Provider.Should().Be(provider.Name);
        started.AvailableToolNames.Should().Equal(tool.Name);
        var completed = chunks.Should().ContainSingle(chunk => chunk.LLMInvocationCompleted != null)
            .Which.LLMInvocationCompleted!;
        completed.OperationId.Should().Be(started.OperationId);
        completed.Success.Should().BeTrue();
        chunks.Select(chunk => chunk.LLMInvocationStarted != null ? "start" : "end")
            .Should().Equal("start", "end");
        execution.Continuation.LlmStepResult!.ToolCalls.Should().ContainSingle()
            .Which.Name.Should().Be(tool.Name);
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenToolEnabledFirstRoundHasSuccessfulMutatingReceipt_ShouldKeepGroundingConstraint()
    {
        var provider = new RecordingProvider();
        var executor = CreateToolEnabledExecutor(new CountingTool("submit_record"), provider);
        var workItem = BuildToolEnabledWorkItem(
            new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Success,
                SideEffectKind = "record.submit",
                Effect = AgentToolReceiptEffect.Mutating,
            });

        await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().ContainSingle().Which.Name.Should().Be("submit_record");
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

        var providerRequest = provider.Requests.Should().ContainSingle().Subject;
        providerRequest.ToolCatalogProof.Should().NotBeNull();
        providerRequest.ToolCatalogProof!.ToolDescriptors.Should().BeEmpty();
        var providerImagePart = providerRequest
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
            CommandName: "project-summary",
            OriginalCommand: "使用精确名称为 project-summary 的 skill",
            PrimarySkillName: "project-summary",
            MaxOrnnSearchAttempts: 2,
            CommandArguments: "生成项目摘要并运行 workflow");
        var toolContext = AgentToolExecutionContext.Empty with { SkillRecovery = recovery };
        var executor = CreateToolEnabledExecutor([useSkill, searchSkills], provider, toolContext: toolContext);
        var workItem = BuildToolEnabledWorkItem();
        workItem.StepState.ToolContext = toolContext.ToPayload();
        var priorUseSkillCall = new ToolCall
        {
            Id = "prior-use-skill",
            Name = "use_skill",
            ArgumentsJson = "{\"skill\":\"project-summary\"}",
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
        call.ArgumentsJson.Should().Contain("project-summary");
        execution.AuthorizedToolStep.Should().NotBeNull();
        execution.AuthorizedToolCallSafeties.Should().ContainSingle()
            .Which.ToolName.Should().Be("ornn_search_skills");
        useSkill.ExecuteCount.Should().Be(0);
        searchSkills.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenRouteToolHintExists_ShouldPreferHintBeforeSkillRecovery()
    {
        var startWorkflow = new CountingTool("aevatar_start_workflow");
        var searchSkills = new CountingTool("ornn_search_skills");
        var provider = new RecordingProvider();
        var recovery = new AgentSkillRecoveryContext(
            RequireInitialOrnnSearch: true,
            RequireOrnnSearchOnBlocker: true,
            CommandName: "phone-restaurant-reservation",
            OriginalCommand: "今晚 7 点帮我订餐，两位，餐厅是海底捞",
            PrimarySkillName: "phone-restaurant-reservation",
            MaxOrnnSearchAttempts: 2);
        var toolContext = AgentToolExecutionContext.Empty with { SkillRecovery = recovery };
        var executor = CreateToolEnabledExecutor([startWorkflow, searchSkills], provider, toolContext: toolContext);
        var workItem = BuildToolEnabledWorkItem();
        workItem.StepState.ToolContext = toolContext.ToPayload();
        workItem.Request.TargetRef = new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel
            {
                ToolChoiceHint = new ChatRouteToolChoiceHint
                {
                    ToolName = startWorkflow.Name,
                    PrefilledArguments = new Struct
                    {
                        Fields =
                        {
                            ["workflow_id"] = Google.Protobuf.WellKnownTypes.Value.ForString(
                                "phone-restaurant-reservation"),
                        },
                    },
                },
            },
        };

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        provider.Requests.Should().BeEmpty();
        var call = execution.Continuation.LlmStepResult.ToolCalls.Should().ContainSingle().Which;
        call.Name.Should().Be(startWorkflow.Name);
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        arguments.RootElement.GetProperty("workflow_id").GetString().Should().Be("phone-restaurant-reservation");
        arguments.RootElement.GetProperty("inputs").GetProperty("prompt").GetString().Should().Be("run");
        execution.AuthorizedToolStep.Should().NotBeNull();
        execution.AuthorizedToolCallSafeties.Should().ContainSingle()
            .Which.ToolName.Should().Be(startWorkflow.Name);
        startWorkflow.ExecuteCount.Should().Be(0);
        searchSkills.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenRouteToolHintAlreadyHasReceipt_ShouldNotRepeatHintCall()
    {
        var startWorkflow = new CountingTool("aevatar_start_workflow");
        var provider = new RecordingProvider();
        var executor = CreateToolEnabledExecutor(startWorkflow, provider);
        var workItem = BuildToolEnabledWorkItem(new AgentToolReceipt
        {
            CallId = "route_tool_choice_hint",
            ToolName = startWorkflow.Name,
            Status = AgentToolReceiptStatus.Success,
            Effect = AgentToolReceiptEffect.Mutating,
        });
        workItem.Request.TargetRef = new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel
            {
                ToolChoiceHint = new ChatRouteToolChoiceHint
                {
                    ToolName = startWorkflow.Name,
                    PrefilledArguments = new Struct
                    {
                        Fields =
                        {
                            ["workflow_id"] = Google.Protobuf.WellKnownTypes.Value.ForString(
                                "phone-restaurant-reservation"),
                        },
                    },
                },
            },
        };

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        provider.Requests.Should().ContainSingle();
        execution.Continuation.LlmStepResult.ToolCalls.Should().BeEmpty();
        startWorkflow.ExecuteCount.Should().Be(0);
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
                CommandName: "project-summary",
                OriginalCommand: "使用精确名称为 project-summary 的 skill",
                PrimarySkillName: "project-summary",
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
                CommandName: "project-summary",
                OriginalCommand: "使用精确名称为 project-summary 的 skill",
                PrimarySkillName: "project-summary",
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
            ArgumentsJson = "{\"skill\":\"project-summary\"}",
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
        publishedChunks.Should().OnlyContain(chunk =>
            chunk.LLMInvocationStarted != null || chunk.LLMInvocationCompleted != null);
        publishedChunks.Should().ContainSingle(chunk => chunk.LLMInvocationStarted != null);
        publishedChunks.Should().ContainSingle(chunk => chunk.LLMInvocationCompleted != null);
        execution.Continuation.LlmStepResult.AccumulatedText.Should().BeEmpty();
        execution.Continuation.LlmStepResult.Content.Should().BeEmpty();
        execution.Continuation.LlmStepResult.ToolCalls.Should().ContainSingle()
            .Which.Name.Should().Be("ornn_search_skills");
        execution.AuthorizedToolStep.Should().NotBeNull();
        var recoverySnapshot = execution.AuthorizedToolCallSafeties.Should()
            .ContainSingle().Which;
        recoverySnapshot.ToolName.Should().Be("ornn_search_skills");
        recoverySnapshot.Presentation.Should().NotBeNull();
        recoverySnapshot.Presentation!.InvocationName.Should().Be("ornn_search_skills");
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
                CommandName: "project-summary",
                OriginalCommand: "使用精确名称为 project-summary 的 skill",
                PrimarySkillName: "project-summary",
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
            ArgumentsJson = "{\"skill\":\"project-summary\"}",
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
        publishedChunks.Should().ContainSingle(chunk => chunk.LLMInvocationStarted != null);
        publishedChunks.Should().ContainSingle(chunk => chunk.DeltaContent == "workflow completed");
        publishedChunks.Should().ContainSingle(chunk => chunk.LLMInvocationCompleted != null);
        publishedChunks.Select(chunk =>
                chunk.LLMInvocationStarted != null
                    ? "start"
                    : chunk.LLMInvocationCompleted != null
                        ? "end"
                        : "content")
            .Should().Equal("start", "content", "end");
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
            CommandName: "project-summary",
            OriginalCommand: "使用精确名称为 project-summary 的 skill",
            PrimarySkillName: "project-summary",
            MaxOrnnSearchAttempts: 2,
            CommandArguments: "生成项目摘要并运行 workflow");
        var toolContext = AgentToolExecutionContext.Empty with { SkillRecovery = recovery };
        var executor = CreateToolEnabledExecutor(
            [useSkill, searchSkills],
            provider,
            toolContext: toolContext);
        var workItem = BuildToolEnabledWorkItem();
        workItem.StepState.ToolContext = toolContext.ToPayload();
        var call = new ToolCall
        {
            Id = "call-load-project-skill",
            Name = "use_skill",
            ArgumentsJson = "{\"skill\":\"project-summary\"}",
        };
        var assistant = AgentRunReplyStepMappers.ToProto(new ChatMessage
        {
            Role = "assistant",
            ToolCalls = [call],
        });
        var result = AgentRunReplyStepMappers.ToProto(ToolCallLoop.BuildToolResultMessage(
            call.Id,
            call.Name,
            "# project-summary\n\nIf summarization failed, return a typed error artifact."));
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
        var useSkill = new UseSkillTool(new LocalSkillCatalog());
        var provider = new RecordingProvider();
        var recovery = new AgentSkillRecoveryContext(
            RequireInitialOrnnSearch: true,
            RequireOrnnSearchOnBlocker: true,
            CommandName: "project-review",
            OriginalCommand: "查找并运行项目评审 skill",
            PrimarySkillName: null,
            MaxOrnnSearchAttempts: 2,
            CommandArguments: "评审项目并运行 workflow");
        var toolContext = AgentToolExecutionContext.Empty with { SkillRecovery = recovery };
        var executor = CreateToolEnabledExecutor(useSkill, provider, toolContext: toolContext);
        var workItem = BuildToolEnabledWorkItem();
        workItem.StepState.ToolContext = toolContext.ToPayload();
        var call = new ToolCall
        {
            Id = "call-search-project-skill",
            Name = "ornn_search_skills",
            ArgumentsJson = "{\"query\":\"project\",\"scope\":\"mixed\"}",
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
            {"result_type":"skill_search","status":"success","matches":[{"skill_name":"project-summary","description":"Summarize projects","is_private":false,"category":"productivity","tags":["summary"]}],"http_status":200,"text":"one match"}
            """));
        workItem.StepState.Messages.Add(assistant);
        workItem.StepState.Messages.Add(result);
        workItem.StepState.PendingHistoryMessages.Add(assistant.Clone());
        workItem.StepState.PendingHistoryMessages.Add(result.Clone());

        var roundTripped = AgentRunReplyStepMappers.FromProto(result);
        var search = roundTripped.ToolResultView!.SkillSearch!;
        search.Status.Should().Be(ToolResultViewStatus.Success);
        search.HttpStatus.Should().Be(200);
        search.Matches.Should().ContainSingle().Which.SkillName.Should().Be("project-summary");

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);

        provider.Requests.Should().BeEmpty();
        var planned = execution.Continuation.LlmStepResult.ToolCalls.Should().ContainSingle().Which;
        planned.Name.Should().Be("use_skill");
        planned.ArgumentsJson.Should().Contain("project-summary");
        execution.AuthorizedToolStep.Should().NotBeNull();
        var presentation = execution.AuthorizedToolCallSafeties.Should()
            .ContainSingle().Which.Presentation;
        presentation.Should().NotBeNull();
        presentation!.Kind.Should().Be(ToolPresentationKind.Skill);
        presentation.Skill.SkillName.Should().Be("project-summary");
    }

    [Fact]
    public async Task NyxIdChatTurnExecutor_UseSkillAfterPreamble_ShouldPublishOneExactSkillStart()
    {
        const string skillName = "project-summary";
        const string preamble = "I will load the requested workflow skill.";
        var useSkill = new UseSkillTool(new LocalSkillCatalog());
        var generationExecutor = CreateToolEnabledExecutor(
            useSkill,
            new TextThenToolCallProvider(
                useSkill.Name,
                preamble,
                $$"""{"skill":"{{skillName}}"}"""));
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var session = new NyxIdChatTransientExecutionSession();
        var progress = new List<NyxIdChatOperationProgressSignal>();
        Task ReportProgressAsync(
            NyxIdChatOperationProgressSignal signal,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            progress.Add(signal.Clone());
            return Task.CompletedTask;
        }

        var llmExecution = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = BuildOperationKey("step-use-skill-plan", "operation-use-skill-plan"),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "load the project summary skill",
                        SessionId = "turn-1",
                    },
                },
            },
            session,
            ReportProgressAsync,
            CancellationToken.None);
        var call = llmExecution.Result.Llm.ToolCalls.Should().ContainSingle().Which;
        llmExecution.Result.Llm.ToolCatalogCaptured.Should().BeTrue();
        call.Presentation.Kind.Should().Be(ToolPresentationKind.Skill);
        call.Presentation.Skill.SkillName.Should().Be(skillName);
        progress.Should().NotContain(signal =>
            signal.ProgressCase == NyxIdChatOperationProgressSignal.ProgressOneofCase.Text &&
            signal.Text.Delta.Contains(preamble, StringComparison.Ordinal));
        var modelStartedSignal = progress.Should().ContainSingle(signal =>
                signal.ProgressCase ==
                NyxIdChatOperationProgressSignal.ProgressOneofCase.ModelStarted)
            .Which;
        modelStartedSignal.ModelStarted.AvailableToolNames.Should().Equal(useSkill.Name);
        var modelCompletedSignal = progress.Should().ContainSingle(signal =>
                signal.ProgressCase ==
                NyxIdChatOperationProgressSignal.ProgressOneofCase.ModelCompleted)
            .Which;
        modelCompletedSignal.ModelCompleted.OperationId.Should()
            .Be(modelStartedSignal.ModelStarted.OperationId);
        var modelStartFrame = NyxIdChatConversationAguiFrameBuilder.BuildProgressed(
                "turn-1",
                new NyxIdChatOperationProgressedEvent
                {
                    Progress = modelStartedSignal.Clone(),
                    ProgressSequence = modelStartedSignal.Sequence,
                })
            .Should().ContainSingle().Which;
        modelStartFrame.ModelCallStart.AvailableToolNames.Should().Equal(useSkill.Name);
        var modelEndFrame = NyxIdChatConversationAguiFrameBuilder.BuildProgressed(
                "turn-1",
                new NyxIdChatOperationProgressedEvent
                {
                    Progress = modelCompletedSignal.Clone(),
                    ProgressSequence = modelCompletedSignal.Sequence,
                })
            .Should().ContainSingle().Which;
        modelEndFrame.ModelCallEnd.OperationId.Should().Be(modelStartFrame.ModelCallStart.OperationId);

        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = BuildOperationKey("step-use-skill", "operation-use-skill"),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = call.CallId,
                    ToolName = call.ToolName,
                    ArgumentsJson = call.ArgumentsJson,
                    MayChangeExternalState = call.Safety.MayChangeExternalState,
                    Presentation = call.Presentation.Clone(),
                },
            },
            session,
            ReportProgressAsync,
            CancellationToken.None);

        var start = progress.Should().ContainSingle(signal =>
                signal.ProgressCase ==
                NyxIdChatOperationProgressSignal.ProgressOneofCase.ToolStarted &&
                signal.ToolStarted.CallId == call.CallId)
            .Which.ToolStarted;
        start.Presentation.Kind.Should().Be(ToolPresentationKind.Skill);
        start.Presentation.Skill.SkillName.Should().Be(skillName);
        start.Presentation.Skill.Source.Should().Be("local-or-remote");
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
                CommandName: "project-summary",
                OriginalCommand: "使用精确名称为 project-summary 的 skill",
                PrimarySkillName: "project-summary",
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
                        Prompt = "生成项目摘要并运行 workflow",
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
    public async Task BuildToolStepContinuation_WithOnlyTransientPlanCredential_ShouldRebuildCredentialGatedCatalog()
    {
        const string planToken = "fresh-plan-token";
        var registeredTool = new CredentialCapturingTool("connected_effect");
        var executor = CreateCredentialGatedExecutor(
            registeredTool,
            new ToolCallProvider(registeredTool.Name),
            planToken);
        var llmWorkItem = BuildToolEnabledWorkItem();
        var planContext = BuildCredentialContext(planToken);
        llmWorkItem.StepState.ToolContext = planContext.ToPayload();
        llmWorkItem.Request.ToolContext = planContext.ToPayload();
        llmWorkItem.Request.LlmControl = new LLMControlContextPayload
        {
            NyxIdAccessToken = planToken,
        };
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = BuildDurablyAuthorizedToolStepWorkItem(llmWorkItem, execution.Continuation);
        toolWorkItem = toolWorkItem with
        {
            StepState = AgentRunReplyStepCredentials.StripRuntimeCredentials(toolWorkItem.StepState),
        };
        toolWorkItem.Request.LlmControl = null;

        var continuation = await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            authorizedToolStep: null,
            CancellationToken.None);

        continuation.ToolStepResult.AuthorizationOutcome.Should().Be(
            AgentRunToolAuthorizationOutcome.DurableMatched);
        registeredTool.ExecutionTokens.Should().Equal(planToken);
    }

    [Fact]
    public async Task BuildToolStepContinuation_AfterPersistedStateStrip_ShouldRestoreSupplementalSourceCredential()
    {
        const string executionToken = "fresh-proxy-token";
        const string sourceToken = "fresh-source-readable-token";
        var registeredTool = new CredentialCapturingTool("connected_effect");
        var executor = CreateCredentialGatedExecutor(
            registeredTool,
            new ToolCallProvider(registeredTool.Name),
            executionToken,
            sourceToken);
        var llmWorkItem = BuildToolEnabledWorkItem();
        var requestContext = BuildCredentialContext(
            executionToken,
            AgentToolNyxIdCredentialKind.ProxyDelegation,
            sourceToken);
        llmWorkItem.StepState.ToolContext = requestContext.ToPayload();
        llmWorkItem.Request.ToolContext = requestContext.ToPayload();
        llmWorkItem.Request.LlmControl = new LLMControlContextPayload
        {
            NyxIdAccessToken = executionToken,
        };
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = BuildDurablyAuthorizedToolStepWorkItem(llmWorkItem, execution.Continuation);
        toolWorkItem = toolWorkItem with
        {
            StepState = AgentRunReplyStepCredentials.StripRuntimeCredentials(toolWorkItem.StepState),
        };

        var continuation = await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            authorizedToolStep: null,
            CancellationToken.None);

        continuation.ToolStepResult.AuthorizationOutcome.Should().Be(
            AgentRunToolAuthorizationOutcome.DurableMatched);
        registeredTool.ExecutionCredentials.Should().ContainSingle().Which.Should().Be(
            (executionToken, AgentToolNyxIdCredentialKind.ProxyDelegation, sourceToken));
        toolWorkItem.StepState.ToolContext.Credentials.NyxIdAccessToken.Should().BeEmpty();
        toolWorkItem.StepState.ToolContext.Credentials.SourceReadableNyxIdAccessToken.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildToolStepContinuation_WithRuntimeCredential_ShouldOverrideTransientPlanCredential()
    {
        const string planToken = "stale-plan-token";
        const string runtimeToken = "fresh-runtime-token";
        var registeredTool = new CredentialCapturingTool("connected_effect");
        var executor = CreateCredentialGatedExecutor(
            registeredTool,
            new ToolCallProvider(registeredTool.Name),
            runtimeToken);
        var llmWorkItem = BuildToolEnabledWorkItem();
        llmWorkItem.StepState.ToolContext = BuildCredentialContext(planToken).ToPayload();
        llmWorkItem.Request.ToolContext = BuildCredentialContext(planToken).ToPayload();
        llmWorkItem.Request.LlmControl = new LLMControlContextPayload
        {
            NyxIdAccessToken = runtimeToken,
        };
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = BuildDurablyAuthorizedToolStepWorkItem(llmWorkItem, execution.Continuation);

        var continuation = await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            authorizedToolStep: null,
            CancellationToken.None);

        continuation.ToolStepResult.AuthorizationOutcome.Should().Be(
            AgentRunToolAuthorizationOutcome.DurableMatched);
        registeredTool.ExecutionTokens.Should().Equal(runtimeToken);
    }

    [Fact]
    public async Task BuildToolStepContinuation_WithPreservedPlanCredentialAndDefinitionDrift_ShouldFailClosed()
    {
        const string planToken = "fresh-plan-token";
        var registeredTool = new CredentialCapturingTool("connected_effect");
        var executor = CreateCredentialGatedExecutor(
            registeredTool,
            new ToolCallProvider(registeredTool.Name),
            planToken);
        var llmWorkItem = BuildToolEnabledWorkItem();
        var planContext = BuildCredentialContext(planToken);
        llmWorkItem.StepState.ToolContext = planContext.ToPayload();
        llmWorkItem.Request.ToolContext = planContext.ToPayload();
        llmWorkItem.Request.LlmControl = new LLMControlContextPayload
        {
            NyxIdAccessToken = planToken,
        };
        var execution = await executor.BuildLlmStepExecutionAsync(
            llmWorkItem,
            CancellationToken.None);
        var toolWorkItem = BuildDurablyAuthorizedToolStepWorkItem(llmWorkItem, execution.Continuation);
        toolWorkItem.Request.LlmControl = null;
        registeredTool.DriftDefinition();

        var continuation = await executor.BuildToolStepContinuationAsync(
            toolWorkItem,
            authorizedToolStep: null,
            CancellationToken.None);

        continuation.ToolStepResult.AuthorizationOutcome.Should().Be(
            AgentRunToolAuthorizationOutcome.Rejected);
        registeredTool.ExecutionTokens.Should().BeEmpty();
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

    private static AgentRunReplyGenerationExecutor CreateCredentialGatedExecutor(
        CredentialCapturingTool tool,
        ILLMProvider provider,
        string expectedExecutionToken,
        string? expectedCatalogToken = null) =>
        new(
            Substitute.For<IActorDispatchPort>(),
            new CredentialGatedStepPlanReplyGenerator(
                tool,
                provider,
                expectedExecutionToken,
                expectedCatalogToken ?? expectedExecutionToken),
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);

    private static ProfiledChannelExecutorFixture CreateProfiledChannelExecutor()
    {
        var tool = new CountingTool("workspace_profile_tool");
        var provider = new RecordingProvider();
        var generator = new CatalogAwareStepPlanReplyGenerator(tool, provider);
        var profile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-channel-alpha",
            ProfileVersion = "profile-v1",
            PublishedRevision = 1,
            AgentKind = AgentProfilePolicies.ChannelReplyAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = AgentProfilePolicies.ChannelReplyRouteToolSet,
            ActivationMode = AgentProfileActivationMode.Enforced,
        });
        var authority = new AgentProfileTurnAuthorityState
        {
            ReconciliationKey = new AgentProfileTurnReconciliationKey
            {
                SessionId = "run-1",
                Attempt = 1,
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
        };
        authority.AuthorityCeilingToolNames.Add(tool.Name);
        var catalog = new AgentTurnToolCatalog(
            [tool.Name],
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: null,
            candidateIntentId: null,
            exactTools: [tool]);
        var profileResolver = Substitute.For<IAgentProfileTurnSnapshotResolver>();
        profileResolver.ResolveAsync(
                "scope-alpha",
                "run-1",
                ChatRouteAgentProfileKind.ChannelReply,
                null,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AgentProfileTurnSnapshotResolution.Selected(profile)));
        var profilePlanner = Substitute.For<IAgentProfileTurnToolCatalogPlanner>();
        profilePlanner.PrepareAsync(
                Arg.Any<AgentProfileSnapshot>(),
                "run-1",
                "run",
                Arg.Any<IReadOnlyList<IAgentTool>>(),
                Arg.Any<AgentToolExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AgentProfileTurnAuthorityPreparation.Create(authority)));
        profilePlanner.MaterializeCommittedAsync(
                Arg.Any<AgentProfileSnapshot>(),
                Arg.Any<AgentProfileTurnAuthorityState>(),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<IAgentTool>>(),
                Arg.Any<AgentToolExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AgentTurnToolCatalogMaterialization.Create(catalog, authority)));
        var executor = new AgentRunReplyGenerationExecutor(
            Substitute.For<IActorDispatchPort>(),
            generator,
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance,
            profileSnapshotResolver: profileResolver,
            profileCatalogPlanner: profilePlanner);
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Caller = new AgentToolCallerContext("scope-alpha", "scope-alpha", "run-1"),
        };
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
            ToolContext = toolContext.ToPayload(),
            TargetRef = new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel
                {
                    ProfileKind = ChatRouteAgentProfileKind.ChannelReply,
                    ToolSetRef = new ChatRouteToolSetRef
                    {
                        Name = AgentProfilePolicies.ChannelReplyRouteToolSet,
                    },
                },
            },
        };
        return new ProfiledChannelExecutorFixture(
            executor,
            request,
            profile,
            authority,
            catalog,
            tool,
            provider,
            generator,
            profileResolver,
            profilePlanner);
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

    private static AgentToolExecutionContext BuildCredentialContext(
        string token,
        AgentToolNyxIdCredentialKind credentialKind = AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
        string? sourceReadableToken = null) =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = token,
                NyxIdCredentialKind = credentialKind,
                SourceReadableNyxIdAccessToken = sourceReadableToken,
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

    private sealed record ProfiledChannelExecutorFixture(
        AgentRunReplyGenerationExecutor Executor,
        NeedsLlmReplyEvent Request,
        AgentProfileSnapshot Profile,
        AgentProfileTurnAuthorityState Authority,
        AgentTurnToolCatalog Catalog,
        CountingTool Tool,
        RecordingProvider Provider,
        CatalogAwareStepPlanReplyGenerator Generator,
        IAgentProfileTurnSnapshotResolver ProfileResolver,
        IAgentProfileTurnToolCatalogPlanner ProfilePlanner);

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

    private sealed class TextThenToolCallProvider(
        string toolName,
        string content,
        string argumentsJson = "{}") : ILLMProvider
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
                    ArgumentsJson = argumentsJson,
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

    private sealed class ShortCircuitLlmMiddleware : ILLMCallMiddleware
    {
        public Task InvokeAsync(LLMCallContext context, Func<Task> next)
        {
            _ = next;
            context.Response = new LLMResponse { Content = "middleware-answer" };
            context.Terminate = true;
            return Task.CompletedTask;
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

    private sealed class CredentialCapturingTool : IAgentTool
    {
        private readonly string _name;
        private string _description;

        public CredentialCapturingTool(string name)
        {
            _name = name;
            _description = name;
        }

        public List<string> ExecutionTokens { get; } = [];
        public List<(string ExecutionToken, AgentToolNyxIdCredentialKind CredentialKind,
            string SourceReadableToken)> ExecutionCredentials { get; } = [];
        public string Name => _name;
        public string Description => _description;
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            var credentials = AgentToolRequestContext.Current?.Credentials ?? AgentToolCredentials.Empty;
            ExecutionTokens.Add(credentials.NyxIdAccessToken ?? string.Empty);
            ExecutionCredentials.Add((
                credentials.NyxIdAccessToken ?? string.Empty,
                credentials.NyxIdCredentialKind,
                credentials.SourceReadableNyxIdAccessToken ?? string.Empty));
            return Task.FromResult("{}");
        }

        public void DriftDefinition() => _description = "changed definition";
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
                System.Enum.TryParse<SafetyMode>(mode, ignoreCase: false, out var parsed))
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
            AgentTurnToolCatalog? turnCatalog = null) =>
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

    private sealed class CatalogAwareStepPlanReplyGenerator(
        IAgentTool legacyDiscoveredTool,
        ILLMProvider provider) : IAgentRunStepConversationReplyGenerator
    {
        public AgentTurnToolCatalog? ReceivedCatalog { get; private set; }

        public Task<AgentRunReplyStepPlan> BuildStepPlanAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IReadOnlyList<ConversationHistoryEntry>? priorHistory,
            ChatAttachmentInputContext? attachmentContext,
            bool forceDisableTools,
            CancellationToken ct,
            AgentTurnToolCatalog? turnCatalog = null)
        {
            ReceivedCatalog = turnCatalog;
            var tools = new ToolManager();
            tools.Register(legacyDiscoveredTool);
            var runtime = new ChatRuntime(
                () => provider,
                new ChatHistory(),
                new ToolCallLoop(tools),
                hooks: null,
                requestBuilder: _ => new LLMRequest
                {
                    Messages = [],
                    Tools = tools.GetAll(),
                });
            return Task.FromResult(new AgentRunReplyStepPlan(
                runtime.CreateStepExecutor(turnCatalog),
                new Dictionary<string, string>(),
                llmControl ?? LLMControlContext.Empty,
                toolContext ?? AgentToolExecutionContext.Empty,
                InitialMessages: [],
                MaxToolRounds: 1));
        }

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

    private sealed class CredentialGatedStepPlanReplyGenerator(
        IAgentTool tool,
        ILLMProvider provider,
        string expectedExecutionToken,
        string expectedCatalogToken) : IAgentRunStepConversationReplyGenerator
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
            AgentTurnToolCatalog? turnCatalog = null)
        {
            var tools = new ToolManager();
            if (!forceDisableTools &&
                string.Equals(
                    toolContext?.Credentials.NyxIdAccessToken,
                    expectedExecutionToken,
                    StringComparison.Ordinal) &&
                string.Equals(
                    AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(toolContext?.Credentials),
                    expectedCatalogToken,
                    StringComparison.Ordinal))
            {
                tools.Register(tool);
            }

            var runtime = new ChatRuntime(
                () => provider,
                new ChatHistory(),
                new ToolCallLoop(
                    tools,
                    toolExecutionPort: new ChannelConversationTurnRunnerTests.TestAgentToolExecutionPort()),
                hooks: null,
                requestBuilder: _ => new LLMRequest
                {
                    Messages = [],
                    Tools = tools.GetAll(),
                    ToolContext = toolContext ?? AgentToolExecutionContext.Empty,
                });
            return Task.FromResult(new AgentRunReplyStepPlan(
                runtime.CreateStepExecutor(turnCatalog: null),
                new Dictionary<string, string>(),
                llmControl ?? LLMControlContext.Empty,
                toolContext ?? AgentToolExecutionContext.Empty,
                InitialMessages: [],
                MaxToolRounds: 1));
        }

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
