using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.StatusDashboard.Executors;

namespace Aevatar.Mainnet.Host.Api.Status;

/// <summary>
/// Exposes the channel-bot registration read model count as a
/// <see cref="IReadmodelFreshnessSource"/> so the status dashboard can probe
/// the channel-bot runtime indirectly (no committed-at timestamp on the
/// readmodel today, so the snapshot reports count only).
/// </summary>
internal sealed class ChannelBotRegistrationFreshnessSource : IReadmodelFreshnessSource
{
    public const string SourceName = "channel-bot-registrations";

    private readonly IChannelBotRegistrationQueryPort _queryPort;

    public ChannelBotRegistrationFreshnessSource(IChannelBotRegistrationQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public string Name => SourceName;

    public async Task<ReadmodelFreshnessSnapshot> GetFreshnessAsync(CancellationToken ct)
    {
        var entries = await _queryPort.QueryAllAsync(ct);
        return new ReadmodelFreshnessSnapshot(entries.Count, LastUpdatedAt: null);
    }
}
