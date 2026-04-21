namespace Aevatar.GAgents.ChannelRuntime;

public sealed class ChannelRuntimeTombstoneCompactionOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);
}
