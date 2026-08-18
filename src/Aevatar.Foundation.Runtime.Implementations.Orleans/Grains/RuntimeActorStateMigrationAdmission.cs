using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.Runtime;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal static class RuntimeActorStateMigrationPersistence
{
    internal static async Task<bool> ApplyAndPersistAsync(
        IPersistentState<RuntimeActorGrainState> persistentState,
        AgentImplementation implementation,
        IRuntimeFleetCapabilityAdmissionReader admissionReader,
        IRuntimeLocalMembershipIdentityReader membershipReader,
        TimeProvider? timeProvider = null,
        RuntimeActorStateMigrationAdmissionOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persistentState);
        ArgumentNullException.ThrowIfNull(implementation);
        ArgumentNullException.ThrowIfNull(admissionReader);
        ArgumentNullException.ThrowIfNull(membershipReader);
        ct.ThrowIfCancellationRequested();

        var persisted = persistentState.State;
        var identity = persisted.Identity;
        if (identity == null)
            return false;

        var decision = await RuntimeActorStateMigrationAdmission.EvaluateAsync(
            identity,
            persisted.AgentStateTypeName,
            persisted.AgentStateSnapshot,
            implementation,
            admissionReader,
            membershipReader,
            timeProvider,
            options,
            ct);
        if (!decision.IsAdmitted)
            return false;

        var revalidated = await RuntimeActorStateMigrationAdmission.EvaluateAsync(
            identity,
            persisted.AgentStateTypeName,
            persisted.AgentStateSnapshot,
            implementation,
            admissionReader,
            membershipReader,
            timeProvider,
            options,
            ct);
        if (!RuntimeActorStateMigrationAdmission.HasSameAdmissionProof(
                decision,
                revalidated))
        {
            return false;
        }

        var originalSnapshot = persisted.AgentStateSnapshot;
        var originalStateTypeName = persisted.AgentStateTypeName;
        var originalIdentity = identity.Clone();
        persisted.AgentStateSnapshot = decision.Snapshot;
        persisted.AgentStateTypeName = decision.StateTypeName;
        identity.StateSchemaVersion = decision.StateSchemaVersion;
        identity.StateSchemaAdoptions.Add(decision.AdoptionReceipts);

        try
        {
            await persistentState.WriteStateAsync(ct);
            return true;
        }
        catch
        {
            persisted.AgentStateSnapshot = originalSnapshot;
            persisted.AgentStateTypeName = originalStateTypeName;
            persisted.Identity = originalIdentity;
            throw;
        }
    }
}
