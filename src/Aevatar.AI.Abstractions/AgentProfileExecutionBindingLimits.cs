namespace Aevatar.AI.Abstractions;

public static class AgentProfileExecutionBindingLimits
{
    public const int CanonicalIdentifierMaxUtf8Bytes = 128;
    public const int ProfileInstructionsMaxUtf8Bytes = 32_768;
    public const int ProfileInstructionsMaxEstimatedTokens =
        (ProfileInstructionsMaxUtf8Bytes + 3) / 4;
    public const int RawAuthoritativeAggregateContentMaxUtf8Bytes = 65_536;
    public const int RawAuthoritativeAggregateContentMaxEstimatedTokens = 65_536;
    public const int MaterializedProfileLayerMaxUtf8Bytes = 65_536;
    public const int MaterializedProfileLayerMaxEstimatedTokens = 65_536;
}
