using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class ChannelChatDepsTests
{
    [Fact]
    public void FromServices_captures_background_dependencies_before_request_scope_is_disposed()
    {
        var actorRuntime = Substitute.For<Aevatar.Foundation.Abstractions.IActorRuntime>();
        var subscriptionProvider = Substitute.For<Aevatar.Foundation.Abstractions.Streaming.IActorEventSubscriptionProvider>();
        var adapter = Substitute.For<IPlatformAdapter>();
        adapter.Platform.Returns("lark");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<Aevatar.Foundation.Abstractions.IActorRuntime>(actorRuntime);
        services.AddSingleton<Aevatar.Foundation.Abstractions.Streaming.IActorEventSubscriptionProvider>(subscriptionProvider);
        services.AddSingleton<IPlatformAdapter>(adapter);
        services.AddSingleton(new NyxIdApiClient(new NyxIdToolOptions
        {
            BaseUrl = "https://nyx.example.com",
        }));

        using var rootProvider = services.BuildServiceProvider(validateScopes: true);
        ChannelChatDeps deps;

        using (var scope = rootProvider.CreateScope())
        {
            deps = ChannelChatDeps.FromServices(scope.ServiceProvider);
        }

        var act = () => deps.RecordDiagnostic("Chat:start", "lark", "reg-1");

        act.Should().NotThrow();
        deps.ActorRuntime.Should().BeSameAs(actorRuntime);
        deps.SubscriptionProvider.Should().BeSameAs(subscriptionProvider);
        deps.Adapters.Should().ContainSingle().Which.Should().BeSameAs(adapter);

        var cache = rootProvider.GetRequiredService<IMemoryCache>();
        var entries = cache.Get<List<object>>(ChannelDiagnosticKeys.RecentErrors);
        entries.Should().NotBeNull();
        entries.Should().ContainSingle();
    }
}
