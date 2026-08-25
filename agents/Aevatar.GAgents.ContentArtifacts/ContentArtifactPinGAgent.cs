using System.Security.Cryptography;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ContentArtifacts;

/// <summary>
/// Authority for the single mutable ContentArtifact pointer identified by scope and pin key.
/// </summary>
[GAgent("studio.content-artifact-pin")]
public sealed class ContentArtifactPinGAgent : GAgentBase<ContentArtifactPinState>, IProjectedActor
{
    public static string ProjectionKind => "content-artifact-pin";

    // Implement (issue #3527):
    //   Behavior: one scope + pin_key actor atomically replaces the current pinned artifact.
    //   Why this shape: the cross-artifact uniqueness invariant must live in one authority actor.
    [EventHandler(EndpointName = "setContentArtifactPin")]
    public async Task HandleSetAsync(SetContentArtifactPinCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scopeId = ContentArtifactConventions.NormalizeScopeId(command.ScopeId);
        var pinKey = ContentArtifactConventions.NormalizeLabelKey(command.PinKey, "pin_key");
        var artifactId = ContentArtifactConventions.NormalizeArtifactId(command.ArtifactId);
        ValidatePrincipal(command.RequestedBy);
        ValidateExpectedVersion(command.ExpectedPinVersion);
        var mutationId = ContentArtifactConventions.NormalizeRequired(command.MutationId, "mutation_id");
        EnsureActorAddress(scopeId, pinKey);
        var mutationHash = HashMutation(command);
        if (IsReplay(mutationId, mutationHash))
            return;

        if (command.ExpectedPinVersion != State.PinVersion)
        {
            await PersistRejectedAsync(
                scopeId,
                pinKey,
                command.RequestedBy,
                mutationId,
                mutationHash,
                command.RequestedAtUtc);
            return;
        }

        await PersistDomainEventAsync(new ContentArtifactPinSetEvent
        {
            ScopeId = scopeId,
            PinKey = pinKey,
            ArtifactId = artifactId,
            PinnedBy = command.RequestedBy.Clone(),
            PinVersion = checked(State.PinVersion + 1),
            MutationId = mutationId,
            MutationHash = mutationHash,
            UpdatedAtUtc = ResolveRequestedAt(command.RequestedAtUtc),
        });
    }

    [EventHandler(EndpointName = "clearContentArtifactPin")]
    public async Task HandleClearAsync(ClearContentArtifactPinCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scopeId = ContentArtifactConventions.NormalizeScopeId(command.ScopeId);
        var pinKey = ContentArtifactConventions.NormalizeLabelKey(command.PinKey, "pin_key");
        ValidatePrincipal(command.RequestedBy);
        ValidateExpectedVersion(command.ExpectedPinVersion);
        var mutationId = ContentArtifactConventions.NormalizeRequired(command.MutationId, "mutation_id");
        EnsureActorAddress(scopeId, pinKey);
        var mutationHash = HashMutation(command);
        if (IsReplay(mutationId, mutationHash))
            return;

        if (command.ExpectedPinVersion != State.PinVersion)
        {
            await PersistRejectedAsync(
                scopeId,
                pinKey,
                command.RequestedBy,
                mutationId,
                mutationHash,
                command.RequestedAtUtc);
            return;
        }

        await PersistDomainEventAsync(new ContentArtifactPinClearedEvent
        {
            ScopeId = scopeId,
            PinKey = pinKey,
            RequestedBy = command.RequestedBy.Clone(),
            PinVersion = checked(State.PinVersion + 1),
            MutationId = mutationId,
            MutationHash = mutationHash,
            UpdatedAtUtc = ResolveRequestedAt(command.RequestedAtUtc),
        });
    }

