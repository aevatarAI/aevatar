using Aevatar.AI.Abstractions;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public static class AgentProfileValidationLimits
{
    public const int DisplayNameMaxUtf8Bytes = 256;
    public const int PurposeMaxUtf8Bytes = 4_096;
    public const int IdentifierMaxUtf8Bytes = AgentProfileExecutionBindingLimits.CanonicalIdentifierMaxUtf8Bytes;
    public const int ExpectedOrnnNameMaxUtf8Bytes = 64;
    public const int PublisherIdMaxUtf8Bytes = 256;
    public const int SkillBindingMaxCount = 32;
    public const int ExplicitToolNameMaxCount = 128;
    public const int ToolSetRefMaxCount = 32;
    public const int ProfileInstructionsMaxUtf8Bytes =
        AgentProfileExecutionBindingLimits.ProfileInstructionsMaxUtf8Bytes;
    public const int RawAuthoritativeAggregateContentMaxUtf8Bytes =
        AgentProfileExecutionBindingLimits.RawAuthoritativeAggregateContentMaxUtf8Bytes;
    public const int RawAuthoritativeAggregateContentMaxEstimatedTokens =
        AgentProfileExecutionBindingLimits.RawAuthoritativeAggregateContentMaxEstimatedTokens;
    public const int MaterializedProfileLayerMaxUtf8Bytes =
        AgentProfileExecutionBindingLimits.MaterializedProfileLayerMaxUtf8Bytes;
    public const int MaterializedProfileLayerMaxEstimatedTokens =
        AgentProfileExecutionBindingLimits.MaterializedProfileLayerMaxEstimatedTokens;
    public const int TextAssetMaxUtf8Bytes = 262_144;
    public const int SealedSkillMaxSerializedBytes = 1_048_576;
    public const int PublishedSnapshotMaxSerializedBytes = 4_194_304;
    public const int DiagnosticMaxCount = 64;
    public const int DiagnosticMessageMaxUtf8Bytes = 512;
}
