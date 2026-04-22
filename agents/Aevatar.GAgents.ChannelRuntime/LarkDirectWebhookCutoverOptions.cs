namespace Aevatar.GAgents.ChannelRuntime;

public sealed class LarkDirectWebhookCutoverOptions
{
    public const string SectionName = "ChannelRuntime:LarkDirectWebhookCutover";

    public bool AllowLegacyDirectCallback { get; set; }

    public DateTimeOffset? RollbackWindowEndsUtc { get; set; }

    public bool AllowsLegacyDirectCallbackAt(DateTimeOffset nowUtc)
    {
        if (!AllowLegacyDirectCallback)
            return false;

        return !RollbackWindowEndsUtc.HasValue || nowUtc <= RollbackWindowEndsUtc.Value;
    }
}
