using Aevatar.Foundation.Abstractions.EventModules;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowModuleDescriptor<TModule> : IWorkflowModuleDescriptor
    where TModule : class, IEventModule
{
    private readonly Func<IEventModule> _factory;

    public WorkflowModuleDescriptor(
        Func<IEventModule> factory,
        params string[] names)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

        if (names.Length == 0)
            throw new ArgumentException("At least one module name is required.", nameof(names));

        Names = names
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (Names.Count == 0)
            throw new ArgumentException("At least one non-empty module name is required.", nameof(names));
    }

    public IReadOnlyList<string> Names { get; }

    public IEventModule Create() => _factory();
}
