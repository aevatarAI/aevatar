using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdConnectedServiceToolSource : IAgentToolSource
{
    private readonly NyxIdToolOptions _options;
    private readonly NyxIdServiceInstanceClient _client;
    private readonly ILogger _logger;

    public NyxIdConnectedServiceToolSource(
        NyxIdToolOptions options,
        NyxIdServiceInstanceClient client,
        ILogger<NyxIdConnectedServiceToolSource>? logger = null)
    {
        _options = options;
        _client = client;
        _logger = logger ?? NullLogger<NyxIdConnectedServiceToolSource>.Instance;
    }

    public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return [];
        var userToken = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(userToken))
            return [];

        try
        {
            var discovered = await _client.DiscoverAsync(
                userToken,
                AgentToolRequestContext.NyxIdOrgToken,
                ct);
            var bindings = discovered
                .Where(static binding =>
                    binding.Instance.IsActive &&
                    binding.Instance.CredentialAllowed &&
                    !string.IsNullOrWhiteSpace(binding.Instance.ProxySpecServiceId))
                .ToArray();
            if (bindings.Length == 0)
                return [];

            var tools = new List<IAgentTool>(NyxIdServiceTools.Create(_client, bindings));
            var candidates = await DiscoverOperationCandidatesAsync(bindings, ct);
            tools.AddRange(CreateOperationTools(candidates));
            return ExactAgentToolSet.Create(tools).ToolsByName.Values.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID connected-service tool discovery failed");
            return [];
        }
    }

    private async Task<IReadOnlyList<OperationCandidate>> DiscoverOperationCandidatesAsync(
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings,
        CancellationToken ct)
    {
        var candidates = new List<OperationCandidate>();
        foreach (var binding in bindings)
        {
            try
            {
                var spec = await _client.GetSpecAsync(binding, ct);
                candidates.AddRange(OpenApiToolSpecParser.Parse(spec)
                    .AdmittedOperations()
                    .Select(operation => new OperationCandidate(
                        ConnectedServiceToolNaming.Build(operation.Marker?.Name ?? operation.OperationId),
                        operation,
                        binding)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "NyxID connected-service spec discovery failed for instance {UserServiceId}",
                    binding.Instance.UserServiceId);
            }
        }
        return candidates;
    }

    private IEnumerable<IAgentTool> CreateOperationTools(IReadOnlyList<OperationCandidate> candidates)
    {
        foreach (var group in candidates.GroupBy(static candidate => candidate.Name, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var contract = first.Operation.CanonicalContract();
            if (group.Any(candidate =>
                    !string.Equals(contract, candidate.Operation.CanonicalContract(), StringComparison.Ordinal)))
            {
                _logger.LogWarning("NyxID operation contract collision on tool {ToolName}", group.Key);
                continue;
            }
            if (group.Any(candidate =>
                    !Equals(first.Binding.Instance.RouteConstraint, candidate.Binding.Instance.RouteConstraint)))
            {
                _logger.LogWarning("NyxID operation route collision on tool {ToolName}", group.Key);
                continue;
            }

            var groupedBindings = ResolveBindings(group);
            if (groupedBindings.Count > 0)
                yield return new ConnectedServiceOperationTool(_client, group.Key, first.Operation, groupedBindings);
        }
    }

    private static IReadOnlyList<NyxIdServiceInstanceBinding> ResolveBindings(IEnumerable<OperationCandidate> candidates)
    {
        var bindings = new Dictionary<string, NyxIdServiceInstanceBinding>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var id = candidate.Binding.Instance.UserServiceId;
            if (conflicts.Contains(id))
                continue;
            if (!bindings.TryGetValue(id, out var existing))
            {
                bindings.Add(id, candidate.Binding);
                continue;
            }
            if (ReferenceEquals(existing, candidate.Binding))
                continue;

            bindings.Remove(id);
            conflicts.Add(id);
        }
        return bindings.Values.OrderBy(static binding => binding.Instance.UserServiceId, StringComparer.Ordinal).ToArray();
    }

    private sealed record OperationCandidate(
        string Name,
        ConnectedServiceToolOperation Operation,
        NyxIdServiceInstanceBinding Binding);
}
