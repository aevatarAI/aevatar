using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.ExternalCapabilities;

public sealed class WorkflowExplicitRequestPreviewService(
    IWorkflowDefinitionParser parser,
    IExternalWorkflowCapabilityReadinessPort readinessPort) :
    IWorkflowExplicitRequestPreviewService
{
    private const string DurableAuthorizationUnavailableCode =
        "DURABLE_AUTHORIZATION_UNAVAILABLE";

    public async Task<WorkflowExplicitRequestPreviewResult> PreviewAsync(
        WorkflowExplicitRequestPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Access);
        if (request.ExecutionMode == ExternalCapabilityExecutionMode.Unspecified)
            throw new InvalidOperationException("External capability execution mode is required.");
        var workflowId = NormalizeRequired(request.WorkflowId, nameof(request.WorkflowId));
        var revisionId = ResolveRevisionId(request.RevisionId);

        var invocations = await ParseInvocationsAsync(request, cancellationToken);
        var items = new List<WorkflowExplicitRequestPreviewItem>();
        foreach (var invocation in invocations.Where(static invocation =>
                     invocation.Selector.SelectorCase ==
                     ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest))
        {
            var readiness = await readinessPort.InspectAsync(
                new InspectExternalWorkflowCapabilityReadinessRequest(
                    request.Access,
                    invocation.Selector,
                    request.ExecutionMode),
                cancellationToken);
            if (readiness.Status != ExternalCapabilityReadinessStatus.Ready &&
                !CanReviewBeforeDurableAuthorizationPreparation(request.ExecutionMode, readiness))
                throw new WorkflowExternalCapabilityAdmissionException(readiness);
            if (readiness.SelectedCapability?.CapabilityCase !=
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest)
            {
                throw new InvalidOperationException(
                    "Explicit request readiness did not return a canonical request capability.");
            }

            var capability = readiness.SelectedCapability.NyxIdUserRequest;
            var canonical = capability.Request ?? throw new InvalidOperationException(
                "Explicit request readiness did not return a canonical request contract.");
            var policy = capability.ExecutionPolicy;
            var risk = policy?.Risk ?? NyxIdOperationRisk.Unspecified;
            var allowedExecutionModes = new List<ExternalCapabilityExecutionMode>
            {
                ExternalCapabilityExecutionMode.Interactive,
            };
            if (request.ExecutionMode == ExternalCapabilityExecutionMode.Durable &&
                policy?.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Durable) == true)
                allowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);

            var approvalRequired = policy?.Approval == NyxIdOperationApproval.Required;
            items.Add(new WorkflowExplicitRequestPreviewItem(
                invocation.CallSiteId,
                WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdRequestContractDigest(canonical),
                canonical.UserServiceId,
                canonical.Method,
                canonical.PathTemplate,
                canonical.BodyMode,
                canonical.BodyRequired,
                canonical.ResponseMode,
                risk,
                approvalRequired,
                approvalRequired
                    ? WorkflowExplicitRequestApprovalEnforcement.BindTimeConfirmationAndRunTimeToolApproval
                    : WorkflowExplicitRequestApprovalEnforcement.None,
                allowedExecutionModes));
        }

        return new WorkflowExplicitRequestPreviewResult(workflowId, revisionId, items);
    }

    private static bool CanReviewBeforeDurableAuthorizationPreparation(
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadiness readiness)
    {
        if (executionMode != ExternalCapabilityExecutionMode.Durable ||
            readiness.ExecutionMode != executionMode ||
            readiness.Status != ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable ||
            readiness.Blockers.Count == 0 ||
            readiness.Blockers.Any(static blocker =>
                blocker.Status != ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable ||
                !string.Equals(blocker.Code, DurableAuthorizationUnavailableCode, StringComparison.Ordinal)) ||
            readiness.SelectedCapability?.CapabilityCase !=
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest)
        {
            return false;
        }

        var request = readiness.SelectedCapability.NyxIdUserRequest;
        return request.Request?.Method is (
                   NyxIdRequestMethod.Get or
                   NyxIdRequestMethod.Head or
                   NyxIdRequestMethod.Options) &&
               request.ExecutionPolicy?.Risk == NyxIdOperationRisk.ReadOnly &&
               request.ExecutionPolicy.AllowedExecutionModes.Contains(
                   ExternalCapabilityExecutionMode.Durable);
    }

    private async Task<IReadOnlyList<ExternalToolInvocationSpec>> ParseInvocationsAsync(
        WorkflowExplicitRequestPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var yamls = request.WorkflowYamls is { Count: > 0 }
            ? request.WorkflowYamls
            : new[] { request.WorkflowYaml }
                .Concat((request.InlineWorkflowYamls ?? new Dictionary<string, string>())
                    .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                    .Select(static entry => entry.Value))
                .ToArray();
        var invocations = new List<ExternalToolInvocationSpec>();
        foreach (var yaml in yamls)
        {
            var parse = await parser.ParseWorkflowYamlForPublicationAsync(yaml ?? string.Empty, cancellationToken);
            if (!parse.Succeeded)
            {
                if (parse.ExternalCapabilityReadiness is not null)
                {
                    var readiness = parse.ExternalCapabilityReadiness.Clone();
                    readiness.ExecutionMode = request.ExecutionMode;
                    throw new WorkflowExternalCapabilityAdmissionException(readiness);
                }

                throw new InvalidOperationException(parse.Error);
            }

            invocations.AddRange(
                (parse.AuthorizationDependencies?.ExternalInvocations ?? [])
                .Select(static invocation => invocation.Clone()));
        }

        var ordered = invocations
            .OrderBy(static invocation => invocation.CallSiteId, StringComparer.Ordinal)
            .ToArray();
        var duplicate = ordered
            .GroupBy(static invocation => invocation.CallSiteId, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Workflow external capability call site '{duplicate.Key}' is duplicated.");
        }

        return ordered;
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new InvalidOperationException($"{parameterName} is required.")
            : normalized;
    }

    private static string ResolveRevisionId(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? $"rev-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"
            : normalized;
    }
}
