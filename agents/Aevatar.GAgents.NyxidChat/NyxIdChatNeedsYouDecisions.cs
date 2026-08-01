using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

public sealed record NyxIdChatNeedsYouDecision<TResolution>(
    bool ShouldCommit,
    bool IsExactReplay,
    NyxIdChatConversationGAgentState State,
    TResolution? Resolution)
    where TResolution : class, IMessage<TResolution>;

public static class NyxIdChatNeedsYouDecisions
{
    private const int ResolutionHistoryLimit = 32;

    public static NyxIdChatNeedsYouDecision<NyxIdChatPendingInputState> RequestInput(
        NyxIdChatConversationGAgentState state,
        NyxIdChatInputRequestCommand command,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(now);

        if (!MatchesConversation(state, command.ScopeId, command.ConversationActorId) ||
            !MatchesActiveTask(state, command.TurnId, command.TaskId, command.StepId) ||
            string.IsNullOrWhiteSpace(command.RequestId) ||
            string.IsNullOrWhiteSpace(command.Prompt) ||
            (!command.AllowFreeText && command.Options.Count == 0))
        {
            return NoCommit<NyxIdChatPendingInputState>(state);
        }

        var pending = new NyxIdChatPendingInputState
        {
            RequestId = command.RequestId.Trim(),
            TurnId = command.TurnId.Trim(),
            TaskId = command.TaskId.Trim(),
            StepId = command.StepId.Trim(),
            Prompt = command.Prompt.Trim(),
            AskedAt = now.Clone(),
            AllowFreeText = command.AllowFreeText,
            MultiSelect = command.MultiSelect,
        };
        pending.Options.AddRange(command.Options.Select(NormalizeOption));

        if (state.RecentInputResolutions.Any(result =>
                string.Equals(result.RequestId, pending.RequestId, StringComparison.Ordinal)))
        {
            return NoCommit<NyxIdChatPendingInputState>(state);
        }

        if (state.PendingInput is not null)
        {
            var replay = state.PendingInput.Clone();
            replay.AskedAt = pending.AskedAt;
            return replay.Equals(pending)
                ? new(false, true, state.Clone(), state.PendingInput.Clone())
                : NoCommit<NyxIdChatPendingInputState>(state);
        }

        if (state.PendingApproval is not null)
            return NoCommit<NyxIdChatPendingInputState>(state);

        var next = state.Clone();
        next.PendingInput = pending.Clone();
        next.ProgressSequence = checked(Math.Max(0, next.ProgressSequence) + 1);
        next.UpdatedAt = now.Clone();
        return new(true, false, next, pending);
    }

    public static NyxIdChatNeedsYouDecision<NyxIdChatInputResolutionState> ResolveInput(
        NyxIdChatConversationGAgentState state,
        NyxIdChatInputResolveCommand command,
        long currentStateVersion,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(now);

        var answerHash = Hash(command.Answer?.Trim() ?? string.Empty);
        var replay = state.RecentInputResolutions.FirstOrDefault(result =>
            string.Equals(result.RequestId, command.RequestId?.Trim(), StringComparison.Ordinal));
        if (replay is not null)
        {
            var exact = MatchesConversation(
                            state,
                            command.ScopeId,
                            command.ConversationActorId) &&
                        string.Equals(
                            replay.ClientRequestId,
                            command.ClientRequestId?.Trim(),
                            StringComparison.Ordinal) &&
                        replay.AnswerSha256.Equals(answerHash);
            return new(false, exact, state.Clone(), exact ? replay.Clone() : null);
        }

        if (command.ExpectedStateVersion != currentStateVersion ||
            !MatchesConversation(state, command.ScopeId, command.ConversationActorId) ||
            string.IsNullOrWhiteSpace(command.ClientRequestId) ||
            string.IsNullOrWhiteSpace(command.Answer) ||
            state.PendingInput is not { } pending ||
            !string.Equals(pending.RequestId, command.RequestId?.Trim(), StringComparison.Ordinal))
        {
            return NoCommit<NyxIdChatInputResolutionState>(state);
        }

        var resolution = new NyxIdChatInputResolutionState
        {
            RequestId = pending.RequestId,
            ClientRequestId = command.ClientRequestId.Trim(),
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            AnswerSha256 = answerHash,
            CommittedAt = now.Clone(),
        };
        var next = state.Clone();
        next.PendingInput = null;
        next.LatestInputResolution = resolution.Clone();
        AppendBounded(next.RecentInputResolutions, resolution);
        next.ProgressSequence = checked(Math.Max(0, next.ProgressSequence) + 1);
        next.UpdatedAt = now.Clone();
        return new(true, false, next, resolution);
    }

