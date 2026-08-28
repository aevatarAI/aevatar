using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Runtime;

[GAgent(
    RuntimeFleetCapabilityAuthorityIdentity.AgentKind,
    StateSchemaVersion = SupportedStateSchemaVersion)]
public sealed class RuntimeFleetCapabilityAuthorityGAgent
    : GAgentBase<RuntimeFleetCapabilityAuthorityState>
{
    public const int SupportedStateSchemaVersion = 1;

    private readonly IRuntimeFleetMembershipSnapshotSource _membershipSource;
    private readonly IRuntimeFleetReconcileScheduleOwner _scheduleOwner;
    private readonly IRuntimeFleetReconcileDeliveryAttestationReader _attestationReader;
    private readonly TimeProvider _timeProvider;
    private readonly RuntimeFleetCapabilityAuthorityOptions _options;

    public RuntimeFleetCapabilityAuthorityGAgent(
        IRuntimeFleetMembershipSnapshotSource membershipSource,
        IRuntimeFleetReconcileScheduleOwner scheduleOwner,
        IRuntimeFleetReconcileDeliveryAttestationReader attestationReader,
        TimeProvider? timeProvider = null,
        RuntimeFleetCapabilityAuthorityOptions? options = null)
    {
        _membershipSource = membershipSource ??
            throw new ArgumentNullException(nameof(membershipSource));
        _scheduleOwner = scheduleOwner ??
            throw new ArgumentNullException(nameof(scheduleOwner));
        _attestationReader = attestationReader ??
            throw new ArgumentNullException(nameof(attestationReader));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new RuntimeFleetCapabilityAuthorityOptions();
        ValidateOptions(_options);
    }

    [AllEventHandler(Priority = 0, AllowSelfHandling = true)]
    public async Task HandleCommandAsync(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Payload?.Is(RuntimeFleetReconcileRequested.Descriptor) != true)
            return;

        EnsureAuthorityIdentity();
        var attestation = RequireCurrentAttestation(envelope);
        if (string.Equals(
                State.LastReconcileDeliveryEnvelopeId,
                attestation.EnvelopeId,
                StringComparison.Ordinal))
        {
            await _scheduleOwner.AcknowledgeDeliveryAsync(
                attestation,
                CancellationToken.None);
            return;
        }

        EnsureNewerDelivery(attestation);
        await HandleReconcileAsync(attestation);
        await _scheduleOwner.AcknowledgeDeliveryAsync(
            attestation,
            CancellationToken.None);
    }

    private async Task HandleReconcileAsync(
        RuntimeFleetReconcileDeliveryAttestation attestation)
    {
        var transitionId = BuildTransitionId(attestation);

        var now = _timeProvider.GetUtcNow();
        var observation = await ObserveMembershipAsync(now);
        var normalized = observation.Membership;
        var observationOutcome = observation.Outcome;

        if (normalized != null && !IsMonotonicMembership(normalized))
        {
            normalized = null;
            observationOutcome = RuntimeFleetMembershipObservationOutcome.RegressedOrConflicted;
        }
        else if (normalized != null)
        {
            observationOutcome = RuntimeFleetMembershipObservationOutcome.Observed;
        }

        if (normalized != null)
        {
            var preTransitionObservation = await ObserveMembershipAsync(
                _timeProvider.GetUtcNow());
            if (preTransitionObservation.Membership == null)
            {
                normalized = null;
                observationOutcome = preTransitionObservation.Outcome;
            }
            else if (!HasSameMembershipProof(
                         normalized,
                         preTransitionObservation.Membership) ||
                     !IsMonotonicMembership(preTransitionObservation.Membership))
            {
                normalized = null;
                observationOutcome =
                    RuntimeFleetMembershipObservationOutcome.RegressedOrConflicted;
            }
            else
            {
                normalized = preTransitionObservation.Membership;
                observationOutcome = RuntimeFleetMembershipObservationOutcome.Observed;
            }
        }

        var reconciliation = new RuntimeFleetReconciliationRecordedEvent
        {
            TransitionId = transitionId,
            DeliveryEnvelopeId = attestation.EnvelopeId,
            CallbackGeneration = attestation.Generation,
            CallbackFireIndex = attestation.FireIndex,
            CallbackSlotEpoch = attestation.SlotEpoch,
            ObservationOutcome = observationOutcome,
            AttemptedAt = Timestamp.FromDateTimeOffset(now),
        };

        if (normalized == null)
        {
            var events = BuildRevocations(
                    transitionId,
                    RevocationReason(observationOutcome),
                    now,
                    State.Gates.Where(static gate =>
                        gate.Status == RuntimeFleetCapabilityGateStatus.Open))
                .Cast<IMessage>()
                .ToList();
            // Close live gates before publishing the failed observation. Each committed event has
            // its own state_root, so recording the failure first would briefly re-publish OPEN.
            events.Add(reconciliation);
            await PersistDomainEventsAsync(events);
            return;
        }

        var validEvents = BuildGateTransitions(normalized, transitionId, now).ToList();
        // Gate states are derived from the new proof but remain unreadable against the old state
        // membership. Publish membership last so no per-event state_root can expose a new OPEN
        // grant before every gate has been reconciled.
        validEvents.Add(reconciliation);
        validEvents.Add(new RuntimeFleetMembershipObservedEvent
        {
            TransitionId = transitionId,
            Membership = normalized,
        });
        await PersistDomainEventsAsync(validEvents);
    }

    private async Task<RuntimeFleetMembershipObservation> ObserveMembershipAsync(
        DateTimeOffset now)
    {
        RuntimeFleetMembershipSnapshot? membership;
        try
        {
            membership = await _membershipSource.GetCurrentAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new RuntimeFleetMembershipObservation(
                null,
                RuntimeFleetMembershipObservationOutcome.SourceFailed);
        }

        if (membership == null)
        {
            return new RuntimeFleetMembershipObservation(
                null,
                RuntimeFleetMembershipObservationOutcome.Unavailable);
        }

        try
        {
            return new RuntimeFleetMembershipObservation(
                RuntimeFleetMembershipValidation.NormalizeAndValidate(
                    membership,
                    now,
                    _options),
                RuntimeFleetMembershipObservationOutcome.Observed);
        }
        catch (InvalidOperationException)
        {
            return new RuntimeFleetMembershipObservation(
                null,
                RuntimeFleetMembershipObservationOutcome.Invalid);
        }
    }

    private static bool HasSameMembershipProof(
        RuntimeFleetMembershipSnapshot initial,
        RuntimeFleetMembershipSnapshot current) =>
        initial.MembershipEpoch == current.MembershipEpoch &&
        string.Equals(
            initial.MembershipDigest,
            current.MembershipDigest,
            StringComparison.Ordinal) &&
        string.Equals(
            initial.DeploymentRevision,
            current.DeploymentRevision,
            StringComparison.Ordinal);

    private bool IsMonotonicMembership(RuntimeFleetMembershipSnapshot membership)
    {
        if (State.Membership == null)
            return true;
        if (membership.MembershipEpoch < State.Membership.MembershipEpoch)
            return false;
        return membership.MembershipEpoch != State.Membership.MembershipEpoch ||
               string.Equals(
                   membership.MembershipDigest,
                   State.Membership.MembershipDigest,
                   StringComparison.Ordinal);
    }

    private IEnumerable<IMessage> BuildGateTransitions(
        RuntimeFleetMembershipSnapshot membership,
        string transitionId,
        DateTimeOffset now)
    {
        var requirements = _options.ManagedCapabilities
            .Where(CanManageAtCurrentStateSchema)
            .OrderBy(static requirement => requirement.Capability)
            .ToArray();
        var managed = requirements
            .Select(static requirement => requirement.Capability)
            .ToHashSet();
        var closingEvents = new List<IMessage>();
        var openingEvents = new List<IMessage>();

        closingEvents.AddRange(BuildRevocations(
            transitionId,
            "capability-no-longer-managed",
            now,
            State.Gates.Where(gate =>
                gate.Status == RuntimeFleetCapabilityGateStatus.Open &&
                !managed.Contains(gate.Capability))));

        foreach (var requirement in requirements)
        {
            var current = FindGate(requirement.Capability);
            if (current?.Status == RuntimeFleetCapabilityGateStatus.Quiesced)
                continue;

            var quiescence = _options.QuiescencePolicies.SingleOrDefault(policy =>
                policy.TargetCapability == requirement.Capability);
            if (quiescence != null &&
                IsUnanimouslySupported(
                    membership,
                    requirement.Capability,
                    quiescence.ContractId,
                    quiescence.MinimumReaderContractVersion))
            {
                closingEvents.AddRange(CreateQuiescenceTransitions(
                    current,
                    requirement,
                    quiescence,
                    membership,
                    transitionId,
                    now));
                continue;
            }

            var supported = HasCommittedPrerequisites(requirement) &&
                IsUnanimouslySupported(
                    membership,
                    requirement.Capability,
                    requirement.ContractId,
                    requirement.MinimumReaderContractVersion);

            if (!supported)
            {
                if (current?.Status == RuntimeFleetCapabilityGateStatus.Open)
                {
                    closingEvents.Add(CreateRevocation(
                        current,
                        transitionId,
                        "fleet-capability-not-unanimous",
                        now));
                }
                continue;
            }

            var alreadyOpenForExactMembership =
                current?.Status == RuntimeFleetCapabilityGateStatus.Open &&
                current.MembershipEpoch == membership.MembershipEpoch &&
                string.Equals(current.MembershipDigest, membership.MembershipDigest, StringComparison.Ordinal) &&
                string.Equals(current.DeploymentRevision, membership.DeploymentRevision, StringComparison.Ordinal) &&
                string.Equals(current.RequiredContractId, requirement.ContractId, StringComparison.Ordinal) &&
                current.MinimumReaderContractVersion == requirement.MinimumReaderContractVersion;
            if (alreadyOpenForExactMembership)
                continue;

            var currentEpoch = current?.CapabilityEpoch ?? 0;
            if (currentEpoch < 0)
            {
                throw new InvalidOperationException(
                    $"Capability {requirement.Capability} has a negative committed epoch.");
            }
            openingEvents.Add(new RuntimeFleetCapabilityGateOpenedEvent
            {
                Gate = new RuntimeFleetCapabilityGateState
                {
                    Capability = requirement.Capability,
                    Status = RuntimeFleetCapabilityGateStatus.Open,
                    CapabilityEpoch = checked(currentEpoch + 1),
                    MembershipEpoch = membership.MembershipEpoch,
                    MembershipDigest = membership.MembershipDigest,
                    DeploymentRevision = membership.DeploymentRevision,
                    MinimumReaderContractVersion = requirement.MinimumReaderContractVersion,
                    RequiredContractId = requirement.ContractId,
                    ChangedAt = Timestamp.FromDateTimeOffset(now),
                    LastTransitionId = GateTransitionId(transitionId, requirement.Capability, "open"),
                },
            });
        }

        return closingEvents.Concat(openingEvents);
    }

    private bool CanManageAtCurrentStateSchema(
        RuntimeFleetCapabilityRequirement requirement)
    {
        if (requirement.Capability !=
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV3)
        {
            return true;
        }

        var context = Services
            .GetService(typeof(IRuntimeActorStateSchemaContextReader)) as
            IRuntimeActorStateSchemaContextReader;
        var current = context?.Current;
        if (current == null ||
            !string.Equals(
                current.AgentKind,
                RuntimeFleetCapabilityAuthorityIdentity.AgentKind,
                StringComparison.Ordinal) ||
            current.StateSchemaVersion < SupportedStateSchemaVersion)
        {
            return false;
        }

        var receipts = current.AdoptionReceipts.Where(receipt =>
            receipt.StateSchemaVersion == SupportedStateSchemaVersion &&
            receipt.RequiredCapability ==
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2 &&
            string.Equals(
                receipt.RequiredContractId,
                RuntimeFleetCapabilityContracts
                    .ProjectionScopeStatusTerminalQuiescenceV1,
                StringComparison.Ordinal) &&
            receipt.RequiredContractVersion ==
                RuntimeFleetCapabilityContracts
                    .ProjectionScopeStatusTerminalQuiescenceReaderVersion &&
            receipt.EvidenceStatus == RuntimeFleetCapabilityGateStatus.Quiesced &&
            receipt.CapabilityEpoch == long.MaxValue &&
            !string.IsNullOrWhiteSpace(receipt.QuiescenceTransitionId));
        return receipts.Count() == 1;
    }

    private bool HasCommittedPrerequisites(
        RuntimeFleetCapabilityRequirement requirement)
    {
        if (requirement.Capability !=
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV3)
        {
            return true;
        }

        var bridge = FindGate(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
        return bridge is
        {
            Status: RuntimeFleetCapabilityGateStatus.Quiesced,
            CapabilityEpoch: long.MaxValue,
            MembershipEpoch: > 0,
        } &&
            string.Equals(
                bridge.RequiredContractId,
                RuntimeFleetCapabilityContracts
                    .ProjectionScopeStatusTerminalQuiescenceV1,
                StringComparison.Ordinal) &&
            bridge.MinimumReaderContractVersion ==
                RuntimeFleetCapabilityContracts
                    .ProjectionScopeStatusTerminalQuiescenceReaderVersion &&
            bridge.QuiescenceReaderContractVersion ==
                RuntimeFleetCapabilityContracts
                    .ProjectionScopeStatusTerminalQuiescenceReaderVersion &&
            !string.IsNullOrWhiteSpace(bridge.MembershipDigest) &&
            !string.IsNullOrWhiteSpace(bridge.DeploymentRevision) &&
            !string.IsNullOrWhiteSpace(bridge.LastTransitionId);
    }

    private static bool IsUnanimouslySupported(
        RuntimeFleetMembershipSnapshot membership,
        RuntimeFleetCapability capability,
        string contractId,
        int minimumReaderContractVersion) =>
        membership.ActiveMembers.All(member =>
            member.Capabilities.Count(candidate =>
                candidate.Capability == capability &&
                candidate.ReaderContractVersion >= minimumReaderContractVersion &&
                string.Equals(candidate.ContractId, contractId, StringComparison.Ordinal)) == 1);

    private static IReadOnlyList<IMessage> CreateQuiescenceTransitions(
        RuntimeFleetCapabilityGateState? current,
        RuntimeFleetCapabilityRequirement requirement,
        RuntimeFleetCapabilityQuiescencePolicy policy,
        RuntimeFleetMembershipSnapshot membership,
        string transitionId,
        DateTimeOffset now)
    {
        if (current?.CapabilityEpoch < 0)
        {
            throw new InvalidOperationException(
                $"Capability {requirement.Capability} has a negative committed epoch.");
        }

        var changedAt = Timestamp.FromDateTimeOffset(now);
        var tombstoneGate = new RuntimeFleetCapabilityGateState
        {
            Capability = requirement.Capability,
            Status = RuntimeFleetCapabilityGateStatus.Revoked,
            CapabilityEpoch = long.MaxValue,
            MembershipEpoch = membership.MembershipEpoch,
            MembershipDigest = membership.MembershipDigest,
            DeploymentRevision = membership.DeploymentRevision,
            MinimumReaderContractVersion = policy.MinimumReaderContractVersion,
            RequiredContractId = policy.ContractId,
            ChangedAt = changedAt,
            LastTransitionId = GateTransitionId(
                transitionId,
                requirement.Capability,
                "quiescence-compatibility-tombstone"),
            RevocationReason = "quiescence-compatibility-tombstone",
            QuiescenceReaderContractVersion = policy.MinimumReaderContractVersion,
        };

        var quiescedGate = tombstoneGate.Clone();
        quiescedGate.Status = RuntimeFleetCapabilityGateStatus.Quiesced;
        quiescedGate.LastTransitionId = GateTransitionId(
            transitionId,
            requirement.Capability,
            "quiesce");
        quiescedGate.RevocationReason = string.Empty;

        // The compatibility tombstone and typed quiescence are appended in one atomic event-store
        // batch. A pre-bridge authority ignores the typed event but reconstructs REVOKED/max;
        // its old OPEN transition then overflows under checked arithmetic and fails closed.
        return
        [
            new RuntimeFleetCapabilityGateRevokedEvent { Gate = tombstoneGate },
            new RuntimeFleetCapabilityGateQuiescedEvent { Gate = quiescedGate },
        ];
    }

    private static IEnumerable<IMessage> BuildRevocations(
        string transitionId,
        string reason,
        DateTimeOffset now,
        IEnumerable<RuntimeFleetCapabilityGateState> gates) =>
        gates.Select(gate => CreateRevocation(gate, transitionId, reason, now));

    private static RuntimeFleetCapabilityGateRevokedEvent CreateRevocation(
        RuntimeFleetCapabilityGateState current,
        string transitionId,
        string reason,
        DateTimeOffset now)
    {
        if (current.CapabilityEpoch <= 0)
        {
            throw new InvalidOperationException(
                $"Capability {current.Capability} has no positive committed epoch to revoke.");
        }

        var next = current.Clone();
        next.Status = RuntimeFleetCapabilityGateStatus.Revoked;
        next.CapabilityEpoch = checked(current.CapabilityEpoch + 1);
        next.ChangedAt = Timestamp.FromDateTimeOffset(now);
        next.LastTransitionId = GateTransitionId(transitionId, current.Capability, "revoke");
        next.RevocationReason = reason;
        return new RuntimeFleetCapabilityGateRevokedEvent { Gate = next };
    }

    protected override RuntimeFleetCapabilityAuthorityState TransitionState(
        RuntimeFleetCapabilityAuthorityState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<RuntimeFleetReconciliationRecordedEvent>(ApplyReconciliationRecorded)
            .On<RuntimeFleetMembershipObservedEvent>(ApplyMembershipObserved)
            .On<RuntimeFleetCapabilityGateOpenedEvent>(ApplyGateOpened)
            .On<RuntimeFleetCapabilityGateRevokedEvent>(ApplyGateRevoked)
            .On<RuntimeFleetCapabilityGateQuiescedEvent>(ApplyGateQuiesced)
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureAuthorityIdentity();
        await _scheduleOwner.EnsureScheduledAsync(ct);
    }

    private void EnsureAuthorityIdentity()
    {
        if (!string.Equals(
                Id,
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Fleet capability authority must use actor id " +
                $"'{RuntimeFleetCapabilityAuthorityIdentity.ActorId}'.");
        }
    }

    private RuntimeFleetReconcileDeliveryAttestation RequireCurrentAttestation(
        EventEnvelope envelope)
    {
        var attestation = _attestationReader.Current;
        if (attestation == null ||
            string.IsNullOrWhiteSpace(envelope.Id) ||
            !string.Equals(envelope.Id, attestation.EnvelopeId, StringComparison.Ordinal) ||
            attestation.Generation <= 0 ||
            attestation.FireIndex <= 0 ||
            attestation.SlotEpoch == RuntimeCallbackSlotEpoch.Unspecified)
        {
            throw new InvalidOperationException(
                "Runtime fleet reconciliation requires an exact scheduler delivery attestation bound by runtime ingress.");
        }

        return attestation;
    }

    private void EnsureNewerDelivery(
        RuntimeFleetReconcileDeliveryAttestation attestation)
    {
        if (attestation.Generation < State.LastReconcileCallbackGeneration ||
            (attestation.Generation == State.LastReconcileCallbackGeneration &&
             attestation.FireIndex <= State.LastReconcileCallbackFireIndex))
        {
            throw new InvalidOperationException(
                $"Runtime fleet reconcile delivery {attestation.Generation}/{attestation.FireIndex} " +
                $"is not newer than committed delivery " +
                $"{State.LastReconcileCallbackGeneration}/{State.LastReconcileCallbackFireIndex}.");
        }
    }

    private RuntimeFleetCapabilityGateState? FindGate(RuntimeFleetCapability capability) =>
        State.Gates.SingleOrDefault(gate => gate.Capability == capability);

    private static RuntimeFleetCapabilityAuthorityState ApplyReconciliationRecorded(
        RuntimeFleetCapabilityAuthorityState state,
        RuntimeFleetReconciliationRecordedEvent evt)
    {
        var next = state.Clone();
        next.LastReconcileTransitionId = evt.TransitionId ?? string.Empty;
        next.LastReconcileDeliveryEnvelopeId = evt.DeliveryEnvelopeId ?? string.Empty;
        next.LastReconcileCallbackGeneration = evt.CallbackGeneration;
        next.LastReconcileCallbackFireIndex = evt.CallbackFireIndex;
        next.LastReconcileCallbackSlotEpoch = evt.CallbackSlotEpoch;
        next.LastMembershipObservationOutcome = evt.ObservationOutcome;
        next.LastReconcileAttemptedAt = evt.AttemptedAt?.Clone();
        return next;
    }

    private static RuntimeFleetCapabilityAuthorityState ApplyMembershipObserved(
        RuntimeFleetCapabilityAuthorityState state,
        RuntimeFleetMembershipObservedEvent evt)
    {
        var next = state.Clone();
        next.Membership = evt.Membership?.Clone();
        next.LastMembershipTransitionId = evt.TransitionId ?? string.Empty;
        return next;
    }

    private static RuntimeFleetCapabilityAuthorityState ApplyGateOpened(
        RuntimeFleetCapabilityAuthorityState state,
        RuntimeFleetCapabilityGateOpenedEvent evt) =>
        ReplaceGate(state, evt.Gate);

    private static RuntimeFleetCapabilityAuthorityState ApplyGateRevoked(
        RuntimeFleetCapabilityAuthorityState state,
        RuntimeFleetCapabilityGateRevokedEvent evt) =>
        ReplaceGate(state, evt.Gate);

    private static RuntimeFleetCapabilityAuthorityState ApplyGateQuiesced(
        RuntimeFleetCapabilityAuthorityState state,
        RuntimeFleetCapabilityGateQuiescedEvent evt) =>
        ReplaceGate(state, evt.Gate);

    private static RuntimeFleetCapabilityAuthorityState ReplaceGate(
        RuntimeFleetCapabilityAuthorityState state,
        RuntimeFleetCapabilityGateState? gate)
    {
        var next = state.Clone();
        if (gate == null)
            return next;
        var existing = next.Gates.SingleOrDefault(candidate =>
            candidate.Capability == gate.Capability);
        if (existing != null)
            next.Gates.Remove(existing);
        next.Gates.Add(gate.Clone());
        return next;
    }

    private static string RequireText(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{fieldName} is required.");
        return value.Trim();
    }

    private static string GateTransitionId(
        string reconcileTransitionId,
        RuntimeFleetCapability capability,
        string action) =>
        $"{reconcileTransitionId}:{(int)capability}:{action}";

    private static string BuildTransitionId(
        RuntimeFleetReconcileDeliveryAttestation attestation) =>
        $"runtime-fleet-reconcile:{attestation.Generation}:{attestation.FireIndex}:" +
        attestation.EnvelopeId;

    private static string RevocationReason(
        RuntimeFleetMembershipObservationOutcome outcome) => outcome switch
    {
        RuntimeFleetMembershipObservationOutcome.Unavailable => "membership-unavailable",
        RuntimeFleetMembershipObservationOutcome.Invalid => "membership-invalid",
        RuntimeFleetMembershipObservationOutcome.SourceFailed => "membership-source-failed",
        RuntimeFleetMembershipObservationOutcome.RegressedOrConflicted =>
            "membership-regressed-or-conflicted",
        _ => throw new InvalidOperationException(
            $"Runtime membership observation outcome {outcome} cannot revoke a gate."),
    };

    private static void ValidateOptions(RuntimeFleetCapabilityAuthorityOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            options.MaxMembershipEvidenceTtl,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxClockSkew,
            TimeSpan.Zero);
        RuntimeFleetCapabilityRequirement.ValidateAll(options.ManagedCapabilities);
        RuntimeFleetCapabilityQuiescencePolicy.ValidateAll(
            options.QuiescencePolicies,
            options.ManagedCapabilities);
    }

    private sealed record RuntimeFleetMembershipObservation(
        RuntimeFleetMembershipSnapshot? Membership,
        RuntimeFleetMembershipObservationOutcome Outcome);
}

