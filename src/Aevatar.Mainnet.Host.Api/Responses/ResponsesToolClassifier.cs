using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal interface IResponsesToolProvider
{
    IReadOnlyList<IAgentTool> GetSubstituteTools() => [];

    IReadOnlyList<IAgentTool> GetAdditiveTools() => [];
}

internal sealed record ResponsesToolClassification(
    IReadOnlyList<ResponsesToolDeclaration> ForwardedTools,
    IReadOnlyList<IAgentTool> EffectiveTools,
    IReadOnlyList<string> SubstitutedToolNames,
    IReadOnlyList<string> AdditiveToolNames);

internal static class ResponsesToolClassifier
{
    public static ResponsesToolClassification Classify(
        IReadOnlyList<ResponsesToolDeclaration> declaredTools,
        IEnumerable<IResponsesToolProvider> providers,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(declaredTools);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(logger);

        // Materialize providers once — substitute names are derived from the
        // provider's actual tool list, so there is no second hardcoded
        // registry to keep in sync.
        var providerList = providers as IReadOnlyList<IResponsesToolProvider>
                           ?? providers.ToArray();
        var substituteTools = providerList
            .SelectMany(static provider => provider.GetSubstituteTools())
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var substituteNames = new HashSet<string>(substituteTools.Keys, StringComparer.Ordinal);
        var additiveTools = providerList
            .SelectMany(static provider => provider.GetAdditiveTools())
            .Where(static tool => tool.Name.StartsWith("aevatar_", StringComparison.Ordinal))
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();

        var forwarded = new List<ResponsesToolDeclaration>();
        var effective = new List<IAgentTool>();
        var substitutedNames = new List<string>();

        foreach (var declaration in declaredTools)
        {
            if (!substituteNames.Contains(declaration.Name))
            {
                forwarded.Add(declaration);
                effective.Add(new ResponsesForwardedTool(declaration));
                continue;
            }

            substitutedNames.Add(declaration.Name);
            if (substituteTools.TryGetValue(declaration.Name, out var substitute))
            {
                if (!string.Equals(
                        ResponsesToolSchemaHashes.Compute(substitute.ParametersSchema),
                        declaration.SchemaHash,
                        StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Responses substitute tool {ToolName} schema differs from client declaration; using Aevatar tool schema.",
                        declaration.Name);
                }

                effective.Add(substitute);
                continue;
            }

            logger.LogWarning(
                "Responses substitute tool {ToolName} has no registered Aevatar implementation; using unavailable stub.",
                declaration.Name);
            effective.Add(new ResponsesUnavailableSubstituteTool(declaration));
        }

        effective.AddRange(additiveTools);

        return new ResponsesToolClassification(
            forwarded,
            effective,
            substitutedNames,
            additiveTools.Select(static tool => tool.Name).ToArray());
    }

    private sealed class ResponsesUnavailableSubstituteTool : IAgentTool
    {
        private readonly ResponsesToolDeclaration _declaration;

        public ResponsesUnavailableSubstituteTool(ResponsesToolDeclaration declaration)
        {
            _declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
        }

        public string Name => _declaration.Name;

        public string Description => _declaration.Description;

        public string ParametersSchema => _declaration.ParametersJson;

        public bool IsReadOnly => false;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            var payload = new
            {
                error = "aevatar_substitute_tool_unavailable",
                tool_name = Name,
            };
            return Task.FromResult(JsonSerializer.Serialize(payload));
        }
    }
}
