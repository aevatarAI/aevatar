using Aevatar.AGUI.Contracts;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Presentation-only mapping from committed controller facts to AGUI frames.
/// It never derives task state: snapshots and step changes are the exact
/// protobuf values already committed by the conversation authority.
/// </summary>
internal static class NyxIdChatConversationAguiFrameBuilder
{
    public const string TaskSnapshotEventName = "nyxid.task.snapshot";
    public const string TaskStepChangedEventName = "nyxid.task.step.changed";
    public const string ControlChangedEventName = "nyxid.control.changed";
    public const string ActionRequestEventName = "nyxid.action.request";
    public const string ContinuationChangedEventName = "nyxid.continuation.changed";
    public const string StepControlChangedEventName = "nyxid.step.control.changed";
    public const string InputRequestEventName = "nyxid.input.request";
    public const string InputChangedEventName = "nyxid.input.changed";
    public const string ApprovalRequestEventName = "nyxid.approval.request";
    public const string ApprovalChangedEventName = "nyxid.approval.changed";

    private const string TerminalStateConflictCode = "NYXID_CHAT_TERMINAL_STATE_CONFLICT";
    private const string TerminalStateConflictMessage =
        "The committed task and turn terminal states were inconsistent.";

    public static IReadOnlyList<AGUIEvent> BuildStarted(
        string actorId,
        string turnId,
        NyxIdChatConversationGAgentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ActiveTask is null || state.ActiveTurn is null || state.ProgressSequence <= 0)
            return [];

