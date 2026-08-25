using Aevatar.Foundation.Abstractions.Compatibility;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Runtime;

/// <summary>
/// Runtime-neutral state migration admission. It prepares a candidate state
/// row but never persists it; runtime adapters own their atomic write boundary.
/// </summary>
public static class RuntimeActorStateMigrationAdmission
{
    public static Task<RuntimeActorStateMigrationDecision> EvaluateAsync(
        RuntimeActorIdentity identity,
        string? stateTypeName,
        byte[]? snapshot,
        AgentImplementation implementation,
        IRuntimeFleetCapabilityAdmissionReader admissionReader,
        IRuntimeLocalMembershipIdentityReader membershipReader,
        TimeProvider? timeProvider = null,
        RuntimeActorStateMigrationAdmissionOptions? options = null,
        CancellationToken ct = default) =>
        EvaluateAsync(
            identity,
            stateTypeName,
            snapshot,
            implementation,
            admissionReader,
            membershipReader,
            timeProvider,
            options,
            ct,
            quiescenceReader: null);

    public static async Task<RuntimeActorStateMigrationDecision> EvaluateAsync(
        RuntimeActorIdentity identity,
        string? stateTypeName,
        byte[]? snapshot,
        AgentImplementation implementation,
        IRuntimeFleetCapabilityAdmissionReader admissionReader,
        IRuntimeLocalMembershipIdentityReader membershipReader,
        TimeProvider? timeProvider,
        RuntimeActorStateMigrationAdmissionOptions? options,
        CancellationToken ct,
        IRuntimeFleetCapabilityQuiescenceReader? quiescenceReader)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(implementation);
        ArgumentNullException.ThrowIfNull(admissionReader);
        ArgumentNullException.ThrowIfNull(membershipReader);
        ct.ThrowIfCancellationRequested();

        var migration = Prepare(identity, stateTypeName, snapshot, implementation);
        if (migration == null)
        {
            ValidatePersistedAdoptions(identity, implementation);
            return RuntimeActorStateMigrationDecision.Current;
        }

