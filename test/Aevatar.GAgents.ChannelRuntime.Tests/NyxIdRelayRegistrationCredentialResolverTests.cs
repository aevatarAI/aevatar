using Aevatar.Configuration;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdRelayRegistrationCredentialResolverTests
{
    [Fact]
    public async Task ResolveAsync_ShouldLoadRelaySecretFromSecretsStore()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        queryPort.GetByNyxAgentApiKeyIdAsync("key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                NyxAgentApiKeyId = "key-1",
                CredentialRef = "vault://channels/lark/registrations/reg-1/relay-hmac",
            }));

        var secretsStore = new InMemorySecretsStore();
        secretsStore.Set("vault://channels/lark/registrations/reg-1/relay-hmac", "relay-secret");

        var resolver = new NyxIdRelayRegistrationCredentialResolver(queryPort, secretsStore);

        var credential = await resolver.ResolveAsync("key-1");

        credential.Should().NotBeNull();
        credential!.RegistrationId.Should().Be("reg-1");
        credential.RelayApiKeyId.Should().Be("key-1");
        credential.RelayApiKeyHash.Should().Be("relay-secret");
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenCredentialRefHasNoSecret()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        queryPort.GetByNyxAgentApiKeyIdAsync("key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                NyxAgentApiKeyId = "key-1",
                CredentialRef = "vault://channels/lark/registrations/reg-1/relay-hmac",
            }));

        var resolver = new NyxIdRelayRegistrationCredentialResolver(queryPort, new InMemorySecretsStore());

        var credential = await resolver.ResolveAsync("key-1");

        credential.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenRegistrationLookupMisses()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        queryPort.GetByNyxAgentApiKeyIdAsync("key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));

        var resolver = new NyxIdRelayRegistrationCredentialResolver(queryPort, new InMemorySecretsStore());

        var credential = await resolver.ResolveAsync("key-1");

        credential.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenRegistrationHasNoCredentialRef()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        queryPort.GetByNyxAgentApiKeyIdAsync("key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                NyxAgentApiKeyId = "key-1",
                CredentialRef = string.Empty,
            }));

        var resolver = new NyxIdRelayRegistrationCredentialResolver(queryPort, new InMemorySecretsStore());

        var credential = await resolver.ResolveAsync("key-1");

        credential.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenRelayApiKeyIdIsWhitespace()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();

        var resolver = new NyxIdRelayRegistrationCredentialResolver(queryPort, new InMemorySecretsStore());

        var credential = await resolver.ResolveAsync("   ");

        credential.Should().BeNull();
        await queryPort.DidNotReceiveWithAnyArgs()
            .GetByNyxAgentApiKeyIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ShouldNormalizeWhitespaceBeforeLookup()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        queryPort.GetByNyxAgentApiKeyIdAsync("key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                NyxAgentApiKeyId = "key-1",
                CredentialRef = "vault://channels/lark/registrations/reg-1/relay-hmac",
            }));

        var secretsStore = new InMemorySecretsStore();
        secretsStore.Set("vault://channels/lark/registrations/reg-1/relay-hmac", "relay-secret");

        var resolver = new NyxIdRelayRegistrationCredentialResolver(queryPort, secretsStore);

        var credential = await resolver.ResolveAsync("  key-1  ");

        credential.Should().NotBeNull();
        credential!.RelayApiKeyId.Should().Be("key-1");
        await queryPort.Received(1)
            .GetByNyxAgentApiKeyIdAsync("key-1", Arg.Any<CancellationToken>());
    }

    private sealed class InMemorySecretsStore : IAevatarSecretsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);
        public string? GetApiKey(string providerName) => _values.GetValueOrDefault(providerName);
        public string? GetDefaultProvider() => null;
        public IReadOnlyDictionary<string, string> GetAll() => _values;
        public void Set(string key, string value) => _values[key] = value;
        public void Remove(string key) => _values.Remove(key);
    }
}
