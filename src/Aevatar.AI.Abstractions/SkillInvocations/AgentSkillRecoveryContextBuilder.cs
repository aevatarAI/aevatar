using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Abstractions.SkillInvocations;

public static class AgentSkillRecoveryContextBuilder
{
    public static AgentSkillRecoveryContext FromTrigger(SkillInvocationTrigger? trigger)
    {
        if (trigger is null)
            return AgentSkillRecoveryContext.Empty;

        if (trigger.IsDiscovery)
        {
            return new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: true,
                RequireOrnnSearchOnBlocker: false,
                CommandName: null,
                OriginalCommand: trigger.OriginalText,
                PrimarySkillName: null,
                MaxOrnnSearchAttempts: 1,
                CommandArguments: null,
                DiscoveryRequested: true);
        }

        return new AgentSkillRecoveryContext(
            RequireInitialOrnnSearch: true,
            RequireOrnnSearchOnBlocker: true,
            CommandName: trigger.Name,
            OriginalCommand: trigger.OriginalText,
            PrimarySkillName: trigger.Name,
            MaxOrnnSearchAttempts: 2,
            CommandArguments: trigger.Arguments,
            DiscoveryRequested: false);
    }
}
