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

    protected override UserAgentCatalogState TransitionState(UserAgentCatalogState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<UserAgentCatalogUpsertedEvent>(ApplyUpserted)
            .On<UserAgentCatalogTombstonedEvent>(ApplyTombstoned)
            .On<UserAgentCatalogApiKeyRevocationRequestedEvent>(ApplyApiKeyRevocationRequested)
            .On<UserAgentCatalogApiKeyRevocationAttemptRecordedEvent>(ApplyApiKeyRevocationAttemptRecorded)
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
            SharingGrant = existing?.SharingGrant?.Clone(),
            TargetPlatform = MergeNonEmpty(command.TargetPlatform, existing?.TargetPlatform),
            ChannelAddress = UserAgentCatalogChannelAddress.Merge(
                command.ChannelAddress,
                existing?.ChannelAddress,
                command.TargetPlatform,
                command.NyxProviderSlug,
                command.ConversationId,
#pragma warning disable CS0612 // deprecated command fields are read only as a channel_address compatibility bridge
                command.LarkReceiveId,
                command.LarkReceiveIdType,
                command.LarkReceiveIdFallback,
                command.LarkReceiveIdTypeFallback,
#pragma warning restore CS0612
#pragma warning disable CS0612 // deprecated state fields are read only as a channel_address compatibility bridge
                existing?.LarkReceiveId,
                existing?.LarkReceiveIdType,
                existing?.LarkReceiveIdFallback,
                existing?.LarkReceiveIdTypeFallback),
#pragma warning restore CS0612
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
        if (!existing.Tombstoned && ShouldRequestApiKeyRevocation(existing))
        {
            await PersistDomainEventAsync(new UserAgentCatalogApiKeyRevocationRequestedEvent
            {
                Revocation = BuildApiKeyRevocation(existing),
            });
        }

        await PersistDomainEventAsync(new UserAgentCatalogTombstonedEvent
        {
            AgentId = command.AgentId.Trim(),
            TombstoneStateVersion = NextCommittedVersion(),
        });
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

        var pending = State.PendingApiKeyRevocations.FirstOrDefault(revocation =>
            string.Equals(revocation.AgentId, command.AgentId.Trim(), StringComparison.Ordinal) &&
            string.Equals(revocation.ApiKeyId, command.ApiKeyId.Trim(), StringComparison.Ordinal));
        if (pending is null)
        {
            Logger.LogWarning(
                "Cannot record API key revocation attempt without a pending revocation: agentId={AgentId} apiKeyId={ApiKeyId}",
                command.AgentId.Trim(),
                command.ApiKeyId.Trim());
            return;
        }

        if (!command.Completed && pending.AttemptCount >= MaxApiKeyRevocationAttempts)
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
        });
    }

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
        var existing = next.PendingApiKeyRevocations.FirstOrDefault(revocation =>
            string.Equals(revocation.AgentId, evt.Revocation.AgentId, StringComparison.Ordinal) &&
            string.Equals(revocation.ApiKeyId, evt.Revocation.ApiKeyId, StringComparison.Ordinal));
        if (existing is not null)
            next.PendingApiKeyRevocations.Remove(existing);

        next.PendingApiKeyRevocations.Add(evt.Revocation.Clone());
        return next;
    }

    private static UserAgentCatalogState ApplyApiKeyRevocationAttemptRecorded(
        UserAgentCatalogState current,
        UserAgentCatalogApiKeyRevocationAttemptRecordedEvent evt)
    {
        var next = current.Clone();
        var existing = next.PendingApiKeyRevocations.FirstOrDefault(revocation =>
            string.Equals(revocation.AgentId, evt.AgentId, StringComparison.Ordinal) &&
            string.Equals(revocation.ApiKeyId, evt.ApiKeyId, StringComparison.Ordinal));
        if (existing is null)
            return current;

        if (evt.Completed)
        {
            next.PendingApiKeyRevocations.Remove(existing);
            return next;
        }

        existing.AttemptCount++;
        existing.LastAttemptAt = evt.AttemptedAt?.Clone();
        existing.LastHttpStatus = evt.HttpStatus;
        existing.LastError = evt.Error ?? string.Empty;
        existing.FailureKind = evt.FailureKind;
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
        };

        if (entry.NyxApiKeyReference is not null)
            revocation.NyxApiKeyReference = entry.NyxApiKeyReference.Clone();
        if (entry.OwnerScope is not null)
            revocation.OwnerScope = entry.OwnerScope.Clone();

        return revocation;
    }

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
