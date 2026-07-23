namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public static class AgentProfileValidationLimits
{
    public const int DisplayNameMaxUtf8Bytes = 256;
    public const int PurposeMaxUtf8Bytes = 4_096;
    public const int IdentifierMaxUtf8Bytes = 128;
    public const int ExpectedOrnnNameMaxUtf8Bytes = 64;
    public const int PublisherIdMaxUtf8Bytes = 256;
    public const int SkillBindingMaxCount = 32;
    public const int ExplicitToolNameMaxCount = 128;
    public const int ToolSetRefMaxCount = 32;
    public const int ProfileInstructionsMaxUtf8Bytes = 32_768;
    public const int AggregatePromptMaxUtf8Bytes = 65_536;
    public const int AggregatePromptMaxTokens = 65_536;
    public const int TextAssetMaxUtf8Bytes = 262_144;
    public const int SealedSkillMaxSerializedBytes = 1_048_576;
    public const int PublishedSnapshotMaxSerializedBytes = 4_194_304;
    public const int DiagnosticMaxCount = 64;
    public const int DiagnosticMessageMaxUtf8Bytes = 512;
}
