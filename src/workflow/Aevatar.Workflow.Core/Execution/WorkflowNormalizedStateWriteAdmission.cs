using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Core.Runtime;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowNormalizedStateWriteAdmission
{
    internal const string ContractId =
        RuntimeFleetCapabilityContracts.WorkflowNormalizedStateV1;
    internal const int RequiredReaderContractVersion =
        RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersion;

    internal static bool IsGranted(IRuntimeActorStateSchemaContextReader? reader)
    {
        var context = reader?.Current;
        if (context == null || context.StateSchemaVersion < 1)
            return false;

        var receipts = context.AdoptionReceipts
            .Where(static receipt => receipt.StateSchemaVersion == 1)
            .ToArray();
        return receipts.Length == 1 &&
               receipts[0].RequiredCapability ==
                   RuntimeFleetCapability.WorkflowNormalizedStateWritesV1 &&
               string.Equals(receipts[0].RequiredContractId, ContractId, StringComparison.Ordinal) &&
               receipts[0].RequiredContractVersion >= RequiredReaderContractVersion &&
               receipts[0].CapabilityEpoch > 0 &&
               receipts[0].AuthorityStateVersion > 0 &&
               string.Equals(
                   receipts[0].AuthorityActorId,
                   RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                   StringComparison.Ordinal);
    }

    internal static Task<bool> IsLiveGateGrantedAsync(
        IWorkflowExecutionStateHost stateHost,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        var admissionReader = stateHost.RuntimeFleetCapabilityAdmissionReader;
        var membershipReader = stateHost.RuntimeLocalMembershipIdentityReader;
        if (admissionReader == null || membershipReader == null)
            return Task.FromResult(false);

        return RuntimeFleetCapabilityAdmissionValidation.IsGrantedAsync(
            RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            ContractId,
            RequiredReaderContractVersion,
            admissionReader,
            membershipReader,
            stateHost.RuntimeFleetAdmissionTimeProvider,
            stateHost.RuntimeFleetAdmissionOptions,
            ct);
    }

    internal static async Task<WorkflowExecutionValueRepresentation> SelectNewRunRepresentationAsync(
        IWorkflowExecutionStateHost stateHost,
        WorkflowRunForkSeed? forkSeed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        var normalizedFork = forkSeed?.NormalizedValues != null;
        if (forkSeed != null && !normalizedFork)
            return WorkflowExecutionValueRepresentation.Legacy;

        var schemaGranted = IsGranted(stateHost.RuntimeStateSchemaContextReader);
        if (!schemaGranted)
        {
            if (normalizedFork)
            {
                throw new InvalidOperationException(
                    "A normalized workflow fork seed requires a runtime-owned schema adoption receipt.");
            }

            return WorkflowExecutionValueRepresentation.Legacy;
        }

        var liveGateGranted = await IsLiveGateGrantedAsync(stateHost, ct);
        if (!liveGateGranted)
        {
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
}
