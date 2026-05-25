using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.ToolProviders.ToolSetRegistry;

public sealed class ToolSetRegistryOptions
{
    private readonly List<ToolSetRegistration> _registrations = [];

    public IReadOnlyList<ToolSetRegistration> Registrations => _registrations;

    public ToolSetRegistryOptions AddToolSet<TSource>(
        string name,
        string? description = null)
        where TSource : class, IAgentToolSource =>
        AddToolSet(name, sp => sp.GetRequiredService<TSource>(), description);

    public ToolSetRegistryOptions AddToolSet(
        string name,
        Func<IServiceProvider, IAgentToolSource> sourceFactory,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);

        AddRegistration(
            name,
            [],
            [new ToolSetSourceRegistration(sourceFactory)],
            description);
        return this;
    }

    public ToolSetRegistryOptions AddToolSet(
        string name,
        IReadOnlyList<string> includeToolSetNames,
        IReadOnlyList<Func<IServiceProvider, IAgentToolSource>> sourceFactories,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(includeToolSetNames);
        ArgumentNullException.ThrowIfNull(sourceFactories);
        if (includeToolSetNames.Count == 0 && sourceFactories.Count == 0)
        {
            throw new ArgumentException(
                "A tool set must contain at least one included tool set or source factory.",
                nameof(sourceFactories));
        }

        if (sourceFactories.Any(static factory => factory is null))
            throw new ArgumentException("Tool set source factories cannot contain null entries.", nameof(sourceFactories));

        var includes = includeToolSetNames
            .Select(static include => include?.Trim() ?? string.Empty)
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .ToArray();
        if (includes.Length != includeToolSetNames.Count)
            throw new ArgumentException("Included tool set names cannot contain empty entries.", nameof(includeToolSetNames));

        AddRegistration(
            name,
            includes,
            sourceFactories.Select(static factory => new ToolSetSourceRegistration(factory)).ToArray(),
            description);
        return this;
    }

    public ToolSetRegistryOptions AddToolSet(
        string name,
        IReadOnlyList<Func<IServiceProvider, IAgentToolSource>> sourceFactories,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(sourceFactories);
        if (sourceFactories.Count == 0)
            throw new ArgumentException("A tool set must contain at least one source factory.", nameof(sourceFactories));
        if (sourceFactories.Any(static factory => factory is null))
            throw new ArgumentException("Tool set source factories cannot contain null entries.", nameof(sourceFactories));

        AddRegistration(
            name,
            [],
            sourceFactories.Select(static factory => new ToolSetSourceRegistration(factory)).ToArray(),
            description);
        return this;
    }

    public ToolSetRegistryOptions AddToolSet(
        string name,
        IReadOnlyList<Type> sourceTypes,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(sourceTypes);
        if (sourceTypes.Count == 0)
            throw new ArgumentException("A tool set must contain at least one source type.", nameof(sourceTypes));
        foreach (var sourceType in sourceTypes)
        {
            if (!typeof(IAgentToolSource).IsAssignableFrom(sourceType))
            {
                throw new ArgumentException(
                    $"Tool set source type '{sourceType.FullName}' must implement {nameof(IAgentToolSource)}.",
                    nameof(sourceTypes));
            }
        }

        return AddToolSet(
            name,
            sourceTypes.Select(static sourceType =>
            {
                IAgentToolSource Resolve(IServiceProvider sp) =>
                    (IAgentToolSource)sp.GetRequiredService(sourceType);
                return (Func<IServiceProvider, IAgentToolSource>)Resolve;
            }).ToArray(),
            description);
    }

    private void AddRegistration(
        string name,
        IReadOnlyList<string> includeToolSetNames,
        IReadOnlyList<ToolSetSourceRegistration> sources,
        string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(includeToolSetNames);
        ArgumentNullException.ThrowIfNull(sources);

        var normalizedName = name.Trim();
        if (_registrations.Any(registration =>
                string.Equals(registration.Name, normalizedName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Tool set '{normalizedName}' is already registered.");
        }

        _registrations.Add(new ToolSetRegistration(
            normalizedName,
            includeToolSetNames.ToArray(),
            sources.ToArray(),
            description ?? string.Empty));
    }
}

public sealed record ToolSetRegistration(
    string Name,
    IReadOnlyList<string> IncludeToolSetNames,
    IReadOnlyList<ToolSetSourceRegistration> Sources,
    string Description);

public sealed record ToolSetSourceRegistration(
    Func<IServiceProvider, IAgentToolSource> SourceFactory);
