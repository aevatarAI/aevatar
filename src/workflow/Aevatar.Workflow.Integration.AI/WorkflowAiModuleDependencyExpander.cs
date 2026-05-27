using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Integration.AI;

// Refactor (iter129/cluster-triage-workflow-llm-nyx-coupling): Old pattern: workflows with llm_call depended on ambient AI role actor handling. New principle: Integration.AI installs the workflow-owned LLM stream adapter when LLM execution is required.
internal sealed class WorkflowAiModuleDependencyExpander : IWorkflowModuleDependencyExpander
{
    public int Order => 250;

    public void Expand(WorkflowDefinition? workflow, ISet<string> moduleNames)
    {
        _ = workflow;
        if (moduleNames.Contains("llm_call"))
            moduleNames.Add("workflow_ai_message_adapter");
    }
}
