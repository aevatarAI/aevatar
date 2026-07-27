using System.Collections.Frozen;
using Aevatar.AI.Abstractions;

namespace Aevatar.Mainnet.Host.Api.AgentProfiles;

public sealed class NyxIdChatAgentProfileOptions
{
    public const string SectionName = "Aevatar:AgentProfiles:NyxIdChat";
    public const string StableExternalReference = "nyxid-chat";

    public bool Enabled { get; set; }

    public string ExternalReference { get; set; } = StableExternalReference;

    public AgentProfileSnapshot? Profile { get; set; }
}

public sealed class NyxIdChatAgentProfileValidationBaseline
{
    public NyxIdChatAgentProfileValidationBaseline(
        IEnumerable<string> requiredRecoveryToolNames,
        IEnumerable<string> deniedLegacyToolNames)
    {
        ArgumentNullException.ThrowIfNull(requiredRecoveryToolNames);
        ArgumentNullException.ThrowIfNull(deniedLegacyToolNames);
        RequiredRecoveryToolNames = FreezeNames(
            requiredRecoveryToolNames,
            nameof(requiredRecoveryToolNames));
        DeniedLegacyToolNames = FreezeNames(
            deniedLegacyToolNames,
            nameof(deniedLegacyToolNames));
    }

    public IReadOnlySet<string> RequiredRecoveryToolNames { get; }

    public IReadOnlySet<string> DeniedLegacyToolNames { get; }

    private static FrozenSet<string> FreezeNames(IEnumerable<string> source, string parameterName)
    {
        var names = source.ToArray();
        var frozen = names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        if (frozen.Count != names.Length)
            throw new ArgumentException("Tool names cannot contain duplicates ignoring case.", parameterName);
        return frozen;
    }
}
