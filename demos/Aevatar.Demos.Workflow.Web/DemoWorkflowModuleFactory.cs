using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Demos.Workflow.Web;

public sealed class DemoWorkflowModuleFactory
    : WorkflowModuleFactory,
        IEventModuleFactory<IEventHandlerContext>
{
    private readonly IServiceProvider _serviceProvider;

    private static readonly IReadOnlyDictionary<string, Type> DemoModuleTypes =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["demo_template"] = typeof(DemoTemplateModule),
            ["demo_format"] = typeof(DemoTemplateModule),
            ["demo_csv_markdown"] = typeof(DemoCsvMarkdownModule),
            ["demo_table"] = typeof(DemoCsvMarkdownModule),
            ["demo_json_pick"] = typeof(DemoJsonPickModule),
            ["demo_json_path"] = typeof(DemoJsonPickModule),
        };

    public DemoWorkflowModuleFactory(
        IServiceProvider serviceProvider,
        IEnumerable<IWorkflowModulePack> modulePacks)
        : base(serviceProvider, modulePacks)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public override bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
    {
        if (base.TryCreate(name, out module) && module != null)
            return true;

        module = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (!DemoModuleTypes.TryGetValue(name.Trim(), out var moduleType))
            return false;

        module = (IEventModule<IWorkflowExecutionContext>)ActivatorUtilities.CreateInstance(_serviceProvider, moduleType);
        return true;
    }

    bool IEventModuleFactory<IEventHandlerContext>.TryCreate(
        string name,
        out IEventModule<IEventHandlerContext>? module)
    {
        module = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (!DemoModuleTypes.TryGetValue(name.Trim(), out var moduleType))
            return false;

        module = (IEventModule<IEventHandlerContext>)ActivatorUtilities.CreateInstance(_serviceProvider, moduleType);
        return true;
    }
}
