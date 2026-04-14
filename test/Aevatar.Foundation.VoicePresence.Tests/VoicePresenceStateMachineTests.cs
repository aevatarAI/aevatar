using Aevatar.Foundation.VoicePresence;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoicePresenceStateMachineTests
{
    [Fact]
    public void Initial_state_is_Idle_and_safe_to_inject()
    {
        var stateMachine = new VoicePresenceStateMachine();

        stateMachine.State.ShouldBe(VoicePresenceState.Idle);
        stateMachine.IsSafeToInject.ShouldBeTrue();
    }

    [Fact]
    public void SpeechStarted_transitions_to_UserSpeaking_and_blocks_inject()
    {
        var stateMachine = new VoicePresenceStateMachine();

        stateMachine.OnSpeechStarted();

        stateMachine.State.ShouldBe(VoicePresenceState.UserSpeaking);
        stateMachine.IsSafeToInject.ShouldBeFalse();
    }

    [Fact]
    public void Response_lifecycle_traverses_InProgress_then_Draining_then_Idle_after_ack()
    {
        var stateMachine = new VoicePresenceStateMachine();

        var responseId = stateMachine.AllocateNextResponseId();
        stateMachine.State.ShouldBe(VoicePresenceState.ResponseInProgress);
        stateMachine.IsSafeToInject.ShouldBeFalse();

        stateMachine.OnResponseDone(responseId);
        stateMachine.State.ShouldBe(VoicePresenceState.AudioDraining);

        stateMachine.OnDrainAcknowledged(responseId, 100);
        stateMachine.State.ShouldBe(VoicePresenceState.Idle);
        stateMachine.IsSafeToInject.ShouldBeTrue();
    }

    [Fact]
    public void Stale_drain_ack_for_prior_epoch_is_ignored()
    {
        var stateMachine = new VoicePresenceStateMachine();

        var first = stateMachine.AllocateNextResponseId();
        stateMachine.OnResponseDone(first);
        stateMachine.OnDrainAcknowledged(first, 50);
        stateMachine.IsSafeToInject.ShouldBeTrue();

        var second = stateMachine.AllocateNextResponseId();
        stateMachine.OnResponseDone(second);
        stateMachine.OnDrainAcknowledged(first, 9999);

        stateMachine.State.ShouldBe(VoicePresenceState.AudioDraining);
        stateMachine.IsSafeToInject.ShouldBeFalse();

        stateMachine.OnDrainAcknowledged(second, 200);
        stateMachine.IsSafeToInject.ShouldBeTrue();
    }

    [Fact]
    public void Cancelled_response_returns_to_Idle()
    {
        var stateMachine = new VoicePresenceStateMachine();

        var responseId = stateMachine.AllocateNextResponseId();
        stateMachine.OnResponseCancelled(responseId);

        stateMachine.State.ShouldBe(VoicePresenceState.Idle);
    }

    [Fact]
    public void Response_id_is_monotonic()
    {
        var stateMachine = new VoicePresenceStateMachine();

        var ids = new[]
        {
            stateMachine.AllocateNextResponseId(),
            stateMachine.AllocateNextResponseId(),
            stateMachine.AllocateNextResponseId(),
        };

        ids.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void Inject_is_unsafe_while_response_in_progress()
    {
        var stateMachine = new VoicePresenceStateMachine();

        stateMachine.AllocateNextResponseId();

        stateMachine.IsSafeToInject.ShouldBeFalse();
    }

    [Fact]
    public void Speech_started_during_response_indicates_barge_in()
    {
        var stateMachine = new VoicePresenceStateMachine();

        var responseId = stateMachine.AllocateNextResponseId();
        stateMachine.OnSpeechStarted();

        stateMachine.State.ShouldBe(VoicePresenceState.UserSpeaking);
        stateMachine.CurrentResponseId.ShouldBe(responseId);
    }

    [Fact]
    public void Stale_response_started_is_ignored()
    {
        var stateMachine = new VoicePresenceStateMachine();

        stateMachine.AllocateNextResponseId();
        stateMachine.OnResponseStarted(0);

        stateMachine.CurrentResponseId.ShouldBe(1);
        stateMachine.State.ShouldBe(VoicePresenceState.ResponseInProgress);
    }

    [Fact]
    public void Response_done_from_idle_is_ignored()
    {
        var stateMachine = new VoicePresenceStateMachine();

        stateMachine.OnResponseDone(999);

        stateMachine.State.ShouldBe(VoicePresenceState.Idle);
    }

    [Fact]
    public void Speech_stopped_is_noop()
    {
        var stateMachine = new VoicePresenceStateMachine();

        stateMachine.OnSpeechStarted();
        stateMachine.OnSpeechStopped();

        stateMachine.State.ShouldBe(VoicePresenceState.UserSpeaking);
    }

    [Fact]
    public void Provider_disconnected_resets_to_Idle_and_opens_fence()
    {
        var stateMachine = new VoicePresenceStateMachine();

        stateMachine.AllocateNextResponseId();
        stateMachine.State.ShouldBe(VoicePresenceState.ResponseInProgress);
        stateMachine.IsSafeToInject.ShouldBeFalse();

        stateMachine.OnProviderDisconnected();

        stateMachine.State.ShouldBe(VoicePresenceState.Idle);
        stateMachine.IsSafeToInject.ShouldBeTrue();
    }

    [Fact]
    public void Cancelled_response_allows_injection()
    {
        var stateMachine = new VoicePresenceStateMachine();

        var first = stateMachine.AllocateNextResponseId();
        stateMachine.OnResponseDone(first);
        stateMachine.OnDrainAcknowledged(first, 50);
        stateMachine.IsSafeToInject.ShouldBeTrue();

        var second = stateMachine.AllocateNextResponseId();
        stateMachine.IsSafeToInject.ShouldBeFalse();

        stateMachine.OnResponseCancelled(second);
        stateMachine.State.ShouldBe(VoicePresenceState.Idle);
        stateMachine.IsSafeToInject.ShouldBeTrue();
    }
}
