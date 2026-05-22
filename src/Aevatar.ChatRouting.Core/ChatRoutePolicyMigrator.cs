using Aevatar.ChatRouting.Abstractions;

namespace Aevatar.ChatRouting.Core;

public static class ChatRoutePolicyMigrator
{
    public static ChatRoutePolicySnapshot MigrateLegacyActions(ChatRoutePolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ChatRoutePolicySnapshot(
            ChatRouteActionTranslator.ToCanonicalPolicyAction(snapshot.DefaultTarget),
            snapshot.Rules.Select(MigrateRule));
    }

    private static ChatRouteRule MigrateRule(ChatRouteRule rule)
    {
        var migrated = rule.Clone();
        if (migrated.Action is not null &&
            migrated.Action.ActionCase != ChatRouteAction.ActionOneofCase.None)
        {
            migrated.Action = ChatRouteActionTranslator.ToCanonicalPolicyAction(migrated.Action);
        }

        return migrated;
    }
}
