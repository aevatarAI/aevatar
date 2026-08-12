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
            string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return;
        }

        var bearerToken = access.NyxIdCallerCredential?.SourceReadableUserBearerToken;
        if (string.IsNullOrWhiteSpace(bearerToken) ||
            access.NyxIdCallerCredential?.CanManageUserServices != true)
            return;

        try
        {
            var result = await reconciler.ReconcileAsync(
                    bearerToken,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (result.Attempted && !result.Verified)
            {
                logger.LogWarning(
                    "Code execution route repair was not verified. failureKind={FailureKind}",
                    result.FailureKind);
                throw new WorkflowExternalCapabilityAdmissionException(
                    RepairUnverified(selector, executionMode));
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
        ExternalCapabilityExecutionMode executionMode)
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
            SafeMessage = "The platform code execution route repair could not be verified.",
        });
        readiness.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = ExternalCapabilityRemediationActionKind.RefreshSource,
            Label = "Retry platform code route repair",
        });
        return readiness;
    }
}
