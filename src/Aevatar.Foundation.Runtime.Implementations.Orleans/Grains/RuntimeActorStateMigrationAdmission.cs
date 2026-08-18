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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            persisted.AgentStateSnapshot = originalSnapshot;
            persisted.AgentStateTypeName = originalStateTypeName;
            persisted.Identity = originalIdentity;
            throw;
        }
        catch (Exception exception)
        {
            persisted.AgentStateSnapshot = originalSnapshot;
            persisted.AgentStateTypeName = originalStateTypeName;
            persisted.Identity = originalIdentity;
            throw new RuntimeActorStateMigrationPersistenceException(
                identity.Kind,
                originalIdentity.StateSchemaVersion,
                decision.StateSchemaVersion,
                exception);
        }
    }
}

/// <summary>
/// The admitted migration could not be durably written. The in-memory grain state has been
/// restored to the persisted schema, so the actor may still activate at that version; the
/// adoption is simply retried on a later activation.
/// </summary>
public sealed class RuntimeActorStateMigrationPersistenceException(
    string agentKind,
    int persistedStateSchemaVersion,
    int targetStateSchemaVersion,
    Exception innerException)
    : Exception(
        $"Actor kind '{agentKind}' could not persist the state schema migration " +
        $"{persistedStateSchemaVersion}->{targetStateSchemaVersion}.",
        innerException)
{
    public string AgentKind { get; } = agentKind;

    public int PersistedStateSchemaVersion { get; } = persistedStateSchemaVersion;

    public int TargetStateSchemaVersion { get; } = targetStateSchemaVersion;
}