        ValidatePersistedAdoptions(identity, implementation);
        var clock = timeProvider ?? TimeProvider.System;
        var policy = options ?? new RuntimeActorStateMigrationAdmissionOptions();
        var receipts = new List<RuntimeActorStateSchemaAdoptionReceipt>(migration.Steps.Count);
        RuntimeLocalMembershipIdentity? localMembership = null;
        foreach (var step in migration.Steps)
        {
            if (step.RequiredGateStatus == RuntimeFleetCapabilityGateStatus.Quiesced)
            {
                if (quiescenceReader == null)
                    return RuntimeActorStateMigrationDecision.Blocked;

                RuntimeFleetCapabilityQuiescenceReceipt? quiescence;
                try
                {
                    quiescence = await RuntimeFleetCapabilityAdmissionValidation
                        .GetQuiescenceReceiptAsync(
                            step.RequiredCapability,
                            step.RequiredContractId,
                            step.RequiredContractVersion,
                            quiescenceReader,
                            ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return RuntimeActorStateMigrationDecision.Blocked;
                }
                if (quiescence == null)
                    return RuntimeActorStateMigrationDecision.Blocked;

                receipts.Add(CreateQuiescenceReceipt(step, quiescence.Evidence, clock.GetUtcNow()));
                continue;
            }

            if (localMembership == null)
            {
                try
                {
                    localMembership = await membershipReader.GetCurrentAsync(ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return RuntimeActorStateMigrationDecision.Blocked;
                }
                if (!RuntimeFleetCapabilityAdmissionValidation.IsValidLocalMembership(localMembership))
                    return RuntimeActorStateMigrationDecision.Blocked;
            }

            RuntimeFleetCapabilityAdmission? admission;
            try
            {
                admission = await admissionReader.GetAsync(step.RequiredCapability, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return RuntimeActorStateMigrationDecision.Blocked;
            }
            if (!RuntimeFleetCapabilityAdmissionValidation.IsGranted(
                    admission,
                    step.RequiredCapability,
                    step.RequiredContractId,
                    step.RequiredContractVersion,
                    localMembership!,
                    clock.GetUtcNow(),
                    policy))
                return RuntimeActorStateMigrationDecision.Blocked;

            receipts.Add(CreateOpenReceipt(step, admission!, clock.GetUtcNow()));
        }

        var migrated = migration.InitialSnapshot;
        foreach (var step in migration.Steps)
        {
            migrated = step.Apply(migrated)
                ?? throw new InvalidOperationException(
                    $"Actor kind '{identity.Kind}' state migration " +
                    $"{step.FromStateVersion}->{step.ToStateVersion} returned null.");
        }

        return new RuntimeActorStateMigrationDecision(
            IsMigrationRequired: true,
            IsAdmitted: true,
            Snapshot: migrated,
            StateTypeName: migration.StateTypeName,
            StateSchemaVersion: migration.TargetStateSchemaVersion,
            AdoptionReceipts: receipts,
            AdmissionMembership: localMembership);
    }

    public static bool HasSameAdmissionProof(
        RuntimeActorStateMigrationDecision initial,
        RuntimeActorStateMigrationDecision current)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(current);
        if (!initial.IsAdmitted || !current.IsAdmitted ||
            initial.StateSchemaVersion != current.StateSchemaVersion ||
            initial.AdmissionMembership != current.AdmissionMembership ||
            initial.AdoptionReceipts.Count != current.AdoptionReceipts.Count)
        {
            return false;
        }

        return initial.AdoptionReceipts
            .Zip(current.AdoptionReceipts)
            .All(static pair => HasSameReceiptProof(pair.First, pair.Second));
    }

    private static bool HasSameReceiptProof(
        RuntimeActorStateSchemaAdoptionReceipt left,
        RuntimeActorStateSchemaAdoptionReceipt right) =>
        left.StateSchemaVersion == right.StateSchemaVersion &&
        left.RequiredCapability == right.RequiredCapability &&
        string.Equals(left.RequiredContractId, right.RequiredContractId, StringComparison.Ordinal) &&
        left.RequiredContractVersion == right.RequiredContractVersion &&
        left.CapabilityEpoch == right.CapabilityEpoch &&
        left.AuthorityStateVersion == right.AuthorityStateVersion &&
        left.MembershipEpoch == right.MembershipEpoch &&
        string.Equals(left.DeploymentRevision, right.DeploymentRevision, StringComparison.Ordinal) &&
        string.Equals(left.AuthorityActorId, right.AuthorityActorId, StringComparison.Ordinal) &&
        string.Equals(left.MembershipDigest, right.MembershipDigest, StringComparison.Ordinal) &&
        left.EvidenceStatus == right.EvidenceStatus &&
        string.Equals(
            left.QuiescenceTransitionId,
            right.QuiescenceTransitionId,
            StringComparison.Ordinal);

    private static PreparedMigration? Prepare(
        RuntimeActorIdentity identity,
        string? stateTypeName,
        byte[]? snapshot,
        AgentImplementation implementation)
    {
        var storedVersion = identity.StateSchemaVersion;
        var supportedVersion = implementation.Metadata.StateSchemaVersion;
        if (storedVersion < 0)
        {
            throw new InvalidOperationException(
                $"Actor kind '{identity.Kind}' has negative persisted state schema version {storedVersion}.");
        }
        if (storedVersion > supportedVersion)
        {
            throw new InvalidOperationException(
                $"Actor kind '{identity.Kind}' persisted state schema version {storedVersion} " +
                $"is newer than supported version {supportedVersion}.");
        }
        if (storedVersion == supportedVersion)
            return null;

        if (!typeof(IMessage).IsAssignableFrom(implementation.StateContractType))
        {
            throw new InvalidOperationException(
                $"Actor kind '{identity.Kind}' cannot migrate non-protobuf state contract " +
                $"'{implementation.StateContractType.FullName}'.");
        }

        var hasSnapshot = snapshot is { Length: > 0 };
        if ((hasSnapshot || !string.IsNullOrWhiteSpace(stateTypeName)) &&
            !ProtobufContractCompatibility.IsCompatibleClrTypeName(
                implementation.StateContractType,
                stateTypeName))
        {
            throw new InvalidOperationException(
                $"Actor kind '{identity.Kind}' snapshot type '{stateTypeName ?? "(missing)"}' " +
                $"does not match state contract '{implementation.StateContractType.FullName}'.");
        }

        var byFromVersion = (implementation.StateMigrations ?? [])
            .ToDictionary(static step => step.FromStateVersion);
        var steps = new List<ActorStateMigrationStep>(supportedVersion - storedVersion);
        for (var version = storedVersion; version < supportedVersion; version++)
        {
            if (!byFromVersion.TryGetValue(version, out var step) ||
                step.ToStateVersion != version + 1 ||
                step.StateContractType != implementation.StateContractType)
            {
                throw new InvalidOperationException(
                    $"Actor kind '{identity.Kind}' has no complete typed state migration chain " +
                    $"from version {version} to {version + 1}.");
            }
            ValidateStepAdmissionContract(identity.Kind, step);
            steps.Add(step);
        }

        return new PreparedMigration(
            hasSnapshot ? snapshot!.ToArray() : CreateEmptyState(implementation.StateContractType),
            implementation.StateContractType.FullName ?? implementation.StateContractType.Name,
            supportedVersion,
            steps);
    }

    private static void ValidatePersistedAdoptions(
        RuntimeActorIdentity identity,
        AgentImplementation implementation)
    {
        ValidateNoFutureAdoptions(identity);
        if (identity.StateSchemaVersion == 0)
            return;

        var byTargetVersion = (implementation.StateMigrations ?? [])
            .ToDictionary(static step => step.ToStateVersion);
        for (var version = 1; version <= identity.StateSchemaVersion; version++)
        {
            if (!byTargetVersion.TryGetValue(version, out var step))
            {
                throw new InvalidOperationException(
                    $"Actor kind '{identity.Kind}' has no migration contract for persisted schema version {version}.");
            }

            ValidateStepAdmissionContract(identity.Kind, step);
            var receipts = identity.StateSchemaAdoptions
                .Where(receipt => receipt.StateSchemaVersion == version)
                .ToArray();
            if (receipts.Length != 1 || !ReceiptMatchesStep(receipts[0], step))
            {
                throw new InvalidOperationException(
                    $"Actor kind '{identity.Kind}' persisted schema version {version} has no unique matching adoption receipt.");
            }
        }
    }

    private static void ValidateNoFutureAdoptions(RuntimeActorIdentity identity)
    {
        var duplicate = identity.StateSchemaAdoptions
            .GroupBy(static receipt => receipt.StateSchemaVersion)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate != null)
        {
            throw new InvalidOperationException(
                $"Actor kind '{identity.Kind}' has duplicate adoption receipts for schema version {duplicate.Key}.");
        }

        if (identity.StateSchemaAdoptions.Any(receipt =>
                receipt.StateSchemaVersion <= 0 ||
                receipt.StateSchemaVersion > identity.StateSchemaVersion))
        {
            throw new InvalidOperationException(
                $"Actor kind '{identity.Kind}' has an adoption receipt outside its persisted schema version.");
        }
    }

