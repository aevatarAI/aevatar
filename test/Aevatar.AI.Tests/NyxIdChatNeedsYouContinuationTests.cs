using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatNeedsYouContinuationTests
{
    [Fact]
    public async Task InputContinuation_ShouldInjectTwoTypedSelectionsAndContinueExactSession()
    {
        var generation = new AskUserGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var answer = new NyxIdChatInputAnswer
        {
            Selection = new NyxIdChatInputSelectionAnswer(),
        };
        answer.Selection.OptionIds.AddRange(["option-singapore", "option-frankfurt"]);
        var continuation = new NyxIdChatOperationDispatchCommand
        {
            Key = Key("step-input-continuation", "operation-input-continuation"),
            InputContinuation = new NyxIdChatInputContinuationInput
            {
                RequestId = "input-alpha",
                ToolCallId = "call-ask-user-alpha",
                Answer = answer,
                ToolContext = new AgentToolExecutionContextPayload
                {
                    Credentials = new AgentToolCredentialsPayload
                    {
                        NyxIdAccessToken = "refreshed-input-token",
                        NyxIdCredentialKind =
                            AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
                    },
                },
                SelectedOptions =
                {
                    new NyxIdChatInputOption
                    {
                        OptionId = "option-singapore",
                        Label = "Singapore",
                    },
                    new NyxIdChatInputOption
                    {
                        OptionId = "option-frankfurt",
                        Label = "Frankfurt",
                    },
                },
            },
        };

        var execution = await executor.ExecuteAsync(
            continuation,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Llm);
        execution.Result.Llm.Content.Should().Be("continued after input");
        generation.LlmStates.Should().HaveCount(2);
        var continued = generation.LlmStates[1];
        continued.PendingToolCalls.Should().BeEmpty();
        continued.ToolContext.Credentials.NyxIdAccessToken.Should().Be("refreshed-input-token");
        var toolMessage = continued.Messages.Should().ContainSingle(message =>
            message.Role == "tool" && message.ToolCallId == "call-ask-user-alpha").Which;
        using var response = JsonDocument.Parse(toolMessage.Content);
        var root = response.RootElement;
        root.GetProperty("type").GetString().Should().Be("ask_user_response");
        root.GetProperty("source_input_request_id").GetString().Should().Be("input-alpha");
        root.GetProperty("selected_options").EnumerateArray()
            .Select(static option => option.GetProperty("option_id").GetString())
            .Should().Equal("option-singapore", "option-frankfurt");
    }

    [Fact]
    public async Task InputContinuation_ShouldInjectExactFreeTextAndSourceIdentityIntoTransientToolResult()
    {
        const string rawAnswer =
            "Singapore; budget SGD 200; launch 2026-08-20. Keep defaults editable.";
        var generation = new AskUserGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var continuation = new NyxIdChatOperationDispatchCommand
        {
            Key = Key("step-input-free-text", "operation-input-free-text"),
            InputContinuation = new NyxIdChatInputContinuationInput
            {
                RequestId = "input-free-text",
                ToolCallId = "call-ask-user-alpha",
                Answer = new NyxIdChatInputAnswer { FreeText = rawAnswer },
                ToolContext = ReplacementToolContext("refreshed-input-free-text-token"),
            },
        };

        var execution = await executor.ExecuteAsync(
            continuation,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Llm);
        generation.LlmStates.Should().HaveCount(2);
        var continued = generation.LlmStates[1];
        continued.PendingToolCalls.Should().BeEmpty();
        var toolMessage = continued.Messages.Should().ContainSingle(message =>
            message.Role == "tool" && message.ToolCallId == "call-ask-user-alpha").Which;
        using var response = JsonDocument.Parse(toolMessage.Content);
        response.RootElement.GetProperty("type").GetString().Should().Be("ask_user_response");
        response.RootElement.GetProperty("source_input_request_id").GetString().Should()
            .Be("input-free-text");
        response.RootElement.GetProperty("free_text").GetString().Should().Be(rawAnswer);
        response.RootElement.TryGetProperty("selected_options", out _).Should().BeFalse();
    }

    [Fact]
    public async Task NumericInputContinuation_ShouldDriveExactActorConditionProposal()
    {
        var generation = new ThresholdConditionGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var resolution = ResolveNumericInput();

        var execution = await executor.ExecuteAsync(
            resolution.NextCommand!,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Llm);
        var conditionCall = execution.Result.Llm.ToolCalls.Should().ContainSingle().Which;
        conditionCall.ToolName.Should().Be(NyxIdChatConditionEvaluateContract.ToolName);
        NyxIdChatConditionEvaluateContract.TryParse(
            conditionCall.ArgumentsJson,
            out var proposal).Should().BeTrue();
        proposal.SourceInputRequestId.Should().Be("input-threshold");
        generation.SourceInputRequestId.Should().Be("input-threshold");

        var condition = NyxIdChatTaskLifecycle.ApplyOperationResult(
            resolution.State,
            execution.Result,
            Now());

        condition.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        condition.NextCommand.Should().NotBeNull();
        condition.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.ConditionContinuation);
        condition.NextCommand.ConditionContinuation.Condition.SourceInputRequestId.Should()
            .Be("input-threshold");
        condition.NextCommand.ConditionContinuation.Condition.EffectiveThreshold.Should().Be(75);
        NyxIdChatTurnOperationDispatchPort.MayDispatchExternalEffect(condition.NextCommand)
            .Should().BeFalse();
    }

    [Fact]
    public async Task InputContinuationWithoutRequestIdentity_ShouldFailClosed()
    {
        var generation = new AskUserGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var execution = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = Key("step-input-missing-identity", "operation-input-missing-identity"),
                InputContinuation = new NyxIdChatInputContinuationInput
                {
                    RequestId = " ",
                    ToolCallId = "call-ask-user-alpha",
                    Answer = new NyxIdChatInputAnswer { FreeText = "75" },
                    ToolContext = ReplacementToolContext("refreshed-input-token"),
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolCapabilityLostCode);
        execution.Result.Failure.ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.NotStarted);
        generation.LlmStates.Should().ContainSingle();
    }

    [Fact]
    public async Task NumericInputContinuationWithWrongSourceIdentity_ShouldFailClosedInActorLifecycle()
    {
        var generation = new ThresholdConditionGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var resolution = ResolveNumericInput();
        resolution.NextCommand!.InputContinuation.RequestId = "input-other";

        var execution = await executor.ExecuteAsync(
            resolution.NextCommand,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var rejected = NyxIdChatTaskLifecycle.ApplyOperationResult(
            resolution.State,
            execution.Result,
            Now());

        generation.SourceInputRequestId.Should().Be("input-other");
        rejected.ReasonCode.Should().Be(NyxIdChatTaskLifecycle.ConditionSourceStale);
        rejected.NextCommand.Should().BeNull();
        rejected.State.ActiveTask.Steps.Should().NotContain(step =>
            step.Kind == NyxIdChatStepKind.Condition);
        rejected.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
    }

    [Fact]
    public async Task ConditionContinuation_ShouldInjectActorEvaluatedResultAndContinueExactSession()
    {
        var generation = new ConditionGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var execution = await executor.ExecuteAsync(
            ConditionContinuation(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Llm);
        execution.Result.Llm.Content.Should().Be("continued after condition");
        generation.LlmStates.Should().HaveCount(2);
        var continued = generation.LlmStates[1];
        continued.PendingToolCalls.Should().BeEmpty();
        var toolMessage = continued.Messages.Should().ContainSingle(message =>
            message.Role == "tool" && message.ToolCallId == "call-condition-alpha").Which;
        using var response = JsonDocument.Parse(toolMessage.Content);
        response.RootElement.GetProperty("type").GetString().Should()
            .Be("condition_evaluate_response");
        response.RootElement.GetProperty("outcome").GetBoolean().Should().BeTrue();
        response.RootElement.GetProperty("effective_threshold").GetInt64().Should().Be(75);
        response.RootElement.GetProperty("guarded_tool_name").GetString().Should()
            .Be("repository_update");
    }

    [Fact]
    public async Task ConditionContinuation_WhenActorFactsAreTampered_ShouldFailClosed()
    {
        var generation = new ConditionGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var continuation = ConditionContinuation();
        continuation.ConditionContinuation.Condition.ObservedValue = 79;

        var execution = await executor.ExecuteAsync(
            continuation,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolCapabilityLostCode);
        execution.Result.Failure.ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.NotStarted);
        generation.LlmStates.Should().ContainSingle();
    }

    [Fact]
    public async Task InputContinuationWithoutReplacementCredentials_ShouldFailClosed()
    {
        var generation = new AskUserGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var execution = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = Key("step-input-missing-credentials", "operation-input-missing-credentials"),
                InputContinuation = new NyxIdChatInputContinuationInput
                {
                    RequestId = "input-missing-credentials",
                    ToolCallId = "call-ask-user-alpha",
                    Answer = new NyxIdChatInputAnswer { FreeText = "continue" },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolAuthorizationMismatchCode);
        generation.LlmStates.Should().ContainSingle();
    }

    [Fact]
    public async Task ApprovalContinuation_ShouldExecuteExactCallWithGrantAndFreshCredentials()
    {
        var generation = new ApprovalGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generation);
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
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            ReportProgressAsync,
            CancellationToken.None);
        var waiting = await executor.ExecuteAsync(
            ToolCommand(),
            session,
            ReportProgressAsync,
            CancellationToken.None);

        waiting.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.ApprovalRequired);
        var resolution = ResolveApproval(approved: true);
        var approved = await executor.ExecuteAsync(
            resolution.NextCommand!,
            session,
            ReportProgressAsync,
            CancellationToken.None);

        approved.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        approved.Result.Tool.Receipt.CallId.Should().Be("call-danger-alpha");
        approved.Result.Tool.Receipt.ApprovalRequestId.Should().Be("approval-alpha");
        approved.Result.Tool.Receipt.ProviderResourceId.Should().Be("provider-resource-alpha");
        approved.Result.Key.OperationGeneration.Should().Be(2);
        var toolStarts = progress.Where(signal =>
            signal.ProgressCase ==
            NyxIdChatOperationProgressSignal.ProgressOneofCase.ToolStarted).ToArray();
        toolStarts.Should().ContainSingle();
        toolStarts[0].ToolStarted.CallId.Should().Be("call-danger-alpha");
        toolStarts[0].ToolStarted.Presentation.Should().BeNull();
        generation.ToolExecutions.Should().Be(2);
        generation.Grants.Should().HaveCount(2);
        generation.Grants[0].Should().BeNull();
        generation.Grants[1].Should().NotBeNull();
        var grant = generation.Grants[1]!;
        grant.ApprovalRequestId.Should().Be("approval-alpha");
        grant.RequestId.Should().Be("request-alpha");
        grant.ToolName.Should().Be("dangerous_tool");
        grant.ToolCallId.Should().Be("call-danger-alpha");
        grant.ArgumentsSha256.Should().Be(
            AgentToolArgumentsDigest.ComputeSha256("{\"target\":\"repo-alpha\"}"));
        generation.AccessTokens.Should().Equal("initial-token", "refreshed-approval-token");
        var reconciled = NyxIdChatTaskLifecycle.ApplyOperationResult(
            resolution.State,
            approved.Result,
            Now());
        var effectStep = reconciled.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        effectStep.Operation.Key.OperationGeneration.Should().Be(2);
        effectStep.ApprovalRequestId.Should().Be("approval-alpha");
        effectStep.Source.Tool.ProviderResourceId.Should().Be("provider-resource-alpha");
        effectStep.Status.Should().Be(NyxIdChatStepStatus.Waiting);
        effectStep.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        var verificationStep = reconciled.State.ActiveTask.Steps.Last();
        verificationStep.Kind.Should().Be(NyxIdChatStepKind.Postcondition);
        verificationStep.Status.Should().Be(NyxIdChatStepStatus.Uncertain);
        reconciled.NextCommand.Should().BeNull();
    }

    [Fact]
    public async Task ApprovalDenial_ShouldNotExecuteToolAgainAndShouldReturnTypedReceipt()
    {
        var generation = new ApprovalGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        await executor.ExecuteAsync(
            ToolCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var resolution = ResolveApproval(approved: false);
        var denied = await executor.ExecuteAsync(
            resolution.NextCommand!,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        generation.ToolExecutions.Should().Be(1);
        denied.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Tool);
        denied.Result.Tool.Receipt.CallId.Should().Be("call-danger-alpha");
        denied.Result.Tool.Receipt.ToolName.Should().Be("dangerous_tool");
        denied.Result.Tool.Receipt.ApprovalRequestId.Should().Be("approval-alpha");
        denied.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Denied);
        denied.Result.Tool.Receipt.Effect.Should().Be(AgentToolReceiptEffect.Mutating);
        denied.Result.Tool.Receipt.ErrorCode.Should().Be("approval_denied");
        denied.Result.Tool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        var reconciled = NyxIdChatTaskLifecycle.ApplyOperationResult(
            resolution.State,
            denied.Result,
            Now());
        reconciled.NextCommand.Should().BeNull();
        reconciled.State.PendingApproval.Should().BeNull();
        reconciled.State.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool)
            .Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        reconciled.State.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.Postcondition &&
                step.DependsOn.Contains("step-tool-alpha"))
            .Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        reconciled.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        reconciled.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
    }

    [Fact]
    public void ExactApprovalAuthority_ShouldPersistAndResumeWithoutTransientCapability()
    {
        var state = ApprovalWaitingState();
        state.PendingApproval = null;
        var step = state.ActiveTask.Steps.Single(candidate =>
            candidate.StepId == "step-tool-alpha");
        step.Status = NyxIdChatStepStatus.Running;
        step.ApprovalRequestId = string.Empty;
        step.Operation.Phase = NyxIdChatOperationPhase.Requested;
        step.Source.Tool.OperationAdmission = new AgentToolOperationAdmissionPayload
        {
            ServiceInstanceId = "us-alpha",
            ServiceSlug = "lark",
            PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
            {
                EndpointId = "message.create",
            },
            CatalogDigest = "sha256:catalog",
            ContractDigest = "sha256:contract",
        };
        var expiresAt = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
        var authority = new NyxIdExactServiceApprovalAuthority
        {
            RequestId = "request-exact-alpha",
            UserServiceId = "us-alpha",
            EndpointId = "message.create",
            CatalogDigest = "sha256:catalog",
            EndpointContractDigest = "sha256:contract",
            OperationDigest = "sha256:operation",
            OperationId = "operation-tool-alpha",
            OperationGeneration = 1,
            IdempotencyKey = "operation-tool-alpha",
            ExpiresAt = expiresAt,
        };
        var reconciled = NyxIdChatTaskLifecycle.ApplyOperationResult(
            state,
            new NyxIdChatOperationResultSignal
            {
                Key = step.Operation.Key.Clone(),
                Tool = new NyxIdChatToolOperationResult
                {
                    ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
                    Receipt = new AgentToolReceipt
                    {
                        CallId = "call-danger-alpha",
                        ToolName = "dangerous_tool",
                        ApprovalRequestId = authority.RequestId,
                        Status = AgentToolReceiptStatus.ApprovalRequired,
                        Effect = AgentToolReceiptEffect.Mutating,
                        ExactServiceApproval = authority.Clone(),
                    },
                },
            },
            Now());

        reconciled.State.PendingApproval.Should().NotBeNull();
        reconciled.State.PendingApproval.ExactServiceApproval.Should()
            .BeEquivalentTo(authority);
        reconciled.State.PendingApproval.ExpiresAt.Should().Be(expiresAt);
        reconciled.State.ActiveTask.Steps.Single(candidate =>
                candidate.StepId == "step-tool-alpha")
            .Status.Should().Be(NyxIdChatStepStatus.Waiting);

        var reactivated = NyxIdChatConversationGAgentState.Parser.ParseFrom(
            reconciled.State.ToByteArray());
        var resolved = NyxIdChatNeedsYouDecisions.ResolveApproval(
            reactivated,
            new NyxIdChatApprovalResolveCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = "conversation-alpha",
                RequestId = authority.RequestId,
                ClientRequestId = "client-exact-approve",
                Approved = true,
                ExpectedStateVersion = 10,
                ToolContext = ReplacementToolContext("fresh-exact-token"),
            },
            currentStateVersion: 10,
            Now());

        resolved.ShouldCommit.Should().BeTrue();
        resolved.NextCommand.Should().NotBeNull();
        resolved.NextCommand!.ToolApprovalContinuation.ExactServiceApproval.Should()
            .BeEquivalentTo(authority);
        resolved.NextCommand.ToolApprovalContinuation.ToolCallId.Should().Be(
            "call-danger-alpha");
        resolved.NextCommand.ToolApprovalContinuation.ToolName.Should().Be("dangerous_tool");
        reactivated.ToString().Should().NotContain("fresh-exact-token");
        resolved.State.ToString().Should().NotContain("fresh-exact-token");
    }

    [Fact]
    public async Task ApprovalRequiredWithoutNyxIdRequestIdentity_ShouldFailClosed()
    {
        var generation = new ApprovalGenerationExecutor(approvalRequestId: string.Empty);
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var execution = await executor.ExecuteAsync(
            ToolCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolApprovalRequestIdRequiredCode);
        execution.Result.Failure.ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.NotStarted);
    }

    [Theory]
    [InlineData(AgentToolReceiptStatus.Denied, "")]
    [InlineData(AgentToolReceiptStatus.ApprovalRequired, "tool_approval")]
    public async Task NyxIdDecisionWithoutRealRequestIdentity_ShouldFailClosed(
        AgentToolReceiptStatus status,
        string approvalRequestId)
    {
        var generation = new ApprovalGenerationExecutor(approvalRequestId, status);
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var execution = await executor.ExecuteAsync(
            ToolCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolApprovalRequestIdRequiredCode);
        generation.ToolExecutions.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApprovalContinuationWithoutReplacementCredentials_ShouldFailClosed(
        bool approved)
    {
        var generation = new ApprovalGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        await executor.ExecuteAsync(
            ToolCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var resolution = ResolveApproval(approved);
        resolution.NextCommand!.ToolApprovalContinuation.ToolContext = null;

        var execution = await executor.ExecuteAsync(
            resolution.NextCommand,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolAuthorizationMismatchCode);
        generation.ToolExecutions.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContinuationAfterCapabilityLoss_ShouldFailClosed(bool approval)
    {
        var executor = new NyxIdChatTurnOperationExecutor(new AskUserGenerationExecutor());
        NyxIdChatConversationGAgentState resolvedState;
        NyxIdChatOperationDispatchCommand command;
        if (approval)
        {
            var resolution = ResolveApproval(approved: true);
            resolvedState = resolution.State;
            command = resolution.NextCommand!;
        }
        else
        {
            var resolution = ResolveInput();
            resolvedState = resolution.State;
            command = resolution.NextCommand!;
        }

        var execution = await executor.ExecuteAsync(
            command,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolCapabilityLostCode);
        execution.Result.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        var reconciled = NyxIdChatTaskLifecycle.ApplyOperationResult(
            resolvedState,
            execution.Result,
            Now());
        reconciled.State.PendingInput.Should().BeNull();
        reconciled.State.PendingApproval.Should().BeNull();
        reconciled.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        reconciled.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        reconciled.State.ActiveTask.Steps.Should().OnlyContain(step =>
            step.Status != NyxIdChatStepStatus.Waiting &&
            step.Status != NyxIdChatStepStatus.Running);
    }

    private static NyxIdChatOperationDispatchCommand InitialLlmCommand() => new()
    {
        Key = Key("step-llm-alpha", "operation-llm-alpha"),
        Llm = new NyxIdChatLLMOperationInput
        {
            Request = new ChatRequestEvent
            {
                Prompt = "continue with needs-you",
                SessionId = "turn-alpha",
                ToolContext = new AgentToolExecutionContextPayload
                {
                    Credentials = new AgentToolCredentialsPayload
                    {
                        NyxIdAccessToken = "initial-token",
                        NyxIdCredentialKind =
                            AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
                    },
                },
            },
        },
    };

    private static NyxIdChatOperationDispatchCommand ToolCommand() => new()
    {
        Key = Key("step-tool-alpha", "operation-tool-alpha"),
        Tool = new NyxIdChatToolOperationInput
        {
            CallId = "call-danger-alpha",
            ToolName = "dangerous_tool",
            ArgumentsJson = "{\"target\":\"repo-alpha\"}",
            MayChangeExternalState = true,
        },
    };

    private static NyxIdChatOperationDispatchCommand ConditionContinuation() => new()
    {
        Key = Key("step-condition-continuation", "operation-condition-continuation"),
        ConditionContinuation = new NyxIdChatConditionContinuationInput
        {
            ToolCallId = "call-condition-alpha",
            Condition = new NyxIdChatNumericConditionState
            {
                ConditionId = "condition-alpha",
                SourceInputRequestId = "input-threshold",
                SuggestedThreshold = 70,
                EffectiveThreshold = 75,
                ThresholdOrigin = NyxIdChatThresholdOrigin.UserOverride,
                ObservedValue = 80,
                Comparison = NyxIdChatIntegerComparison.Gte,
                Outcome = NyxIdChatConditionOutcome.True,
                EvaluatedAt = Now(),
                GuardedToolName = "repository_update",
            },
        },
    };

    private static NyxIdChatNeedsYouDecision<NyxIdChatApprovalResolutionState> ResolveApproval(
        bool approved) =>
        NyxIdChatNeedsYouDecisions.ResolveApproval(
            ApprovalWaitingState(),
            new NyxIdChatApprovalResolveCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = "conversation-alpha",
                RequestId = "approval-alpha",
                ClientRequestId = $"client-approval-{approved}",
                Approved = approved,
                ExpectedStateVersion = 10,
                ToolContext = new AgentToolExecutionContextPayload
                {
                    Credentials = new AgentToolCredentialsPayload
                    {
                        NyxIdAccessToken = "refreshed-approval-token",
                        NyxIdCredentialKind =
                            AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
                    },
                },
            },
            currentStateVersion: 10,
            Now());

    private static AgentToolExecutionContextPayload ReplacementToolContext(string accessToken) =>
        new()
        {
            Credentials = new AgentToolCredentialsPayload
            {
                NyxIdAccessToken = accessToken,
                NyxIdCredentialKind =
                    AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
            },
        };

    private static NyxIdChatNeedsYouDecision<NyxIdChatInputResolutionState> ResolveInput()
    {
        var state = new NyxIdChatConversationGAgentState
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTaskStatus.Active,
                ActiveStepId = "step-input-alpha",
                Steps =
                {
                    new NyxIdChatTaskStepState
                    {
                        StepId = "step-input-alpha",
                        Order = 1,
                        Kind = NyxIdChatStepKind.Input,
                        Status = NyxIdChatStepStatus.Waiting,
                        Required = true,
                        Source = new NyxIdChatStepSource
                        {
                            Input = new NyxIdChatInputStepSource { RequestId = "input-alpha" },
                        },
                    },
                },
            },
            PendingInput = new NyxIdChatPendingInputState
            {
                RequestId = "input-alpha",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-input-alpha",
                ToolCallId = "call-ask-user-alpha",
                Prompt = "Provide private input.",
                AllowFreeText = true,
            },
        };
        return NyxIdChatNeedsYouDecisions.ResolveInput(
            state,
            new NyxIdChatInputResolveCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = "conversation-alpha",
                RequestId = "input-alpha",
                ClientRequestId = "client-input-alpha",
                Answer = new NyxIdChatInputAnswer { FreeText = "transient-only" },
                ExpectedStateVersion = 10,
            },
            currentStateVersion: 10,
            Now());
    }

    private static NyxIdChatNeedsYouDecision<NyxIdChatInputResolutionState> ResolveNumericInput()
    {
        var state = new NyxIdChatConversationGAgentState
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTaskStatus.Active,
                ActiveStepId = "step-input-threshold",
                Steps =
                {
                    new NyxIdChatTaskStepState
                    {
                        StepId = "step-input-threshold",
                        Order = 1,
                        Kind = NyxIdChatStepKind.Input,
                        Status = NyxIdChatStepStatus.Waiting,
                        Required = true,
                        Source = new NyxIdChatStepSource
                        {
                            Input = new NyxIdChatInputStepSource
                            {
                                RequestId = "input-threshold",
                            },
                        },
                    },
                },
            },
            PendingInput = new NyxIdChatPendingInputState
            {
                RequestId = "input-threshold",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-input-threshold",
                ToolCallId = "call-ask-user-alpha",
                Prompt = "Choose the threshold.",
                AllowFreeText = true,
                NumericThreshold = new NyxIdChatNumericThresholdInputSpec
                {
                    SuggestedValue = 70,
                    MinimumValue = 0,
                    MaximumValue = 100,
                },
            },
        };
        return NyxIdChatNeedsYouDecisions.ResolveInput(
            state,
            new NyxIdChatInputResolveCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = "conversation-alpha",
                RequestId = "input-threshold",
                ClientRequestId = "client-input-threshold",
                Answer = new NyxIdChatInputAnswer { FreeText = "75" },
                ExpectedStateVersion = 10,
                ToolContext = ReplacementToolContext("refreshed-input-threshold-token"),
            },
            currentStateVersion: 10,
            Now());
    }

    private static NyxIdChatConversationGAgentState ApprovalWaitingState() => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        ActiveTurn = new NyxIdChatTurnState
        {
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            Status = NyxIdChatTurnStatus.Active,
        },
        ActiveTask = new NyxIdChatTaskState
        {
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            Status = NyxIdChatTaskStatus.Active,
            ActiveStepId = "step-tool-alpha",
            Steps =
            {
                new NyxIdChatTaskStepState
                {
                    StepId = "step-tool-alpha",
                    Order = 1,
                    Kind = NyxIdChatStepKind.Tool,
                    Status = NyxIdChatStepStatus.Waiting,
                    Required = true,
                    MayChangeExternalState = true,
                    ApprovalRequestId = "approval-alpha",
                    Source = new NyxIdChatStepSource
                    {
                        Tool = new NyxIdChatToolStepSource { ToolName = "dangerous_tool" },
                    },
                    Operation = new NyxIdChatOperationState
                    {
                        Key = Key("step-tool-alpha", "operation-tool-alpha"),
                        Kind = NyxIdChatStepKind.Tool,
                        Phase = NyxIdChatOperationPhase.Succeeded,
                        MayChangeExternalState = true,
                    },
                },
                new NyxIdChatTaskStepState
                {
                    StepId = "step-verification-alpha",
                    Order = 2,
                    Kind = NyxIdChatStepKind.Postcondition,
                    Status = NyxIdChatStepStatus.Planned,
                    Required = true,
                    DependsOn = { "step-tool-alpha" },
                    Source = new NyxIdChatStepSource
                    {
                        Postcondition = new NyxIdChatPostconditionStepSource
                        {
                            EffectStepId = "step-tool-alpha",
                            Check = "verification_unavailable",
                        },
                    },
                    Operation = new NyxIdChatOperationState
                    {
                        Key = Key("step-verification-alpha", "operation-verification-alpha"),
                        Kind = NyxIdChatStepKind.Postcondition,
                        Phase = NyxIdChatOperationPhase.Requested,
                    },
                },
            },
        },
        PendingApproval = new NyxIdChatPendingApprovalState
        {
            ApprovalRequestId = "approval-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-tool-alpha",
            ToolCallId = "call-danger-alpha",
            ToolName = "dangerous_tool",
        },
    };

    private static Timestamp Now() => Timestamp.FromDateTimeOffset(
        new DateTimeOffset(2026, 8, 2, 8, 0, 0, TimeSpan.Zero));

    private static NyxIdChatOperationKey Key(
        string stepId,
        string operationId,
        long generation = 1) => new()
    {
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = stepId,
        OperationId = operationId,
        OperationGeneration = generation,
    };

    private abstract class GenerationExecutorBase : IAgentRunReplyGenerationExecutorPort
    {
        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentRunReplyStepState
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                NextStepIndex = 1,
                MaxToolRounds = 4,
                ToolContext = request.Request.ToolContext?.Clone(),
            });
        }

        public abstract Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct);

        public virtual async Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct)
        {
            var result = authorizedToolStep?.Matches(request) == true
                ? await authorizedToolStep.ExecuteAsync(ct)
                : new AgentRunToolStepResult { AdvanceRound = true };
            return new AgentRunNextToolStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                ToolStepResult = result,
            };
        }

        protected static AgentRunNextLlmStepRequestedEvent LlmContinuation(
            AgentRunReplyStepExecutionRequest request,
            AgentRunLlmStepResult result) => new()
        {
            RunId = request.RunId,
            CorrelationId = request.Request.CorrelationId,
            TargetActorId = request.Request.TargetActorId,
            Attempt = request.Attempt,
            StepIndex = request.StepIndex + 1,
            Request = request.Request.Clone(),
            LlmStepResult = result,
        };
    }

    private sealed class AskUserGenerationExecutor : GenerationExecutorBase
    {
        public List<AgentRunReplyStepState> LlmStates { get; } = [];

        public override Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LlmStates.Add(request.StepState.Clone());
            var result = new AgentRunLlmStepResult
            {
                Content = request.StepState.Round == 0 ? "choose" : "continued after input",
                AccumulatedText = request.StepState.Round == 0 ? "choose" : "continued after input",
                FinishReason = request.StepState.Round == 0 ? "tool_calls" : "stop",
                HasStreamedTextContent = true,
            };
            if (request.StepState.Round == 0)
            {
                result.ToolCalls.Add(new AgentRunToolCall
                {
                    Id = "call-ask-user-alpha",
                    Name = "ask_user",
                    ArgumentsJson = "{}",
                });
            }
            return Task.FromResult(new AgentRunLlmStepExecution(
                LlmContinuation(request, result),
                AuthorizedToolStep: null));
        }
    }

    private sealed class ThresholdConditionGenerationExecutor : GenerationExecutorBase
    {
        public string SourceInputRequestId { get; private set; } = string.Empty;

        public override Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var firstRound = request.StepState.Round == 0;
            var result = new AgentRunLlmStepResult
            {
                Content = firstRound ? "choose threshold" : "evaluate threshold",
                AccumulatedText = firstRound ? "choose threshold" : "evaluate threshold",
                FinishReason = "tool_calls",
                HasStreamedTextContent = true,
            };
            var call = new AgentRunToolCall
            {
                Id = firstRound ? "call-ask-user-alpha" : "call-condition-threshold",
                Name = firstRound
                    ? NyxIdChatAskUserContract.ToolName
                    : NyxIdChatConditionEvaluateContract.ToolName,
                ArgumentsJson = "{}",
            };
            if (!firstRound)
            {
                var inputResult = request.StepState.Messages.Should().ContainSingle(message =>
                    message.Role == "tool" &&
                    message.ToolCallId == "call-ask-user-alpha").Which;
                using var response = JsonDocument.Parse(inputResult.Content);
                SourceInputRequestId = response.RootElement
                    .GetProperty("source_input_request_id")
                    .GetString()!;
                call.ArgumentsJson = JsonSerializer.Serialize(new
                {
                    source_input_request_id = SourceInputRequestId,
                    observed_value = 80,
                    guarded_tool_name = "repository_update",
                });
            }
            result.ToolCalls.Add(call);
            var safety = firstRound
                ? null
                : new[]
                {
                    new AgentRunAuthorizedToolCallSafety(
                        call.Id,
                        call.Name,
                        call.ArgumentsJson,
                        new AgentToolCallSafety(
                            RequiresApproval: false,
                            IsReadOnly: true,
                            IsDestructive: false),
                        SideEffectKind: string.Empty),
                };
            return Task.FromResult(new AgentRunLlmStepExecution(
                LlmContinuation(request, result),
                AuthorizedToolStep: null,
                safety));
        }
    }

    private sealed class ConditionGenerationExecutor : GenerationExecutorBase
    {
        public List<AgentRunReplyStepState> LlmStates { get; } = [];

        public override Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LlmStates.Add(request.StepState.Clone());
            var firstRound = request.StepState.Round == 0;
            var result = new AgentRunLlmStepResult
            {
                Content = firstRound ? "evaluate condition" : "continued after condition",
                AccumulatedText = firstRound
                    ? "evaluate condition"
                    : "continued after condition",
                FinishReason = firstRound ? "tool_calls" : "stop",
                HasStreamedTextContent = true,
            };
            if (firstRound)
            {
                result.ToolCalls.Add(new AgentRunToolCall
                {
                    Id = "call-condition-alpha",
                    Name = NyxIdChatConditionEvaluateContract.ToolName,
                    ArgumentsJson =
                        "{\"source_input_request_id\":\"input-threshold\"," +
                        "\"observed_value\":80," +
                        "\"guarded_tool_name\":\"repository_update\"}",
                });
            }
            return Task.FromResult(new AgentRunLlmStepExecution(
                LlmContinuation(request, result),
                AuthorizedToolStep: null));
        }
    }

    private sealed class ApprovalGenerationExecutor(
        string approvalRequestId = "approval-alpha",
        AgentToolReceiptStatus initialStatus = AgentToolReceiptStatus.ApprovalRequired)
        : GenerationExecutorBase
    {
        private static readonly AgentRunToolCall ToolCall = new()
        {
            Id = "call-danger-alpha",
            Name = "dangerous_tool",
            ArgumentsJson = "{\"target\":\"repo-alpha\"}",
        };

        public int ToolExecutions { get; private set; }

        public List<AgentToolApprovalGrant?> Grants { get; } = [];

        public List<string?> AccessTokens { get; } = [];

        public override Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var result = new AgentRunLlmStepResult
            {
                Content = "approval required",
                AccumulatedText = "approval required",
                FinishReason = "tool_calls",
                HasStreamedTextContent = true,
            };
            result.ToolCalls.Add(ToolCall.Clone());
            var continuation = LlmContinuation(request, result);
            var context = AgentToolExecutionContextMapper.FromPayload(request.Request.ToolContext) with
            {
                Request = new AgentToolRequestIdentity("request-alpha", ToolCall.Id),
                ExecutionOwner = AgentToolExecutionOwners.Actor("conversation-alpha"),
            };
            var capability = new AgentRunAuthorizedToolStep(
                request.RunId,
                request.Request.CorrelationId,
                request.Attempt,
                continuation.StepIndex,
                [ToolCall],
                context,
                (executionContext, approvalGrant, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    ToolExecutions++;
                    Grants.Add(approvalGrant);
                    AccessTokens.Add(executionContext.Credentials.NyxIdAccessToken);
                    var status = approvalGrant is null
                        ? initialStatus
                        : AgentToolReceiptStatus.Success;
                    var resultJson = approvalGrant is null
                        ? "{\"status\":\"approval_required\"}"
                        : "{\"ok\":true}";
                    return Task.FromResult(new AgentRunToolStepResult
                    {
                        AdvanceRound = approvalGrant is not null,
                        ResultMessages =
                        {
                            new AgentRunChatMessage
                            {
                                Role = "tool",
                                ToolCallId = ToolCall.Id,
                                Content = resultJson,
                            },
                        },
                        ToolReceipts =
                        {
                            new AgentToolReceipt
                            {
                                CallId = ToolCall.Id,
                                ToolName = ToolCall.Name,
                                ApprovalRequestId = approvalGrant is null
                                    ? approvalRequestId
                                    : string.Empty,
                                Status = status,
                                ResultJson = resultJson,
                                IsDestructive = true,
                                ProviderResourceId = approvalGrant is null
                                    ? string.Empty
                                    : "provider-resource-alpha",
                            },
                        },
                    });
                });
            return Task.FromResult(new AgentRunLlmStepExecution(
                continuation,
                capability,
                [
                    new AgentRunAuthorizedToolCallSafety(
                        ToolCall.Id,
                        ToolCall.Name,
                        ToolCall.ArgumentsJson,
                        new AgentToolCallSafety(
                            RequiresApproval: true,
                            IsReadOnly: false,
                            IsDestructive: true),
                        SideEffectKind: "repository.delete"),
                ]));
        }
    }
}
