using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Demos.Workflow.Web;

public sealed class DemoWorkflowModuleFactory : IEventModuleFactory
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
        IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public bool TryCreate(string name, out IEventModule? module)
    {
        module = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (!DemoModuleTypes.TryGetValue(name.Trim(), out var moduleType))
            return false;

        module = (IEventModule)ActivatorUtilities.CreateInstance(_serviceProvider, moduleType);
        return true;
    }
}
