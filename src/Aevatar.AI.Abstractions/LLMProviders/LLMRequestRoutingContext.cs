namespace Aevatar.AI.Abstractions.LLMProviders;

// Refactor (iter24/cluster-002-agent-tool-context-generic-metadata-bag):
//   Old pattern: model, route, tool-round controls lived in request Metadata keys.
//   New principle: owned LLM routing controls are typed fields; Metadata is external passthrough only.
public sealed record LLMRequestRoutingContext(
    string? ModelOverride,
    string? NyxIdRoutePreference,
    int? MaxToolRoundsOverride,
    string? UserMemoryPrompt)
{
    public static LLMRequestRoutingContext Empty { get; } = new(null, null, null, null);

    public LLMRouteTarget? RouteTarget { get; init; }
}
