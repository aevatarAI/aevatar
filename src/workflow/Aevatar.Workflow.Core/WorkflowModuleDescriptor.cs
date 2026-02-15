using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowModuleDescriptor<TModule> : IWorkflowModuleDescriptor
    where TModule : class, IEventModule
{
    public WorkflowModuleDescriptor(params string[] names)
    {
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

    public IEventModule Create(IServiceProvider services) =>
        ActivatorUtilities.CreateInstance<TModule>(services);
}
