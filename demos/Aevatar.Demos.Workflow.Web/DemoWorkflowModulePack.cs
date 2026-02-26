using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;

namespace Aevatar.Demos.Workflow.Web;

public sealed class DemoWorkflowModulePack : IWorkflowModulePack
{
    private static readonly IReadOnlyList<WorkflowModuleRegistration> ModuleRegistrations =
    [
        WorkflowModuleRegistration.Create<DemoTemplateModule>("demo_template", "demo_format"),
        WorkflowModuleRegistration.Create<DemoCsvMarkdownModule>("demo_csv_markdown", "demo_table"),
        WorkflowModuleRegistration.Create<DemoJsonPickModule>("demo_json_pick", "demo_json_path"),
    ];

    public string Name => "workflow.demo.web.modules";

    public IReadOnlyList<WorkflowModuleRegistration> Modules => ModuleRegistrations;

    public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders => [];

    public IReadOnlyList<IWorkflowModuleConfigurator> Configurators => [];
}
