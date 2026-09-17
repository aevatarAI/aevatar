namespace Aevatar.AI.Abstractions.SkillInvocations;

public sealed record SkillInvocationTrigger(
    string? Name,
    string Arguments,
    bool IsDiscovery,
    string OriginalText,
    string TriggerToken,
    string Platform,
    bool MountWorkflowsRequested = false);