        var frames = BuildTaskFrames(
            state.ActiveTask,
            state.ActiveTask.Steps.Take(1),
            state.ProgressSequence);
        frames.Add(new AGUIEvent
        {
            Sequence = state.ProgressSequence,
            TextMessageStart = new TextMessageStartEvent
            {
                MessageId = turnId,
                Role = "assistant",
            },
        });
        return frames;
    }

    public static IReadOnlyList<AGUIEvent> BuildProgressed(
        string turnId,
        NyxIdChatOperationProgressedEvent progressed)
    {
        ArgumentNullException.ThrowIfNull(progressed);
        if (progressed.Progress is null || progressed.ProgressSequence <= 0)
            return [];

        var frame = progressed.Progress.ProgressCase switch
        {
            NyxIdChatOperationProgressSignal.ProgressOneofCase.Text => new AGUIEvent
            {
                TextMessageContent = new TextMessageContentEvent
                {
                    MessageId = turnId,
                    Delta = progressed.Progress.Text.Delta,
                },
            },
            NyxIdChatOperationProgressSignal.ProgressOneofCase.Reasoning => new AGUIEvent
            {
                Custom = new CustomEvent
                {
                    Name = "aevatar.llm.reasoning",
                    Payload = Any.Pack(progressed.Progress.Reasoning),
                },
            },
            NyxIdChatOperationProgressSignal.ProgressOneofCase.ToolStarted => new AGUIEvent
            {
                ToolCallStart = new ToolCallStartEvent
                {
                    ToolCallId = progressed.Progress.ToolStarted.CallId,
                    ToolName = progressed.Progress.ToolStarted.ToolName,
                },
            },
            _ => null,
        };
        if (frame is null)
            return [];

        frame.Sequence = progressed.ProgressSequence;
        return [frame];
    }

    public static IReadOnlyList<AGUIEvent> BuildReconciled(
        string actorId,
        string turnId,
        NyxIdChatOperationReconciledEvent reconciled)
    {
        ArgumentNullException.ThrowIfNull(reconciled);
        var state = reconciled.State;
        if (state?.ActiveTask is null ||
            state.ActiveTurn is null ||
            reconciled.Result?.Key is null ||
            reconciled.ProgressSequence <= 0)
        {
            return [];
        }

        var changedSteps = ResolveChangedSteps(state.ActiveTask, reconciled.Result.Key);
        var frames = BuildTaskFrames(
            state.ActiveTask,
            changedSteps,
            reconciled.ProgressSequence);
        if (state.PendingApproval is not null)
        {
            frames.Insert(0, Custom(
                ApprovalRequestEventName,
                state.PendingApproval,
                reconciled.ProgressSequence));
        }
        AppendOperationEvidence(frames, reconciled, turnId);

        if (state.ActiveTurn.Status == NyxIdChatTurnStatus.Active &&
            state.ActiveTask.Status == NyxIdChatTaskStatus.Active)
        {
            return frames;
        }

        frames.Add(new AGUIEvent
        {
            Sequence = reconciled.ProgressSequence,
            TextMessageEnd = new TextMessageEndEvent { MessageId = turnId },
        });
        frames.Add(BuildTerminal(actorId, turnId, state, reconciled.ProgressSequence));
        return frames;
    }

    public static IReadOnlyList<AGUIEvent> BuildControlChanged(
        string actorId,
        string turnId,
        NyxIdChatControlFenceCommittedEvent committed,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(committed);
        if (committed.Fence is null || committed.Task is null || committed.Turn is null || sequence <= 0)
            return [];

        var frames = BuildTaskFrames(committed.Task, ResolveActiveOrLast(committed.Task), sequence);
        frames.Insert(0, Custom(ControlChangedEventName, committed.Fence, sequence));
        AppendTerminalIfNeeded(frames, actorId, turnId, committed.Task, committed.Turn, sequence);
        return frames;
    }

    public static IReadOnlyList<AGUIEvent> BuildActionRequested(
        string actorId,
        string turnId,
        NyxIdChatActionRequestedEvent committed,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(committed);
        if (committed.Request is null || committed.Task is null || committed.OriginTurn is null || sequence <= 0)
            return [];

        var wirePayload = MapActionRequestWirePayload(committed.Request);
        if (wirePayload is null)
            return [];

        var frames = BuildTaskFrames(committed.Task, ResolveActiveOrLast(committed.Task), sequence);
        frames.Insert(0, Custom(ActionRequestEventName, wirePayload, sequence));
        AppendTerminalIfNeeded(frames, actorId, turnId, committed.Task, committed.OriginTurn, sequence);
        return frames;
    }

    public static IReadOnlyList<AGUIEvent> BuildInputRequested(
        NyxIdChatInputRequestedEvent committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        var sequence = committed.State?.ProgressSequence ?? 0;
        if (committed.PendingInput is null || sequence <= 0)
            return [];

        return [Custom(InputRequestEventName, committed.PendingInput, sequence)];
    }

    public static IReadOnlyList<AGUIEvent> BuildInputChanged(
        NyxIdChatInputResolutionCommittedEvent committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        var sequence = committed.State?.ProgressSequence ?? 0;
        if (committed.Resolution is null || sequence <= 0)
            return [];

        return [Custom(InputChangedEventName, committed.Resolution, sequence)];
    }

    public static IReadOnlyList<AGUIEvent> BuildApprovalChanged(
        NyxIdChatApprovalResolutionCommittedEvent committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        var sequence = committed.State?.ProgressSequence ?? 0;
        if (committed.Resolution is null || sequence <= 0)
            return [];

        return [Custom(ApprovalChangedEventName, committed.Resolution, sequence)];
    }

    private static NyxIdAssistantActionRequestWirePayload? MapActionRequestWirePayload(
        NyxIdChatActionRequestState request)
    {
        if (request.SchemaVersion != NyxIdAssistantActionRegistry.SupportedSchemaVersion ||
            request.Action != NyxIdAssistantActionKind.ServiceConnect ||
            request.Params is null)
        {
            return null;
        }

        var wireParams = request.Params.ParamsCase switch
        {
            NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect =>
                new NyxIdAssistantActionWireParams
                {
                    CatalogService = request.Params.CatalogServiceConnect.Clone(),
                },
            NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect =>
                new NyxIdAssistantActionWireParams
                {
                    CustomService = request.Params.CustomServiceConnect.Clone(),
                },
            _ => null,
        };
        if (wireParams is null)
            return null;

        return new NyxIdAssistantActionRequestWirePayload
        {
            SchemaVersion = request.SchemaVersion,
            ActorId = request.ConversationActorId,
            OriginTurnId = request.OriginTurnId,
            TaskId = request.TaskId,
            StepId = request.StepId,
            ActionRequestId = request.ActionRequestId,
            Action = "service.connect",
            Params = wireParams,
        };
    }

    public static IReadOnlyList<AGUIEvent> BuildContinuationChanged(
        string actorId,
        string turnId,
        NyxIdChatContinuationAdmissionCommittedEvent committed,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(committed);
        if (committed.Admission is null || sequence <= 0)
            return [];

        var frames = new List<AGUIEvent>
        {
            Custom(ContinuationChangedEventName, committed.Admission, sequence),
        };
        if (committed.Admission.Kind == NyxIdChatContinuationKind.Action &&
            committed.Admission.Status == NyxIdChatContinuationAdmissionStatus.Rejected)
        {
            frames.Add(new AGUIEvent
            {
                Sequence = sequence,
                RunError = new RunErrorEvent
                {
                    RunId = turnId,
                    Code = committed.Admission.ReasonCode,
                    Message = committed.Admission.SafeMessage,
                },
            });
            return frames;
        }
        if (committed.Admission.Kind != NyxIdChatContinuationKind.Action ||
            committed.State?.ActiveTask is not { } task ||
            committed.State.ActiveTurn is not { } turn ||
            !string.Equals(task.TurnId, turnId, StringComparison.Ordinal) ||
            !string.Equals(turn.TurnId, turnId, StringComparison.Ordinal))
        {
            return frames;
        }

        frames.AddRange(BuildTaskFrames(task, ResolveActiveOrLast(task), sequence));
        AppendTerminalIfNeeded(frames, actorId, turnId, task, turn, sequence);
        return frames;
    }

    public static IReadOnlyList<AGUIEvent> BuildLateOperationEvidence(
        NyxIdChatLateOperationEvidenceCommittedEvent committed,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(committed);
        if (committed.Key is null ||
            committed.State?.ActiveTask is null ||
            committed.State.ActiveTurn is null ||
            sequence <= 0)
        {
            return [];
        }

        var step = committed.State.ActiveTask.Steps.FirstOrDefault(candidate =>
            KeysEqual(candidate.Operation?.Key, committed.Key));
        if (step is null)
            return [];

        var frames = BuildTaskFrames(committed.State.ActiveTask, [step], sequence);
        if (committed.ToolReceipt is { CallId.Length: > 0 } receipt)
        {
            frames.Add(new AGUIEvent
            {
                Sequence = sequence,
                ToolCallEnd = new ToolCallEndEvent
                {
                    ToolCallId = receipt.CallId,
                    Result = receipt.Status ==
                        Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success
                        ? "completed"
                        : string.IsNullOrWhiteSpace(receipt.ErrorMessage)
                            ? "not completed"
                            : receipt.ErrorMessage,
                },
            });
        }

        return frames;
    }

    public static IReadOnlyList<AGUIEvent> BuildStepControlChanged(
        NyxIdChatStepControlCommittedEvent committed,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(committed);
        if (committed.Result is null ||
            committed.State?.ActiveTask is null ||
            committed.State.ActiveTurn is null ||
            sequence <= 0)
        {
            return [];
        }

        var frames = new List<AGUIEvent>
        {
            Custom(StepControlChangedEventName, committed.Result, sequence),
        };
        var step = committed.State.ActiveTask.Steps.FirstOrDefault(candidate =>
            string.Equals(
                candidate.StepId,
                committed.Result.StepId,
                StringComparison.Ordinal));
        if (step is not null)
            frames.AddRange(BuildTaskFrames(committed.State.ActiveTask, [step], sequence));
        if (committed.Result.Outcome == NyxIdChatTransitionOutcome.Accepted)
        {
            AppendTerminalIfNeeded(
                frames,
                committed.State.ConversationActorId,
                committed.Result.TurnId,
                committed.State.ActiveTask,
                committed.State.ActiveTurn,
                sequence);
        }
        return frames;
    }

    public static IReadOnlyList<AGUIEvent> BuildTurnAdmissionRejected(
        NyxIdChatTurnAdmissionRejectedEvent rejected,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        if (string.IsNullOrWhiteSpace(rejected.RequestedTurnId) ||
            string.IsNullOrWhiteSpace(rejected.ReasonCode) ||
            sequence <= 0)
        {
            return [];
        }

        return
        [
            new AGUIEvent
            {
                Sequence = sequence,
                RunError = new RunErrorEvent
                {
                    RunId = rejected.RequestedTurnId,
                    Code = rejected.ReasonCode,
                    Message = rejected.SafeMessage,
                },
            },
        ];
    }

    private static List<AGUIEvent> BuildTaskFrames(
        NyxIdChatTaskState task,
        IEnumerable<NyxIdChatTaskStepState> changedSteps,
        long sequence)
    {
        var frames = new List<AGUIEvent>
        {
            Custom(TaskSnapshotEventName, task, sequence),
        };
        frames.AddRange(changedSteps.Select(step =>
            Custom(TaskStepChangedEventName, step, sequence)));
        return frames;
    }

    private static IEnumerable<NyxIdChatTaskStepState> ResolveChangedSteps(
        NyxIdChatTaskState task,
        NyxIdChatOperationKey reconciledKey)
    {
        var changed = task.Steps.Where(step =>
            string.Equals(step.StepId, reconciledKey.StepId, StringComparison.Ordinal)).ToList();
        if (!string.IsNullOrWhiteSpace(task.ActiveStepId) &&
            !string.Equals(task.ActiveStepId, reconciledKey.StepId, StringComparison.Ordinal))
        {
            var successor = task.Steps.FirstOrDefault(step =>
                string.Equals(step.StepId, task.ActiveStepId, StringComparison.Ordinal));
            if (successor is not null)
                changed.Add(successor);
        }
        return changed;
    }

    private static bool KeysEqual(NyxIdChatOperationKey? left, NyxIdChatOperationKey? right) =>
        left is not null &&
        right is not null &&
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) &&
        left.OperationGeneration == right.OperationGeneration;

    private static IEnumerable<NyxIdChatTaskStepState> ResolveActiveOrLast(
        NyxIdChatTaskState task)
    {
        var active = task.Steps.FirstOrDefault(step =>
            string.Equals(step.StepId, task.ActiveStepId, StringComparison.Ordinal));
        if (active is not null)
            return [active];
        return task.Steps.Count == 0 ? [] : [task.Steps[^1]];
    }

    private static void AppendOperationEvidence(
        List<AGUIEvent> frames,
        NyxIdChatOperationReconciledEvent reconciled,
        string turnId)
    {
        if (reconciled.Result?.Llm?.Usage is { } usage)
        {
            frames.Add(new AGUIEvent
            {
                Sequence = reconciled.ProgressSequence,
                Usage = new UsageEvent
                {
                    Available = true,
                    PromptTokens = usage.PromptTokens,
                    CompletionTokens = usage.CompletionTokens,
                    TotalTokens = usage.TotalTokens,
                },
            });
        }

        var receipt = reconciled.Result?.Tool?.Receipt;
        if (receipt is not null && !string.IsNullOrWhiteSpace(receipt.CallId))
        {
            frames.Add(new AGUIEvent
            {
                Sequence = reconciled.ProgressSequence,
                ToolCallEnd = new ToolCallEndEvent
                {
                    ToolCallId = receipt.CallId,
                    Result = receipt.Status == Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success
                        ? "completed"
                        : string.IsNullOrWhiteSpace(receipt.ErrorMessage)
                            ? "not completed"
                            : receipt.ErrorMessage,
                },
            });
        }
    }

    private static void AppendTerminalIfNeeded(
        List<AGUIEvent> frames,
        string actorId,
        string turnId,
        NyxIdChatTaskState task,
        NyxIdChatTurnState turn,
        long sequence)
    {
        if (task.Status == NyxIdChatTaskStatus.Active && turn.Status == NyxIdChatTurnStatus.Active)
            return;

        frames.Add(new AGUIEvent
        {
            Sequence = sequence,
            TextMessageEnd = new TextMessageEndEvent { MessageId = turnId },
        });
        var state = new NyxIdChatConversationGAgentState
        {
            ActiveTask = task.Clone(),
            ActiveTurn = turn.Clone(),
        };
        frames.Add(BuildTerminal(actorId, turnId, state, sequence));
    }

    internal static AGUIEvent BuildTerminal(
        string actorId,
        string turnId,
        NyxIdChatConversationGAgentState state,
        long sequence)
    {
        var task = state.ActiveTask;
        var turn = state.ActiveTurn;
        if (task.Status == NyxIdChatTaskStatus.Failed &&
            turn.Status == NyxIdChatTurnStatus.Failed)
        {
            return new AGUIEvent
            {
                Sequence = sequence,
                RunError = new RunErrorEvent
                {
                    RunId = turnId,
                    Code = string.IsNullOrWhiteSpace(task.FailureCode)
                        ? "NYXID_CHAT_TASK_FAILED"
                        : task.FailureCode,
                    Message = string.IsNullOrWhiteSpace(task.SafeMessage)
                        ? "The chat task failed."
                        : task.SafeMessage,
                },
            };
        }

        if (task.Status == NyxIdChatTaskStatus.Succeeded &&
            turn.Status == NyxIdChatTurnStatus.Succeeded)
        {
            return Finished(actorId, turnId, RunCompletionStatus.Completed, sequence);
        }

        if ((task.Status == NyxIdChatTaskStatus.Blocked &&
             turn.Status == NyxIdChatTurnStatus.Blocked) ||
            (task.Status == NyxIdChatTaskStatus.Stopped &&
             turn.Status == NyxIdChatTurnStatus.Stopped))
        {
            return Finished(actorId, turnId, RunCompletionStatus.Blocked, sequence);
        }

        return new AGUIEvent
        {
            Sequence = sequence,
            RunError = new RunErrorEvent
            {
                RunId = turnId,
                Code = TerminalStateConflictCode,
                Message = TerminalStateConflictMessage,
            },
        };
    }

    private static AGUIEvent Finished(
        string actorId,
        string turnId,
        RunCompletionStatus status,
        long sequence) =>
        new()
        {
            Sequence = sequence,
            RunFinished = new RunFinishedEvent
            {
                ThreadId = actorId,
                RunId = turnId,
                Status = status,
                Result = Any.Pack(new StringValue()),
            },
        };

    private static AGUIEvent Custom(
        string name,
        Google.Protobuf.IMessage payload,
        long sequence) =>
        new()
        {
            Sequence = sequence,
            Custom = new CustomEvent
            {
                Name = name,
                Payload = Any.Pack(payload),
            },
        };
}
