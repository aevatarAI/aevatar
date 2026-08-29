using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;

namespace Aevatar.GAgentService.Core.Services;

public static class ServiceInvocationCatalogCompaction
{
    public static Dictionary<string, ServiceInvocationRevisionReadinessState> ProjectRevisions(
        IEnumerable<KeyValuePair<string, ServiceRevisionRecordState>> revisions)
    {
        ArgumentNullException.ThrowIfNull(revisions);

        return revisions.ToDictionary(
            static pair => pair.Key,
            static pair => ProjectRevision(pair.Key, pair.Value),
            StringComparer.Ordinal);
    }

    public static void Compact(ServiceInvocationCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.RevisionReadiness.Count == 0 && state.Revisions.Count > 0)
            state.RevisionReadiness.Add(ProjectRevisions(state.Revisions));
        state.Revisions.Clear();
    }

    public static void Compact(ServiceInvocationCatalogObservedEvent stateEvent)
    {
        ArgumentNullException.ThrowIfNull(stateEvent);

        if (stateEvent.RevisionReadiness.Count == 0 && stateEvent.Revisions.Count > 0)
            stateEvent.RevisionReadiness.Add(ProjectRevisions(stateEvent.Revisions));
        stateEvent.Revisions.Clear();
    }

    private static ServiceInvocationRevisionReadinessState ProjectRevision(
        string revisionId,
        ServiceRevisionRecordState revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        var artifact = revision.PreparedArtifact;
        var projected = new ServiceInvocationRevisionReadinessState
        {
            Status = revision.Status,
            PreparedArtifactRevisionId = artifact?.RevisionId ?? string.Empty,
            PreparedArtifactCompatible = IsPreparedArtifactCompatible(revisionId, revision, artifact),
        };
        if (artifact != null)
        {
            projected.PreparedEndpointIds.Add(
                artifact.Endpoints
                    .Select(static endpoint => endpoint.EndpointId ?? string.Empty)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static endpointId => endpointId, StringComparer.Ordinal));
        }

        return projected;
    }

    private static bool IsPreparedArtifactCompatible(
        string revisionId,
        ServiceRevisionRecordState revision,
        PreparedServiceRevisionArtifact? artifact)
    {
        if (artifact == null)
            return false;
        if (revision.Spec?.ImplementationKind != ServiceImplementationKind.Workflow)
            return true;

        return WorkflowServiceDeploymentPlanIntegrity.IsCompatible(artifact, revisionId) &&
               !WorkflowServiceArtifactReadiness.RequiresCapabilityAdmissionRebind(artifact);
    }
}
