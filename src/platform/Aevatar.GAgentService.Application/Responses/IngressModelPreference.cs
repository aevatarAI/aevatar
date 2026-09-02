namespace Aevatar.GAgentService.Application.Responses;

/// <summary>
/// Single precedence for the OpenAI-compatible ingress preferred model, shared by the
/// Chat Completions / Responses / Messages facades so the resolution can never drift across
/// entry points:
/// <para>explicit caller model &gt; account UserConfig preference &gt; chat route policy
/// <c>ForwardToModel</c> &gt; deployment default.</para>
/// The account UserConfig (<c>DefaultModel</c>/<c>PreferredLlmRoute</c>) is the single
/// "preferred model" source; an explicit caller-supplied <c>model</c> is a per-request override
/// (standard OpenAI semantics); a stale per-owner route-policy <c>ForwardToModel</c> can no longer
/// silently swap the model — it only fills in when neither the caller nor the account specified one.
/// </summary>
internal static class IngressModelPreference
{
    /// <param name="deploymentDefaultModel">
    /// The already-normalized fallback model (the caller value when supplied, otherwise the
    /// configured ingress default). Always non-empty after request normalization, so it is the
    /// guaranteed tail of the chain.
    /// </param>
    public static string ResolveModel(
        string? explicitCallerModel,
        string? accountDefaultModel,
        string? routePolicyForwardModel,
        string deploymentDefaultModel) =>
        QualifiedOrNull(explicitCallerModel)
        ?? Normalize(accountDefaultModel)
        ?? Normalize(routePolicyForwardModel)
        ?? deploymentDefaultModel;

    // Only a *qualified* explicit model (one carrying a vendor slug, e.g. "chrono-llm/gpt-5.5")
    // is treated as a fully-explicit caller choice that wins outright. A bare model (e.g.
    // "gpt-4o-mini") is under-specified, so routing/account may still complete it — it falls
    // through to deploymentDefaultModel (which IS the bare caller value when one was supplied).
    private static string? QualifiedOrNull(string? model)
    {
        var normalized = Normalize(model);
        return normalized is not null && ResponsesModelRouteParser.Parse(normalized).RouteSlug is not null
            ? normalized
            : null;
    }

    internal static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