internal static class RuntimeFleetMembershipValidation
{
    internal static RuntimeFleetMembershipSnapshot NormalizeAndValidate(
        RuntimeFleetMembershipSnapshot source,
        DateTimeOffset now,
        RuntimeFleetCapabilityAuthorityOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.MembershipEpoch <= 0)
            throw new InvalidOperationException("Runtime membership epoch must be positive.");
        if (string.IsNullOrWhiteSpace(source.DeploymentRevision))
            throw new InvalidOperationException("Runtime deployment revision is required.");
        if (source.ActiveMembers.Count == 0)
            throw new InvalidOperationException("Runtime membership must contain at least one active member.");
        EnsureFresh(source, now, options);

        var normalized = source.Clone();
        normalized.DeploymentRevision = normalized.DeploymentRevision.Trim();
        normalized.ActiveMembers.Clear();
        var memberIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceMember in source.ActiveMembers
                     .OrderBy(member => member.MemberId, StringComparer.Ordinal)
                     .ThenBy(member => member.Incarnation, StringComparer.Ordinal))
        {
            var member = sourceMember.Clone();
            member.MemberId = RequireText(member.MemberId, "Runtime member id");
            member.Incarnation = RequireText(member.Incarnation, "Runtime member incarnation");
            member.DeploymentRevision = RequireText(
                member.DeploymentRevision,
                "Runtime member deployment revision");
            if (!memberIds.Add(member.MemberId))
                throw new InvalidOperationException($"Duplicate active runtime member '{member.MemberId}'.");
            if (!string.Equals(
                    member.DeploymentRevision,
                    normalized.DeploymentRevision,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Runtime member '{member.MemberId}' advertises deployment revision " +
                    $"'{member.DeploymentRevision}', expected '{normalized.DeploymentRevision}'.");
            }

            var capabilities = member.Capabilities
                .OrderBy(capability => capability.Capability)
                .ToArray();
            member.Capabilities.Clear();
            var seenCapabilities = new HashSet<RuntimeFleetCapability>();
            foreach (var capability in capabilities)
            {
                ValidateCapability(capability.Capability);
                if (!seenCapabilities.Add(capability.Capability))
                {
                    throw new InvalidOperationException(
                        $"Runtime member '{member.MemberId}' advertises {capability.Capability} more than once.");
                }
                if (capability.ReaderContractVersion < 0)
                {
                    throw new InvalidOperationException(
                        $"Runtime member '{member.MemberId}' advertises a negative reader contract version.");
                }
                capability.ContractId = RequireText(
                    capability.ContractId,
                    $"Runtime member '{member.MemberId}' contract id");
                member.Capabilities.Add(capability);
            }
            normalized.ActiveMembers.Add(member);
        }

        normalized.MembershipDigest = RuntimeFleetMembershipDigest.Compute(normalized);
        return normalized;
    }

    internal static void EnsureFresh(
        RuntimeFleetMembershipSnapshot membership,
        DateTimeOffset now,
        RuntimeFleetCapabilityAuthorityOptions options)
    {
        if (membership.ObservedAt == null || membership.ValidUntil == null)
            throw new InvalidOperationException("Runtime membership freshness timestamps are required.");
        var observedAt = membership.ObservedAt.ToDateTimeOffset();
        var validUntil = membership.ValidUntil.ToDateTimeOffset();
        if (observedAt > now + options.MaxClockSkew ||
            validUntil <= now ||
            validUntil <= observedAt ||
            validUntil > observedAt + options.MaxMembershipEvidenceTtl)
            throw new InvalidOperationException("Runtime membership evidence is missing, future-dated, or expired.");
    }

    internal static void ValidateCapability(RuntimeFleetCapability capability)
    {
        if (capability == RuntimeFleetCapability.Unspecified ||
            !System.Enum.IsDefined(capability))
        {
            throw new InvalidOperationException($"Runtime fleet capability '{capability}' is invalid.");
        }
    }

    private static string RequireText(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{fieldName} is required.");
        return value.Trim();
    }
}