    private static RuntimeActorStateSchemaAdoptionReceipt CreateOpenReceipt(
        ActorStateMigrationStep step,
        RuntimeFleetCapabilityAdmission admission,
        DateTimeOffset adoptedAt) =>
        new()
        {
            StateSchemaVersion = step.ToStateVersion,
            RequiredCapability = step.RequiredCapability,
            RequiredContractId = step.RequiredContractId,
            RequiredContractVersion = step.RequiredContractVersion,
            CapabilityEpoch = admission.CapabilityEpoch,
            AuthorityStateVersion = admission.AuthorityStateVersion,
            MembershipEpoch = admission.MembershipEpoch,
            DeploymentRevision = admission.DeploymentRevision,
            AdoptedAt = Timestamp.FromDateTimeOffset(adoptedAt),
            AuthorityActorId = admission.AuthorityActorId,
            MembershipDigest = admission.MembershipDigest,
            EvidenceStatus = RuntimeFleetCapabilityGateStatus.Open,
        };

    private static RuntimeActorStateSchemaAdoptionReceipt CreateQuiescenceReceipt(
        ActorStateMigrationStep step,
        RuntimeFleetCapabilityQuiescenceEvidence evidence,
        DateTimeOffset adoptedAt) =>
        new()
        {
            StateSchemaVersion = step.ToStateVersion,
            RequiredCapability = step.RequiredCapability,
            RequiredContractId = step.RequiredContractId,
            RequiredContractVersion = step.RequiredContractVersion,
            CapabilityEpoch = evidence.CapabilityEpoch,
            AuthorityStateVersion = evidence.AuthorityStateVersion,
            MembershipEpoch = evidence.QuiescedMembershipEpoch,
            DeploymentRevision = evidence.QuiescedDeploymentRevision,
            AdoptedAt = Timestamp.FromDateTimeOffset(adoptedAt),
            AuthorityActorId = evidence.AuthorityActorId,
            MembershipDigest = evidence.QuiescedMembershipDigest,
            EvidenceStatus = RuntimeFleetCapabilityGateStatus.Quiesced,
            QuiescenceTransitionId = evidence.QuiescenceTransitionId,
        };

