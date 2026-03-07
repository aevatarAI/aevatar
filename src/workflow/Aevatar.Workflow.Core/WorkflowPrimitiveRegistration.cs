using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Core;

/// <summary>
/// Registration descriptor for one workflow primitive handler implementation with one or more aliases.
/// </summary>
public sealed class WorkflowPrimitiveRegistration
{
    private readonly Func<IServiceProvider, IWorkflowPrimitiveExecutor> _factory;

    private WorkflowPrimitiveRegistration(
        Type handlerType,
        Func<IServiceProvider, IWorkflowPrimitiveExecutor> factory,
        IReadOnlyList<string> names)
    {
        ModuleType = handlerType;
        _factory = factory;
        Names = names;
    }

    public Type ModuleType { get; }

    public IReadOnlyList<string> Names { get; }

    public static WorkflowPrimitiveRegistration Create<TModule>(params string[] names)
        where TModule : class, IWorkflowPrimitiveExecutor
    {
        var cleanedNames = NormalizeNames(names);
        return new WorkflowPrimitiveRegistration(
            typeof(TModule),
            sp => ActivatorUtilities.CreateInstance<TModule>(sp),
            cleanedNames);
    }

    public IWorkflowPrimitiveExecutor Create(IServiceProvider serviceProvider)
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
