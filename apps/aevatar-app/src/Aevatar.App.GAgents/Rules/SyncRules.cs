namespace Aevatar.App.GAgents.Rules;

public enum SyncRuleResult
{
    Created,
    Updated,
    Stale
}

public static class SyncRules
{
    public static SyncRuleResult Evaluate(SyncEntity? existing, SyncEntity incoming)
    {
        if (existing is null && incoming.Revision == 0)
            return SyncRuleResult.Created;

        if (existing is not null && incoming.Revision == existing.Revision)
            return SyncRuleResult.Updated;

        return SyncRuleResult.Stale;
    }
}