    private static bool ReceiptMatchesStep(
        RuntimeActorStateSchemaAdoptionReceipt receipt,
        ActorStateMigrationStep step) =>
        receipt.RequiredCapability == step.RequiredCapability &&
        string.Equals(receipt.RequiredContractId, step.RequiredContractId, StringComparison.Ordinal) &&
        receipt.RequiredContractVersion == step.RequiredContractVersion &&
        receipt.CapabilityEpoch > 0 &&
        receipt.AuthorityStateVersion > 0 &&
        receipt.MembershipEpoch > 0 &&
        !string.IsNullOrWhiteSpace(receipt.DeploymentRevision) &&
        receipt.AdoptedAt != null &&
        string.Equals(
            receipt.AuthorityActorId,
            RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(receipt.MembershipDigest) &&
        ReceiptHasRequiredEvidenceStatus(receipt, step);

    private static bool ReceiptHasRequiredEvidenceStatus(
        RuntimeActorStateSchemaAdoptionReceipt receipt,
        ActorStateMigrationStep step) =>
        step.RequiredGateStatus switch
        {
            RuntimeFleetCapabilityGateStatus.Open =>
                receipt.EvidenceStatus is RuntimeFleetCapabilityGateStatus.Unspecified or
                    RuntimeFleetCapabilityGateStatus.Open &&
                string.IsNullOrWhiteSpace(receipt.QuiescenceTransitionId),
            RuntimeFleetCapabilityGateStatus.Quiesced =>
                receipt.EvidenceStatus == RuntimeFleetCapabilityGateStatus.Quiesced &&
                receipt.CapabilityEpoch == long.MaxValue &&
                !string.IsNullOrWhiteSpace(receipt.QuiescenceTransitionId),
            _ => false,
        };

    private static void ValidateStepAdmissionContract(
        string kind,
        ActorStateMigrationStep step)
    {
        if (step.RequiredCapability == RuntimeFleetCapability.Unspecified ||
            !System.Enum.IsDefined(step.RequiredCapability) ||
            string.IsNullOrWhiteSpace(step.RequiredContractId) ||
            step.RequiredContractVersion <= 0 ||
            step.RequiredGateStatus is not RuntimeFleetCapabilityGateStatus.Open and
                not RuntimeFleetCapabilityGateStatus.Quiesced)
        {
            throw new InvalidOperationException(
                $"Actor kind '{kind}' migration {step.FromStateVersion}->{step.ToStateVersion} " +
                "has no exact versioned fleet admission contract.");
        }
    }

    private static byte[] CreateEmptyState(System.Type stateContractType)
    {
        var state = Activator.CreateInstance(stateContractType) as IMessage
            ?? throw new InvalidOperationException(
                $"State contract '{stateContractType.FullName}' could not be constructed.");
        return state.ToByteArray();
    }

    private sealed record PreparedMigration(
        byte[] InitialSnapshot,
        string StateTypeName,
        int TargetStateSchemaVersion,
        IReadOnlyList<ActorStateMigrationStep> Steps);
}

public sealed record RuntimeActorStateMigrationDecision(
    bool IsMigrationRequired,
    bool IsAdmitted,
    byte[]? Snapshot,
    string? StateTypeName,
    int StateSchemaVersion,
    IReadOnlyList<RuntimeActorStateSchemaAdoptionReceipt> AdoptionReceipts,
    RuntimeLocalMembershipIdentity? AdmissionMembership)
{
    public static RuntimeActorStateMigrationDecision Current { get; } =
        new(false, false, null, null, 0, [], null);

    public static RuntimeActorStateMigrationDecision Blocked { get; } =
        new(true, false, null, null, 0, [], null);
}

public sealed record RuntimeActorStateMigrationAdmissionOptions
{
    public TimeSpan MaxMembershipEvidenceTtl { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromSeconds(5);
}
