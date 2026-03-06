using Aevatar.Workflow.Core;

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
}
