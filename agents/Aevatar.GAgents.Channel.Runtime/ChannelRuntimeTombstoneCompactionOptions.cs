namespace Aevatar.GAgents.Channel.Runtime;

public sealed class ChannelRuntimeTombstoneCompactionOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);
}
