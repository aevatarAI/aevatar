using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Audit;

internal static class WorkflowAuditScopeResolver
{
    public static string Resolve(
        CommittedAuditTranslationContext context,
        string? eventScopeId = null)
    {
        if (!string.IsNullOrWhiteSpace(eventScopeId))
            return eventScopeId;

        var stateRoot = context.Published.StateRoot;
        if (stateRoot?.Is(WorkflowRunState.Descriptor) != true)
            return string.Empty;

        return stateRoot.Unpack<WorkflowRunState>().ScopeId ?? string.Empty;
    }
}
