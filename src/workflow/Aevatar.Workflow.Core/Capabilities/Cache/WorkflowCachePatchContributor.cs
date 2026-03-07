using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowCachePatchContributor : IWorkflowRunStatePatchContributor
{
    public void ContributeBuild(
        WorkflowRunState current,
        WorkflowRunState next,
        WorkflowRunStatePatchedEvent patch,
        ref bool changed)
    {
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.CacheEntries,
            next.CacheEntries,
            value => patch.CacheEntries = value,
            CreateCacheEntryFacts);
        changed |= WorkflowRunStatePatchContributorSupport.AssignMapSliceIfChanged(
            current.PendingCacheCalls,
            next.PendingCacheCalls,
            value => patch.PendingCacheCalls = value,
            CreatePendingCacheFacts);
    }

    public void Apply(WorkflowRunState target, WorkflowRunStatePatchedEvent patch)
    {
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.CacheEntries, patch.CacheEntries?.Entries);
        WorkflowRunStatePatchContributorSupport.ReplaceMapIfPresent(target.PendingCacheCalls, patch.PendingCacheCalls?.Entries);
    }

    private static WorkflowRunCacheEntryFacts CreateCacheEntryFacts(MapField<string, WorkflowCacheEntry> source)
    {
        var facts = new WorkflowRunCacheEntryFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingCacheFacts CreatePendingCacheFacts(MapField<string, WorkflowPendingCacheState> source)
    {
        var facts = new WorkflowRunPendingCacheFacts();
        WorkflowRunStatePatchContributorSupport.CopyMap(source, facts.Entries);
        return facts;
    }
}
