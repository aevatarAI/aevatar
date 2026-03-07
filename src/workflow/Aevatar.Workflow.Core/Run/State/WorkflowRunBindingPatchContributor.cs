namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunBindingPatchContributor : IWorkflowRunStatePatchContributor
{
    public void ContributeBuild(
        WorkflowRunState current,
        WorkflowRunState next,
        WorkflowRunStatePatchedEvent patch,
        ref bool changed)
    {
        if (!string.Equals(current.WorkflowName, next.WorkflowName, StringComparison.Ordinal) ||
            !string.Equals(current.WorkflowYaml, next.WorkflowYaml, StringComparison.Ordinal) ||
            current.Compiled != next.Compiled ||
            !string.Equals(current.CompilationError, next.CompilationError, StringComparison.Ordinal) ||
            !WorkflowRunStatePatchContributorSupport.MapEquals(current.InlineWorkflowYamls, next.InlineWorkflowYamls))
        {
            patch.Binding = new WorkflowRunBindingFacts
            {
                WorkflowName = next.WorkflowName ?? string.Empty,
                WorkflowYaml = next.WorkflowYaml ?? string.Empty,
                Compiled = next.Compiled,
                CompilationError = next.CompilationError ?? string.Empty,
            };
            WorkflowRunStatePatchContributorSupport.CopyMap(next.InlineWorkflowYamls, patch.Binding.InlineWorkflowYamls);
            changed = true;
        }
    }

    public void Apply(WorkflowRunState target, WorkflowRunStatePatchedEvent patch)
    {
        if (patch.Binding == null)
            return;

        target.WorkflowName = patch.Binding.WorkflowName;
        target.WorkflowYaml = patch.Binding.WorkflowYaml;
        target.Compiled = patch.Binding.Compiled;
        target.CompilationError = patch.Binding.CompilationError;
        WorkflowRunStatePatchContributorSupport.ReplaceMap(target.InlineWorkflowYamls, patch.Binding.InlineWorkflowYamls);
    }
}
