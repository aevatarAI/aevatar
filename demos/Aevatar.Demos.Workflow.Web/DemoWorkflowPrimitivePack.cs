using Aevatar.Workflow.Core;

namespace Aevatar.Demos.Workflow.Web;

public sealed class DemoWorkflowPrimitivePack : IWorkflowPrimitivePack
{
    private static readonly IReadOnlyList<WorkflowPrimitiveRegistration> ExecutorRegistrations =
    [
        WorkflowPrimitiveRegistration.Create<DemoTemplatePrimitiveExecutor>("demo_template", "demo_format"),
        WorkflowPrimitiveRegistration.Create<DemoCsvMarkdownPrimitiveExecutor>("demo_csv_markdown", "demo_table"),
        WorkflowPrimitiveRegistration.Create<DemoJsonPickPrimitiveExecutor>("demo_json_pick", "demo_json_path"),
    ];

    public string Name => "workflow.demo.web.executors";

    public IReadOnlyList<WorkflowPrimitiveRegistration> Executors => ExecutorRegistrations;
}
