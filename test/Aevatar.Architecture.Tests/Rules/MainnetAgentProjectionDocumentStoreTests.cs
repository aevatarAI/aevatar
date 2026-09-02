using Aevatar.Audit.Core.Projection;
using Aevatar.Audit.Core.Stores;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        Assert.IsType<InMemoryAuditTrailStore>(provider.GetRequiredService<IAuditTrailArtifactStore>());
        Assert.DoesNotContain(typeof(IProjectionReadModel), typeof(AuditTrailArtifactStorageDocument).GetInterfaces());
        AssertProviderStore<ChannelBotRegistrationDocument, InMemoryProjectionDocumentStore<ChannelBotRegistrationDocument, string>>(provider);
        AssertProviderStore<ConversationDeliveryCurrentStateDocument, InMemoryProjectionDocumentStore<ConversationDeliveryCurrentStateDocument, string>>(provider);
        AssertProviderStore<ProjectionScopeStatusDocument, InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>>(provider);
        AssertProviderStore<ExternalIdentityBindingDocument, InMemoryProjectionDocumentStore<ExternalIdentityBindingDocument, string>>(provider);
        AssertProviderStore<AevatarOAuthClientDocument, InMemoryProjectionDocumentStore<AevatarOAuthClientDocument, string>>(provider);
        AssertProviderStore<ManagedCodexCredentialDocument, InMemoryProjectionDocumentStore<ManagedCodexCredentialDocument, string>>(provider);
        AssertProviderStore<ChatRoutePolicyCurrentStateDocument, InMemoryProjectionDocumentStore<ChatRoutePolicyCurrentStateDocument, string>>(provider);
        AssertProviderStore<DeviceRegistrationDocument, InMemoryProjectionDocumentStore<DeviceRegistrationDocument, string>>(provider);
        AssertProviderStore<UserAgentCatalogDocument, InMemoryProjectionDocumentStore<UserAgentCatalogDocument, string>>(provider);
        AssertProviderStore<UserAgentCatalogNyxCredentialDocument, InMemoryProjectionDocumentStore<UserAgentCatalogNyxCredentialDocument, string>>(provider);
        Assert.IsType<InMemoryHealthProbeOperationalSnapshotStore>(
            provider.GetRequiredService<IHealthProbeOperationalSnapshotStore>());
        AssertProviderStore<StreamingProxyChatSessionTerminalSnapshot, InMemoryProjectionDocumentStore<StreamingProxyChatSessionTerminalSnapshot, string>>(provider);
        AssertProviderStore<StreamingProxyRoomParticipantsSnapshot, InMemoryProjectionDocumentStore<StreamingProxyRoomParticipantsSnapshot, string>>(provider);
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(AevatarOAuthClientEsAclStartupGuard));
    }

    [Fact]
    public void AddMainnetAgentProjectionDocumentStores_WithElasticsearchProvider_RegistersStoresAndWarnAclGuardByDefault()
    {
        var services = BuildAgentServices();

        services.AddMainnetAgentProjectionDocumentStores(BuildElasticsearchConfiguration());

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IAuditTrailArtifactStore>());
        Assert.Same(
            provider.GetRequiredService<IAuditTrailArtifactStore>(),
            provider.GetRequiredService<IAuditTrailQueryPort>());
        Assert.DoesNotContain(typeof(IProjectionReadModel), typeof(AuditTrailArtifactStorageDocument).GetInterfaces());
        AssertProviderStore<ChannelBotRegistrationDocument, ElasticsearchProjectionDocumentStore<ChannelBotRegistrationDocument, string>>(provider);
        AssertProviderStore<ConversationDeliveryCurrentStateDocument, ElasticsearchProjectionDocumentStore<ConversationDeliveryCurrentStateDocument, string>>(provider);
        AssertProviderStore<ProjectionScopeStatusDocument, ElasticsearchProjectionDocumentStore<ProjectionScopeStatusDocument, string>>(provider);
        AssertProviderStore<ExternalIdentityBindingDocument, ElasticsearchProjectionDocumentStore<ExternalIdentityBindingDocument, string>>(provider);
        AssertProviderStore<AevatarOAuthClientDocument, ElasticsearchProjectionDocumentStore<AevatarOAuthClientDocument, string>>(provider);
        AssertProviderStore<ManagedCodexCredentialDocument, ElasticsearchProjectionDocumentStore<ManagedCodexCredentialDocument, string>>(provider);
        AssertProviderStore<ChatRoutePolicyCurrentStateDocument, ElasticsearchProjectionDocumentStore<ChatRoutePolicyCurrentStateDocument, string>>(provider);
        AssertProviderStore<DeviceRegistrationDocument, ElasticsearchProjectionDocumentStore<DeviceRegistrationDocument, string>>(provider);
        AssertProviderStore<UserAgentCatalogDocument, ElasticsearchProjectionDocumentStore<UserAgentCatalogDocument, string>>(provider);
        AssertProviderStore<UserAgentCatalogNyxCredentialDocument, ElasticsearchProjectionDocumentStore<UserAgentCatalogNyxCredentialDocument, string>>(provider);
        Assert.Equal(
            "ElasticsearchHealthProbeOperationalSnapshotStore",
            provider.GetRequiredService<IHealthProbeOperationalSnapshotStore>().GetType().Name);
        Assert.DoesNotContain(
            provider.GetServices<IProjectionReadModelDescriptor>(),
            static descriptor => descriptor.Name.Contains("health-probe", StringComparison.OrdinalIgnoreCase));
        AssertProviderStore<StreamingProxyChatSessionTerminalSnapshot, ElasticsearchProjectionDocumentStore<StreamingProxyChatSessionTerminalSnapshot, string>>(provider);
        AssertProviderStore<StreamingProxyRoomParticipantsSnapshot, ElasticsearchProjectionDocumentStore<StreamingProxyRoomParticipantsSnapshot, string>>(provider);
        Assert.IsType<ElasticsearchProjectionDocumentStore<AevatarOAuthClientDocument, string>>(
            provider.GetRequiredService<IProjectionIndexConsistencyProbe<AevatarOAuthClientDocument>>());
        Assert.Equal(
            AevatarOAuthClientEsAclEnforcementMode.Warn,
            provider.GetRequiredService<IOptions<AevatarOAuthClientEsAclOptions>>().Value.EnforcementMode);
        Assert.IsType<HttpOAuthClientEsAclProbe>(provider.GetRequiredService<IOAuthClientEsAclProbe>());
        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(AevatarOAuthClientEsAclStartupGuard));
    }

    [Fact]
    public void AddMainnetAgentProjectionDocumentStores_WithExplicitStrictAclMode_PreservesOperatorPolicy()
    {
        var configuration = BuildElasticsearchConfiguration(AevatarOAuthClientEsAclEnforcementMode.Strict);
        var services = BuildAgentServices(configuration);

        services.AddMainnetAgentProjectionDocumentStores(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.Equal(
            AevatarOAuthClientEsAclEnforcementMode.Strict,
            provider.GetRequiredService<IOptions<AevatarOAuthClientEsAclOptions>>().Value.EnforcementMode);
    }

    [Fact]
    public async Task AddMainnetAgentProjectionDocumentStores_WithUnverifiableAclProbe_DoesNotBlockDefaultStartup()
    {
        var services = BuildAgentServices();
        services.AddMainnetAgentProjectionDocumentStores(BuildElasticsearchConfiguration());
        services.Replace(ServiceDescriptor.Singleton<IOAuthClientEsAclProbe>(
            new FakeOAuthClientEsAclProbe(EsAclProbeResult.Unverifiable(
                "The configured identity can read the index, but other grants cannot be disproved."))));

        await using var provider = services.BuildServiceProvider();
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        await guard.StartAsync(CancellationToken.None);
    }

    [Fact]
    public void AddMainnetAgentProjectionDocumentStores_WithCustomAclVerifier_PreservesVerifier()
    {
        var services = BuildAgentServices();
        var verifier = new FakeOAuthClientEsAclProbe(
            EsAclProbeResult.Restricted("Deployment verifier proved the effective grant."));
        services.AddSingleton<IOAuthClientEsAclProbe>(verifier);

        services.AddMainnetAgentProjectionDocumentStores(BuildElasticsearchConfiguration());

        using var provider = services.BuildServiceProvider();
        Assert.Same(verifier, provider.GetRequiredService<IOAuthClientEsAclProbe>());
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ImplementationType == typeof(UnavailableOAuthClientEsAclProbe));
    }

    [Fact]
    public void AddMainnetAgentProjectionDocumentStores_WithMultipleCustomAclVerifiers_FailsComposition()
    {
        var services = BuildAgentServices();
        services.AddSingleton<IOAuthClientEsAclProbe>(
            new FakeOAuthClientEsAclProbe(EsAclProbeResult.Restricted("first verifier")));
        services.AddSingleton<IOAuthClientEsAclProbe>(
            new FakeOAuthClientEsAclProbe(EsAclProbeResult.Restricted("second verifier")));

        var act = () => services.AddMainnetAgentProjectionDocumentStores(
            BuildElasticsearchConfiguration());

        var exception = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("exactly one custom IOAuthClientEsAclProbe", exception.Message, StringComparison.Ordinal);
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
        Assert.IsType<InMemoryAuditTrailStore>(provider.GetRequiredService<IAuditTrailArtifactStore>());
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
            options.EnforcementMode = AevatarOAuthClientEsAclEnforcementMode.Strict);
        services.AddMainnetAgentProjectionDocumentStores(BuildElasticsearchConfiguration());
        services.AddSingleton<IOAuthClientEsAclProbe>(new FakeOAuthClientEsAclProbe(EsAclProbeResult.Restricted(
            "Test grant is restricted.")));

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
            options.EnforcementMode = AevatarOAuthClientEsAclEnforcementMode.Strict;
            options.GrantMatchesGrainEventStoreInternal = true;
            options.GrantDescription = "Test grant matches grain/event-store internal services.";
        });
        services.AddMainnetAgentProjectionDocumentStores(BuildElasticsearchConfiguration());
        services.Replace(ServiceDescriptor.Singleton<IOAuthClientEsAclProbe>(
            new FakeOAuthClientEsAclProbe(EsAclProbeResult.Restricted("Test grant is restricted."))));

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AevatarOAuthClientEsAclOptions>>().Value;
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        Assert.True(options.GrantMatchesGrainEventStoreInternal);
        Func<Task> act = () => guard.StartAsync(CancellationToken.None);

        await act();
    }

    private static ServiceCollection BuildAgentServices(IConfiguration? configuration = null)
    {
        configuration ??= new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddSingleton<ISecretVault, InMemorySecretVault>();
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

    private static IConfiguration BuildElasticsearchConfiguration(
        AevatarOAuthClientEsAclEnforcementMode? enforcementMode = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://127.0.0.1:9200",
            ["Projection:Document:Providers:Elasticsearch:IndexPrefix"] = "mainnet-agent-tests",
            ["Projection:Document:Providers:InMemory:Enabled"] = "false",
        };
        if (enforcementMode.HasValue)
        {
            values[$"{AevatarOAuthClientEsAclOptions.SectionName}:EnforcementMode"] =
                enforcementMode.Value.ToString();
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static void AssertProviderStore<TDocument, TStore>(IServiceProvider provider)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        Assert.IsType<TStore>(provider.GetRequiredService<IProjectionDocumentReader<TDocument, string>>());
        Assert.IsType<TStore>(provider.GetRequiredService<IProjectionDocumentWriter<TDocument>>());
    }

    private sealed class FakeOAuthClientEsAclProbe(EsAclProbeResult result) : IOAuthClientEsAclProbe
    {
        public Task<EsAclProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }
}
