using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.DependencyInjection;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.ChatRouting;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgents.StatusDashboard;
using Aevatar.GAgents.StatusDashboard.DependencyInjection;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.Mainnet.Host.Api.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Architecture.Tests.Rules;

public sealed class MainnetAgentProjectionDocumentStoreTests
{
    [Fact]
    public void AddMainnetAgentProjectionDocumentStores_WithInMemoryProvider_RegistersAllAgentDocuments()
    {
        var services = BuildAgentServices();

        services.AddMainnetAgentProjectionDocumentStores(BuildInMemoryConfiguration());

        using var provider = services.BuildServiceProvider();
        AssertProviderStore<ChannelBotRegistrationDocument, InMemoryProjectionDocumentStore<ChannelBotRegistrationDocument, string>>(provider);
        AssertProviderStore<ConversationDeliveryCurrentStateDocument, InMemoryProjectionDocumentStore<ConversationDeliveryCurrentStateDocument, string>>(provider);
        AssertProviderStore<ProjectionScopeStatusDocument, InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>>(provider);
        AssertProviderStore<ExternalIdentityBindingDocument, InMemoryProjectionDocumentStore<ExternalIdentityBindingDocument, string>>(provider);
        AssertProviderStore<AevatarOAuthClientDocument, InMemoryProjectionDocumentStore<AevatarOAuthClientDocument, string>>(provider);
        AssertProviderStore<ChatRoutePolicyCurrentStateDocument, InMemoryProjectionDocumentStore<ChatRoutePolicyCurrentStateDocument, string>>(provider);
        AssertProviderStore<DeviceRegistrationDocument, InMemoryProjectionDocumentStore<DeviceRegistrationDocument, string>>(provider);
        AssertProviderStore<UserAgentCatalogDocument, InMemoryProjectionDocumentStore<UserAgentCatalogDocument, string>>(provider);
        AssertProviderStore<SkillRunnerExecutionDocument, InMemoryProjectionDocumentStore<SkillRunnerExecutionDocument, string>>(provider);
        AssertProviderStore<UserAgentCatalogNyxCredentialDocument, InMemoryProjectionDocumentStore<UserAgentCatalogNyxCredentialDocument, string>>(provider);
        AssertProviderStore<HealthProbeTargetDocument, InMemoryProjectionDocumentStore<HealthProbeTargetDocument, string>>(provider);
        AssertProviderStore<StreamingProxyChatSessionTerminalSnapshot, InMemoryProjectionDocumentStore<StreamingProxyChatSessionTerminalSnapshot, string>>(provider);
        AssertProviderStore<StreamingProxyRoomParticipantsSnapshot, InMemoryProjectionDocumentStore<StreamingProxyRoomParticipantsSnapshot, string>>(provider);
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(AevatarOAuthClientEsAclStartupGuard));
    }

    [Fact]
    public void AddMainnetAgentProjectionDocumentStores_WithElasticsearchProvider_RegistersStoresAndAclGuard()
    {
        var services = BuildAgentServices();

        services.AddMainnetAgentProjectionDocumentStores(BuildElasticsearchConfiguration());

        using var provider = services.BuildServiceProvider();
        AssertProviderStore<ChannelBotRegistrationDocument, ElasticsearchProjectionDocumentStore<ChannelBotRegistrationDocument, string>>(provider);
        AssertProviderStore<ConversationDeliveryCurrentStateDocument, ElasticsearchProjectionDocumentStore<ConversationDeliveryCurrentStateDocument, string>>(provider);
        AssertProviderStore<ProjectionScopeStatusDocument, ElasticsearchProjectionDocumentStore<ProjectionScopeStatusDocument, string>>(provider);
        AssertProviderStore<ExternalIdentityBindingDocument, ElasticsearchProjectionDocumentStore<ExternalIdentityBindingDocument, string>>(provider);
        AssertProviderStore<AevatarOAuthClientDocument, ElasticsearchProjectionDocumentStore<AevatarOAuthClientDocument, string>>(provider);
        AssertProviderStore<ChatRoutePolicyCurrentStateDocument, ElasticsearchProjectionDocumentStore<ChatRoutePolicyCurrentStateDocument, string>>(provider);
        AssertProviderStore<DeviceRegistrationDocument, ElasticsearchProjectionDocumentStore<DeviceRegistrationDocument, string>>(provider);
        AssertProviderStore<UserAgentCatalogDocument, ElasticsearchProjectionDocumentStore<UserAgentCatalogDocument, string>>(provider);
        AssertProviderStore<SkillRunnerExecutionDocument, ElasticsearchProjectionDocumentStore<SkillRunnerExecutionDocument, string>>(provider);
        AssertProviderStore<UserAgentCatalogNyxCredentialDocument, ElasticsearchProjectionDocumentStore<UserAgentCatalogNyxCredentialDocument, string>>(provider);
        AssertProviderStore<HealthProbeTargetDocument, ElasticsearchProjectionDocumentStore<HealthProbeTargetDocument, string>>(provider);
        AssertProviderStore<StreamingProxyChatSessionTerminalSnapshot, ElasticsearchProjectionDocumentStore<StreamingProxyChatSessionTerminalSnapshot, string>>(provider);
        AssertProviderStore<StreamingProxyRoomParticipantsSnapshot, ElasticsearchProjectionDocumentStore<StreamingProxyRoomParticipantsSnapshot, string>>(provider);
        Assert.IsType<ElasticsearchProjectionDocumentStore<AevatarOAuthClientDocument, string>>(
            provider.GetRequiredService<IProjectionIndexConsistencyProbe<AevatarOAuthClientDocument>>());
        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(AevatarOAuthClientEsAclStartupGuard));
    }

    [Fact]
    public void AddMainnetAgentProjectionDocumentStores_WithSameProviderPartialRegistration_FillsMissingReaders()
    {
        var services = BuildAgentServices();
        services.AddInMemoryDocumentProjectionStore<ChannelBotRegistrationDocument, string>(
            keySelector: static document => document.Id,
            keyFormatter: static key => key);

        services.AddMainnetAgentProjectionDocumentStores(BuildInMemoryConfiguration());

        using var provider = services.BuildServiceProvider();
        AssertProviderStore<ChannelBotRegistrationDocument, InMemoryProjectionDocumentStore<ChannelBotRegistrationDocument, string>>(provider);
        AssertProviderStore<ConversationDeliveryCurrentStateDocument, InMemoryProjectionDocumentStore<ConversationDeliveryCurrentStateDocument, string>>(provider);
        AssertProviderStore<ProjectionScopeStatusDocument, InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>>(provider);
        AssertProviderStore<StreamingProxyRoomParticipantsSnapshot, InMemoryProjectionDocumentStore<StreamingProxyRoomParticipantsSnapshot, string>>(provider);
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IProjectionDocumentReader<ChannelBotRegistrationDocument, string>));
    }

    [Fact]
    public void AddMainnetAgentProjectionDocumentStores_WithDifferentProviderPartialRegistration_ShouldReject()
    {
        var services = BuildAgentServices();
        services.AddInMemoryDocumentProjectionStore<ChannelBotRegistrationDocument, string>(
            keySelector: static document => document.Id,
            keyFormatter: static key => key);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMainnetAgentProjectionDocumentStores(BuildElasticsearchConfiguration()));

        Assert.Contains(nameof(ChannelBotRegistrationDocument), exception.Message, StringComparison.Ordinal);
        Assert.Contains("different provider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AevatarOAuthClientEsAclStartupGuard_WhenStrictAndElasticsearchHostAclMissing_ShouldFailClosed()
    {
        var services = BuildAgentServices();
        services.Configure<AevatarOAuthClientEsAclOptions>(options =>
        {
            options.EnforcementMode = AevatarOAuthClientEsAclEnforcementMode.Strict;
        });
        services.AddMainnetAgentProjectionDocumentStores(BuildElasticsearchConfiguration());

        await using var provider = services.BuildServiceProvider();
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        Func<Task> act = () => guard.StartAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("GrantMatchesGrainEventStoreInternal=true", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AevatarOAuthClientEsAclStartupGuard_WhenElasticsearchHostAclAsserted_ShouldPass()
    {
        var services = BuildAgentServices();
        services.Configure<AevatarOAuthClientEsAclOptions>(options =>
        {
            options.GrantMatchesGrainEventStoreInternal = true;
            options.GrantDescription = "Test grant matches grain/event-store internal services.";
        });
        services.AddMainnetAgentProjectionDocumentStores(BuildElasticsearchConfiguration());

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AevatarOAuthClientEsAclOptions>>().Value;
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        Assert.True(options.GrantMatchesGrainEventStoreInternal);
        Func<Task> act = () => guard.StartAsync(CancellationToken.None);

        await act();
    }

    private static ServiceCollection BuildAgentServices()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddChannelRuntime(configuration);
        services.AddChannelIdentity(configuration);
        services.AddChatRoutingAgents(configuration);
        services.AddDeviceRegistration(configuration);
        services.AddScheduledAgents(configuration);
        services.AddStatusDashboard(configuration);
        services.AddStreamingProxy(configuration);

        return services;
    }

    private static IConfiguration BuildInMemoryConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
                ["Projection:Document:Providers:InMemory:Enabled"] = "true",
            })
            .Build();

    private static IConfiguration BuildElasticsearchConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://127.0.0.1:9200",
                ["Projection:Document:Providers:Elasticsearch:IndexPrefix"] = "mainnet-agent-tests",
                ["Projection:Document:Providers:InMemory:Enabled"] = "false",
            })
            .Build();

    private static void AssertProviderStore<TDocument, TStore>(IServiceProvider provider)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        Assert.IsType<TStore>(provider.GetRequiredService<IProjectionDocumentReader<TDocument, string>>());
        Assert.IsType<TStore>(provider.GetRequiredService<IProjectionDocumentWriter<TDocument>>());
    }
}
