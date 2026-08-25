using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Reconciles the caller-owned platform code route before command-side admission. The following
/// readiness inspection remains the authority: this preparer never manufactures a readiness proof.
/// </summary>
public sealed class NyxIdCodeExecutionRouteAdmissionPreparer(
    NyxIdCodeExecutionRoutePolicyReconciler reconciler,
    NyxIdToolOptions options,
    ILogger<NyxIdCodeExecutionRouteAdmissionPreparer> logger) :
    IExternalWorkflowCapabilityAdmissionPreparer
{
    public ExternalWorkflowCapabilitySelector.SelectorOneofCase SelectorKind =>
        ExternalWorkflowCapabilitySelector.SelectorOneofCase.CodeExecution;

    public bool CanConverge(ExternalCapabilityReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        return readiness.Status == ExternalCapabilityReadinessStatus.ContractDrift &&
               readiness.Sources.Count > 0 &&
               readiness.Blockers.Count == 1 &&
               string.Equals(
                   readiness.Blockers[0].Code,
                   "CODE_EXECUTION_ROUTE_POLICY_MISMATCH",
                   StringComparison.Ordinal);
    }

    public async Task PrepareAsync(
        ExternalWorkflowCapabilityAccessContext access,
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(selector);
        if (selector.SelectorCase != SelectorKind ||
            executionMode == ExternalCapabilityExecutionMode.Unspecified ||
            string.IsNullOrWhiteSpace(options.EffectiveTransportBaseUrl))
        {
            return;
        }

        if (!NyxIdUserServiceRouteMutationAuthority.TryCreate(
                access.NyxIdCallerCredential,
                out var mutationAuthority) ||
            mutationAuthority is null)
        {
            return;
        }

        try
        {
            var result = await reconciler.ReconcileAsync(
                    mutationAuthority,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (result.Attempted && !result.Verified)
            {
                logger.LogWarning(
                    "Code execution route repair was not verified. failureKind={FailureKind} httpStatus={HttpStatus}",
                    result.FailureKind,
                    result.HttpStatus);
                throw new WorkflowExternalCapabilityAdmissionException(
                    RepairUnverified(selector, executionMode, result.FailureKind, result.HttpStatus));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkflowExternalCapabilityAdmissionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Code execution route repair failed. failureKind=source_exception exceptionType={ExceptionType}",
                exception.GetType().Name);
        }
    }

    private static ExternalCapabilityReadiness RepairUnverified(
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        NyxIdCodeExecutionRouteRepairFailureKind failureKind,
        int httpStatus)
    {
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
            SelectedSelector = selector.Clone(),
        };
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = readiness.Status,
            Code = "CODE_EXECUTION_ROUTE_REPAIR_UNVERIFIED",
            SafeMessage = FormatRepairUnverifiedMessage(failureKind, httpStatus),
        });
        readiness.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = ExternalCapabilityRemediationActionKind.RefreshSource,
            Label = "Retry platform code route repair",
        });
        return readiness;
    }

    internal static string FormatRepairUnverifiedMessage(
        NyxIdCodeExecutionRouteRepairFailureKind failureKind,
        int httpStatus) =>
        httpStatus > 0
            ? $"The platform code execution route repair could not be verified. failureKind={failureKind} httpStatus={httpStatus}"
            : $"The platform code execution route repair could not be verified. failureKind={failureKind}";
}
