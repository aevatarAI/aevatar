using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Actor-backed channel bot registration store.
/// State is event-sourced and persisted in the cluster event store — no local
/// filesystem dependency. Suitable for cloud deployment.
///
/// Actor ID convention: a single well-known instance "channel-bot-registration-store".
/// CLAUDE.md: "long-lived actor for fact owners: definition/catalog/manager/index"
/// </summary>
[GAgent("channel.runtime.channel-bot-registration")]
public sealed class ChannelBotRegistrationGAgent : GAgentBase<ChannelBotRegistrationStoreState>
{
    // Refactor (iter27/cluster-003-channel-registration-scope-backfill):
    //   Old pattern: live scope repair commands patched registrations from readmodel-derived backfill candidates.
    //   New principle: delete live repair/backfill command paths; keep ChannelBotScopeIdRepairedEvent state transition for committed event replay.
    public const string WellKnownId = "channel-bot-registration-store";

    protected override ChannelBotRegistrationStoreState TransitionState(ChannelBotRegistrationStoreState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ChannelBotRegisteredEvent>(ApplyRegistered)
            .On<ChannelBotRegistrationRejectedEvent>(static (state, _) => state)
            .On<ChannelBotScopeIdRepairedEvent>(ApplyScopeIdRepaired)
            .On<ChannelBotUnregisteredEvent>(ApplyUnregistered)
            .On<ChannelBotInboundObservedEvent>(ApplyInboundObserved)
            .On<ChannelBotTombstonesCompactedEvent>(ApplyTombstonesCompacted)
            .On<ChannelBotWorkflowResultDeliveryRepairRequestedEvent>(ApplyWorkflowResultDeliveryRepairRequested)
            .On<ChannelBotWorkflowResultDeliveryRepairPreparedEvent>(ApplyWorkflowResultDeliveryRepairPrepared)
            .On<ChannelBotWorkflowResultDeliveryRepairCompletedEvent>(ApplyWorkflowResultDeliveryRepairCompleted)
            .On<ChannelBotWorkflowResultDeliveryRepairFailedEvent>(ApplyWorkflowResultDeliveryRepairFailed)
            .On<ChannelBotWorkflowResultDeliveryRepairRejectedEvent>(static (state, _) => state)
            .OrCurrent();

    // ─── Commands ───

    /// <summary>
    /// Platforms whose registrations are allowed to land in the local mirror. Aligned with the
    /// set of <c>INyxChannelBotProvisioningService</c> registered on the supported production
    /// contract. Anything outside this set is treated as a retired direct-callback dispatch and
    /// dropped without persistence so legacy producers cannot resurface old wire shapes.
    /// </summary>
    private static readonly HashSet<string> SupportedPlatforms =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "lark",
            "telegram",
        };

