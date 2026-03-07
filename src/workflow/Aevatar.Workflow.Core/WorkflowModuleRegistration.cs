using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Core;

/// <summary>
/// Registration descriptor for one workflow primitive handler implementation with one or more aliases.
/// </summary>
public sealed class WorkflowModuleRegistration
{
    private readonly Func<IServiceProvider, IWorkflowPrimitiveHandler> _factory;

    private WorkflowModuleRegistration(
        Type handlerType,
        Func<IServiceProvider, IWorkflowPrimitiveHandler> factory,
        IReadOnlyList<string> names)
    {
        ModuleType = handlerType;
        _factory = factory;
        Names = names;
    }

    public Type ModuleType { get; }

    public IReadOnlyList<string> Names { get; }

    public static WorkflowModuleRegistration Create<TModule>(params string[] names)
        where TModule : class, IWorkflowPrimitiveHandler
    {
        var cleanedNames = NormalizeNames(names);
        return new WorkflowModuleRegistration(
            typeof(TModule),
            sp => ActivatorUtilities.CreateInstance<TModule>(sp),
            cleanedNames);
    }

    public IWorkflowPrimitiveHandler Create(IServiceProvider serviceProvider)
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
