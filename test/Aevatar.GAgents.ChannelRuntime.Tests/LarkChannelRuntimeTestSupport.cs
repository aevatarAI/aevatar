using FoundationCredentialProvider = Aevatar.Foundation.Abstractions.Credentials.ICredentialProvider;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

internal sealed class TestCredentialProvider(IReadOnlyDictionary<string, string> values) : FoundationCredentialProvider
{
    public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(values.TryGetValue(credentialRef, out var value) ? value : null);
    }
}
