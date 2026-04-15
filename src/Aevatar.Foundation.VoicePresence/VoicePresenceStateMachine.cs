namespace Aevatar.Foundation.VoicePresence;

/// <summary>
/// Voice turn state machine with turn epoch and safe-to-inject fence semantics.
/// </summary>
public enum VoicePresenceState
{
    Idle,
    UserSpeaking,
    ResponseInProgress,
    AudioDraining,
}

/// <summary>
/// Maintains the per-session voice response epoch and drain-ack fence.
/// </summary>
public sealed class VoicePresenceStateMachine
{
    public VoicePresenceState State { get; private set; } = VoicePresenceState.Idle;

    public int CurrentResponseId { get; private set; }

    public int LastDrainAckResponseId { get; private set; } = -1;

    public long LastDrainAckPlayoutSequence { get; private set; } = -1;

    public void OnSpeechStarted() => State = VoicePresenceState.UserSpeaking;

    public void OnSpeechStopped()
    {
    }

    public void OnResponseStarted(int responseId)
    {
        if (responseId < CurrentResponseId)
            return;

        CurrentResponseId = responseId;
        State = VoicePresenceState.ResponseInProgress;
    }

    public void OnResponseDone(int responseId)
    {
        if (responseId == CurrentResponseId &&
            (State == VoicePresenceState.ResponseInProgress || State == VoicePresenceState.UserSpeaking))
        {
            State = VoicePresenceState.AudioDraining;
        }
    }

    public void OnResponseCancelled(int responseId)
    {
        if (responseId != CurrentResponseId)
            return;

        LastDrainAckResponseId = responseId;
        State = VoicePresenceState.Idle;
    }

    public void OnDrainAcknowledged(int responseId, long playoutSequence)
    {
        if (responseId != CurrentResponseId)
            return;

        LastDrainAckResponseId = responseId;
        LastDrainAckPlayoutSequence = playoutSequence;

        if (State == VoicePresenceState.AudioDraining)
            State = VoicePresenceState.Idle;
    }

    public void OnProviderDisconnected()
    {
        LastDrainAckResponseId = CurrentResponseId;
        State = VoicePresenceState.Idle;
    }

    public bool IsSafeToInject =>
        State == VoicePresenceState.Idle &&
        (CurrentResponseId == 0 || LastDrainAckResponseId == CurrentResponseId);

    public int AllocateNextResponseId()
    {
        CurrentResponseId += 1;
        State = VoicePresenceState.ResponseInProgress;
        return CurrentResponseId;
    }
}