    [EventHandler]
    public async Task HandleRegister(ChannelBotRegisterCommand cmd)
    {
        if (!SupportedPlatforms.Contains(cmd.Platform ?? string.Empty))
        {
            Logger.LogWarning(
                "Ignoring registration request for unsupported platform: platform={Platform}, requestedId={RequestedId}",
                cmd.Platform,
                cmd.RequestedId);
            return;
        }

        if (string.IsNullOrWhiteSpace(cmd.ScopeId))
        {
            // Elevated to LogError so log-based metrics surface upstream
            // contract breaks; persisted as a domain event so the audit trail
            // captures the rejection without polluting the registration set.
            Logger.LogError(
                "Rejecting channel bot registration without scope id: platform={Platform}, requestedId={RequestedId}, apiKeyId={ApiKeyId}",
                cmd.Platform,
                cmd.RequestedId,
                cmd.NyxAgentApiKeyId);
            await PersistDomainEventAsync(new ChannelBotRegistrationRejectedEvent
            {
                Reason = "missing_scope_id",
                Platform = cmd.Platform ?? string.Empty,
                RequestedId = cmd.RequestedId ?? string.Empty,
                NyxAgentApiKeyId = cmd.NyxAgentApiKeyId ?? string.Empty,
                RejectedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });
            return;
        }

        var entry = new ChannelBotRegistrationEntry
        {
            Id = !string.IsNullOrWhiteSpace(cmd.RequestedId) ? cmd.RequestedId : Guid.NewGuid().ToString("N"),
            Platform = cmd.Platform,
            NyxProviderSlug = cmd.NyxProviderSlug,
            ScopeId = cmd.ScopeId.Trim(),
            WebhookUrl = cmd.WebhookUrl,
            NyxChannelBotId = cmd.NyxChannelBotId ?? string.Empty,
            NyxAgentApiKeyId = cmd.NyxAgentApiKeyId ?? string.Empty,
            NyxConversationRouteId = cmd.NyxConversationRouteId ?? string.Empty,
            WorkflowResultDeliveryCredential = cmd.WorkflowResultDeliveryCredential?.Clone(),
            // Canonical skill-name form matches SkillInvocationTriggerParser output
            // (lowercase, no leading trigger token) so inbound routing compares 1:1.
            DefaultSkillName = (cmd.DefaultSkillName ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant(),
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await PersistDomainEventAsync(new ChannelBotRegisteredEvent { Entry = entry });
        Logger.LogInformation("Registered channel bot: id={Id}, platform={Platform}, slug={Slug}",
            entry.Id, entry.Platform, entry.NyxProviderSlug);
    }

    [EventHandler]
    public async Task HandleUnregister(ChannelBotUnregisterCommand cmd)
    {
        var entry = State.Registrations.FirstOrDefault(r => r.Id == cmd.RegistrationId);
        if (entry is null || entry.Tombstoned)
        {
            Logger.LogWarning("Cannot unregister: channel bot registration not found: {Id}", cmd.RegistrationId);
            return;
        }

        await PersistDomainEventAsync(new ChannelBotUnregisteredEvent
        {
            RegistrationId = cmd.RegistrationId,
            TombstoneStateVersion = NextCommittedVersion(),
        });
        Logger.LogInformation("Unregistered channel bot: id={Id}", cmd.RegistrationId);
    }

    [EventHandler]
    public async Task HandleRecordInbound(ChannelBotRecordInboundCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.RegistrationId))
            return;

        var entry = State.Registrations.FirstOrDefault(r => r.Id == cmd.RegistrationId);
        if (entry is null || entry.Tombstoned)
            return;

        // Activation marker: set once on the first verified inbound. Deliberately NOT
        // refreshed on every message — this is a single store actor, so a per-message
        // event would grow its log unboundedly (CLAUDE.md: no needless EventStore growth).
        if (entry.LastInboundAtUtc is not null)
            return;

        await PersistDomainEventAsync(new ChannelBotInboundObservedEvent
        {
            RegistrationId = cmd.RegistrationId,
            ObservedAtUtc = cmd.ObservedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        Logger.LogInformation("Channel bot activated by first verified inbound: id={Id}", cmd.RegistrationId);
    }

    [EventHandler]
    public async Task HandleCompactTombstones(ChannelBotCompactTombstonesCommand cmd)
    {
        if (cmd.SafeStateVersion <= 0)
            return;

        var registrationIds = State.Registrations
            .Where(static entry => entry.Tombstoned)
            .Where(entry => entry.TombstoneStateVersion > 0 && entry.TombstoneStateVersion <= cmd.SafeStateVersion)
            .Select(static entry => entry.Id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (registrationIds.Length == 0)
            return;

        await PersistDomainEventAsync(new ChannelBotTombstonesCompactedEvent
        {
            RegistrationIds = { registrationIds },
            SafeStateVersion = cmd.SafeStateVersion,
        });
    }

    [EventHandler]
    public async Task HandleWorkflowResultDeliveryRepairRequest(
        ChannelBotWorkflowResultDeliveryRepairRequestCommand cmd)
    {
        var registrationId = Normalize(cmd.RegistrationId);
        var requestId = Normalize(cmd.RequestId);
        var entry = FindActiveRegistration(registrationId);
        var invalidReason = ValidateRequest(entry, cmd);
        if (invalidReason != ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified)
        {
            await PersistRepairRejectedAsync(
                registrationId,
                requestId,
                ChannelWorkflowResultDeliveryRepairPhase.RequestAdmission,
                invalidReason,
                cmd.RequestedAtUnixMs);
            return;
        }

        if (entry!.WorkflowResultDeliveryRepair is { } existing)
        {
            if (!string.Equals(existing.RequestId, requestId, StringComparison.Ordinal) ||
                !SameRequestFacts(existing, cmd))
            {
                await PersistRepairRejectedAsync(
                    registrationId,
                    requestId,
                    ChannelWorkflowResultDeliveryRepairPhase.RequestAdmission,
                    ChannelWorkflowResultDeliveryRepairFailureReason.RequestConflict,
                    cmd.RequestedAtUnixMs);
                return;
            }

            await PersistDomainEventAsync(new ChannelBotWorkflowResultDeliveryRepairRequestedEvent
            {
                RegistrationId = registrationId,
                Repair = existing.Clone(),
            });
            return;
        }

        await PersistDomainEventAsync(new ChannelBotWorkflowResultDeliveryRepairRequestedEvent
        {
            RegistrationId = registrationId,
            Repair = new ChannelWorkflowResultDeliveryRepairState
            {
                RequestId = requestId,
                Status = ChannelWorkflowResultDeliveryRepairStatus.Requested,
                ExpectedApiKeyId = Normalize(cmd.ExpectedApiKeyId),
                ExpectedConversationRouteId = Normalize(cmd.ExpectedConversationRouteId),
                RequestedBySubjectId = Normalize(cmd.RequestedBySubjectId),
                RequestedAtUnixMs = cmd.RequestedAtUnixMs,
                UpdatedAtUnixMs = cmd.RequestedAtUnixMs,
            },
        });
    }

    [EventHandler]
    public async Task HandleWorkflowResultDeliveryRepairPrepare(
        ChannelBotWorkflowResultDeliveryRepairPrepareCommand cmd)
    {
        var registrationId = Normalize(cmd.RegistrationId);
        var requestId = Normalize(cmd.RequestId);
        var entry = FindActiveRegistration(registrationId);
        var invalidReason = ValidatePhaseCommand(
            entry,
            requestId,
            cmd.ExpectedApiKeyId,
            cmd.UpdatedAtUnixMs);
        if (invalidReason == ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified &&
            (string.IsNullOrWhiteSpace(cmd.RotatedApiKeyId) ||
             string.Equals(cmd.RotatedApiKeyId.Trim(), cmd.ExpectedApiKeyId.Trim(), StringComparison.Ordinal) ||
             !IsPreparedReferenceUsable(entry!, cmd.PreparedSecretReference)))
        {
            invalidReason = ChannelWorkflowResultDeliveryRepairFailureReason.InvalidRequest;
        }

        var repair = entry?.WorkflowResultDeliveryRepair;
        if (invalidReason == ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified &&
            repair is not null &&
            repair.Status == ChannelWorkflowResultDeliveryRepairStatus.CredentialPrepared)
        {
            if (SamePreparedFacts(repair, cmd.RotatedApiKeyId, cmd.PreparedSecretReference))
            {
                await PersistDomainEventAsync(new ChannelBotWorkflowResultDeliveryRepairPreparedEvent
                {
                    RegistrationId = registrationId,
                    Repair = repair.Clone(),
                });
                return;
            }

            invalidReason = ChannelWorkflowResultDeliveryRepairFailureReason.RequestConflict;
        }

        if (invalidReason == ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified &&
            repair is not null &&
            repair.Status == ChannelWorkflowResultDeliveryRepairStatus.Failed &&
            repair.PreparedSecretReference is not null)
        {
            invalidReason = ChannelWorkflowResultDeliveryRepairFailureReason.RequestConflict;
        }

        if (invalidReason != ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified)
        {
            await PersistRepairRejectedAsync(
                registrationId,
                requestId,
                ChannelWorkflowResultDeliveryRepairPhase.CredentialPreparation,
                invalidReason,
                cmd.UpdatedAtUnixMs);
            return;
        }

        var prepared = repair!.Clone();
        prepared.Status = ChannelWorkflowResultDeliveryRepairStatus.CredentialPrepared;
        prepared.RotatedApiKeyId = Normalize(cmd.RotatedApiKeyId);
        prepared.PreparedSecretReference = cmd.PreparedSecretReference.Clone();
        prepared.FailurePhase = ChannelWorkflowResultDeliveryRepairPhase.Unspecified;
        prepared.FailureReason = ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified;
        prepared.UpdatedAtUnixMs = cmd.UpdatedAtUnixMs;
        await PersistDomainEventAsync(new ChannelBotWorkflowResultDeliveryRepairPreparedEvent
        {
            RegistrationId = registrationId,
            Repair = prepared,
        });
    }

    [EventHandler]
    public async Task HandleWorkflowResultDeliveryRepairComplete(
        ChannelBotWorkflowResultDeliveryRepairCompleteCommand cmd)
    {
        var registrationId = Normalize(cmd.RegistrationId);
        var requestId = Normalize(cmd.RequestId);
        var entry = FindActiveRegistration(registrationId);

        if (entry is not null &&
            entry.WorkflowResultDeliveryRepair is null &&
            string.Equals(entry.NyxAgentApiKeyId, Normalize(cmd.RotatedApiKeyId), StringComparison.Ordinal) &&
            Equals(entry.WorkflowResultDeliveryCredential, cmd.PreparedSecretReference) &&
            IsPreparedReferenceUsable(entry, cmd.PreparedSecretReference))
        {
            await PersistDomainEventAsync(CreateCompletedEvent(registrationId, requestId, cmd));
            return;
        }

        var invalidReason = ValidatePhaseCommand(
            entry,
            requestId,
            cmd.ExpectedApiKeyId,
            cmd.UpdatedAtUnixMs);
        var repair = entry?.WorkflowResultDeliveryRepair;
        if (invalidReason == ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified &&
            (repair is null ||
             repair.Status is not (ChannelWorkflowResultDeliveryRepairStatus.CredentialPrepared or
                 ChannelWorkflowResultDeliveryRepairStatus.Failed) ||
             !SamePreparedFacts(repair, cmd.RotatedApiKeyId, cmd.PreparedSecretReference) ||
             !IsPreparedReferenceUsable(entry!, cmd.PreparedSecretReference)))
        {
            invalidReason = ChannelWorkflowResultDeliveryRepairFailureReason.InvalidRequest;
        }

        if (invalidReason != ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified)
        {
            await PersistRepairRejectedAsync(
                registrationId,
                requestId,
                ChannelWorkflowResultDeliveryRepairPhase.ActorCompletion,
                invalidReason,
                cmd.UpdatedAtUnixMs);
            return;
        }

        await PersistDomainEventAsync(CreateCompletedEvent(registrationId, requestId, cmd));
    }

    [EventHandler]
    public async Task HandleWorkflowResultDeliveryRepairFail(
        ChannelBotWorkflowResultDeliveryRepairFailCommand cmd)
    {
        var registrationId = Normalize(cmd.RegistrationId);
        var requestId = Normalize(cmd.RequestId);
        var entry = FindActiveRegistration(registrationId);
        var invalidReason = ValidatePhaseCommand(
            entry,
            requestId,
            cmd.ExpectedApiKeyId,
            cmd.UpdatedAtUnixMs);
        var repair = entry?.WorkflowResultDeliveryRepair;
        if (invalidReason == ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified &&
            (cmd.FailurePhase == ChannelWorkflowResultDeliveryRepairPhase.Unspecified ||
             cmd.FailureReason == ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified))
        {
            invalidReason = ChannelWorkflowResultDeliveryRepairFailureReason.InvalidRequest;
        }

        if (invalidReason == ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified &&
            repair is not null &&
            repair.Status == ChannelWorkflowResultDeliveryRepairStatus.Failed &&
            SameFailureFacts(repair, cmd))
        {
            await PersistDomainEventAsync(new ChannelBotWorkflowResultDeliveryRepairFailedEvent
            {
                RegistrationId = registrationId,
                Repair = repair.Clone(),
            });
            return;
        }

        if (invalidReason == ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified &&
            repair?.PreparedSecretReference is not null &&
            !SamePreparedFacts(repair, cmd.RotatedApiKeyId, cmd.PreparedSecretReference))
        {
            invalidReason = ChannelWorkflowResultDeliveryRepairFailureReason.RequestConflict;
        }

        if (invalidReason == ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified &&
            cmd.PreparedSecretReference is not null &&
            !IsPreparedReferenceUsable(entry!, cmd.PreparedSecretReference))
        {
            invalidReason = ChannelWorkflowResultDeliveryRepairFailureReason.InvalidRequest;
        }

        if (invalidReason != ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified)
        {
            await PersistRepairRejectedAsync(
                registrationId,
                requestId,
                cmd.FailurePhase,
                invalidReason,
                cmd.UpdatedAtUnixMs);
            return;
        }

        var failed = repair!.Clone();
        failed.Status = ChannelWorkflowResultDeliveryRepairStatus.Failed;
        if (!string.IsNullOrWhiteSpace(cmd.RotatedApiKeyId))
            failed.RotatedApiKeyId = cmd.RotatedApiKeyId.Trim();
        if (cmd.PreparedSecretReference is not null)
            failed.PreparedSecretReference = cmd.PreparedSecretReference.Clone();
        failed.FailurePhase = cmd.FailurePhase;
        failed.FailureReason = cmd.FailureReason;
        failed.UpdatedAtUnixMs = cmd.UpdatedAtUnixMs;
        await PersistDomainEventAsync(new ChannelBotWorkflowResultDeliveryRepairFailedEvent
        {
            RegistrationId = registrationId,
            Repair = failed,
        });
    }

    // ─── State transitions ───

    private static ChannelBotRegistrationStoreState ApplyRegistered(ChannelBotRegistrationStoreState current, ChannelBotRegisteredEvent evt)
    {
        var next = current.Clone();
        var existing = next.Registrations.FirstOrDefault(r => r.Id == evt.Entry.Id);
        if (existing is not null)
            next.Registrations.Remove(existing);
        var entry = evt.Entry.Clone();
        entry.Tombstoned = false;
        entry.TombstoneStateVersion = 0;
        next.Registrations.Add(entry);
        return next;
    }

    // Repair-only transition: rewrites the scope id of an existing entry while
    // preserving created_at and the rest of the registration shape.
    private static ChannelBotRegistrationStoreState ApplyScopeIdRepaired(
        ChannelBotRegistrationStoreState current,
        ChannelBotScopeIdRepairedEvent evt)
    {
        var next = current.Clone();
        var target = next.Registrations.FirstOrDefault(r => r.Id == evt.RegistrationId);
        if (target is null || target.Tombstoned)
            return current;

        target.ScopeId = evt.ScopeId ?? string.Empty;
        return next;
    }

    // Soft-delete to retain the entry until the durable projector watermark
    // has advanced past this state version (Channel RFC §7.1.1).
    private static ChannelBotRegistrationStoreState ApplyUnregistered(ChannelBotRegistrationStoreState current, ChannelBotUnregisteredEvent evt)
    {
        var next = current.Clone();
        var entry = next.Registrations.FirstOrDefault(r => r.Id == evt.RegistrationId);
        if (entry is not null)
        {
            entry.Tombstoned = true;
            entry.TombstoneStateVersion = evt.TombstoneStateVersion;
        }
        return next;
    }

    private static ChannelBotRegistrationStoreState ApplyInboundObserved(
        ChannelBotRegistrationStoreState current,
        ChannelBotInboundObservedEvent evt)
    {
        var next = current.Clone();
        var entry = next.Registrations.FirstOrDefault(r => r.Id == evt.RegistrationId);
        if (entry is null || entry.Tombstoned)
            return current;

        entry.LastInboundAtUtc = evt.ObservedAtUtc;
        return next;
    }

    private static ChannelBotRegistrationStoreState ApplyTombstonesCompacted(
        ChannelBotRegistrationStoreState current,
        ChannelBotTombstonesCompactedEvent evt)
    {
        if (evt.RegistrationIds.Count == 0)
            return current;

        var next = current.Clone();
        var compacted = evt.RegistrationIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var removable = next.Registrations
            .Where(entry => compacted.Contains(entry.Id))
            .ToArray();
        foreach (var entry in removable)
            next.Registrations.Remove(entry);
        return next;
    }

    private static ChannelBotRegistrationStoreState ApplyWorkflowResultDeliveryRepairRequested(
        ChannelBotRegistrationStoreState current,
        ChannelBotWorkflowResultDeliveryRepairRequestedEvent evt) =>
        ApplyRepairState(current, evt.RegistrationId, evt.Repair);

    private static ChannelBotRegistrationStoreState ApplyWorkflowResultDeliveryRepairPrepared(
        ChannelBotRegistrationStoreState current,
        ChannelBotWorkflowResultDeliveryRepairPreparedEvent evt) =>
        ApplyRepairState(current, evt.RegistrationId, evt.Repair);

    private static ChannelBotRegistrationStoreState ApplyWorkflowResultDeliveryRepairFailed(
        ChannelBotRegistrationStoreState current,
        ChannelBotWorkflowResultDeliveryRepairFailedEvent evt) =>
        ApplyRepairState(current, evt.RegistrationId, evt.Repair);

    private static ChannelBotRegistrationStoreState ApplyRepairState(
        ChannelBotRegistrationStoreState current,
        string registrationId,
        ChannelWorkflowResultDeliveryRepairState? repair)
    {
        var next = current.Clone();
        var entry = next.Registrations.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, registrationId, StringComparison.Ordinal));
        if (entry is null || entry.Tombstoned || repair is null)
            return current;

        entry.WorkflowResultDeliveryRepair = repair.Clone();
        return next;
    }

    private static ChannelBotRegistrationStoreState ApplyWorkflowResultDeliveryRepairCompleted(
        ChannelBotRegistrationStoreState current,
        ChannelBotWorkflowResultDeliveryRepairCompletedEvent evt)
    {
        var next = current.Clone();
        var entry = next.Registrations.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, evt.RegistrationId, StringComparison.Ordinal));
        if (entry is null || entry.Tombstoned || evt.PreparedSecretReference is null)
            return current;

