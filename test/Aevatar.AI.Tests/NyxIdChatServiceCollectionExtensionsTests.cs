using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatServiceCollectionExtensionsTests
{
    [Fact]
    public void AddNyxIdChat_ShouldNotRegisterRelayReplayGuard()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:Relay:CallbackReplayWindowSeconds"] = "420",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddNyxIdChat(configuration);
        using var provider = services.BuildServiceProvider();

        services.Any(descriptor =>
                descriptor.ServiceType.FullName is { } name &&
                name.Contains("NyxIdRelayReplayGuard", StringComparison.Ordinal))
            .Should().BeFalse();
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(NyxIdRelayAuthValidator));
    }
}
