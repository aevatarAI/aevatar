using Aevatar.AI.ToolProviders.Channel;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Platform.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddChannelRuntime_RegistersRegistrationProjectionServices_ForInMemoryStore()
    {
        var services = new ServiceCollection();

        var result = services.AddChannelRuntime();
        services.AddNyxIdRelayChannel();
        services.AddLarkPlatform();
        services.AddChannelInteractiveReplyTools();
        services.AddTelegramPlatform();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelMessageComposerRegistry>();

        result.Should().BeSameAs(services);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<ChannelBotRegistrationDocument>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<ProjectionScopeStatusDocument>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentReader<ProjectionScopeStatusDocument, string>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionScopeWatermarkQueryPort) &&
            descriptor.ImplementationType == typeof(ProjectionScopeStatusQueryPort));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType.Name.Contains("AevatarSecretsStore", StringComparison.Ordinal));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelBotRegistrationRuntimeQueryPort));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelBotRegistrationQueryByNyxIdentityPort));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(INyxIdRelayScopeResolver));
        services.Any(descriptor =>
                descriptor.ServiceType.FullName is { } name &&
                name.Contains("NyxIdRelayReplayGuard", StringComparison.Ordinal))
            .Should().BeFalse();
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(ChannelBotRegistrationStartupService));
        // Refactor (iter20/cluster-003):
        //   Old pattern: Lark-local durable inbox subscriber worker stream path(orphan)
        //   New principle: delete orphan path,NyxID relay 唯一 ingress
        AssertNoRetiredLarkConversationInboxRegistration(services);
        registry.Get(ChannelId.From("lark")).Should().BeOfType<LarkMessageComposer>();
        services.Count(descriptor => descriptor.ServiceType == typeof(IPlatformAdapter))
            .Should().Be(0);
        services.Count(descriptor => descriptor.ServiceType == typeof(INyxChannelBotProvisioningService))
            .Should().Be(2);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ChannelRelayRegistrationFacade));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ChannelRegistrationCommandFacade));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ICommandDispatchService<ChannelBotRegisterCommand, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError>));
        registry.Get(ChannelId.From("telegram")).Should().BeOfType<Aevatar.GAgents.Platform.Telegram.TelegramMessageComposer>();
    }

    [Fact]
    public void AddChannelRuntime_RegistersLarkInteractiveReplyProducer_SoDispatcherCanFindIt()
    {
        var services = new ServiceCollection();

        services.AddChannelRuntime();
        services.AddNyxIdRelayChannel();
        services.AddLarkPlatform();
        services.AddChannelInteractiveReplyTools();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelMessageComposerRegistry>();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IInteractiveReplyDispatcher));
        provider.GetRequiredService<IInteractiveReplyCollector>().Should().NotBeNull();
        registry.GetNativeProducer(ChannelId.From("lark")).Should().BeOfType<LarkChannelNativeMessageProducer>();
        registry.Get(ChannelId.From("lark")).Should().BeOfType<LarkMessageComposer>();
    }

    [Fact]
    public void AddChannelRuntime_RegistersOnlyPublicRegistrationProjectionServices_ForElasticsearchStore()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://127.0.0.1:9200",
            })
            .Build();
        var services = new ServiceCollection();

        var result = services.AddChannelRuntime(configuration);
        services.AddNyxIdRelayChannel();
        services.AddLarkPlatform();
        services.AddChannelInteractiveReplyTools();
        services.AddTelegramPlatform();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelMessageComposerRegistry>();

        result.Should().BeSameAs(services);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<ChannelBotRegistrationDocument>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<ProjectionScopeStatusDocument>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentReader<ProjectionScopeStatusDocument, string>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionScopeWatermarkQueryPort) &&
            descriptor.ImplementationType == typeof(ProjectionScopeStatusQueryPort));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType.Name.Contains("AevatarSecretsStore", StringComparison.Ordinal));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelBotRegistrationRuntimeQueryPort));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelBotRegistrationQueryByNyxIdentityPort));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(INyxIdRelayScopeResolver));
        services.Any(descriptor =>
                descriptor.ServiceType.FullName is { } name &&
                name.Contains("NyxIdRelayReplayGuard", StringComparison.Ordinal))
            .Should().BeFalse();
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(ChannelBotRegistrationStartupService));
        AssertNoRetiredLarkConversationInboxRegistration(services);
        registry.Get(ChannelId.From("lark")).Should().BeOfType<LarkMessageComposer>();
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType.Name.Contains("ChannelBotDirectCallbackBinding", StringComparison.Ordinal));
    }

    private static void AssertNoRetiredLarkConversationInboxRegistration(IServiceCollection services)
    {
        services.Any(descriptor =>
            ContainsLarkConversationInboxName(descriptor.ServiceType.FullName) ||
            ContainsLarkConversationInboxName(descriptor.ImplementationType?.FullName))
            .Should().BeFalse();
    }

    private static bool ContainsLarkConversationInboxName(string? name) =>
        name is not null && name.Contains("LarkConversationInbox", StringComparison.Ordinal);
}
