using Aevatar.Foundation.Abstractions.Credentials;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class CredentialProviderContractTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsSecret_WhenReferenceIsKnown()
    {
        var provider = new FakeCredentialProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ref-a"] = "secret-a",
        });

        var resolved = await provider.ResolveAsync("ref-a");

        resolved.ShouldBe("secret-a");
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenReferenceIsUnknown()
    {
        var provider = new FakeCredentialProvider(new Dictionary<string, string>(StringComparer.Ordinal));

        var resolved = await provider.ResolveAsync("missing");

        resolved.ShouldBeNull();
    }

    private sealed class FakeCredentialProvider(IReadOnlyDictionary<string, string> references) : ICredentialProvider
    {
        public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(references.TryGetValue(credentialRef, out var secret) ? secret : null);
        }
    }
}
