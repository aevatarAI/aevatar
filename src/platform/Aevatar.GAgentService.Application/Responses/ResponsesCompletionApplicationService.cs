using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Responses;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.Responses;

public sealed record ResponsesApplicationToolDeclaration(
    string Name,
    string Description,
    string ParametersJson,
    string SchemaHash);

public sealed record ResponsesToolClassification(
    IReadOnlyList<ResponsesApplicationToolDeclaration> ForwardedTools,
    IReadOnlyList<IAgentTool> EffectiveTools,
    IReadOnlyList<string> SubstitutedToolNames,
    IReadOnlyList<string> AdditiveToolNames);

public static class ResponsesToolClassifier
{
    public static async ValueTask<ResponsesToolClassification> ClassifyAsync(
        IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        IEnumerable<IResponsesToolProvider> providers,
        ResponsesToolProviderContext context,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(declaredTools);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        // Materialize providers once: substitute names are derived from the
        // provider's actual tool list, so there is no second hardcoded registry.
        var providerList = providers as IReadOnlyList<IResponsesToolProvider>
                           ?? providers.ToArray();

        var discoveredSubstituteTools = new List<IAgentTool>();
        foreach (var provider in providerList)
        {
            ct.ThrowIfCancellationRequested();
            discoveredSubstituteTools.AddRange(
                await provider.GetSubstituteToolsAsync(context, ct).ConfigureAwait(false));
        }

        var substituteTools = discoveredSubstituteTools
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var substituteNames = new HashSet<string>(substituteTools.Keys, StringComparer.Ordinal);

        var discoveredAdditiveTools = new List<IAgentTool>();
        foreach (var provider in providerList)
        {
            ct.ThrowIfCancellationRequested();
            discoveredAdditiveTools.AddRange(
                await provider.GetAdditiveToolsAsync(context, ct).ConfigureAwait(false));
        }

        var additiveTools = discoveredAdditiveTools
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();

        var forwarded = new List<ResponsesApplicationToolDeclaration>();
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
            var substitute = substituteTools[declaration.Name];
            if (!string.Equals(
                    ResponsesToolSchemaHasher.Compute(substitute.ParametersSchema),
                    declaration.SchemaHash,
                    StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Responses substitute tool {ToolName} schema differs from client declaration; using Aevatar tool schema.",
                    declaration.Name);
            }

            effective.Add(substitute);
        }

        var effectiveNames = new HashSet<string>(
            effective.Select(static tool => tool.Name),
            StringComparer.Ordinal);
        var addedAdditiveNames = new List<string>();
        foreach (var additive in additiveTools)
        {
            if (!effectiveNames.Add(additive.Name))
            {
                logger.LogWarning(
                    "Responses additive tool {ToolName} skipped because an effective tool with the same name already exists.",
                    additive.Name);
                continue;
            }

            effective.Add(additive);
            addedAdditiveNames.Add(additive.Name);
        }

        return new ResponsesToolClassification(
            forwarded,
            effective,
            substitutedNames,
            addedAdditiveNames);
    }
}

internal sealed class ResponsesForwardedTool : IAgentTool
{
    public ResponsesForwardedTool(ResponsesApplicationToolDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        Name = declaration.Name;
        Description = declaration.Description;
        ParametersSchema = declaration.ParametersJson;
        SchemaHash = declaration.SchemaHash;
    }

    public string Name { get; }

    public string Description { get; }

    public string ParametersSchema { get; }

    public string SchemaHash { get; }

    public bool IsReadOnly => true;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            $"Forwarded Responses tool '{Name}' must be executed by the client, not by Aevatar.");
}

internal static class ResponsesToolSchemaHasher
{
    public static string Compute(string parametersJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(parametersJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

internal sealed class ResponsesToolCallAccumulator
{
    private readonly Dictionary<string, ToolCallAggregate> _aggregates = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private int _anonymousCounter;
    private string? _activeAnonymousKey;

    public ToolCall TrackDelta(ToolCall delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        var aggregate = ResolveAggregate(delta);
        if (!string.IsNullOrWhiteSpace(delta.Name))
            aggregate.Name = delta.Name;

        if (!string.IsNullOrEmpty(delta.ArgumentsJson))
            aggregate.Arguments.Append(delta.ArgumentsJson);

        return new ToolCall
        {
            Id = aggregate.Id,
            Name = string.IsNullOrWhiteSpace(delta.Name)
                ? aggregate.Name ?? string.Empty
                : delta.Name,
            ArgumentsJson = delta.ArgumentsJson ?? string.Empty,
        };
    }

    public IReadOnlyList<ToolCall> BuildToolCalls()
    {
        var result = new List<ToolCall>(_order.Count);
        foreach (var key in _order)
        {
            var aggregate = _aggregates[key];
            result.Add(new ToolCall
            {
                Id = aggregate.Id,
                Name = aggregate.Name ?? string.Empty,
                ArgumentsJson = aggregate.Arguments.ToString(),
            });
        }

        return result;
    }

    private ToolCallAggregate ResolveAggregate(ToolCall delta)
    {
        if (!string.IsNullOrWhiteSpace(delta.Id))
            return ResolveKnownIdAggregate(delta.Id);

        return ResolveAnonymousAggregate();
    }

    private ToolCallAggregate ResolveKnownIdAggregate(string id)
    {
        var knownKey = $"id:{id}";
        if (TryPromoteActiveAnonymousAggregate(knownKey, id, out var promoted))
        {
            _activeAnonymousKey = null;
            return promoted;
        }

        _activeAnonymousKey = null;
        if (!_aggregates.TryGetValue(knownKey, out var aggregate))
        {
            aggregate = new ToolCallAggregate(id);
            _aggregates[knownKey] = aggregate;
            _order.Add(knownKey);
        }

        return aggregate;
    }

    private ToolCallAggregate ResolveAnonymousAggregate()
    {
        if (!string.IsNullOrWhiteSpace(_activeAnonymousKey))
            return _aggregates[_activeAnonymousKey];

        _anonymousCounter++;
        var anonymousKey = $"anon:{_anonymousCounter}";
        var anonymousId = $"stream-tool-call-{_anonymousCounter}";
        var aggregate = new ToolCallAggregate(anonymousId);
        _aggregates[anonymousKey] = aggregate;
        _order.Add(anonymousKey);
        _activeAnonymousKey = anonymousKey;
        return aggregate;
    }

    private bool TryPromoteActiveAnonymousAggregate(
        string knownKey,
        string knownId,
        out ToolCallAggregate aggregate)
    {
        aggregate = default!;

        if (string.IsNullOrWhiteSpace(_activeAnonymousKey))
            return false;

        var anonymousAggregate = _aggregates[_activeAnonymousKey];

        if (_aggregates.ContainsKey(knownKey))
            return false;

        anonymousAggregate.Id = knownId;
        _aggregates.Remove(_activeAnonymousKey);
        _aggregates[knownKey] = anonymousAggregate;
        ReplaceOrderKey(_activeAnonymousKey, knownKey);
        aggregate = anonymousAggregate;
        return true;
    }

    private void ReplaceOrderKey(string sourceKey, string targetKey)
    {
        _order[_order.IndexOf(sourceKey)] = targetKey;
    }

    private sealed class ToolCallAggregate
    {
        public ToolCallAggregate(string id)
        {
            Id = id;
        }

        public string Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
