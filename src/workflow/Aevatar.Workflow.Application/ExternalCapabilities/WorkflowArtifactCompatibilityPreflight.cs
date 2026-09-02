using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.ExternalCapabilities;

public sealed class WorkflowArtifactCompatibilityPreflight(
    IWorkflowDefinitionParser parser) : IWorkflowArtifactCompatibilityPreflight
{
    public async Task ValidateAsync(
        WorkflowArtifactCompatibilityRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedExecutionMode == ExternalCapabilityExecutionMode.Unspecified)
            throw RebindRequired(request.ExpectedExecutionMode);

        var expectedInvocations = new List<ExternalToolInvocationSpec>();
        await ParseAsync(
            request.WorkflowYaml,
            request.ExpectedExecutionMode,
            expectedInvocations,
            ct);

        foreach (var inlineWorkflowYaml in (request.InlineWorkflowYamls ??
                     new Dictionary<string, string>())
                 .OrderBy(static item => item.Key, StringComparer.Ordinal)
                 .Select(static item => item.Value)
                 .Distinct(StringComparer.Ordinal))
        {
            await ParseAsync(
                inlineWorkflowYaml,
                request.ExpectedExecutionMode,
                expectedInvocations,
                ct);
        }

        var plan = request.CapabilityAdmissionPlan;
        if (plan is null)
        {
            if (expectedInvocations.Count == 0)
                return;
            throw RebindRequired(request.ExpectedExecutionMode);
        }

        var compatibility = WorkflowCapabilityAdmissionPlanIntegrity.CheckCompatibility(
            plan,
            request.WorkflowYaml,
            request.InlineWorkflowYamls,
            request.ExpectedExecutionMode,
            expectedInvocations,
            request.WorkflowId,
            request.RevisionId);
        if (!compatibility.Succeeded)
            throw RebindRequired(request.ExpectedExecutionMode);
    }

    private async Task ParseAsync(
        string workflowYaml,
        ExternalCapabilityExecutionMode executionMode,
        ICollection<ExternalToolInvocationSpec> expectedInvocations,
        CancellationToken ct)
    {
        var parse = await parser.ParseWorkflowYamlAsync(workflowYaml ?? string.Empty, ct);
        if (!parse.Succeeded)
        {
            if (parse.ExternalCapabilityReadiness is not null)
            {
                var readiness = parse.ExternalCapabilityReadiness.Clone();
                readiness.ExecutionMode = executionMode;
                throw new WorkflowExternalCapabilityAdmissionException(readiness);
            }

            throw InvalidDefinition(executionMode);
        }

        foreach (var invocation in parse.AuthorizationDependencies?.ExternalInvocations ?? [])
            expectedInvocations.Add(invocation.Clone());
    }

    private static WorkflowExternalCapabilityAdmissionException InvalidDefinition(
        ExternalCapabilityExecutionMode executionMode) =>
        new(BuildReadiness(
            executionMode,
            ExternalCapabilityReadinessStatus.ContractDrift,
            "WORKFLOW_DEFINITION_INVALID",
            "Workflow definition is invalid."));

    private static WorkflowExternalCapabilityAdmissionException RebindRequired(
        ExternalCapabilityExecutionMode executionMode) =>
        new(BuildReadiness(
            executionMode,
            ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
            WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode,
            "Saved workflow and capability admission no longer match."));

    private static ExternalCapabilityReadiness BuildReadiness(
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadinessStatus status,
        string code,
        string safeMessage) =>
        new()
        {
            ExecutionMode = executionMode,
            Status = status,
            Blockers =
            {
                new ExternalCapabilityBlocker
                {
                    Status = status,
                    Code = code,
                    SafeMessage = safeMessage,
                },
            },
            Remediations =
            {
                new ExternalCapabilityRemediation
                {
                    ActionKind = ExternalCapabilityRemediationActionKind.RebindWorkflow,
                    Label = "Update and rebind workflow",
                },
            },
        };
}
