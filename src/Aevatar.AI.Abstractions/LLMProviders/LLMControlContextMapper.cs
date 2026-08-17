namespace Aevatar.AI.Abstractions.LLMProviders;

public static class LLMControlContextMapper
{
    public static LLMControlContext FromPayload(LLMControlContextPayload? payload)
    {
        if (payload == null)
            return LLMControlContext.Empty;

        return new LLMControlContext(
            LLMControlContext.Normalize(payload.NyxIdAccessToken),
            LLMControlContext.Normalize(payload.NyxIdOrgToken),
            LLMControlContext.Normalize(payload.SenderNyxIdAccessToken),
            LLMControlContext.Normalize(payload.ModelOverride),
            LLMControlContext.Normalize(payload.NyxIdRoutePreference),
            payload.HasMaxToolRoundsOverride ? payload.MaxToolRoundsOverride : null,
            LLMControlContext.Normalize(payload.UserMemoryPrompt))
        {
            RouteTarget = payload.RouteTarget?.Clone(),
        };
    }

    public static LLMControlContextPayload ToPayload(this LLMControlContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = new LLMControlContextPayload
        {
            NyxIdAccessToken = context.NyxIdAccessToken ?? string.Empty,
            NyxIdOrgToken = context.NyxIdOrgToken ?? string.Empty,
            SenderNyxIdAccessToken = context.SenderNyxIdAccessToken ?? string.Empty,
            ModelOverride = context.ModelOverride ?? string.Empty,
            NyxIdRoutePreference = context.NyxIdRoutePreference ?? string.Empty,
            UserMemoryPrompt = context.UserMemoryPrompt ?? string.Empty,
            RouteTarget = context.RouteTarget?.Clone(),
        };

        if (context.MaxToolRoundsOverride.HasValue)
            payload.MaxToolRoundsOverride = context.MaxToolRoundsOverride.Value;

        return payload;
    }
}
