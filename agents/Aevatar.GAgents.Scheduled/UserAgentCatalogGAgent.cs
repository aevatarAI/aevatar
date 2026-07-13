using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

// Refactor (iter1/cluster-001):
//   Old pattern: UserAgentCatalogGAgent owned both catalog membership and per-runner execution summaries.
//   New principle: Catalog actor owns membership only; execution facts remain runner-owned.
[GAgent("scheduled.user-agent-catalog")]
public sealed class UserAgentCatalogGAgent : GAgentBase<UserAgentCatalogState>
{
    public const string WellKnownId = UserAgentCatalogStorageContracts.StoreActorId;
    private const int MaxApiKeyRevocationAttempts = 3;
    private readonly IScheduledAgentCredentialRevocationExecutor _credentialRevocationExecutor;

    public UserAgentCatalogGAgent(IScheduledAgentCredentialRevocationExecutor credentialRevocationExecutor)
    {
        _credentialRevocationExecutor = credentialRevocationExecutor ??
            throw new ArgumentNullException(nameof(credentialRevocationExecutor));
    }

    protected override UserAgentCatalogState TransitionState(UserAgentCatalogState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<UserAgentCatalogUpsertedEvent>(ApplyUpserted)
            .On<UserAgentCatalogTombstonedEvent>(ApplyTombstoned)
            .On<UserAgentCatalogApiKeyRevocationRequestedEvent>(ApplyApiKeyRevocationRequested)
            .On<UserAgentCatalogApiKeyRevocationAttemptRecordedEvent>(ApplyApiKeyRevocationAttemptRecorded)
            .On<UserAgentCatalogCredentialRevocationRepairedEvent>(ApplyCredentialRevocationRepaired)
            .On<UserAgentCatalogTombstonesCompactedEvent>(ApplyTombstonesCompacted)
            .On<UserAgentCatalogSharedEvent>(ApplyShared)
            .On<UserAgentCatalogUnsharedEvent>(ApplyUnshared)
            .OrCurrent();

    [EventHandler]
    public async Task HandleUpsertAsync(UserAgentCatalogUpsertCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.AgentId))
        {
            Logger.LogWarning("Cannot upsert user agent catalog entry with empty agent id");
            return;
        }

        var existing = State.Entries.FirstOrDefault(x => string.Equals(x.AgentId, command.AgentId, StringComparison.Ordinal));
        if (existing is { Tombstoned: false } && !SameOwner(existing, command))
        {
            Logger.LogWarning(
                "Cannot upsert user agent catalog entry owned by another caller: {AgentId}",
                command.AgentId.Trim());
            return;
        }

        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var entry = new UserAgentCatalogEntry
        {
            AgentId = command.AgentId.Trim(),
            ConversationId = MergeNonEmpty(command.ConversationId, existing?.ConversationId),
            NyxProviderSlug = MergeNonEmpty(command.NyxProviderSlug, existing?.NyxProviderSlug),
#pragma warning disable CS0612 // legacy credential field must remain empty on new writes
            NyxApiKey = string.Empty,
#pragma warning restore CS0612
            NyxApiKeyReference = command.NyxApiKeyReference?.Clone() ?? existing?.NyxApiKeyReference?.Clone(),
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            Tombstoned = false,
            AgentType = MergeNonEmpty(command.AgentType, existing?.AgentType),
            TemplateName = MergeNonEmpty(command.TemplateName, existing?.TemplateName),
            ScopeId = MergeNonEmpty(command.ScopeId, existing?.ScopeId),
            ApiKeyId = MergeNonEmpty(command.ApiKeyId, existing?.ApiKeyId),
            ScheduleCron = MergeNonEmpty(command.ScheduleCron, existing?.ScheduleCron),
            ScheduleTimezone = MergeNonEmpty(command.ScheduleTimezone, existing?.ScheduleTimezone),
            LarkReceiveId = MergeNonEmpty(command.LarkReceiveId, existing?.LarkReceiveId),
            LarkReceiveIdType = MergeNonEmpty(command.LarkReceiveIdType, existing?.LarkReceiveIdType),
            LarkReceiveIdFallback = MergeNonEmpty(command.LarkReceiveIdFallback, existing?.LarkReceiveIdFallback),
            LarkReceiveIdTypeFallback = MergeNonEmpty(command.LarkReceiveIdTypeFallback, existing?.LarkReceiveIdTypeFallback),
            SharingGrant = existing?.SharingGrant?.Clone(),
            TargetPlatform = MergeNonEmpty(command.TargetPlatform, existing?.TargetPlatform),
            OutputFormat = command.OutputFormat == SkillRunnerOutputFormat.Auto
                ? existing?.OutputFormat ?? SkillRunnerOutputFormat.Auto
                : command.OutputFormat,
        };

