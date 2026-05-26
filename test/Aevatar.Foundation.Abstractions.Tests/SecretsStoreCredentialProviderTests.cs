using Aevatar.Configuration;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class SecretsStoreCredentialProviderTests
{
    [Fact]
    public async Task ResolveAsync_ShouldReadSecretByCredentialRef()
    {
        var store = new InMemorySecretsStore();
        store.Set("vault://channels/lark/reg-1", """{"encrypt_key":"abc"}""");
        var provider = new SecretsStoreCredentialProvider(store);

        var resolved = await provider.ResolveAsync("vault://channels/lark/reg-1");

        resolved.ShouldBe("""{"encrypt_key":"abc"}""");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveAsync_ShouldReturnNull_ForNullOrWhitespaceCredentialRef(string? credentialRef)
    {
        var provider = new SecretsStoreCredentialProvider(new InMemorySecretsStore());

        var resolved = await provider.ResolveAsync(credentialRef!);

        resolved.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldTrimCredentialRef()
    {
        var store = new InMemorySecretsStore();
        store.Set("vault://channels/lark/reg-2", "secret-2");
        var provider = new SecretsStoreCredentialProvider(store);

        var resolved = await provider.ResolveAsync("  vault://channels/lark/reg-2  ");

        resolved.ShouldBe("secret-2");
    }

    private sealed class InMemorySecretsStore : IAevatarSecretsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public string? GetApiKey(string providerName) => Get(providerName);

        public string? GetDefaultProvider() => null;

        public IReadOnlyDictionary<string, string> GetAll() => _values;

        public void Set(string key, string value) => _values[key] = value;

        public void Remove(string key) => _values.Remove(key);
    }
}
