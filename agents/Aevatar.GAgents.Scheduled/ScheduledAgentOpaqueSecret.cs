namespace Aevatar.GAgents.Scheduled;

using Aevatar.Foundation.Abstractions.Credentials;

internal sealed class ScheduledAgentOpaqueSecret
{
    private readonly string _value;

    public ScheduledAgentOpaqueSecret(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    public Task<StoreSecretResult> StoreAsync(
        ISecretVault secretVault,
        StoreSecretRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(secretVault);
        ArgumentNullException.ThrowIfNull(request);
        return secretVault.PutAsync(request with { Secret = _value }, ct);
    }

    public override string ToString() => "***REDACTED***";
}
