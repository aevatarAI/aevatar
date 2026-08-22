using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.ToolProviders.ToolSetRegistry;

public sealed class ToolSetRegistry : IToolSetRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyDictionary<string, ToolSetRegistration> _registrations;
    private readonly IReadOnlyList<string> _registeredNames;

    public ToolSetRegistry(
        IServiceProvider serviceProvider,
        IOptions<ToolSetRegistryOptions> options)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        ArgumentNullException.ThrowIfNull(options);

        var registrations = options.Value.Registrations;
        var duplicates = registrations
            .GroupBy(static registration => registration.Name, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate tool set registrations: {string.Join(", ", duplicates)}.");
        }

        _registrations = registrations.ToDictionary(
            static registration => registration.Name,
            StringComparer.Ordinal);
        _registeredNames = _registrations.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> GetRegisteredNames() => _registeredNames;

    public ToolSetResolveResult Resolve(string? name)
    {
        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolSetResolveResult.Failure(new ToolSetResolveError(
                ToolSetResolveError.EmptyNameCode,
                string.Empty,
                "Tool set name is required.",
                _registeredNames));
        }

        if (!_registrations.TryGetValue(name, out var registration))
        {
            return ToolSetResolveResult.Failure(new ToolSetResolveError(
                ToolSetResolveError.UnknownNameCode,
                name,
                $"Tool set '{name}' is not registered.",
                _registeredNames));
        }

        var sources = new List<IAgentToolSource>(registration.Sources.Count);
        var resolutionStack = new HashSet<string>(StringComparer.Ordinal);
        var materializedToolSets = new HashSet<string>(StringComparer.Ordinal);
        AddSources(registration, sources, resolutionStack, materializedToolSets);

        return ToolSetResolveResult.Success(registration.Name, sources);
    }

    private void AddSources(
        ToolSetRegistration registration,
        List<IAgentToolSource> sources,
        HashSet<string> resolutionStack,
        HashSet<string> materializedToolSets)
    {
        if (!resolutionStack.Add(registration.Name))
        {
            throw new InvalidOperationException(
                $"Tool set '{registration.Name}' includes itself through a cycle.");
        }

        foreach (var includeName in registration.IncludeToolSetNames)
        {
            if (!_registrations.TryGetValue(includeName, out var includedRegistration))
            {
                throw new InvalidOperationException(
                    $"Tool set '{registration.Name}' includes unknown tool set '{includeName}'.");
            }

            // A tool set reachable through more than one include path contributes its sources
            // once. Materializing it twice would hand discovery two distinct source instances
            // producing the same tool names, which fails closed as a name collision.
            if (!materializedToolSets.Add(includeName))
                continue;

            AddSources(includedRegistration, sources, resolutionStack, materializedToolSets);
        }

        foreach (var sourceRegistration in registration.Sources)
            sources.Add(sourceRegistration.SourceFactory(_serviceProvider));

        resolutionStack.Remove(registration.Name);
    }
}
