using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
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
        root.GetProperty("selected_options").EnumerateArray()
            .Select(static option => option.GetProperty("option_id").GetString())
            .Should().Equal("option-singapore", "option-frankfurt");
    }

    [Fact]
    public async Task ApprovalContinuation_ShouldExecuteExactCallWithGrantAndFreshCredentials()
    {
        var generation = new ApprovalGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            InitialLlmCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var waiting = await executor.ExecuteAsync(
            ToolCommand(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        waiting.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.ApprovalRequired);
        var resolution = ResolveApproval(approved: true);
        var approved = await executor.ExecuteAsync(
            resolution.NextCommand!,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        approved.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        approved.Result.Tool.Receipt.CallId.Should().Be("call-danger-alpha");
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
        reconciled.State.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool)
            .Status.Should().Be(NyxIdChatStepStatus.Done);
        reconciled.State.ActiveTask.Steps.Last().Kind.Should().Be(NyxIdChatStepKind.Llm);
        reconciled.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm);
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
        denied.Result.Tool.Receipt.ErrorCode.Should().Be("approval_denied");
        denied.Result.Tool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        var reconciled = NyxIdChatTaskLifecycle.ApplyOperationResult(
            resolution.State,
            denied.Result,
            Now());
        reconciled.NextCommand.Should().BeNull();
        reconciled.State.PendingApproval.Should().BeNull();
        reconciled.State.ActiveTask.Steps.Single().Status.Should().Be(
            NyxIdChatStepStatus.Cancelled);
        reconciled.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        reconciled.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
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
                    },
                },
            },
            currentStateVersion: 10,
            Now());

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

    private sealed class ApprovalGenerationExecutor : GenerationExecutorBase
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
                        ? AgentToolReceiptStatus.ApprovalRequired
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
                                ApprovalRequestId = "approval-alpha",
                                Status = status,
                                ResultJson = resultJson,
                                IsDestructive = true,
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
