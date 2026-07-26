using System.Threading.Channels;
using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.GAgentService.Hosting.AgentProfiles;

internal interface ISystemAgentProfileBootstrapSignal
{
    ValueTask WaitAsync(CancellationToken ct = default);
}

internal sealed class SystemAgentProfileBootstrapSignal : ISystemAgentProfileBootstrapSignal
{
    private readonly Channel<bool> _pending = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    public ValueTask WaitAsync(CancellationToken ct = default) =>
        ReadSignalAsync(_pending.Reader, ct);

    internal void Pulse()
    {
        _pending.Writer.TryWrite(true);
    }

    private static async ValueTask ReadSignalAsync(
        ChannelReader<bool> reader,
        CancellationToken ct)
    {
        _ = await reader.ReadAsync(ct);
    }
}

internal sealed class SystemAgentProfileBootstrapMaterializationObserver(
    SystemAgentProfileBootstrapSignal signal)
    : IAgentProfileReadModelMaterializationObserver
{
    public void OnAgentProfileReadModelMaterialized() => signal.Pulse();
}
