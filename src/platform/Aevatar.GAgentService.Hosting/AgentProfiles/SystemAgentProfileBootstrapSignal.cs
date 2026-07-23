namespace Aevatar.GAgentService.Hosting.AgentProfiles;

internal interface ISystemAgentProfileBootstrapSignal
{
    ValueTask WaitAsync(CancellationToken ct = default);
}

internal sealed class SystemAgentProfileBootstrapSignal : ISystemAgentProfileBootstrapSignal
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;

    public SystemAgentProfileBootstrapSignal(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask WaitAsync(CancellationToken ct = default) =>
        await Task.Delay(RetryInterval, _timeProvider, ct);
}
