namespace Aevatar.GAgentService.Core.AgentProfiles;

public static class AgentProfileOperationRetentionPolicy
{
    public const int MaxRetainedProfileMutationOperations = 256;
    public const int MaxRetainedNamespaceTerminalOperations = 1_024;
}
