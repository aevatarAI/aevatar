using Aevatar.Foundation.Abstractions.EventModules;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowModuleFactory : IEventModuleFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyDictionary<string, ModuleEntry> _modulesByName;

    public WorkflowModuleFactory(
        IServiceProvider serviceProvider,
        IEnumerable<IWorkflowModulePack> modulePacks)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _modulesByName = BuildModuleMap(modulePacks ?? throw new ArgumentNullException(nameof(modulePacks)));
    }

    public bool TryCreate(string name, out IEventModule? module)
    {
        module = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (!_modulesByName.TryGetValue(name, out var entry))
            return false;

        module = entry.Registration.Create(_serviceProvider);
        return module != null;
    }

    private static IReadOnlyDictionary<string, ModuleEntry> BuildModuleMap(
        IEnumerable<IWorkflowModulePack> modulePacks)
    {
        var map = new Dictionary<string, ModuleEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var modulePack in modulePacks)
        {
            var packName = string.IsNullOrWhiteSpace(modulePack.Name) ? modulePack.GetType().Name : modulePack.Name;
            foreach (var registration in modulePack.Modules)
            {
                foreach (var name in registration.Names)
                {
                    if (map.TryGetValue(name, out var existing))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate workflow module name '{name}' found in packs '{existing.PackName}' and '{packName}'.");
                    }

                    map[name] = new ModuleEntry(packName, registration);
                }
            }
        }

        return map;
    }

    private sealed record ModuleEntry(
        string PackName,
        WorkflowModuleRegistration Registration);
}
