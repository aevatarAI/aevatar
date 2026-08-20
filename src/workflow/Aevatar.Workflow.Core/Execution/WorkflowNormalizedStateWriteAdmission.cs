using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Core.Runtime;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowNormalizedStateWriteAdmission
{
    internal const string ContractId =
        RuntimeFleetCapabilityContracts.WorkflowNormalizedStateV1;
    internal const int RequiredReaderContractVersion =
        RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersionV1;
    internal const int ValueLifecycleRequiredReaderContractVersion =
        RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersionV2;

    internal static bool IsGranted(IRuntimeActorStateSchemaContextReader? reader)
    {
        var context = reader?.Current;
        if (context == null || context.StateSchemaVersion < 1)
            return false;

        return HasExactReceipt(context, stateSchemaVersion: 1, RequiredReaderContractVersion);
    }

    internal static bool IsValueLifecycleGranted(IRuntimeActorStateSchemaContextReader? reader)
    {
        var context = reader?.Current;
        return context != null &&
               context.StateSchemaVersion >= 2 &&
               HasExactReceipt(
                   context,
                   stateSchemaVersion: 2,
                   ValueLifecycleRequiredReaderContractVersion);
    }

    internal static Task<bool> IsLiveGateGrantedAsync(
        IWorkflowExecutionStateHost stateHost,
        CancellationToken ct = default,
        int requiredReaderContractVersion = RequiredReaderContractVersion)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        var admissionReader = stateHost.RuntimeFleetCapabilityAdmissionReader;
        var membershipReader = stateHost.RuntimeLocalMembershipIdentityReader;
        if (admissionReader == null || membershipReader == null)
            return Task.FromResult(false);

        return RuntimeFleetCapabilityAdmissionValidation.IsGrantedAsync(
            RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            ContractId,
            requiredReaderContractVersion,
            admissionReader,
            membershipReader,
            stateHost.RuntimeFleetAdmissionTimeProvider,
            stateHost.RuntimeFleetAdmissionOptions,
            ct);
    }

    internal static async Task<WorkflowExecutionValueRepresentation> SelectNewRunRepresentationAsync(
        IWorkflowExecutionStateHost stateHost,
        WorkflowRunForkSeed? forkSeed,
        CancellationToken ct = default,
        bool requiresValueLifecycle = false)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        var normalizedFork = forkSeed?.NormalizedValues != null;
        var requiresSchemaV2 = requiresValueLifecycle || RequiresSchemaV2(forkSeed?.NormalizedValues);
        if (forkSeed != null && !normalizedFork)
        {
            if (requiresSchemaV2)
                throw WorkflowValueLifecycleException.SchemaUnavailable();
            return WorkflowExecutionValueRepresentation.Legacy;
        }

        var schemaGranted = requiresSchemaV2
            ? IsValueLifecycleGranted(stateHost.RuntimeStateSchemaContextReader)
            : IsGranted(stateHost.RuntimeStateSchemaContextReader);
        if (!schemaGranted)
        {
            if (requiresSchemaV2)
                throw WorkflowValueLifecycleException.SchemaUnavailable();
            if (normalizedFork)
            {
                throw new InvalidOperationException(
                    "A normalized workflow fork seed requires a runtime-owned schema adoption receipt.");
            }

            return WorkflowExecutionValueRepresentation.Legacy;
        }

        var liveGateGranted = await IsLiveGateGrantedAsync(
            stateHost,
            ct,
            requiresSchemaV2
                ? ValueLifecycleRequiredReaderContractVersion
                : RequiredReaderContractVersion);
        if (!liveGateGranted)
        {
            if (requiresSchemaV2)
                throw WorkflowValueLifecycleException.SchemaUnavailable();
            if (normalizedFork)
            {
                throw new InvalidOperationException(
                    "A normalized workflow fork requires a live fleet admission for normalized state writes.");
            }

            // A plain logical run can start safely using the legacy
            // representation. Existing normalized runs do not call this
            // selector and therefore continue under their committed state.
            return WorkflowExecutionValueRepresentation.Legacy;
        }

        return WorkflowExecutionValueRepresentation.Normalized;
    }

    private static bool HasExactReceipt(
        RuntimeActorStateSchemaContext context,
        int stateSchemaVersion,
        int requiredReaderContractVersion)
    {
        var receipts = context.AdoptionReceipts
            .Where(receipt => receipt.StateSchemaVersion == stateSchemaVersion)
            .ToArray();
        return receipts.Length == 1 &&
               receipts[0].RequiredCapability ==
                   RuntimeFleetCapability.WorkflowNormalizedStateWritesV1 &&
               string.Equals(receipts[0].RequiredContractId, ContractId, StringComparison.Ordinal) &&
               receipts[0].RequiredContractVersion == requiredReaderContractVersion &&
               receipts[0].CapabilityEpoch > 0 &&
               receipts[0].AuthorityStateVersion > 0 &&
               string.Equals(
                   receipts[0].AuthorityActorId,
                   RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                   StringComparison.Ordinal);
    }

    private static bool RequiresSchemaV2(WorkflowNormalizedExecutionSeed? seed) =>
        seed != null &&
        (seed.ReleasedBindings.Count > 0 ||
         seed.CanonicalValues.Values.Any(static value => value.Released != null));
}
