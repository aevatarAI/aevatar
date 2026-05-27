using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

// Refactor (iter129/cluster-triage-workflow-llm-nyx-coupling): Old pattern: LLM step execution resolved role actors as provider execution targets. New principle: core configures role LLM settings and emits workflow-owned streaming intents for integration adapters.
internal sealed class LLMCallModuleConfigurator : WorkflowModuleConfiguratorBase<LLMCallModule>
{
    protected override void Configure(LLMCallModule module, WorkflowDefinition workflow)
    {
        module.ConfigureRoles(workflow.Roles);
    }
}
