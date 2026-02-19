using Aevatar.Platform.Abstractions.Catalog;
using Microsoft.Extensions.Options;

namespace Aevatar.Platform.Infrastructure.Catalog;

public sealed class BuiltInAgentCatalog : IAgentCatalog, IAgentCommandRouter, IAgentQueryRouter
{
    private readonly IReadOnlyDictionary<string, SubsystemEndpointRegistration> _registrations;
    private readonly IReadOnlyList<AgentCapability> _capabilities;

    public BuiltInAgentCatalog(IOptions<SubsystemEndpointOptions> options)
    {
        var registrations = options.Value.ResolveRegistrations();
        var map = new Dictionary<string, SubsystemEndpointRegistration>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in registrations)
        {
            if (string.IsNullOrWhiteSpace(registration.Subsystem))
                continue;
            if (string.IsNullOrWhiteSpace(registration.BaseUrl))
                continue;

            map[registration.Subsystem] = registration;
        }

        _registrations = map;
        _capabilities = _registrations.Values
            .Select(x => new AgentCapability(
                AgentType: x.AgentType,
                Subsystem: x.Subsystem,
                CommandEndpoint: BuildAbsoluteEndpoint(x.BaseUrl, x.CommandEndpointPath),
                QueryEndpoint: BuildAbsoluteEndpoint(x.BaseUrl, x.QueryEndpointPath),
                StreamEndpoint: BuildAbsoluteEndpoint(x.BaseUrl, x.StreamEndpointPath)))
            .ToList();
    }

    public IReadOnlyList<AgentCapability> List() => _capabilities;

    public Uri? Resolve(string subsystem, string command) =>
        ResolveInternal(subsystem, command, resolveQuery: false);

    Uri? IAgentQueryRouter.Resolve(string subsystem, string query) =>
        ResolveInternal(subsystem, query, resolveQuery: true);

    private Uri? ResolveInternal(string subsystem, string path, bool resolveQuery)
    {
        if (string.IsNullOrWhiteSpace(subsystem) || string.IsNullOrWhiteSpace(path))
            return null;
        if (!_registrations.TryGetValue(subsystem, out var registration))
            return null;

        var template = resolveQuery
            ? registration.QueryResolveTemplate
            : registration.CommandResolveTemplate;
        var routePath = ApplyTemplate(template, path);
        return new Uri($"{registration.BaseUrl.TrimEnd('/')}{routePath}");
    }

    private static string BuildAbsoluteEndpoint(string baseUrl, string endpointPath)
    {
        if (string.IsNullOrWhiteSpace(endpointPath))
            return string.Empty;

        if (Uri.TryCreate(endpointPath, UriKind.Absolute, out _))
            return endpointPath;

        var normalized = endpointPath.StartsWith('/')
            ? endpointPath
            : $"/{endpointPath}";
        return $"{baseUrl.TrimEnd('/')}{normalized}";
    }

    private static string ApplyTemplate(string template, string path)
    {
        var normalizedTemplate = string.IsNullOrWhiteSpace(template)
            ? "/api/{path}"
            : template;
        var normalizedPath = path.TrimStart('/');
        var routePath = normalizedTemplate.Replace("{path}", normalizedPath, StringComparison.OrdinalIgnoreCase);
        return routePath.StartsWith('/')
            ? routePath
            : $"/{routePath}";
    }
}
