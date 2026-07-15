namespace Aevatar.AI.Abstractions.LLMProviders;

public static class LLMControlContextMapper
{
    public static LLMControlContext FromPayload(LLMControlContextPayload? payload)
    {
        if (payload == null)
            return LLMControlContext.Empty;

        return new LLMControlContext(
            LLMControlContext.Normalize(payload.CredentialRef),
            LLMControlContext.Normalize(payload.OrganizationCredentialRef),
            LLMControlContext.Normalize(payload.SenderCredentialRef),
            LLMControlContext.Normalize(payload.ModelOverride),
            LLMControlContext.Normalize(payload.NyxIdRoutePreference),
            payload.HasMaxToolRoundsOverride ? payload.MaxToolRoundsOverride : null,
            LLMControlContext.Normalize(payload.UserMemoryPrompt));
    }

    public static LLMControlContextPayload ToPayload(this LLMControlContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = new LLMControlContextPayload
        {
            CredentialRef = context.CredentialRef ?? string.Empty,
            OrganizationCredentialRef = context.OrganizationCredentialRef ?? string.Empty,
            SenderCredentialRef = context.SenderCredentialRef ?? string.Empty,
            ModelOverride = context.ModelOverride ?? string.Empty,
            NyxIdRoutePreference = context.NyxIdRoutePreference ?? string.Empty,
            UserMemoryPrompt = context.UserMemoryPrompt ?? string.Empty,
        };

        if (context.MaxToolRoundsOverride.HasValue)
            payload.MaxToolRoundsOverride = context.MaxToolRoundsOverride.Value;

        return payload;
    }
}
