using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Core;

/// <summary>
/// Registration descriptor for one workflow module implementation with one or more aliases.
/// </summary>
public sealed class WorkflowModuleRegistration
{
    private readonly Func<IServiceProvider, IEventModule<IWorkflowExecutionContext>> _factory;

    private WorkflowModuleRegistration(
        Type moduleType,
        Func<IServiceProvider, IEventModule<IWorkflowExecutionContext>> factory,
        IReadOnlyList<string> names)
    {
        ModuleType = moduleType;
        _factory = factory;
        Names = names;
    }

    public Type ModuleType { get; }

    public IReadOnlyList<string> Names { get; }

    public static WorkflowModuleRegistration Create<TModule>(params string[] names)
        where TModule : class, IEventModule<IWorkflowExecutionContext>
    {
        var cleanedNames = NormalizeNames(names);
        return new WorkflowModuleRegistration(
            typeof(TModule),
            sp => ActivatorUtilities.CreateInstance<TModule>(sp),
            cleanedNames);
    }

    public IEventModule<IWorkflowExecutionContext> Create(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return _factory(serviceProvider);
    }

    private static IReadOnlyList<string> NormalizeNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var cleaned = names
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (cleaned.Length == 0)
            throw new ArgumentException("At least one non-empty module name is required.", nameof(names));

        return cleaned;
    }
}