        // Issue #466 critical: copy OwnerScope from the command (or inherit existing on
        // partial upserts from older membership update paths that don't recompute scope).
        // Without this, every catalog row would land with OwnerScope=null and
        // DocumentMatchesCaller would fall through to the legacy backfill path — which
        // returns null for the lark surface, and `/agents` would always be empty.
        // Refactor (iter92/cluster-092):
        //   Old: write path simultaneously emitted deprecated `Platform`/`OwnerNyxUserId`.
        //   New: write path emits only `OwnerScope`; legacy fields are retained only in
        //   the no-`OwnerScope` fallback branch for backwards compatibility.
        var mergedScope = command.OwnerScope ?? existing?.OwnerScope;
        if (mergedScope is not null)
        {
            entry.OwnerScope = mergedScope.Clone();
        }
        else
        {
#pragma warning disable CS0612 // legacy fields persisted only when owner_scope is absent
            entry.Platform = MergeNonEmpty(command.Platform, existing?.Platform);
            entry.OwnerNyxUserId = MergeNonEmpty(command.OwnerNyxUserId, existing?.OwnerNyxUserId);
#pragma warning restore CS0612
        }

        await PersistDomainEventAsync(new UserAgentCatalogUpsertedEvent
        {
            Entry = entry,
        });
    }

    private static bool SameOwner(UserAgentCatalogEntry existing, UserAgentCatalogUpsertCommand command)
    {
        var existingScope = existing.OwnerScope ?? OwnerScope.FromLegacyFields(
#pragma warning disable CS0612 // legacy field read for cross-owner overwrite guard
            existing.OwnerNyxUserId,
            existing.Platform);
#pragma warning restore CS0612

        var commandScope = command.OwnerScope ?? OwnerScope.FromLegacyFields(
#pragma warning disable CS0612 // legacy command shape remains supported
            command.OwnerNyxUserId,
            command.Platform);
#pragma warning restore CS0612

        return existingScope is null || commandScope is null || existingScope.MatchesStrictly(commandScope);
    }

    [EventHandler]
    public async Task HandleTombstoneAsync(UserAgentCatalogTombstoneCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.AgentId))
        {
            Logger.LogWarning("Cannot tombstone user agent catalog entry with empty agent id");
            return;
        }

        if (State.Entries.All(x => !string.Equals(x.AgentId, command.AgentId, StringComparison.Ordinal)))
        {
            Logger.LogWarning("Cannot tombstone missing user agent catalog entry: {AgentId}", command.AgentId);
            return;
        }

        var existing = State.Entries.First(x => string.Equals(x.AgentId, command.AgentId, StringComparison.Ordinal));
        UserAgentApiKeyRevocation? revocation = null;
        if (!existing.Tombstoned && ShouldRequestApiKeyRevocation(existing))
        {
            var requestedRevocation = BuildApiKeyRevocation(existing);
            var currentRevocation = FindRevocationByIdentity(State.PendingApiKeyRevocations, requestedRevocation);
            if (currentRevocation is not null)
            {
                revocation = currentRevocation.Clone();
            }
            else if (HasRevocationAliasConflict(State.PendingApiKeyRevocations, requestedRevocation))
            {
                Logger.LogWarning(
                    "Cannot replace pending credential revocation with an alias: agentId={AgentId} apiKeyId={ApiKeyId} secretReference={SecretReference}",
                    requestedRevocation.AgentId,
                    requestedRevocation.ApiKeyId,
                    GetSecretReferenceRef(requestedRevocation));
            }
            else
            {
                revocation = requestedRevocation;
                await PersistDomainEventAsync(new UserAgentCatalogApiKeyRevocationRequestedEvent
                {
                    Revocation = revocation,
                });
            }
        }

        await PersistDomainEventAsync(new UserAgentCatalogTombstonedEvent
        {
            AgentId = command.AgentId.Trim(),
            TombstoneStateVersion = NextCommittedVersion(),
        });

        if (revocation is not null)
            await _credentialRevocationExecutor.ExecutePendingAsync(command.BearerToken, revocation);
    }

    [EventHandler]
    public async Task HandleRecordApiKeyRevocationAttemptAsync(
        UserAgentCatalogRecordApiKeyRevocationAttemptCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.AgentId) || string.IsNullOrWhiteSpace(command.ApiKeyId))
        {
            Logger.LogWarning("Cannot record API key revocation attempt with empty agent id or API key id");
            return;
        }

        var secretReferenceRef = command.SecretReferenceRef?.Trim() ?? string.Empty;
        var pending = State.PendingApiKeyRevocations.FirstOrDefault(revocation =>
            MatchesRevocationIdentity(
                revocation,
                command.AgentId,
                command.ApiKeyId,
                secretReferenceRef));
        if (pending is null)
        {
            Logger.LogWarning(
                "Cannot record API key revocation attempt without the matching pending revocation: agentId={AgentId} apiKeyId={ApiKeyId} secretReference={SecretReference}",
                command.AgentId.Trim(),
                command.ApiKeyId.Trim(),
                secretReferenceRef);
            return;
        }

        var track = ResolveTrack(pending, command.Track);
        if (track is null ||
            IsTerminal(track) ||
            track.Status == ScheduledCredentialRevocationTrackStatus.BlockedMissingSecretRef)
            return;

        if (!command.Completed && track.AttemptCount >= MaxApiKeyRevocationAttempts)
        {
            Logger.LogWarning(
                "Cannot record API key revocation retry after max attempts: agentId={AgentId} apiKeyId={ApiKeyId}",
                command.AgentId.Trim(),
                command.ApiKeyId.Trim());
            return;
        }

        await PersistDomainEventAsync(new UserAgentCatalogApiKeyRevocationAttemptRecordedEvent
        {
            AgentId = command.AgentId.Trim(),
            ApiKeyId = command.ApiKeyId.Trim(),
            Completed = command.Completed,
            HttpStatus = command.HttpStatus,
            Error = command.Error?.Trim() ?? string.Empty,
            FailureKind = command.Completed
                ? UserAgentApiKeyRevocationFailureKind.None
                : command.FailureKind,
            AttemptedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Track = command.Track,
            SecretReferenceRef = secretReferenceRef,
        });
    }

    [EventHandler]
    public async Task HandleRequestCredentialRevocationAsync(UserAgentCatalogRequestCredentialRevocationCommand command)
    {
        if (command.Revocation is null)
            return;

        var revocation = NormalizeRevocation(command.Revocation);
        if (string.IsNullOrWhiteSpace(revocation.AgentId) || string.IsNullOrWhiteSpace(revocation.ApiKeyId))
            return;

        var currentRevocation = FindRevocationByIdentity(State.PendingApiKeyRevocations, revocation);
        if (currentRevocation is not null)
        {
            await _credentialRevocationExecutor.ExecutePendingAsync(command.BearerToken, currentRevocation.Clone());
            return;
        }

        if (HasRevocationAliasConflict(State.PendingApiKeyRevocations, revocation))
        {
            Logger.LogWarning(
                "Cannot request credential revocation with an aliased identity: agentId={AgentId} apiKeyId={ApiKeyId} secretReference={SecretReference}",
                revocation.AgentId,
                revocation.ApiKeyId,
                GetSecretReferenceRef(revocation));
            return;
        }

        await PersistDomainEventAsync(new UserAgentCatalogApiKeyRevocationRequestedEvent
        {
            Revocation = revocation,
        });
        await _credentialRevocationExecutor.ExecutePendingAsync(command.BearerToken, revocation);
    }

    [EventHandler]
    public async Task HandleRepairCredentialRevocationAsync(UserAgentCatalogRepairCredentialRevocationCommand command)
    {
        var requestId = command.RequestId?.Trim() ?? string.Empty;
        var agentId = command.AgentId?.Trim() ?? string.Empty;
        var apiKeyId = command.ApiKeyId?.Trim() ?? string.Empty;
        var secretSubjectId = command.SecretSubjectId?.Trim() ?? string.Empty;
        var repairReason = command.RepairReason?.Trim() ?? string.Empty;
        var requestedBySubjectId = command.RequestedBySubjectId?.Trim() ?? string.Empty;
        var reference = command.SecretReference;
        if (string.IsNullOrEmpty(agentId) ||
            string.IsNullOrEmpty(apiKeyId) ||
            string.IsNullOrEmpty(requestId) ||
            !string.Equals(apiKeyId, secretSubjectId, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(repairReason) ||
            string.IsNullOrEmpty(requestedBySubjectId) ||
            !IsCompleteReference(reference))
        {
            await PersistRepairRejectedAsync(
                requestId,
                agentId,
                apiKeyId,
                UserAgentCatalogCredentialRevocationRepairRejectionReason.InvalidRequest);
            return;
        }

        var pending = State.PendingApiKeyRevocations.FirstOrDefault(revocation =>
            MatchesRevocationIdentity(revocation, agentId, apiKeyId, string.Empty));
        if (pending?.VaultTrack?.Status != ScheduledCredentialRevocationTrackStatus.BlockedMissingSecretRef)
        {
            await PersistRepairRejectedAsync(
                requestId,
                agentId,
                apiKeyId,
                UserAgentCatalogCredentialRevocationRepairRejectionReason.NotBlocked);
            return;
        }

        var aliasConflict = State.PendingApiKeyRevocations.Any(revocation =>
            !ReferenceEquals(revocation, pending) &&
            (string.Equals(revocation.ApiKeyId, apiKeyId, StringComparison.Ordinal) ||
             string.Equals(GetSecretReferenceRef(revocation), reference.Ref.Trim(), StringComparison.Ordinal)));
        if (aliasConflict)
        {
            await PersistRepairRejectedAsync(
                requestId,
                agentId,
                apiKeyId,
                UserAgentCatalogCredentialRevocationRepairRejectionReason.AliasConflict);
            return;
        }

        await PersistDomainEventAsync(new UserAgentCatalogCredentialRevocationRepairedEvent
        {
            RequestId = requestId,
            AgentId = agentId,
            ApiKeyId = apiKeyId,
            SecretReference = reference.Clone(),
            SecretSubjectId = secretSubjectId,
            RepairReason = repairReason,
            RequestedBySubjectId = requestedBySubjectId,
            RequestedAtUnixMs = command.RequestedAtUnixMs,
        });

        var repaired = State.PendingApiKeyRevocations.First(revocation =>
            MatchesRevocationIdentity(revocation, agentId, apiKeyId, reference.Ref));
        await _credentialRevocationExecutor.ExecutePendingAsync(string.Empty, repaired);
    }

    private Task PersistRepairRejectedAsync(
        string requestId,
        string agentId,
        string apiKeyId,
        UserAgentCatalogCredentialRevocationRepairRejectionReason reason) =>
        PersistDomainEventAsync(new UserAgentCatalogCredentialRevocationRepairRejectedEvent
        {
            RequestId = requestId,
            AgentId = agentId,
            ApiKeyId = apiKeyId,
            Reason = reason,
        });

    [EventHandler]
    public async Task HandleShareAsync(UserAgentCatalogShareCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.AgentId))
        {
            Logger.LogWarning("Cannot share user agent catalog entry with empty agent id");
            return;
        }

        var entry = FindOwnedLiveEntry(command.AgentId, command.OwnerScope);
        if (entry is null)
        {
            Logger.LogWarning("Cannot share missing or non-owned user agent catalog entry: {AgentId}", command.AgentId);
            return;
        }

        if (!UserAgentCatalogSharingAudience.TryBuildKey(entry.OwnerScope, out _))
        {
            Logger.LogWarning("Cannot share user agent catalog entry without a channel owner registration scope: {AgentId}", command.AgentId);
            return;
        }

        await PersistDomainEventAsync(new UserAgentCatalogSharedEvent
        {
            AgentId = entry.AgentId,
            SharingGrant = new ScheduledAgentSharingGrant
            {
                SharedWithRegistrationScope = entry.OwnerScope.RegistrationScopeId.Trim(),
                AllowTrigger = command.AllowTrigger,
                GrantedBy = command.OwnerScope.SenderId ?? string.Empty,
                GrantedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        });
    }

    [EventHandler]
    public async Task HandleUnshareAsync(UserAgentCatalogUnshareCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.AgentId))
        {
            Logger.LogWarning("Cannot unshare user agent catalog entry with empty agent id");
            return;
        }

        var entry = FindOwnedLiveEntry(command.AgentId, command.OwnerScope);
        if (entry is null)
        {
            Logger.LogWarning("Cannot unshare missing or non-owned user agent catalog entry: {AgentId}", command.AgentId);
            return;
        }

        if (entry.SharingGrant is null)
            return;

        await PersistDomainEventAsync(new UserAgentCatalogUnsharedEvent
        {
            AgentId = entry.AgentId,
        });
    }

    [EventHandler]
    public async Task HandleCompactTombstonesAsync(UserAgentCatalogCompactTombstonesCommand command)
    {
        if (command.SafeStateVersion <= 0)
            return;

        var agentIds = State.Entries
            .Where(static entry => entry.Tombstoned)
            .Where(entry => entry.TombstoneStateVersion > 0 && entry.TombstoneStateVersion <= command.SafeStateVersion)
            .Select(static entry => entry.AgentId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (agentIds.Length == 0)
            return;

        await PersistDomainEventAsync(new UserAgentCatalogTombstonesCompactedEvent
        {
            AgentIds = { agentIds },
            SafeStateVersion = command.SafeStateVersion,
        });
    }

    private static UserAgentCatalogState ApplyUpserted(UserAgentCatalogState current, UserAgentCatalogUpsertedEvent evt)
    {
        var next = current.Clone();
        var existing = next.Entries.FirstOrDefault(x => string.Equals(x.AgentId, evt.Entry.AgentId, StringComparison.Ordinal));
        if (existing != null)
            next.Entries.Remove(existing);

        var entry = evt.Entry.Clone();
        entry.Tombstoned = false;
        entry.TombstoneStateVersion = 0;
        next.Entries.Add(entry);
        return next;
    }

    private static UserAgentCatalogState ApplyTombstoned(UserAgentCatalogState current, UserAgentCatalogTombstonedEvent evt)
    {
        var next = current.Clone();
        var existing = next.Entries.FirstOrDefault(x => string.Equals(x.AgentId, evt.AgentId, StringComparison.Ordinal));
        if (existing == null)
            return next;

        existing.Tombstoned = true;
        existing.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        existing.TombstoneStateVersion = evt.TombstoneStateVersion;
        return next;
    }

    private static UserAgentCatalogState ApplyApiKeyRevocationRequested(
        UserAgentCatalogState current,
        UserAgentCatalogApiKeyRevocationRequestedEvent evt)
    {
        if (evt.Revocation is null ||
            string.IsNullOrWhiteSpace(evt.Revocation.AgentId) ||
            string.IsNullOrWhiteSpace(evt.Revocation.ApiKeyId))
        {
            return current;
        }

        var next = current.Clone();
        var requested = NormalizeRevocation(evt.Revocation);
        if (FindRevocationByIdentity(next.PendingApiKeyRevocations, requested) is not null ||
            HasRevocationAliasConflict(next.PendingApiKeyRevocations, requested))
        {
            return next;
        }

        next.PendingApiKeyRevocations.Add(requested);
        return next;
    }

    private static UserAgentCatalogState ApplyApiKeyRevocationAttemptRecorded(
        UserAgentCatalogState current,
        UserAgentCatalogApiKeyRevocationAttemptRecordedEvent evt)
    {
        var next = current.Clone();
        var existing = next.PendingApiKeyRevocations.FirstOrDefault(revocation =>
            MatchesRevocationIdentity(
                revocation,
                evt.AgentId,
                evt.ApiKeyId,
                evt.SecretReferenceRef));
        if (existing is null)
            return current;

        var track = ResolveTrack(existing, evt.Track);
        if (track is null || track.Status == ScheduledCredentialRevocationTrackStatus.BlockedMissingSecretRef)
            return next;

        track.AttemptCount++;
        track.LastAttemptAt = evt.AttemptedAt?.Clone();
        track.LastHttpStatus = evt.HttpStatus;
        track.LastError = evt.Error ?? string.Empty;
        track.FailureKind = evt.FailureKind;
        track.Status = evt.Completed
            ? ScheduledCredentialRevocationTrackStatus.Completed
            : ScheduledCredentialRevocationTrackStatus.Pending;

        existing.AttemptCount = existing.NyxIdTrack?.AttemptCount ?? 0;
        existing.LastAttemptAt = existing.NyxIdTrack?.LastAttemptAt?.Clone();
        existing.LastHttpStatus = existing.NyxIdTrack?.LastHttpStatus ?? 0;
        existing.LastError = existing.NyxIdTrack?.LastError ?? string.Empty;
        existing.FailureKind = existing.NyxIdTrack?.FailureKind ?? UserAgentApiKeyRevocationFailureKind.Unspecified;
        if (IsTerminal(existing.NyxIdTrack) && IsTerminal(existing.VaultTrack))
            next.PendingApiKeyRevocations.Remove(existing);
        return next;
    }

    private static UserAgentCatalogState ApplyCredentialRevocationRepaired(
        UserAgentCatalogState current,
        UserAgentCatalogCredentialRevocationRepairedEvent evt)
    {
        var next = current.Clone();
        var existing = next.PendingApiKeyRevocations.FirstOrDefault(revocation =>
            MatchesRevocationIdentity(revocation, evt.AgentId, evt.ApiKeyId, string.Empty));
        if (existing is null)
            return next;

        existing.NyxApiKeyReference = evt.SecretReference?.Clone();
        existing.SecretSubjectId = evt.SecretSubjectId ?? string.Empty;
        existing.RepairReason = evt.RepairReason ?? string.Empty;
        existing.RequestedBySubjectId = evt.RequestedBySubjectId ?? string.Empty;
        existing.RequestedAtUnixMs = evt.RequestedAtUnixMs;
        existing.VaultTrack = new ScheduledCredentialRevocationTrack
        {
            Status = ScheduledCredentialRevocationTrackStatus.Pending,
            FailureKind = UserAgentApiKeyRevocationFailureKind.Unspecified,
        };
        return next;
    }

    private static UserAgentCatalogState ApplyTombstonesCompacted(
        UserAgentCatalogState current,
        UserAgentCatalogTombstonesCompactedEvent evt)
    {
        if (evt.AgentIds.Count == 0)
            return current;

        var next = current.Clone();
        var compacted = evt.AgentIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var removable = next.Entries
            .Where(entry => compacted.Contains(entry.AgentId))
            .ToArray();
        foreach (var entry in removable)
            next.Entries.Remove(entry);
        return next;
    }

    private static UserAgentCatalogState ApplyShared(UserAgentCatalogState current, UserAgentCatalogSharedEvent evt)
    {
        var next = current.Clone();
        var existing = next.Entries.FirstOrDefault(x => string.Equals(x.AgentId, evt.AgentId, StringComparison.Ordinal));
        if (existing is null || existing.Tombstoned)
            return next;

        existing.SharingGrant = evt.SharingGrant?.Clone();
        existing.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        return next;
    }

    private static UserAgentCatalogState ApplyUnshared(UserAgentCatalogState current, UserAgentCatalogUnsharedEvent evt)
    {
        var next = current.Clone();
        var existing = next.Entries.FirstOrDefault(x => string.Equals(x.AgentId, evt.AgentId, StringComparison.Ordinal));
        if (existing is null)
            return next;

        existing.SharingGrant = null;
        existing.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        return next;
    }

    private UserAgentCatalogEntry? FindOwnedLiveEntry(string agentId, OwnerScope? ownerScope)
    {
        if (ownerScope is null)
            return null;

        var normalizedAgentId = agentId.Trim();
        return State.Entries.FirstOrDefault(entry =>
            !entry.Tombstoned &&
            string.Equals(entry.AgentId, normalizedAgentId, StringComparison.Ordinal) &&
            ownerScope.MatchesStrictly(entry.OwnerScope));
    }

    private static bool ShouldRequestApiKeyRevocation(UserAgentCatalogEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.ApiKeyId);

    private static UserAgentApiKeyRevocation BuildApiKeyRevocation(UserAgentCatalogEntry entry)
    {
        var revocation = new UserAgentApiKeyRevocation
        {
            AgentId = entry.AgentId.Trim(),
            ApiKeyId = entry.ApiKeyId.Trim(),
            RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            FailureKind = UserAgentApiKeyRevocationFailureKind.Unspecified,
            SecretSubjectId = entry.ApiKeyId.Trim(),
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            NyxIdTrack = new ScheduledCredentialRevocationTrack
            {
                Status = ScheduledCredentialRevocationTrackStatus.Pending,
            },
            VaultTrack = new ScheduledCredentialRevocationTrack
            {
                Status = entry.NyxApiKeyReference is null || string.IsNullOrWhiteSpace(entry.NyxApiKeyReference.Ref)
                    ? ScheduledCredentialRevocationTrackStatus.BlockedMissingSecretRef
                    : ScheduledCredentialRevocationTrackStatus.Pending,
            },
        };

        if (entry.NyxApiKeyReference is not null)
            revocation.NyxApiKeyReference = entry.NyxApiKeyReference.Clone();
        if (entry.OwnerScope is not null)
            revocation.OwnerScope = entry.OwnerScope.Clone();

        return revocation;
    }

    private static UserAgentApiKeyRevocation NormalizeRevocation(UserAgentApiKeyRevocation source)
    {
        var revocation = source.Clone();
        revocation.AgentId = revocation.AgentId?.Trim() ?? string.Empty;
        revocation.ApiKeyId = revocation.ApiKeyId?.Trim() ?? string.Empty;
        revocation.SecretSubjectId = string.IsNullOrWhiteSpace(revocation.SecretSubjectId)
            ? revocation.ApiKeyId
            : revocation.SecretSubjectId.Trim();
        revocation.NyxIdTrack ??= new ScheduledCredentialRevocationTrack
        {
            Status = ScheduledCredentialRevocationTrackStatus.Pending,
            AttemptCount = revocation.AttemptCount,
            LastAttemptAt = revocation.LastAttemptAt?.Clone(),
            LastHttpStatus = revocation.LastHttpStatus,
            LastError = revocation.LastError ?? string.Empty,
            FailureKind = revocation.FailureKind,
        };
        revocation.VaultTrack ??= new ScheduledCredentialRevocationTrack
        {
            Status = IsCompleteReference(revocation.NyxApiKeyReference)
                ? ScheduledCredentialRevocationTrackStatus.Pending
                : ScheduledCredentialRevocationTrackStatus.BlockedMissingSecretRef,
        };
        if (!IsCompleteReference(revocation.NyxApiKeyReference) &&
            revocation.VaultTrack.Status == ScheduledCredentialRevocationTrackStatus.Pending)
        {
            revocation.VaultTrack.Status = ScheduledCredentialRevocationTrackStatus.BlockedMissingSecretRef;
        }
        return revocation;
    }

    private static UserAgentApiKeyRevocation? FindRevocationByIdentity(
        IEnumerable<UserAgentApiKeyRevocation> revocations,
        UserAgentApiKeyRevocation candidate) =>
        revocations.FirstOrDefault(revocation =>
            MatchesRevocationIdentity(
                revocation,
                candidate.AgentId,
                candidate.ApiKeyId,
                GetSecretReferenceRef(candidate)));

    private static bool HasRevocationAliasConflict(
        IEnumerable<UserAgentApiKeyRevocation> revocations,
        UserAgentApiKeyRevocation candidate)
    {
        var candidateReference = GetSecretReferenceRef(candidate);
        return revocations.Any(revocation =>
            string.Equals(revocation.ApiKeyId, candidate.ApiKeyId, StringComparison.Ordinal) ||
            (!string.IsNullOrEmpty(candidateReference) &&
             string.Equals(GetSecretReferenceRef(revocation), candidateReference, StringComparison.Ordinal)));
    }

    private static bool MatchesRevocationIdentity(
        UserAgentApiKeyRevocation revocation,
        string agentId,
        string apiKeyId,
        string secretReferenceRef) =>
        string.Equals(revocation.AgentId, agentId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(revocation.ApiKeyId, apiKeyId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(
            GetSecretReferenceRef(revocation),
            secretReferenceRef?.Trim() ?? string.Empty,
            StringComparison.Ordinal);

    private static string GetSecretReferenceRef(UserAgentApiKeyRevocation revocation) =>
        revocation.NyxApiKeyReference?.Ref?.Trim() ?? string.Empty;

    private static ScheduledCredentialRevocationTrack? ResolveTrack(
        UserAgentApiKeyRevocation revocation,
        UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track track) =>
        track switch
        {
            UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId => revocation.NyxIdTrack,
            UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.Vault => revocation.VaultTrack,
            _ => null,
        };

    private static bool IsTerminal(ScheduledCredentialRevocationTrack? track) =>
        track?.Status is ScheduledCredentialRevocationTrackStatus.Completed or
            ScheduledCredentialRevocationTrackStatus.NotApplicable;

    private static bool IsCompleteReference(Aevatar.Foundation.Abstractions.Credentials.SecretReference? reference) =>
        reference is not null &&
        !string.IsNullOrWhiteSpace(reference.Ref) &&
        !string.IsNullOrWhiteSpace(reference.Purpose) &&
        !string.IsNullOrWhiteSpace(reference.OwnerScopeKey) &&
        reference.Version > 0 &&
        !string.IsNullOrWhiteSpace(reference.Fingerprint);

    private long NextCommittedVersion() =>
        (EventSourcing ?? throw new InvalidOperationException("Event sourcing must be configured before computing the next committed version."))
        .CurrentVersion + 1;

    private static string MergeNonEmpty(string? incoming, string? existing)
    {
        var normalizedIncoming = (incoming ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(normalizedIncoming)
            ? normalizedIncoming
            : (existing ?? string.Empty);
    }
}