    public static NyxIdChatNeedsYouDecision<NyxIdChatApprovalResolutionState> ResolveApproval(
        NyxIdChatConversationGAgentState state,
        NyxIdChatApprovalResolveCommand command,
        long currentStateVersion,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(now);

        var decisionHash = Hash($"{command.Approved}:{command.Reason?.Trim() ?? string.Empty}");
        var replay = state.RecentApprovalResolutions.FirstOrDefault(result =>
            string.Equals(result.RequestId, command.RequestId?.Trim(), StringComparison.Ordinal));
        if (replay is not null)
        {
            var exact = MatchesConversation(
                            state,
                            command.ScopeId,
                            command.ConversationActorId) &&
                        string.Equals(
                            replay.ClientRequestId,
                            command.ClientRequestId?.Trim(),
                            StringComparison.Ordinal) &&
                        replay.Approved == command.Approved &&
                        replay.DecisionSha256.Equals(decisionHash);
            return new(false, exact, state.Clone(), exact ? replay.Clone() : null);
        }

        if (command.ExpectedStateVersion != currentStateVersion ||
            !MatchesConversation(state, command.ScopeId, command.ConversationActorId) ||
            string.IsNullOrWhiteSpace(command.ClientRequestId) ||
            state.PendingApproval is not { } pending ||
            !string.Equals(pending.ApprovalRequestId, command.RequestId?.Trim(), StringComparison.Ordinal))
        {
            return NoCommit<NyxIdChatApprovalResolutionState>(state);
        }

        var resolution = new NyxIdChatApprovalResolutionState
        {
            RequestId = pending.ApprovalRequestId,
            ClientRequestId = command.ClientRequestId.Trim(),
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            Approved = command.Approved,
            DecisionSha256 = decisionHash,
            CommittedAt = now.Clone(),
        };
        var next = state.Clone();
        next.PendingApproval = null;
        next.LatestApprovalResolution = resolution.Clone();
        AppendBounded(next.RecentApprovalResolutions, resolution);
        next.ProgressSequence = checked(Math.Max(0, next.ProgressSequence) + 1);
        next.UpdatedAt = now.Clone();
        return new(true, false, next, resolution);
    }

    public static NyxIdChatConversationGAgentState RefreshAttention(
        NyxIdChatConversationGAgentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var next = state.Clone();
        var attention = new NyxIdChatConversationAttentionState
        {
            TaskStatus = next.ActiveTask?.Status ?? NyxIdChatTaskStatus.Unspecified,
            AttentionKind = NyxIdChatAttentionKind.None,
            ActiveStepSummary = ResolveActiveStepSummary(next.ActiveTask),
        };

        if (next.PendingInput is { } input)
        {
            attention.AttentionKind = NyxIdChatAttentionKind.Input;
            attention.AttentionSince = input.AskedAt?.Clone();
        }
        else if (next.PendingApproval is { } approval)
        {
            attention.AttentionKind = NyxIdChatAttentionKind.Approval;
            attention.AttentionSince = approval.AskedAt?.Clone();
        }

        next.Attention = attention;
        return next;
    }

    private static bool MatchesConversation(
        NyxIdChatConversationGAgentState state,
        string? scopeId,
        string? actorId) =>
        !string.IsNullOrWhiteSpace(scopeId) &&
        !string.IsNullOrWhiteSpace(actorId) &&
        string.Equals(state.ScopeId, scopeId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ConversationActorId, actorId.Trim(), StringComparison.Ordinal);

    private static bool MatchesActiveTask(
        NyxIdChatConversationGAgentState state,
        string? turnId,
        string? taskId,
        string? stepId) =>
        state.ActiveTask is { } task &&
        state.ActiveTurn is { } turn &&
        task.Status == NyxIdChatTaskStatus.Active &&
        turn.Status == NyxIdChatTurnStatus.Active &&
        string.Equals(turn.TurnId, turnId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(task.TaskId, taskId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(task.ActiveStepId, stepId?.Trim(), StringComparison.Ordinal) &&
        task.Steps.Any(step =>
            string.Equals(step.StepId, stepId?.Trim(), StringComparison.Ordinal) &&
            step.Status is NyxIdChatStepStatus.Planned or
                NyxIdChatStepStatus.Waiting or
                NyxIdChatStepStatus.Running);

    private static NyxIdChatInputOption NormalizeOption(NyxIdChatInputOption option) =>
        new()
        {
            Label = option?.Label?.Trim() ?? string.Empty,
            Description = option?.Description?.Trim() ?? string.Empty,
        };

    private static string ResolveActiveStepSummary(NyxIdChatTaskState? task)
    {
        if (task is null)
            return string.Empty;
        var step = task.Steps.FirstOrDefault(candidate =>
            string.Equals(candidate.StepId, task.ActiveStepId, StringComparison.Ordinal));
        return step?.Description?.Trim() ?? string.Empty;
    }

    private static ByteString Hash(string value) =>
        ByteString.CopyFrom(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void AppendBounded<T>(Google.Protobuf.Collections.RepeatedField<T> items, T item)
        where T : class, IMessage<T>
    {
        items.Add(item);
        while (items.Count > ResolutionHistoryLimit)
            items.RemoveAt(0);
    }

    private static NyxIdChatNeedsYouDecision<T> NoCommit<T>(
        NyxIdChatConversationGAgentState state)
        where T : class, IMessage<T> =>
        new(false, false, state.Clone(), null);
}
