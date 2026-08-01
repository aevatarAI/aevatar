using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatTaskLifecycleTests
{
    private static readonly Timestamp Now = Timestamp.FromDateTimeOffset(
        new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void LlmToolCall_ShouldCommitNextToolWaterlineFromTypedSafety()
    {
        var state = ActiveState(NyxIdChatStepKind.Llm, "step-llm-alpha", "operation-llm-alpha");
        var original = state.Clone();
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = state.ActiveTask.Steps.Single().Operation.Key.Clone(),
            Llm = new NyxIdChatLLMOperationResult
            {
                Content = "I will update the repository.",
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-alpha",
                        ToolName = "repository_update",
                        ArgumentsJson = "{\"repositoryId\":\"repo-alpha\"}",
                        Safety = new NyxIdChatToolCallSafety
                        {
                            IsReadOnly = false,
                            IsDestructive = false,
                            SideEffectKind = "repository.update",
                            MayChangeExternalState = true,
                        },
                    },
                },
            },
        };

        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(state, signal, Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        state.Should().BeEquivalentTo(original, "lifecycle derivation must not mutate committed actor state");
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        decision.State.ActiveTask.Steps.Should().HaveCount(2);
        decision.State.ActiveTask.Steps[0].Status.Should().Be(NyxIdChatStepStatus.Done);
        var toolStep = decision.State.ActiveTask.Steps[1];
        toolStep.StepId.Should().StartWith("step-");
        toolStep.Kind.Should().Be(NyxIdChatStepKind.Tool);
        toolStep.Status.Should().Be(NyxIdChatStepStatus.Running);
        toolStep.Required.Should().BeTrue();
        toolStep.Source.Tool.ToolName.Should().Be("repository_update");
        toolStep.MayChangeExternalState.Should().BeTrue();
        toolStep.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        toolStep.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        toolStep.Operation.Key.ConversationActorId.Should().Be("conversation-alpha");
        toolStep.Operation.Key.TurnId.Should().Be("turn-alpha");
        toolStep.Operation.Key.TaskId.Should().Be("task-alpha");
        toolStep.Operation.Key.OperationGeneration.Should().Be(1);
        decision.State.ActiveTask.ActiveStepId.Should().Be(toolStep.StepId);
        decision.State.ActiveTask.ActiveOperationId.Should().Be(toolStep.Operation.Key.OperationId);

        decision.NextCommand.Should().NotBeNull();
        decision.NextCommand!.Key.Should().BeEquivalentTo(toolStep.Operation.Key);
        decision.NextCommand.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        decision.NextCommand.Tool.CallId.Should().Be("call-alpha");
        decision.NextCommand.Tool.ToolName.Should().Be("repository_update");
        decision.NextCommand.Tool.ArgumentsJson.Should().Be("{\"repositoryId\":\"repo-alpha\"}");
        decision.NextCommand.Tool.MayChangeExternalState.Should().BeTrue();
    }

    [Fact]
    public void ToolSuccess_ShouldRequestContinuationLlmAndKeepTaskActive()
    {
        var afterPlan = NyxIdChatTaskLifecycle.ApplyOperationResult(
            ActiveState(NyxIdChatStepKind.Llm, "step-llm-alpha", "operation-llm-alpha"),
            LlmWithToolCall(),
            Now).State;
        var toolStep = afterPlan.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = toolStep.Operation.Key.Clone(),
            Tool = new NyxIdChatToolOperationResult
            {
                ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                ResultJson = "{\"updated\":true}",
                Receipt = new AgentToolReceipt
                {
                    CallId = "call-alpha",
                    ToolName = "repository_update",
                    Status = AgentToolReceiptStatus.Success,
                    SideEffectKind = "repository.update",
                    SubjectKind = "repository",
                    SubjectId = "repo-alpha",
                },
            },
        };

        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(afterPlan, signal, Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        var completedTool = decision.State.ActiveTask.Steps.Single(step =>
            step.StepId == toolStep.StepId);
        completedTool.Status.Should().Be(NyxIdChatStepStatus.Done);
        completedTool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.Confirmed);
        var continuation = decision.State.ActiveTask.Steps.Last();
        continuation.Kind.Should().Be(NyxIdChatStepKind.Llm);
        continuation.Status.Should().Be(NyxIdChatStepStatus.Running);
        continuation.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        decision.NextCommand.Should().NotBeNull();
        decision.NextCommand!.Key.Should().BeEquivalentTo(continuation.Operation.Key);
        decision.NextCommand.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm);
        decision.NextCommand.Llm.ContinueSession.Should().BeTrue();
    }

    [Fact]
    public void FinalLlm_ShouldSucceedMultiStepTaskWithoutAnotherDispatch()
    {
        var afterPlan = NyxIdChatTaskLifecycle.ApplyOperationResult(
            ActiveState(NyxIdChatStepKind.Llm, "step-llm-alpha", "operation-llm-alpha"),
            LlmWithToolCall(),
            Now).State;
        var toolStep = afterPlan.ActiveTask.Steps.Last();
        var afterTool = NyxIdChatTaskLifecycle.ApplyOperationResult(
            afterPlan,
            ToolSuccess(toolStep.Operation.Key),
            Now).State;
        var finalStep = afterTool.ActiveTask.Steps.Last();
        var finalSignal = new NyxIdChatOperationResultSignal
        {
            Key = finalStep.Operation.Key.Clone(),
            Llm = new NyxIdChatLLMOperationResult { Content = "Repository updated." },
        };

        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(afterTool, finalSignal, Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.NextCommand.Should().BeNull();
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        decision.State.ActiveTask.Steps.Select(step => step.Status).Should().OnlyContain(
            status => status == NyxIdChatStepStatus.Done);
    }

    [Theory]
    [InlineData(false, NyxIdChatStepStatus.Failed, NyxIdChatEffectEvidence.NotApplied)]
    [InlineData(true, NyxIdChatStepStatus.Uncertain, NyxIdChatEffectEvidence.MayHaveChanged)]
    public void RequiredToolFailureOrUncertainty_ShouldFailTaskWithoutSuccessor(
        bool uncertain,
        NyxIdChatStepStatus expectedStepStatus,
        NyxIdChatEffectEvidence expectedEffect)
    {
        var state = ActiveState(NyxIdChatStepKind.Tool, "step-tool-alpha", "operation-tool-alpha");
        state.ActiveTask.Steps[0].MayChangeExternalState = true;
        state.ActiveTask.Steps[0].Operation.MayChangeExternalState = true;
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = state.ActiveTask.Steps[0].Operation.Key.Clone(),
        };
        if (uncertain)
        {
            signal.Failure = new NyxIdChatOperationFailure
            {
                FailureCode = "TOOL_OUTCOME_UNKNOWN",
                SafeMessage = "The external outcome could not be confirmed.",
                ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
            };
        }
        else
        {
            signal.Tool = new NyxIdChatToolOperationResult
            {
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                Receipt = new AgentToolReceipt
                {
                    CallId = "call-alpha",
                    ToolName = "repository_update",
                    Status = AgentToolReceiptStatus.Error,
                    ErrorCode = "TOOL_FAILED",
                    ErrorMessage = "The tool failed before applying a change.",
                },
            };
        }

        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(state, signal, Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.NextCommand.Should().BeNull();
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        decision.State.ActiveTask.Steps[0].Status.Should().Be(expectedStepStatus);
        decision.State.ActiveTask.Steps[0].ExternalEffect.Should().Be(expectedEffect);
        decision.State.ActiveTask.Steps[0].AvailableActions.Stop.Should().BeFalse();
    }

    [Fact]
    public void ApprovalRequired_ShouldRemainWaitingWithTypedPendingApproval()
    {
        var state = ActiveState(NyxIdChatStepKind.Tool, "step-tool-alpha", "operation-tool-alpha");
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = state.ActiveTask.Steps[0].Operation.Key.Clone(),
            Tool = new NyxIdChatToolOperationResult
            {
                ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
                Receipt = new AgentToolReceipt
                {
                    CallId = "call-alpha",
                    ToolName = "repository_delete",
                    Status = AgentToolReceiptStatus.ApprovalRequired,
                    ApprovalRequestId = "approval-alpha",
                    IsDestructive = true,
                },
            },
        };

        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(state, signal, Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.NextCommand.Should().BeNull();
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        decision.State.ActiveTask.Steps[0].Status.Should().Be(NyxIdChatStepStatus.Waiting);
        decision.State.ActiveTask.Steps[0].ApprovalRequestId.Should().Be("approval-alpha");
        decision.State.PendingApproval.Should().NotBeNull();
        decision.State.PendingApproval.ApprovalRequestId.Should().Be("approval-alpha");
        decision.State.PendingApproval.TurnId.Should().Be("turn-alpha");
        decision.State.PendingApproval.TaskId.Should().Be("task-alpha");
        decision.State.PendingApproval.StepId.Should().Be("step-tool-alpha");
        decision.State.PendingApproval.ToolName.Should().Be("repository_delete");
        decision.State.PendingApproval.AskedAt.Should().Be(Now);
        decision.State.PendingApproval.Presentation.Action.Should().Be("repository_delete");
        decision.State.PendingApproval.Presentation.Target.Should().Be("repository_delete");
        decision.State.PendingApproval.Presentation.ActorLabel.Should()
            .Be(NyxIdChatServiceDefaults.DisplayName);
        decision.State.PendingApproval.Presentation.Reversibility.Should()
            .Be(NyxIdChatApprovalReversibility.Irreversible);
        decision.State.PendingApproval.Presentation.GrantBoundary.Should().Be("within_grant");
    }

    [Fact]
    public void ToolCallWithoutProviderSafety_ShouldFailClosed()
    {
        var state = ActiveState(NyxIdChatStepKind.Llm, "step-llm-alpha", "operation-llm-alpha");
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = state.ActiveTask.Steps[0].Operation.Key.Clone(),
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-alpha",
                        ToolName = "unknown_tool",
                        ArgumentsJson = "{}",
                    },
                },
            },
        };

        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(state, signal, Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.NextCommand.Should().BeNull();
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        decision.State.ActiveTask.FailureCode.Should().Be("NYXID_CHAT_TOOL_SAFETY_REQUIRED");
        decision.State.ActiveTask.Steps[0].Status.Should().Be(NyxIdChatStepStatus.Failed);
    }

    private static NyxIdChatConversationGAgentState ActiveState(
        NyxIdChatStepKind kind,
        string stepId,
        string operationId)
    {
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = "conversation-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = stepId,
            OperationId = operationId,
            OperationGeneration = 1,
        };
        var step = new NyxIdChatTaskStepState
        {
            StepId = stepId,
            Order = 1,
            Kind = kind,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = key,
                Kind = kind,
                Phase = NyxIdChatOperationPhase.Dispatched,
            },
            AvailableActions = new NyxIdChatAvailableActions { Stop = true },
            UpdatedAt = Now.Clone(),
        };
        var task = new NyxIdChatTaskState
        {
            TaskId = "task-alpha",
            TurnId = "turn-alpha",
            Status = NyxIdChatTaskStatus.Active,
            ActiveStepId = stepId,
            ActiveOperationId = operationId,
            CreatedAt = Now.Clone(),
            UpdatedAt = Now.Clone(),
        };
        task.Steps.Add(step);
        return new NyxIdChatConversationGAgentState
        {
            ConversationActorId = "conversation-alpha",
            ScopeId = "scope-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
                CreatedAt = Now.Clone(),
            },
            LatestTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
                CreatedAt = Now.Clone(),
            },
            ActiveTask = task,
            ProgressSequence = 1,
            UpdatedAt = Now.Clone(),
        };
    }

    private static NyxIdChatOperationResultSignal LlmWithToolCall() => new()
    {
        Key = new NyxIdChatOperationKey
        {
            ConversationActorId = "conversation-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-llm-alpha",
            OperationId = "operation-llm-alpha",
            OperationGeneration = 1,
        },
        Llm = new NyxIdChatLLMOperationResult
        {
            ToolCalls =
            {
                new NyxIdChatToolCall
                {
                    CallId = "call-alpha",
                    ToolName = "repository_update",
                    ArgumentsJson = "{\"repositoryId\":\"repo-alpha\"}",
                    Safety = new NyxIdChatToolCallSafety
                    {
                        SideEffectKind = "repository.update",
                        MayChangeExternalState = true,
                    },
                },
            },
        },
    };

    private static NyxIdChatOperationResultSignal ToolSuccess(NyxIdChatOperationKey key) => new()
    {
        Key = key.Clone(),
        Tool = new NyxIdChatToolOperationResult
        {
            ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
            Receipt = new AgentToolReceipt
            {
                CallId = "call-alpha",
                ToolName = "repository_update",
                Status = AgentToolReceiptStatus.Success,
                SideEffectKind = "repository.update",
            },
        },
    };
}
