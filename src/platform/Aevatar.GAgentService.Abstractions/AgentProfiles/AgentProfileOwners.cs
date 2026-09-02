namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public static class AgentProfileOwners
{
    public const string PlatformId = "aevatar";

    public static AgentProfileOwner ForScope(string scopeId)
    {
        var normalizedScopeId = scopeId?.Trim() ?? string.Empty;
        if (normalizedScopeId.Length == 0)
            throw new ArgumentException("Scope id is required.", nameof(scopeId));

        return new AgentProfileOwner
        {
            Scope = new AgentProfileScopeOwner { ScopeId = normalizedScopeId },
        };
    }

    public static AgentProfileOwner ForSystem() =>
        new()
        {
            System = new AgentProfileSystemOwner { PlatformId = PlatformId },
        };
}
