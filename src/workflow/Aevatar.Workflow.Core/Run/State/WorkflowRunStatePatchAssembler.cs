namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunStatePatchAssembler
{
    private readonly IReadOnlyList<IWorkflowRunStatePatchContributor> _contributors;

    public WorkflowRunStatePatchAssembler(IEnumerable<IWorkflowRunStatePatchContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        _contributors = contributors.ToArray();
        if (_contributors.Count == 0)
            throw new InvalidOperationException("Workflow run state patch assembler requires contributors.");
    }

    public WorkflowRunStatePatchedEvent? BuildPatch(WorkflowRunState current, WorkflowRunState next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);

        var patch = new WorkflowRunStatePatchedEvent();
        var changed = false;
        foreach (var contributor in _contributors)
            contributor.ContributeBuild(current, next, patch, ref changed);

        return changed ? patch : null;
    }

    public WorkflowRunState ApplyPatch(WorkflowRunState current, WorkflowRunStatePatchedEvent patch)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(patch);

        var next = current.Clone();
        foreach (var contributor in _contributors)
            contributor.Apply(next, patch);

        return next;
    }
}
