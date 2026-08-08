using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdChatDurableRetryCapabilityTests
{
    private static readonly Timestamp Now = Timestamp.FromDateTimeOffset(
        new DateTimeOffset(2026, 8, 8, 8, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("fresh-per-request-token", AgentToolReceiptStatus.ApprovalRequired)]
    [InlineData("valid-grant-token", AgentToolReceiptStatus.Success)]
    public async Task ConfirmedEffectRetry_AfterFreshTurnSession_ShouldRematerializeExactCapability(
        string retryToken,
        AgentToolReceiptStatus expectedStatus)
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var (state, originalSession) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var toolStep = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);

        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(toolStep, retryToken, expectedStateVersion: 20),
            stateVersion: 20,
            Now);
        retry.ShouldCommit.Should().BeTrue();
        retry.ShouldDispatch.Should().BeFalse();
        retry.State.ActiveTask.Gate.Status.Should().Be(NyxIdChatPlanGateStatus.Pending);

        var confirmed = NyxIdChatPlanGateDecisions.Resolve(
            retry.State,
            BuildPlanConfirmation(retry.State, retryToken, expectedStateVersion: 21),
            currentStateVersion: 21,
            Now);
        confirmed.ShouldCommit.Should().BeTrue();
        confirmed.NextCommand.Should().NotBeNull();
        confirmed.NextCommand!.Key.OperationGeneration.Should().Be(2);
        confirmed.NextCommand.PlanGateContinuation.RetryArguments.Should().NotBeNull();

        var freshSession = new NyxIdChatTransientExecutionSession();
        var result = await executor.ExecuteAsync(
            confirmed.NextCommand,
            freshSession,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.Failure.Should().BeNull();
        result.Result.Tool.Should().NotBeNull();
        result.Result.Tool.Receipt.Status.Should().Be(expectedStatus);
        if (expectedStatus == AgentToolReceiptStatus.ApprovalRequired)
        {
            result.Result.Tool.Receipt.ApprovalRequestId.Should().Be("approval-generation-2");
            result.Result.Tool.Receipt.NyxIdApprovalDecisionMode.Should().Be(
                NyxIdApprovalDecisionMode.PerRequest);
        }
        else
        {
            result.Result.Tool.Receipt.ProviderResourceId.Should().Be("resource-generation-2");
        }

        tool.ExecutionTokens.Should().Equal("uncertain-token", retryToken);
        originalSession.AuthorizedToolStep.Should().BeNull(
            "the original transient capability was consumed before the fresh-session retry");
        Encoding.UTF8.GetString(retry.State.ToByteArray()).Should().NotContain(retryToken);
        retry.State.ToString().Should().NotContain(retryToken);
    }

    [Theory]
    [InlineData("expired-grant-token")]
    [InlineData("revoked-grant-token")]
    [InlineData("scope-mismatched-grant-token")]
    [InlineData("ttl-expired-grant-token")]
    public async Task ConfirmedGrantRetry_WhenStandingGrantIsInvalid_ShouldReenterExactApproval(
        string retryToken)
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var (state, _) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var effect = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);
        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(effect, retryToken, expectedStateVersion: 22),
            stateVersion: 22,
            Now);
        var confirmed = NyxIdChatPlanGateDecisions.Resolve(
            retry.State,
            BuildPlanConfirmation(retry.State, retryToken, expectedStateVersion: 23),
            currentStateVersion: 23,
            Now);

        var execution = await executor.ExecuteAsync(
            confirmed.NextCommand!,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.Failure.Should().BeNull(execution.Result.Failure?.ToString());
        execution.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.ApprovalRequired);
        execution.Result.Tool.Receipt.NyxIdApprovalDecisionMode.Should().Be(
            NyxIdApprovalDecisionMode.Grant);
        execution.Result.Tool.Receipt.ApprovalRequestId.Should().Be("approval-generation-2");
        confirmed.NextCommand!.Key.OperationGeneration.Should().Be(2);
        tool.ExecutionTokens.Should().Equal("uncertain-token", retryToken);
    }

    [Fact]
    public async Task ConfirmedPerRequestRetry_WhenDecisionArrivesDuringInvocation_ShouldVerifyGenerationTwo()
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var (state, _) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var effect = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);
        const string retryToken = "approved-per-request-token";

        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(effect, retryToken, expectedStateVersion: 25),
            stateVersion: 25,
            Now);
        var confirmed = NyxIdChatPlanGateDecisions.Resolve(
            retry.State,
            BuildPlanConfirmation(retry.State, retryToken, expectedStateVersion: 26),
            currentStateVersion: 26,
            Now);

        var execution = await executor.ExecuteAsync(
            confirmed.NextCommand!,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.Failure.Should().BeNull(execution.Result.Failure?.ToString());
        execution.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        confirmed.NextCommand!.Key.OperationGeneration.Should().Be(2);
        tool.PerRequestApprovalIds.Should().Equal("approval-generation-2");
        tool.ExecutionTokens.Should().Equal("uncertain-token", retryToken);

        var afterEffect = NyxIdChatTaskLifecycle.ApplyOperationResult(
            confirmed.State,
            execution.Result,
            Now);
        afterEffect.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        afterEffect.NextCommand.Should().NotBeNull();
        var verification = afterEffect.State.ActiveTask.Steps.Single(step =>
            string.Equals(
                step.StepId,
                afterEffect.NextCommand!.Key.StepId,
                StringComparison.Ordinal));
        verification.Kind.Should().Be(NyxIdChatStepKind.Postcondition);
        verification.DependsOn.Should().Contain(effect.StepId);
        verification.Operation.Key.OperationGeneration.Should().Be(2);
        var readBack = verification.Source.Postcondition.ToolReadBack;

        var completed = NyxIdChatTaskLifecycle.ApplyOperationResult(
            afterEffect.State,
            new NyxIdChatOperationResultSignal
            {
                Key = verification.Operation.Key.Clone(),
                ToolVerification = new NyxIdChatToolVerificationResult
                {
                    EffectStepId = effect.StepId,
                    Disposition = NyxIdChatToolVerificationDisposition.Applied,
                    ReadOperation = readBack.ReadOperation.Clone(),
                    CheckName = readBack.CheckName,
                },
            },
            Now);

        completed.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        completed.NextCommand.Should().BeNull();
        completed.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        completed.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        var completedEffect = completed.State.ActiveTask.Steps.Single(step =>
            string.Equals(step.StepId, effect.StepId, StringComparison.Ordinal));
        completedEffect.Operation.Key.OperationGeneration.Should().Be(2);
        completedEffect.Status.Should().Be(NyxIdChatStepStatus.Done);
        completedEffect.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.Confirmed);
        Encoding.UTF8.GetString(completed.State.ToByteArray()).Should().NotContain(retryToken);
    }

    [Fact]
    public async Task ConfirmedEffectRetry_WhenCurrentDefinitionDrifts_ShouldFailClosedBeforeExecution()
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var (state, _) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var toolStep = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);
        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(toolStep, "fresh-token", expectedStateVersion: 30),
            stateVersion: 30,
            Now);
        var confirmed = NyxIdChatPlanGateDecisions.Resolve(
            retry.State,
            BuildPlanConfirmation(retry.State, "fresh-token", expectedStateVersion: 31),
            currentStateVersion: 31,
            Now);
        tool.DescriptionOverride = "The current catalog now exposes a different definition.";

        var result = await executor.ExecuteAsync(
            confirmed.NextCommand!,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.Failure.Should().NotBeNull();
        result.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolAuthorizationMismatchCode);
        result.Result.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        tool.ExecutionTokens.Should().Equal("uncertain-token");
    }

    [Fact]
    public async Task DirectEffectRetry_AfterFreshTurnSession_ShouldUseCommittedProfileAuthority()
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var (state, _) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        state.ActiveTask.Gate.Mode = NyxIdChatPlanGateMode.Auto;
        state.ActiveTask.Gate.Status = NyxIdChatPlanGateStatus.Satisfied;
        var toolStep = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);

        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(toolStep, "valid-grant-token", expectedStateVersion: 40),
            stateVersion: 40,
            Now);

        retry.ShouldCommit.Should().BeTrue();
        retry.ShouldDispatch.Should().BeTrue();
        retry.NextCommand.Should().NotBeNull();
        retry.NextCommand!.Tool.AgentProfile.Should().BeEquivalentTo(state.AgentProfile);
        retry.NextCommand.Tool.AgentProfileTurnAuthority.Should().BeEquivalentTo(
            state.ActiveTurn.AgentProfileTurnAuthority);
        retry.NextCommand.Tool.RematerializeDurableAuthorization.Should().BeTrue();
        var result = await executor.ExecuteAsync(
            retry.NextCommand,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.Failure.Should().BeNull();
        result.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        tool.ExecutionTokens.Should().Equal("uncertain-token", "valid-grant-token");
    }

    [Fact]
    public async Task ConfirmedEffectRetry_WhenCurrentCatalogRevokesTool_ShouldFailClosed()
    {
        var tool = new RetryEffectTool();
        var registry = new StaticToolSetRegistry("profile.route", [tool]);
        var executor = CreateTurnExecutor(tool, registry);
        var (state, _) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var toolStep = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);
        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(toolStep, "valid-grant-token", expectedStateVersion: 50),
            stateVersion: 50,
            Now);
        var committedAuthority = retry.State.ActiveTurn.AgentProfileTurnAuthority.Clone();
        registry.ReplaceTools([]);
        var confirmed = NyxIdChatPlanGateDecisions.Resolve(
            retry.State,
            BuildPlanConfirmation(retry.State, "valid-grant-token", expectedStateVersion: 51),
            currentStateVersion: 51,
            Now);

        confirmed.ShouldCommit.Should().BeTrue();
        confirmed.NextCommand.Should().NotBeNull();
        confirmed.NextCommand!.PlanGateContinuation.AgentProfileTurnAuthority
            .Should().BeEquivalentTo(committedAuthority);
        confirmed.NextCommand.PlanGateContinuation.AgentProfileTurnAuthority
            .AuthorityCeilingToolNames.Should().ContainSingle(tool.Name);
        var result = await executor.ExecuteAsync(
            confirmed.NextCommand,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.Failure.Should().NotBeNull();
        result.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolAuthorizationMismatchCode);
        result.Result.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        tool.ExecutionTokens.Should().Equal("uncertain-token");
    }

    [Fact]
    public async Task TransientToolStep_WithHigherGeneration_ShouldNotRematerializeDurableAuthorization()
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var llmKey = Key("step-llm", "operation-llm", generation: 1);
        var initialState = ActiveLlmState(llmKey, tool.Name);
        var session = new NyxIdChatTransientExecutionSession();
        var llmResult = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = llmKey.Clone(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "Create the approval record.",
                        SessionId = "turn-alpha",
                        ScopeId = "scope-alpha",
                        ToolContext = ToolContext("uncertain-token"),
                    },
                    AgentProfile = initialState.AgentProfile.Clone(),
                    AgentProfileTurnAuthority =
                        initialState.ActiveTurn.AgentProfileTurnAuthority.Clone(),
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var call = llmResult.Result.Llm.ToolCalls.Should().ContainSingle().Subject;
        var planned = NyxIdChatTaskLifecycle.ApplyOperationResult(
            initialState,
            llmResult.Result,
            Now,
            planGateConfirmationThresholdSeconds: 1);
        var plannedTool = planned.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        plannedTool.Operation.Key.OperationGeneration = 2;
        planned.State.ActiveTask.Gate.Admissions.Single().Key.OperationGeneration = 2;
        plannedTool.RematerializeDurableAuthorization.Should().BeFalse();
        var confirmation = NyxIdChatPlanGateDecisions.Resolve(
            planned.State,
            BuildPlanConfirmation(planned.State, "uncertain-token", expectedStateVersion: 60),
            currentStateVersion: 60,
            Now);
        confirmation.NextCommand.Should().NotBeNull();
        confirmation.NextCommand!.PlanGateContinuation.RetryArguments.Should().BeNull();

        var result = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = Key("step-tool", "operation-tool", generation: 2),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = call.CallId,
                    ToolName = call.ToolName,
                    ArgumentsJson = call.ArgumentsJson,
                    MayChangeExternalState = call.Safety.MayChangeExternalState,
                    IdempotencyKey = "operation-tool",
                    OperationAdmission = call.OperationAdmission.Clone(),
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.Failure.Should().BeNull(result.Result.Failure?.ToString());
        result.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        tool.ExecutionTokens.Should().Equal("uncertain-token");
    }

    private static NyxIdChatTurnOperationExecutor CreateTurnExecutor(
        RetryEffectTool tool,
        StaticToolSetRegistry? registry = null)
    {
        var provider = new ExactToolCallProvider(tool.Name);
        var generationExecutor = new AgentRunReplyGenerationExecutor(
            Substitute.For<IActorDispatchPort>(),
            new RebuildingStepPlanReplyGenerator(tool, provider),
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        var materializer = new AgentProfileTurnCatalogMaterializer(
            registry ?? new StaticToolSetRegistry("profile.route", [tool]),
            new NoMatchClassifier());
        return new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            materializer);
    }

    private static async Task<(NyxIdChatConversationGAgentState State,
        NyxIdChatTransientExecutionSession OriginalSession)> BuildReconciledNotAppliedStateAsync(
        NyxIdChatTurnOperationExecutor executor,
        RetryEffectTool tool)
    {
        var llmKey = Key("step-llm", "operation-llm", generation: 1);
        var initialState = ActiveLlmState(llmKey, tool.Name);
        var session = new NyxIdChatTransientExecutionSession();
        var llmResult = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = llmKey.Clone(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "Create the approval record.",
                        SessionId = "turn-alpha",
                        ScopeId = "scope-alpha",
                        ToolContext = ToolContext("uncertain-token"),
                    },
                    AgentProfile = initialState.AgentProfile.Clone(),
                    AgentProfileTurnAuthority =
                        initialState.ActiveTurn.AgentProfileTurnAuthority.Clone(),
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var call = llmResult.Result.Llm.ToolCalls.Should().ContainSingle().Subject;
        call.OperationAdmission.DurableAuthorization.Should().NotBeNull();
        call.OperationAdmission.DurableAuthorization.ToolDefinitionFingerprint.Should().NotBeNullOrWhiteSpace();

        var planned = NyxIdChatTaskLifecycle.ApplyOperationResult(
            initialState,
            llmResult.Result,
            Now,
            planGateConfirmationThresholdSeconds: 1);
        planned.State.ActiveTask.Gate.Status.Should().Be(NyxIdChatPlanGateStatus.Pending);
        var firstConfirmation = NyxIdChatPlanGateDecisions.Resolve(
            planned.State,
            BuildPlanConfirmation(planned.State, "uncertain-token", expectedStateVersion: 10),
            currentStateVersion: 10,
            Now);
        var firstToolResult = await executor.ExecuteAsync(
            firstConfirmation.NextCommand!,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        firstToolResult.Result.Failure.Should().BeNull(firstToolResult.Result.Failure?.ToString());
        firstToolResult.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        tool.ExecutionTokens.Should().Equal("uncertain-token");

        var uncertain = NyxIdChatTaskLifecycle.ApplyOperationResult(
            firstConfirmation.State,
            firstToolResult.Result,
            Now);
        uncertain.NextCommand.Should().NotBeNull();
        var verification = uncertain.NextCommand!;
        var effectStep = uncertain.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        var readBack = effectStep.Source.Tool.OperationAdmission.ReadBack;
        var reconciled = NyxIdChatTaskLifecycle.ApplyOperationResult(
            uncertain.State,
            new NyxIdChatOperationResultSignal
            {
                Key = verification.Key.Clone(),
                ToolVerification = new NyxIdChatToolVerificationResult
                {
                    EffectStepId = effectStep.StepId,
                    Disposition = NyxIdChatToolVerificationDisposition.NotApplied,
                    ReadOperation = readBack.ReadOperation.Clone(),
                    CheckName = readBack.CheckName,
                    FailureCode = "EFFECT_NOT_FOUND",
                    SafeMessage = "The read-back proved that the effect was not applied.",
                },
            },
            Now);
        var reconciledTool = reconciled.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        reconciledTool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        reconciledTool.AvailableActions.Retry.Should().BeTrue();
        return (reconciled.State, session);
    }

    private static NyxIdChatRetryStepCommand BuildRetryCommand(
        NyxIdChatTaskStepState step,
        string token,
        long expectedStateVersion) => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = step.StepId,
        RetryRequestId = $"retry-{expectedStateVersion}",
        ClientRequestId = $"client-retry-{expectedStateVersion}",
        CommandId = $"command-retry-{expectedStateVersion}",
        CorrelationId = $"correlation-retry-{expectedStateVersion}",
        ExpectedOperationGeneration = step.Operation.Key.OperationGeneration,
        ExpectedStateVersion = expectedStateVersion,
        ToolContext = ToolContext(token),
    };

    private static NyxIdChatPlanResolveCommand BuildPlanConfirmation(
        NyxIdChatConversationGAgentState state,
        string token,
        long expectedStateVersion) => new()
    {
        ScopeId = state.ScopeId,
        ConversationActorId = state.ConversationActorId,
        TaskId = state.ActiveTask.TaskId,
        PlanId = state.ActiveTask.PlanId,
        PlanRevision = state.ActiveTask.PlanRevision,
        RequestId = state.ActiveTask.Gate.RequestId,
        ClientRequestId = $"client-confirm-{expectedStateVersion}",
        Confirmed = true,
        ExpectedStateVersion = expectedStateVersion,
        CommandId = $"command-confirm-{expectedStateVersion}",
        CorrelationId = $"correlation-confirm-{expectedStateVersion}",
        ToolContext = ToolContext(token),
    };

    private static AgentToolExecutionContextPayload ToolContext(string token) =>
        (AgentToolExecutionContext.Empty with
        {
            Caller = new AgentToolCallerContext("scope-alpha", "owner-alpha", null, null),
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = token,
                NyxIdCredentialKind = AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
                SourceReadableNyxIdAccessToken = token,
            },
            ExecutionOwner = new AgentToolExecutionOwner
            {
                Kind = AgentToolExecutionOwnerKind.Actor,
                OwnerId = "conversation-alpha",
            },
            Chat = new AgentChatInvocationContext(
                AgentChatInvocationSurface.NyxIdAssistant,
                "conversation-alpha",
                "turn-alpha",
                "task-alpha",
                null,
                null),
        }).ToPayload();

    private static NyxIdChatConversationGAgentState ActiveLlmState(
        NyxIdChatOperationKey key,
        string toolName)
    {
        var step = new NyxIdChatTaskStepState
        {
            StepId = key.StepId,
            Order = 1,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Description = "Plan the exact connected-service operation.",
            Source = new NyxIdChatStepSource { Llm = new NyxIdChatLLMStepSource() },
            RetryInputRebuildable = true,
            Operation = new NyxIdChatOperationState
            {
                Key = key.Clone(),
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Dispatched,
                Idempotent = true,
                IdempotencyKey = key.OperationId,
                RequestedAt = Now.Clone(),
                DispatchedAt = Now.Clone(),
            },
            AddedBy = NyxIdChatStepAddedBy.Initial,
            AddedInPlanRevision = 1,
            UpdatedAt = Now.Clone(),
        };
        var task = new NyxIdChatTaskState
        {
            TaskId = key.TaskId,
            TurnId = key.TurnId,
            Status = NyxIdChatTaskStatus.Active,
            ActiveStepId = key.StepId,
            ActiveOperationId = key.OperationId,
            SchemaVersion = 4,
            ActorId = key.ConversationActorId,
            PlanId = "plan-alpha",
            PlanRevision = 1,
            Gate = new NyxIdChatPlanGate
            {
                Mode = NyxIdChatPlanGateMode.Auto,
                Status = NyxIdChatPlanGateStatus.Satisfied,
                TaskId = key.TaskId,
                PlanId = "plan-alpha",
                PlanRevision = 1,
            },
            CreatedAt = Now.Clone(),
            UpdatedAt = Now.Clone(),
        };
        task.Steps.Add(step);
        task.PlanRevisions.Add(new NyxIdChatPlanRevisionRecord
        {
            PlanRevision = 1,
            RevisionCause = NyxIdChatPlanRevisionCause.Initial,
            CommittedAt = Now.Clone(),
            AddedStepIds = { step.StepId },
        });
        var turn = new NyxIdChatTurnState
        {
            TurnId = key.TurnId,
            TaskId = key.TaskId,
            Prompt = "Create the approval record.",
            Status = NyxIdChatTurnStatus.Active,
            AgentProfileTurnAuthority = BuildProfileAuthority(toolName),
        };
        return new NyxIdChatConversationGAgentState
        {
            ConversationActorId = key.ConversationActorId,
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            AgentProfile = BuildProfile(toolName),
            ActiveTurn = turn,
            LatestTurn = turn.Clone(),
            ActiveTask = task,
            UpdatedAt = Now.Clone(),
        };
    }

    private static AgentProfileSnapshot BuildProfile(string toolName) =>
        AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-alpha",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { toolName },
            },
            RecoveryToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { toolName },
            },
            ActivationMode = AgentProfileActivationMode.Enforced,
        });

    private static AgentProfileTurnAuthorityState BuildProfileAuthority(string toolName) => new()
    {
        ReconciliationKey = new AgentProfileTurnReconciliationKey
        {
            SessionId = "turn-alpha",
            Attempt = 1,
        },
        AuthorityKind = AgentProfileTurnAuthorityKind.Recovery,
        AuthorityCeilingToolNames = { toolName },
    };

    private static NyxIdChatOperationKey Key(string stepId, string operationId, long generation) => new()
    {
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = stepId,
        OperationId = operationId,
        OperationGeneration = generation,
    };

    private sealed class RebuildingStepPlanReplyGenerator(
        RetryEffectTool tool,
        ILLMProvider provider) : IAgentRunStepConversationReplyGenerator
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
            AgentProfileTurnCatalog? turnCatalog = null)
        {
            var tools = new ToolManager();
            if (!forceDisableTools)
                tools.Register(tool);
            var runtime = new ChatRuntime(
                () => provider,
                new ChatHistory(),
                new ToolCallLoop(tools, toolExecutionPort: new PassthroughExecutionPort()),
                hooks: null,
                requestBuilder: _ => new LLMRequest
                {
                    Messages = [],
                    Tools = tools.GetAll(),
                    ToolContext = toolContext ?? AgentToolExecutionContext.Empty,
                });
            return Task.FromResult(new AgentRunReplyStepPlan(
                runtime.CreateStepExecutor(turnCatalog: turnCatalog),
                new Dictionary<string, string>(),
                llmControl ?? LLMControlContext.Empty,
                toolContext ?? AgentToolExecutionContext.Empty,
                [ChatMessage.User(activity.Content?.Text ?? string.Empty)],
                MaxToolRounds: 1,
                DisableTools: forceDisableTools));
        }

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class StaticToolSetRegistry(
        string name,
        IReadOnlyList<IAgentTool> tools) : IToolSetRegistry
    {
        private IReadOnlyList<IAgentTool> _tools = tools;

        public void ReplaceTools(IReadOnlyList<IAgentTool> replacement) => _tools = replacement;

        public IReadOnlyList<string> GetRegisteredNames() => [name];

        public ToolSetResolveResult Resolve(string? requestedName) =>
            string.Equals(requestedName, name, StringComparison.Ordinal)
                ? ToolSetResolveResult.Success(name, [new StaticToolSource(_tools)])
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    requestedName ?? string.Empty,
                    "missing",
                    GetRegisteredNames()));
    }

    private sealed class PassthroughExecutionPort : IAgentToolExecutionPort
    {
        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            using var contextScope = AgentToolContextScope.Push(request.ExecutionContext);
            var terminal = await request.Tool.ExecuteWithOutcomeAsync(
                request.ExecutionContext.Request.CallId ?? string.Empty,
                request.Tool.Name,
                request.ArgumentsJson,
                ct);
            var safety = request.Tool.GetCallSafety(request.ArgumentsJson);
            return new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                terminal.ResultJson,
                terminal.Receipt!,
                IsMutation: !safety.IsReadOnly,
                FailureCode: string.Empty,
                SafeMessage: string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: false);
        }
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(
            CancellationToken ct = default) => Task.FromResult(tools);
    }

    private sealed class NoMatchClassifier : IAgentProfileTurnClassifier
    {
        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(AgentProfileTurnClassificationResult.NoMatch());
    }

    private sealed class ExactToolCallProvider(string toolName) : ILLMProvider
    {
        public string Name => "exact-tool-call-provider";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call-effect",
                    Name = toolName,
                    ArgumentsJson = "{\"approvalCode\":\"canary\"}",
                },
            };
            await Task.Yield();
        }
    }

    private sealed class RetryEffectTool : IAgentTool, IAgentToolOperationAdmissionOwner
    {
        private readonly AgentToolOperationAdmission _admission =
            AgentToolOperationAdmissionPayloadMapper.FromPayload(ExactWriteAdmission())!;

        public List<string> ExecutionTokens { get; } = [];
        public List<string> PerRequestApprovalIds { get; } = [];
        public string? DescriptionOverride { get; set; }
        public string Name => "connected-effect-alpha";
        public string Description => DescriptionOverride ?? "Create one exact connected-service approval record.";
        public string ParametersSchema =>
            "{\"type\":\"object\",\"properties\":{\"approvalCode\":{\"type\":\"string\"}},\"required\":[\"approvalCode\"]}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;
        public bool IsReadOnly => false;
        public bool IsDestructive => false;
        public string SideEffectKind => "connected_service_operation";
        public AgentToolOperationAdmission OperationAdmission => _admission;

        public AgentToolCallSafety GetCallSafety(string argumentsJson) => new(
            RequiresApproval: false,
            IsReadOnly: false,
            IsDestructive: false);

        public AgentToolReplayPolicy ResolveReplayPolicy(string argumentsJson) =>
            AgentToolReplayPolicy.NonReplayable;

        public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            (await ExecuteWithOutcomeAsync(string.Empty, Name, argumentsJson, ct)).ResultJson;

        public Task<AgentToolTerminalOutcome> ExecuteWithOutcomeAsync(
            string callId,
            string toolName,
            string argumentsJson,
            CancellationToken ct = default)
        {
            var credentials = AgentToolRequestContext.Current?.Credentials;
            var token = credentials?.NyxIdAccessToken ??
                        credentials?.SourceReadableNyxIdAccessToken ??
                        string.Empty;
            ExecutionTokens.Add(token);
            var generation = ExecutionTokens.Count;
            var receipt = new AgentToolReceipt
            {
                CallId = callId,
                ToolName = toolName,
                Effect = AgentToolReceiptEffect.Mutating,
                SideEffectKind = SideEffectKind,
            };
            if (string.Equals(token, "uncertain-token", StringComparison.Ordinal))
            {
                receipt.Status = AgentToolReceiptStatus.Error;
                receipt.ErrorCode = "UPSTREAM_RESULT_LOST";
                receipt.ErrorMessage = "The upstream result could not be observed.";
            }
            else if (string.Equals(token, "fresh-per-request-token", StringComparison.Ordinal))
            {
                receipt.Status = AgentToolReceiptStatus.ApprovalRequired;
                receipt.ApprovalRequestId = $"approval-generation-{generation}";
                receipt.NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.PerRequest;
                PerRequestApprovalIds.Add(receipt.ApprovalRequestId);
            }
            else if (string.Equals(token, "approved-per-request-token", StringComparison.Ordinal))
            {
                PerRequestApprovalIds.Add($"approval-generation-{generation}");
                receipt.Status = AgentToolReceiptStatus.Success;
                receipt.ProviderResourceId = $"resource-generation-{generation}";
                receipt.NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.PerRequest;
            }
            else if (string.Equals(token, "valid-grant-token", StringComparison.Ordinal))
            {
                receipt.Status = AgentToolReceiptStatus.Success;
                receipt.ProviderResourceId = $"resource-generation-{generation}";
                receipt.NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.Grant;
            }
            else if (token is "expired-grant-token" or
                     "revoked-grant-token" or
                     "scope-mismatched-grant-token" or
                     "ttl-expired-grant-token")
            {
                receipt.Status = AgentToolReceiptStatus.ApprovalRequired;
                receipt.ApprovalRequestId = $"approval-generation-{generation}";
                receipt.NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.Grant;
            }
            else
            {
                receipt.Status = AgentToolReceiptStatus.Error;
                receipt.ErrorCode = "UNEXPECTED_TEST_CREDENTIAL";
                receipt.ErrorMessage = "The test credential was not admitted.";
            }
            receipt.ResultJson = "{\"status\":\"bounded\"}";
            return Task.FromResult(new AgentToolTerminalOutcome(receipt.ResultJson, receipt));
        }
    }

    private static AgentToolOperationAdmissionPayload ExactWriteAdmission() => new()
    {
        ServiceInstanceId = "svc-lark",
        ServiceSlug = "lark",
        PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
        {
            EndpointId = "approval.create",
        },
        AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
        HttpMethod = "POST",
        PathTemplate = "/approvals",
        ContractDigest = new string('b', 64),
        CatalogDigest = $"sha256:{new string('a', 64)}",
        ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
        {
            Risk = AgentToolOperationRiskPayload.Write,
            Approval = AgentToolOperationApprovalPayload.Required,
            EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
            AllowedExecutionModes = { AgentToolOperationExecutionModePayload.Interactive },
        },
        ReadBack = new AgentToolOperationReadBackPayload
        {
            ReadOperation = new AgentToolOperationAdmissionPayload
            {
                ServiceInstanceId = "svc-lark",
                ServiceSlug = "lark",
                PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
                {
                    EndpointId = "approval.list",
                },
                AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
                HttpMethod = "GET",
                PathTemplate = "/approvals",
                ContractDigest = new string('c', 64),
                CatalogDigest = $"sha256:{new string('a', 64)}",
                ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
                {
                    Risk = AgentToolOperationRiskPayload.ReadOnly,
                    Approval = AgentToolOperationApprovalPayload.None,
                    EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
                    AllowedExecutionModes = { AgentToolOperationExecutionModePayload.Interactive },
                },
            },
            Arguments = JsonParser.Default.Parse<Struct>("{\"approvalCode\":\"canary\"}"),
            Assertion = new AgentToolReadBackAssertionPayload
            {
                Match = AgentToolReadBackMatchPayload.ArrayContainsEquals,
                JsonPointer = "/items",
                ElementJsonPointer = "/approvalCode",
                ExpectedValue = Google.Protobuf.WellKnownTypes.Value.ForString("canary"),
            },
            CheckName = "approval_exists",
        },
    };
}
