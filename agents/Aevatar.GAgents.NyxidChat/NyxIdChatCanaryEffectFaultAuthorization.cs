namespace Aevatar.GAgents.NyxidChat;

public sealed class NyxIdChatCanaryEffectFaultOptions
{
    public const string ConfigSection = "Aevatar:NyxId:CanaryEffectFault";

    public bool Enabled { get; init; }

    public ISet<string> AllowedOwnerSubjects { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public static NyxIdChatCanaryEffectFaultOptions EnabledFor(params string[] ownerSubjects) =>
        new()
        {
            Enabled = true,
            AllowedOwnerSubjects = ownerSubjects
                .Where(static subject => !string.IsNullOrWhiteSpace(subject))
                .Select(static subject => subject.Trim())
                .ToHashSet(StringComparer.Ordinal),
        };
}

internal interface INyxIdChatCanaryEffectFaultAuthorizationPolicy
{
    bool CanArm(string ownerSubject);
}

internal sealed class NyxIdChatCanaryEffectFaultAuthorizationPolicy(
    NyxIdChatCanaryEffectFaultOptions options)
    : INyxIdChatCanaryEffectFaultAuthorizationPolicy
{
    public bool CanArm(string ownerSubject) =>
        options.Enabled &&
        !string.IsNullOrWhiteSpace(ownerSubject) &&
        options.AllowedOwnerSubjects.Contains(ownerSubject.Trim());
}
