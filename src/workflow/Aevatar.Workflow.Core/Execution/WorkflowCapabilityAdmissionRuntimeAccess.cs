using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.Execution;

internal enum WorkflowCapabilityAdmissionResolution
{
    Resolved,
    PlanMissing,
    CallSiteMissing,
    CallSiteAmbiguous,
    SelectorMismatch,
    ResponseProjectionMismatch,
}

internal readonly record struct WorkflowCapabilityAdmissionLookup(
    WorkflowCapabilityAdmissionResolution Resolution,
    WorkflowCapabilityInvocationAdmission? Admission)
{
    public bool IsResolved => Resolution == WorkflowCapabilityAdmissionResolution.Resolved;

    public string FailureCode => Resolution switch
    {
        WorkflowCapabilityAdmissionResolution.PlanMissing => "EXTERNAL_CAPABILITY_ADMISSION_PLAN_MISSING",
        WorkflowCapabilityAdmissionResolution.CallSiteMissing => "EXTERNAL_CAPABILITY_CALL_SITE_NOT_ADMITTED",
        WorkflowCapabilityAdmissionResolution.CallSiteAmbiguous => "EXTERNAL_CAPABILITY_CALL_SITE_AMBIGUOUS",
        WorkflowCapabilityAdmissionResolution.SelectorMismatch => "EXTERNAL_CAPABILITY_PROOF_SELECTOR_MISMATCH",
        WorkflowCapabilityAdmissionResolution.ResponseProjectionMismatch =>
            "EXTERNAL_CAPABILITY_RESPONSE_PROJECTION_MISMATCH",
        _ => string.Empty,
    };

    public string FailureMessage => Resolution switch
    {
        WorkflowCapabilityAdmissionResolution.PlanMissing =>
            "this run has no committed external capability admission plan",
        WorkflowCapabilityAdmissionResolution.CallSiteMissing =>
            "this call site has no committed external capability admission proof",
        WorkflowCapabilityAdmissionResolution.CallSiteAmbiguous =>
            "this call site resolves to more than one committed admission proof",
        WorkflowCapabilityAdmissionResolution.SelectorMismatch =>
            "the committed admission proof does not match the compiled call-site selector",
        WorkflowCapabilityAdmissionResolution.ResponseProjectionMismatch =>
            "the committed admission proof does not match the compiled response projection",
        _ => string.Empty,
    };
}

/// <summary>
/// Resolves the single committed proof for one compiled call site from actor-owned Run state.
/// This is the only runtime read of admission facts: no registry, no read model, no OpenAPI.
/// </summary>
internal static class WorkflowCapabilityAdmissionRuntimeAccess
{
    public static WorkflowCapabilityAdmissionLookup Resolve(
        IWorkflowExecutionContext ctx,
        ExternalToolInvocationSpec invocation)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        return Resolve(GetPlan(ctx), invocation);
    }

    public static WorkflowCapabilityAdmissionLookup Resolve(
        WorkflowCapabilityAdmissionPlan? plan,
        ExternalToolInvocationSpec invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (plan is null)
            return new WorkflowCapabilityAdmissionLookup(WorkflowCapabilityAdmissionResolution.PlanMissing, null);

        if (plan.InvocationAdmissions.Count == 0)
        {
            return IsPreviousSchemaCodeExecution(plan, invocation)
                ? PreviousSchemaCodeExecution()
                : new WorkflowCapabilityAdmissionLookup(
                    WorkflowCapabilityAdmissionResolution.PlanMissing,
                    null);
        }

        var callSiteId = invocation.CallSiteId?.Trim() ?? string.Empty;
        var matches = plan.InvocationAdmissions
            .Where(admission => string.Equals(admission.CallSiteId, callSiteId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            return IsPreviousSchemaCodeExecution(plan, invocation)
                ? PreviousSchemaCodeExecution()
                : new WorkflowCapabilityAdmissionLookup(
                    WorkflowCapabilityAdmissionResolution.CallSiteMissing,
                    null);
        }
        if (matches.Length > 1)
            return new WorkflowCapabilityAdmissionLookup(WorkflowCapabilityAdmissionResolution.CallSiteAmbiguous, null);

        var admission = matches[0];
        var proof = admission.Capability;
        if (proof is null ||
            !WorkflowCapabilityAdmissionPlanIntegrity.SelectorMatchesCapability(invocation.Selector, proof))
        {
            return new WorkflowCapabilityAdmissionLookup(
                WorkflowCapabilityAdmissionResolution.SelectorMismatch,
                null);
        }
        if (!WorkflowToolResponseProjectionContract.AreEquivalent(
                invocation.ResponseProjection,
                admission.ResponseProjection))
        {
            return new WorkflowCapabilityAdmissionLookup(
                WorkflowCapabilityAdmissionResolution.ResponseProjectionMismatch,
                null);
        }

        return new WorkflowCapabilityAdmissionLookup(
            WorkflowCapabilityAdmissionResolution.Resolved,
            admission.Clone());
    }

    private static bool IsPreviousSchemaCodeExecution(
        WorkflowCapabilityAdmissionPlan plan,
        ExternalToolInvocationSpec invocation) =>
        string.Equals(
            plan.SchemaVersion,
            WorkflowCapabilityAdmissionPlanIntegrity.PreviousSchemaVersion,
            StringComparison.Ordinal) &&
        string.Equals(
            invocation.ToolName,
            WorkflowAuthorizationDependencyEvaluator.CodeExecuteToolName,
            StringComparison.OrdinalIgnoreCase) &&
        invocation.Selector.SelectorCase ==
        ExternalWorkflowCapabilitySelector.SelectorOneofCase.CodeExecution;

    private static WorkflowCapabilityAdmissionLookup PreviousSchemaCodeExecution() =>
        new(WorkflowCapabilityAdmissionResolution.Resolved, null);

    private static WorkflowCapabilityAdmissionPlan? GetPlan(IWorkflowExecutionContext ctx) =>
        ctx switch
        {
            IWorkflowExecutionStateHost stateHost => stateHost.CapabilityAdmissionPlanSnapshot,
            IWorkflowExecutionStateHostAccessor accessor => accessor.StateHost.CapabilityAdmissionPlanSnapshot,
            _ => null,
        };
}
