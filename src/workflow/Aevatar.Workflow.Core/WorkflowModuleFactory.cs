using Aevatar.Foundation.Abstractions.EventModules;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowModuleFactory : IEventModuleFactory
{
    private readonly IReadOnlyDictionary<string, IWorkflowModuleDescriptor> _descriptorsByName;

    public WorkflowModuleFactory(IEnumerable<IWorkflowModuleDescriptor> descriptors)
    {
        _descriptorsByName = BuildDescriptorMap(descriptors);
    }

    public bool TryCreate(string name, out IEventModule? module)
    {
        module = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (!_descriptorsByName.TryGetValue(name, out var descriptor))
            return false;

        module = descriptor.Create();
        return module != null;
    }

    private static IReadOnlyDictionary<string, IWorkflowModuleDescriptor> BuildDescriptorMap(
        IEnumerable<IWorkflowModuleDescriptor> descriptors)
    {
        var map = new Dictionary<string, IWorkflowModuleDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            foreach (var name in descriptor.Names.Where(x => !string.IsNullOrWhiteSpace(x)))
                map[name] = descriptor;
        }

        return map;
    }
}