        entry.NyxAgentApiKeyId = evt.RotatedApiKeyId;
        entry.WorkflowResultDeliveryCredential = evt.PreparedSecretReference.Clone();
        entry.WorkflowResultDeliveryRepair = null;
        return next;
    }

    private ChannelBotRegistrationEntry? FindActiveRegistration(string registrationId) =>
        State.Registrations.FirstOrDefault(entry =>
            !entry.Tombstoned && string.Equals(entry.Id, registrationId, StringComparison.Ordinal));

    private static ChannelWorkflowResultDeliveryRepairFailureReason ValidateRequest(
        ChannelBotRegistrationEntry? entry,
        ChannelBotWorkflowResultDeliveryRepairRequestCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.RegistrationId) ||
            string.IsNullOrWhiteSpace(cmd.RequestId) ||
            string.IsNullOrWhiteSpace(cmd.ExpectedApiKeyId) ||
            string.IsNullOrWhiteSpace(cmd.ExpectedConversationRouteId) ||
            string.IsNullOrWhiteSpace(cmd.RequestedBySubjectId) ||
            cmd.RequestedAtUnixMs <= 0)
        {
            return ChannelWorkflowResultDeliveryRepairFailureReason.InvalidRequest;
        }

        if (entry is null)
            return ChannelWorkflowResultDeliveryRepairFailureReason.RegistrationNotFound;
        if (!IsLark(entry))
            return ChannelWorkflowResultDeliveryRepairFailureReason.UnsupportedPlatform;
        if (IsPreparedReferenceUsable(entry, entry.WorkflowResultDeliveryCredential))
            return ChannelWorkflowResultDeliveryRepairFailureReason.AlreadyEnabled;
        if (!string.Equals(entry.NyxAgentApiKeyId, Normalize(cmd.ExpectedApiKeyId), StringComparison.Ordinal) ||
            !string.Equals(entry.NyxConversationRouteId, Normalize(cmd.ExpectedConversationRouteId), StringComparison.Ordinal))
        {
            return ChannelWorkflowResultDeliveryRepairFailureReason.StaleActiveKey;
        }

        return ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified;
    }

    private static ChannelWorkflowResultDeliveryRepairFailureReason ValidatePhaseCommand(
        ChannelBotRegistrationEntry? entry,
        string requestId,
        string expectedApiKeyId,
        long updatedAtUnixMs)
    {
        if (string.IsNullOrWhiteSpace(requestId) ||
            string.IsNullOrWhiteSpace(expectedApiKeyId) ||
            updatedAtUnixMs <= 0)
        {
            return ChannelWorkflowResultDeliveryRepairFailureReason.InvalidRequest;
        }

        if (entry is null)
            return ChannelWorkflowResultDeliveryRepairFailureReason.RegistrationNotFound;
        if (!IsLark(entry))
            return ChannelWorkflowResultDeliveryRepairFailureReason.UnsupportedPlatform;
        if (entry.WorkflowResultDeliveryRepair is null)
            return ChannelWorkflowResultDeliveryRepairFailureReason.InvalidRequest;
        if (!string.Equals(entry.WorkflowResultDeliveryRepair.RequestId, requestId, StringComparison.Ordinal))
            return ChannelWorkflowResultDeliveryRepairFailureReason.RequestConflict;
        if (!string.Equals(entry.NyxAgentApiKeyId, Normalize(expectedApiKeyId), StringComparison.Ordinal) ||
            !string.Equals(entry.WorkflowResultDeliveryRepair.ExpectedApiKeyId, Normalize(expectedApiKeyId), StringComparison.Ordinal))
        {
            return ChannelWorkflowResultDeliveryRepairFailureReason.StaleActiveKey;
        }

        return ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified;
    }

    private static bool IsLark(ChannelBotRegistrationEntry entry) =>
        string.Equals(entry.Platform, "lark", StringComparison.OrdinalIgnoreCase);

    private static bool IsPreparedReferenceUsable(
        ChannelBotRegistrationEntry entry,
        SecretReference? reference) =>
        reference is not null &&
        !string.IsNullOrWhiteSpace(reference.Ref) &&
        string.Equals(
            reference.Purpose,
            CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            StringComparison.Ordinal) &&
        string.Equals(reference.OwnerScopeKey, entry.ScopeId, StringComparison.Ordinal);

    private static bool SameRequestFacts(
        ChannelWorkflowResultDeliveryRepairState repair,
        ChannelBotWorkflowResultDeliveryRepairRequestCommand cmd) =>
        string.Equals(repair.ExpectedApiKeyId, Normalize(cmd.ExpectedApiKeyId), StringComparison.Ordinal) &&
        string.Equals(repair.ExpectedConversationRouteId, Normalize(cmd.ExpectedConversationRouteId), StringComparison.Ordinal) &&
        string.Equals(repair.RequestedBySubjectId, Normalize(cmd.RequestedBySubjectId), StringComparison.Ordinal) &&
        repair.RequestedAtUnixMs == cmd.RequestedAtUnixMs;

    private static bool SamePreparedFacts(
        ChannelWorkflowResultDeliveryRepairState repair,
        string rotatedApiKeyId,
        SecretReference? reference) =>
        string.Equals(repair.RotatedApiKeyId, Normalize(rotatedApiKeyId), StringComparison.Ordinal) &&
        Equals(repair.PreparedSecretReference, reference);

    private static bool SameFailureFacts(
        ChannelWorkflowResultDeliveryRepairState repair,
        ChannelBotWorkflowResultDeliveryRepairFailCommand cmd) =>
        SamePreparedFacts(repair, cmd.RotatedApiKeyId, cmd.PreparedSecretReference) &&
        repair.FailurePhase == cmd.FailurePhase &&
        repair.FailureReason == cmd.FailureReason &&
        repair.UpdatedAtUnixMs == cmd.UpdatedAtUnixMs;

    private static ChannelBotWorkflowResultDeliveryRepairCompletedEvent CreateCompletedEvent(
        string registrationId,
        string requestId,
        ChannelBotWorkflowResultDeliveryRepairCompleteCommand cmd) =>
        new()
        {
            RegistrationId = registrationId,
            RequestId = requestId,
            ExpectedApiKeyId = Normalize(cmd.ExpectedApiKeyId),
            RotatedApiKeyId = Normalize(cmd.RotatedApiKeyId),
            PreparedSecretReference = cmd.PreparedSecretReference?.Clone(),
            CompletedAtUnixMs = cmd.UpdatedAtUnixMs,
        };

    private async Task PersistRepairRejectedAsync(
        string registrationId,
        string requestId,
        ChannelWorkflowResultDeliveryRepairPhase phase,
        ChannelWorkflowResultDeliveryRepairFailureReason reason,
        long rejectedAtUnixMs)
    {
        await PersistDomainEventAsync(new ChannelBotWorkflowResultDeliveryRepairRejectedEvent
        {
            RegistrationId = registrationId,
            RequestId = requestId,
            Phase = phase,
            Reason = reason,
            RejectedAtUnixMs = rejectedAtUnixMs > 0
                ? rejectedAtUnixMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private long NextCommittedVersion() =>
        (EventSourcing ?? throw new InvalidOperationException("Event sourcing must be configured before computing the next committed version."))
        .CurrentVersion + 1;

}
