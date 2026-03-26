using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GroupChat.Abstractions;
using Google.Protobuf;

namespace Aevatar.GroupChat.Core.GAgents;

public sealed class SourceRegistryGAgent : GAgentBase<GroupSourceRegistryState>
{
    public SourceRegistryGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleRegisterAsync(RegisterGroupSourceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CanonicalLocator);
        if (command.SourceKind == GroupSourceKind.Unspecified)
            throw new InvalidOperationException("source_kind is required.");

        EnsureSourceNotRegistered(command.SourceId);

        await PersistDomainEventAsync(new GroupSourceRegisteredEvent
        {
            SourceId = command.SourceId,
            SourceKind = command.SourceKind,
            CanonicalLocator = command.CanonicalLocator,
        });
    }

    [EventHandler]
    public async Task HandleUpdateTrustAsync(UpdateGroupSourceTrustCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SourceId);
        EnsureSourceRegistered(command.SourceId);

        if (State.AuthorityClass == command.AuthorityClass &&
            State.VerificationStatus == command.VerificationStatus)
        {
            return;
        }

        await PersistDomainEventAsync(new GroupSourceTrustUpdatedEvent
        {
            SourceId = command.SourceId,
            AuthorityClass = command.AuthorityClass,
            VerificationStatus = command.VerificationStatus,
        });
    }

    protected override GroupSourceRegistryState TransitionState(GroupSourceRegistryState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<GroupSourceRegisteredEvent>(ApplyRegistered)
            .On<GroupSourceTrustUpdatedEvent>(ApplyTrustUpdated)
            .OrCurrent();

    private static GroupSourceRegistryState ApplyRegistered(GroupSourceRegistryState state, GroupSourceRegisteredEvent evt)
    {
        var next = state.Clone();
        next.SourceId = evt.SourceId;
        next.SourceKind = evt.SourceKind;
        next.CanonicalLocator = evt.CanonicalLocator;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.SourceId, "registered");
        return next;
    }

    private static GroupSourceRegistryState ApplyTrustUpdated(GroupSourceRegistryState state, GroupSourceTrustUpdatedEvent evt)
    {
        var next = state.Clone();
        next.AuthorityClass = evt.AuthorityClass;
        next.VerificationStatus = evt.VerificationStatus;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.SourceId, "trust-updated");
        return next;
    }

    private void EnsureSourceNotRegistered(string sourceId)
    {
        if (!string.IsNullOrWhiteSpace(State.SourceId))
            throw new InvalidOperationException($"Source '{sourceId}' already exists.");
    }

    private void EnsureSourceRegistered(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(State.SourceId))
            throw new InvalidOperationException($"Source '{sourceId}' does not exist.");

        if (!string.Equals(State.SourceId, sourceId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Source registry actor '{Id}' is bound to '{State.SourceId}', but got '{sourceId}'.");
    }

    private static string BuildEventId(string sourceId, string suffix) => $"source:{sourceId}:{suffix}";
}