public sealed record RuntimeFleetCapabilityAuthorityOptions
{
    public TimeSpan MaxMembershipEvidenceTtl { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromSeconds(5);

    public IReadOnlyList<RuntimeFleetCapabilityRequirement> ManagedCapabilities { get; init; } =
    [
        new(
            RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            RuntimeFleetCapabilityContracts.WorkflowNormalizedStateV1,
            RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersion),
        // Keep the historical V2 route contract managed so the distinct bridge advertisement
        // closes its OPEN gate in both old and new authorities. The quiescence policy below
        // recognizes only unanimous exact bridge advertisements.
        new(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV2,
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersionV2),
        new(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion),
        new(
            RuntimeFleetCapability.ProjectionIncrementalGraphV1,
            RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
            RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion),
    ];

    public IReadOnlyList<RuntimeFleetCapabilityQuiescencePolicy> QuiescencePolicies { get; init; } =
    [
        new(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1,
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceReaderVersion),
    ];
}

public sealed record RuntimeFleetCapabilityQuiescencePolicy(
    RuntimeFleetCapability TargetCapability,
    string ContractId,
    int MinimumReaderContractVersion)
{
    internal static void ValidateAll(
        IReadOnlyList<RuntimeFleetCapabilityQuiescencePolicy>? policies,
        IReadOnlyList<RuntimeFleetCapabilityRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var requirementsByCapability = requirements.ToDictionary(static requirement => requirement.Capability);
        var seenTargets = new HashSet<RuntimeFleetCapability>();
        foreach (var policy in policies)
        {
            ArgumentNullException.ThrowIfNull(policy);
            RuntimeFleetMembershipValidation.ValidateCapability(policy.TargetCapability);
            if (!seenTargets.Add(policy.TargetCapability))
            {
                throw new InvalidOperationException(
                    $"Runtime fleet capability {policy.TargetCapability} has more than one quiescence policy.");
            }

            if (!requirementsByCapability.TryGetValue(policy.TargetCapability, out var requirement) ||
                string.IsNullOrWhiteSpace(policy.ContractId) ||
                string.Equals(policy.ContractId, requirement.ContractId, StringComparison.Ordinal) ||
                policy.MinimumReaderContractVersion <= requirement.MinimumReaderContractVersion)
            {
                throw new InvalidOperationException(
                    $"Runtime fleet capability {policy.TargetCapability} quiescence requires a distinct exact contract and a higher reader revision.");
            }
        }
    }
}

public sealed record RuntimeFleetCapabilityRequirement(
    RuntimeFleetCapability Capability,
    string ContractId,
    int MinimumReaderContractVersion)
{
    internal static void ValidateAll(
        IReadOnlyList<RuntimeFleetCapabilityRequirement>? requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var seen = new HashSet<RuntimeFleetCapability>();
        foreach (var requirement in requirements)
        {
            ArgumentNullException.ThrowIfNull(requirement);
            RuntimeFleetMembershipValidation.ValidateCapability(requirement.Capability);
            if (!seen.Add(requirement.Capability))
            {
                throw new InvalidOperationException(
                    $"Runtime fleet capability {requirement.Capability} is managed more than once.");
            }
            if (string.IsNullOrWhiteSpace(requirement.ContractId) ||
                requirement.MinimumReaderContractVersion <= 0)
            {
                throw new InvalidOperationException(
                    $"Runtime fleet capability {requirement.Capability} has no exact positive reader contract requirement.");
            }
        }
    }
}
