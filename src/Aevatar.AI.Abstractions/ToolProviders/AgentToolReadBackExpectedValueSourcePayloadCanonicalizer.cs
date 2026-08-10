namespace Aevatar.AI.Abstractions.ToolProviders;

public static class AgentToolReadBackExpectedValueSourcePayloadCanonicalizer
{
    public static bool TryGetCanonicalSource(
        AgentToolReadBackAssertionPayload? assertion,
        out AgentToolReadBackExpectedValueSourcePayload source)
    {
        source = AgentToolReadBackExpectedValueSourcePayload.Unspecified;
        if (assertion is null)
            return false;

        var rawSource = (int)assertion.ExpectedValueSource;
        if (UsesExpectedValue(assertion.Match))
        {
            if (assertion.ExpectedValue is not null && rawSource is 0 or 1)
            {
                source = AgentToolReadBackExpectedValueSourcePayload.FrozenValue;
                return true;
            }

            if (assertion.ExpectedValue is null && rawSource is 1 or 2)
            {
                source = AgentToolReadBackExpectedValueSourcePayload.ProviderResourceId;
                return true;
            }

            return false;
        }

        if (rawSource is 0 or 1)
        {
            source = AgentToolReadBackExpectedValueSourcePayload.FrozenValue;
            return true;
        }

        return false;
    }

    public static bool TryCanonicalize(
        AgentToolReadBackAssertionPayload? assertion,
        out AgentToolReadBackAssertionPayload canonical)
    {
        canonical = new AgentToolReadBackAssertionPayload();
        if (!TryGetCanonicalSource(assertion, out var source))
            return false;

        canonical = assertion!.Clone();
        canonical.ExpectedValueSource = source;
        return true;
    }

    public static AgentToolReadBackAssertionPayload CanonicalizeForWrite(
        AgentToolReadBackAssertionPayload assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);

        var valid = UsesExpectedValue(assertion.Match)
            ? assertion.ExpectedValueSource switch
            {
                AgentToolReadBackExpectedValueSourcePayload.FrozenValue =>
                    assertion.ExpectedValue is not null,
                AgentToolReadBackExpectedValueSourcePayload.ProviderResourceId =>
                    assertion.ExpectedValue is null,
                _ => false,
            }
            : assertion.ExpectedValueSource ==
              AgentToolReadBackExpectedValueSourcePayload.FrozenValue;
        if (!valid)
        {
            throw new InvalidOperationException(
                "The read-back expected-value source conflicts with expected-value presence.");
        }

        return assertion;
    }

    private static bool UsesExpectedValue(AgentToolReadBackMatchPayload match) =>
        match is AgentToolReadBackMatchPayload.Equals or
            AgentToolReadBackMatchPayload.ArrayContainsEquals;
}
