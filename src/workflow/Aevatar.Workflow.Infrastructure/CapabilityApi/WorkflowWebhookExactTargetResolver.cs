using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

/// <summary>
/// Resolves a pinned webhook target to the exact committed definition payload
/// used to create the run. Validation is repeated for every delivery so scope
/// or revision drift fails before replay admission and command dispatch.
/// </summary>
internal static class WorkflowWebhookExactTargetResolver
{
    public static async Task<WorkflowWebhookExactTargetResolution> ResolveAsync(
        IWorkflowActorBindingReader? bindingReader,
        WorkflowWebhookIngressBindingOptions binding,
        CancellationToken ct)
    {
        var definitionActorId = Normalize(binding.DefinitionActorId);
        if (definitionActorId == null)
            return WorkflowWebhookExactTargetResolution.NotApplicable;

        var expectedScopeId = Normalize(binding.ScopeId);
        var expectedRevisionId = Normalize(binding.TargetRevisionId);
        if (expectedScopeId == null || expectedRevisionId == null)
        {
            return WorkflowWebhookExactTargetResolution.Failure(
                "WEBHOOK_EXACT_TARGET_INCOMPLETE",
                "Definition webhook binding must pin scope and revision.",
                StatusCodes.Status409Conflict);
        }

        if (bindingReader == null)
        {
            return WorkflowWebhookExactTargetResolution.Failure(
                "WEBHOOK_TARGET_VALIDATION_UNAVAILABLE",
                "Definition actor target cannot be validated on this host.",
                StatusCodes.Status503ServiceUnavailable);
        }

        var target = await bindingReader.GetAsync(definitionActorId, ct);
        if (target == null)
        {
            return WorkflowWebhookExactTargetResolution.Failure(
                "WEBHOOK_TARGET_NOT_FOUND",
                "Pinned workflow definition actor was not found.",
                StatusCodes.Status409Conflict);
        }

        if (target.ActorKind != WorkflowActorKind.Definition ||
            !string.Equals(target.ActorId, definitionActorId, StringComparison.Ordinal))
        {
            return WorkflowWebhookExactTargetResolution.Failure(
                "WEBHOOK_TARGET_NOT_DEFINITION",
                "Pinned webhook target is no longer a workflow definition actor.",
                StatusCodes.Status409Conflict);
        }

        if (!string.Equals(Normalize(target.ScopeId), expectedScopeId, StringComparison.Ordinal))
        {
            return WorkflowWebhookExactTargetResolution.Failure(
                "WEBHOOK_TARGET_SCOPE_DRIFT",
                "Pinned workflow definition scope no longer matches the binding.",
                StatusCodes.Status409Conflict);
        }

        if (!string.Equals(Normalize(target.RevisionId), expectedRevisionId, StringComparison.Ordinal))
        {
            return WorkflowWebhookExactTargetResolution.Failure(
                "WEBHOOK_TARGET_REVISION_DRIFT",
                "Pinned workflow definition revision no longer matches the binding.",
                StatusCodes.Status409Conflict);
        }

        var expectedWorkflowName = Normalize(binding.WorkflowName);
        var targetWorkflowName = Normalize(target.WorkflowName);
        if (targetWorkflowName == null ||
            (expectedWorkflowName != null &&
             !string.Equals(targetWorkflowName, expectedWorkflowName, StringComparison.OrdinalIgnoreCase)))
        {
            return WorkflowWebhookExactTargetResolution.Failure(
                "WEBHOOK_TARGET_WORKFLOW_DRIFT",
                "Pinned workflow definition name no longer matches the binding.",
                StatusCodes.Status409Conflict);
        }

        if (!target.HasDefinitionPayload ||
            target.ExpectedExecutionMode == ExternalCapabilityExecutionMode.Unspecified ||
            !Enum.IsDefined(target.ExpectedExecutionMode))
        {
            return WorkflowWebhookExactTargetResolution.Failure(
                "WEBHOOK_TARGET_NOT_EXECUTABLE",
                "Pinned workflow definition is not executable.",
                StatusCodes.Status409Conflict);
        }

        return WorkflowWebhookExactTargetResolution.Success(new WorkflowDefinitionBinding(
            definitionActorId,
            targetWorkflowName,
            target.WorkflowYaml,
            target.InlineWorkflowYamls,
            target.ExpectedExecutionMode,
            expectedScopeId,
            WorkflowRunOrigins.Webhook,
            SourceKind: target.SourceKind,
            CapabilityAdmissionPlan: target.CapabilityAdmissionPlan?.Clone(),
            WorkflowId: target.WorkflowId,
            RevisionId: expectedRevisionId,
            DefinitionVersion: Math.Max(0, target.SourceVersion),
            ToolCatalogPolicyVersion: target.ToolCatalogPolicyVersion));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record WorkflowWebhookExactTargetResolution(
    WorkflowDefinitionBinding? Definition,
    string? ErrorCode,
    string? ErrorMessage,
    int StatusCode)
{
    public bool Succeeded => ErrorCode == null;

    public static WorkflowWebhookExactTargetResolution NotApplicable { get; } =
        new(null, null, null, StatusCodes.Status200OK);

    public static WorkflowWebhookExactTargetResolution Success(WorkflowDefinitionBinding definition) =>
        new(definition, null, null, StatusCodes.Status200OK);

    public static WorkflowWebhookExactTargetResolution Failure(string code, string message, int statusCode) =>
        new(null, code, message, statusCode);
}
