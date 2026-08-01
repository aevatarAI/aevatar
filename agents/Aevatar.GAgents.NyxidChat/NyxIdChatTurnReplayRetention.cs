using System.Security.Cryptography;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatTurnReplayRetention
{
    public const int MaxRecords = 32;

    public static void RetainTerminalTurn(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTurnState turn)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(turn);
        var task = state.ActiveTask;
        if (task is null ||
            turn.Status is NyxIdChatTurnStatus.Unspecified or NyxIdChatTurnStatus.Active ||
            task.Status is NyxIdChatTaskStatus.Unspecified or NyxIdChatTaskStatus.Active ||
            string.IsNullOrWhiteSpace(turn.TurnId) ||
            string.IsNullOrWhiteSpace(turn.TaskId) ||
            string.IsNullOrWhiteSpace(turn.ClientRequestId) ||
            !string.Equals(task.TaskId, turn.TaskId, StringComparison.Ordinal) ||
            !string.Equals(task.TurnId, turn.TurnId, StringComparison.Ordinal) ||
            turn.AdmissionRequestSha256.Length != 32)
        {
            return;
        }

        var safeTurn = BuildSafeTurnSnapshot(turn);
        var record = new NyxIdChatTurnReplayRecord
        {
            TurnId = safeTurn.TurnId,
            TaskId = safeTurn.TaskId,
            ClientRequestId = safeTurn.ClientRequestId,
            AdmissionRequestSha256 = safeTurn.AdmissionRequestSha256,
            Turn = safeTurn,
            Task = task.Clone(),
            RecordedAt = turn.TerminalAt?.Clone() ?? task.UpdatedAt?.Clone() ?? new Timestamp(),
        };

        var referencedIndex = -1;
        for (var index = 0; index < state.RecentTurnReplayRecords.Count; index++)
        {
            var existing = state.RecentTurnReplayRecords[index];
            var referencesIdentity =
                string.Equals(existing.TurnId, record.TurnId, StringComparison.Ordinal) ||
                string.Equals(existing.ClientRequestId, record.ClientRequestId, StringComparison.Ordinal);
            if (!referencesIdentity)
                continue;

            if (!string.Equals(existing.TurnId, record.TurnId, StringComparison.Ordinal) ||
                !string.Equals(existing.TaskId, record.TaskId, StringComparison.Ordinal) ||
                !string.Equals(existing.ClientRequestId, record.ClientRequestId, StringComparison.Ordinal) ||
                existing.AdmissionRequestSha256.Length != 32 ||
                !CryptographicOperations.FixedTimeEquals(
                    existing.AdmissionRequestSha256.Span,
                    record.AdmissionRequestSha256.Span))
            {
                throw new InvalidOperationException("A retained turn replay identity cannot be replaced.");
            }

            referencedIndex = index;
            break;
        }

        if (referencedIndex >= 0)
            state.RecentTurnReplayRecords[referencedIndex] = record;
        else
            state.RecentTurnReplayRecords.Add(record);

        while (state.RecentTurnReplayRecords.Count > MaxRecords)
            state.RecentTurnReplayRecords.RemoveAt(0);
    }

    public static NyxIdChatTurnState BuildSafeTurnSnapshot(NyxIdChatTurnState source)
    {
        var safe = source.Clone();
        safe.Prompt = string.Empty;
        safe.InputParts.Clear();
        return safe;
    }
}