    protected override ContentArtifactPinState TransitionState(ContentArtifactPinState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ContentArtifactPinSetEvent>(ApplySet)
            .On<ContentArtifactPinClearedEvent>(ApplyCleared)
            .On<ContentArtifactPinMutationRejectedEvent>(ApplyRejected)
            .OrCurrent();

    private Task PersistRejectedAsync(
        string scopeId,
        string pinKey,
        ContentArtifactPrincipal requestedBy,
        string mutationId,
        ByteString mutationHash,
        Timestamp? requestedAt) =>
        PersistDomainEventAsync(new ContentArtifactPinMutationRejectedEvent
        {
            ScopeId = scopeId,
            PinKey = pinKey,
            RequestedBy = requestedBy.Clone(),
            MutationId = mutationId,
            MutationHash = mutationHash,
            RejectionCode = ContentArtifactPinRejectionCode.PinVersionConflict,
            RejectedAtUtc = ResolveRequestedAt(requestedAt),
        });

    private bool IsReplay(string mutationId, ByteString mutationHash)
    {
        if (!string.Equals(State.LastMutationId, mutationId, StringComparison.Ordinal))
            return false;
        if (State.LastMutationHash.Equals(mutationHash))
            return true;
        throw new InvalidOperationException(
            $"ContentArtifact pin mutation_id '{mutationId}' was already used for different facts.");
    }

    private void EnsureActorAddress(string scopeId, string pinKey)
    {
        var expectedActorId = ContentArtifactConventions.BuildPinActorId(scopeId, pinKey);
        if (!string.Equals(Id, expectedActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ContentArtifact pin actor '{Id}' does not match canonical identity '{expectedActorId}'.");
        }
        if ((!string.IsNullOrWhiteSpace(State.ScopeId) &&
             !string.Equals(State.ScopeId, scopeId, StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(State.PinKey) &&
             !string.Equals(State.PinKey, pinKey, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("ContentArtifact pin identity is immutable.");
        }
    }

    private static ContentArtifactPinState ApplySet(
        ContentArtifactPinState state,
        ContentArtifactPinSetEvent evt) =>
        new()
        {
            ScopeId = evt.ScopeId,
            PinKey = evt.PinKey,
            PinnedArtifactId = evt.ArtifactId,
            PinnedBy = evt.PinnedBy.Clone(),
            PinVersion = evt.PinVersion,
            UpdatedAtUtc = evt.UpdatedAtUtc?.Clone(),
            LastMutationId = evt.MutationId,
            LastMutationHash = evt.MutationHash,
            LastMutationStatus = ContentArtifactPinMutationStatus.Succeeded,
            LastRejectionCode = ContentArtifactPinRejectionCode.Unspecified,
            LastMutationRequestedBy = evt.PinnedBy.Clone(),
        };

    private static ContentArtifactPinState ApplyCleared(
        ContentArtifactPinState state,
        ContentArtifactPinClearedEvent evt) =>
        new()
        {
            ScopeId = evt.ScopeId,
            PinKey = evt.PinKey,
            PinVersion = evt.PinVersion,
            UpdatedAtUtc = evt.UpdatedAtUtc?.Clone(),
            LastMutationId = evt.MutationId,
            LastMutationHash = evt.MutationHash,
            LastMutationStatus = ContentArtifactPinMutationStatus.Succeeded,
            LastRejectionCode = ContentArtifactPinRejectionCode.Unspecified,
            LastMutationRequestedBy = evt.RequestedBy.Clone(),
        };

    private static ContentArtifactPinState ApplyRejected(
        ContentArtifactPinState state,
        ContentArtifactPinMutationRejectedEvent evt)
    {
        var next = state.Clone();
        next.ScopeId = evt.ScopeId;
        next.PinKey = evt.PinKey;
        next.UpdatedAtUtc = evt.RejectedAtUtc?.Clone();
        next.LastMutationId = evt.MutationId;
        next.LastMutationHash = evt.MutationHash;
        next.LastMutationStatus = ContentArtifactPinMutationStatus.Rejected;
        next.LastRejectionCode = evt.RejectionCode;
        next.LastMutationRequestedBy = evt.RequestedBy.Clone();
        return next;
    }

    private static void ValidatePrincipal(ContentArtifactPrincipal? principal)
    {
        if (principal == null ||
            string.IsNullOrWhiteSpace(principal.PrincipalId) ||
            string.IsNullOrWhiteSpace(principal.PrincipalKind))
        {
            throw new InvalidOperationException("requested_by principal_id and principal_kind are required.");
        }
    }

    private static void ValidateExpectedVersion(long expectedPinVersion)
    {
        if (expectedPinVersion < 0)
            throw new InvalidOperationException("expected_pin_version must be non-negative.");
    }

    private static ByteString HashMutation(SetContentArtifactPinCommand command)
    {
        var semantic = command.Clone();
        semantic.RequestedAtUtc = null;
        return ByteString.CopyFrom(SHA256.HashData(semantic.ToByteArray()));
    }

    private static ByteString HashMutation(ClearContentArtifactPinCommand command)
    {
        var semantic = command.Clone();
        semantic.RequestedAtUtc = null;
        return ByteString.CopyFrom(SHA256.HashData(semantic.ToByteArray()));
    }

    private static Timestamp ResolveRequestedAt(Timestamp? requestedAt) =>
        requestedAt?.Clone() ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
}
