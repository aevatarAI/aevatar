using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ActorBacked;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class ActorBackedNyxIdUserLlmPreferencesStoreTests
{
    [Fact]
    public async Task GetForBindingAsync_ShouldReadTypedChannelBindingResource()
    {
        var queryPort = new RecordingUserConfigQueryPort(new UserConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: "/api/v1/proxy/s/chrono-llm-public",
            MaxToolRounds: 8));
        var store = new ActorBackedNyxIdUserLlmPreferencesStore(queryPort);

        var preferences = await store.GetForBindingAsync("binding-alpha");

        queryPort.Resources.Should().ContainSingle().Which.Should()
            .Be(UserConfigResourceKey.ForChannelBinding("binding-alpha"));
        preferences.DefaultModel.Should().Be("gpt-5.5");
        preferences.PreferredRoute.Should().Be("/api/v1/proxy/s/chrono-llm-public");
        preferences.MaxToolRounds.Should().Be(8);
    }

    private sealed class RecordingUserConfigQueryPort(UserConfig config) : IUserConfigQueryPort
    {
        public List<UserConfigResourceKey> Resources { get; } = [];

        public Task<UserConfig> GetAsync(
            UserConfigResourceKey resource,
            CancellationToken ct = default)
        {
            Resources.Add(resource);
            return Task.FromResult(config);
        }

        public Task<UserConfig> GetAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<UserConfig> GetAsync(string scopeId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
