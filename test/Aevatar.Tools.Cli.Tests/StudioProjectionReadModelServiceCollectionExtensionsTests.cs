using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Tools.Cli.Tests;

/// <summary>
/// Covers <see cref="StudioProjectionReadModelServiceCollectionExtensions"/>
/// to guard the DI registrations for the actor-backed Studio readmodel stores
/// (role catalog, connector catalog, chat history, chat conversation, gagent
/// registry, user memory, streaming proxy participant). The catalog stores
/// require these readers at startup; missing registrations surface as
/// <c>Unable to resolve service</c> in Development-mode DI validation.
/// </summary>
public sealed class StudioProjectionReadModelServiceCollectionExtensionsTests
{
    // All Studio-owned current-state readmodels that the actor-backed stores read.
    // Kept as Type list so the assertions stay honest against the production list
    // inside StudioProjectionReadModelServiceCollectionExtensions.
    private static readonly Type[] StudioReadModelDocumentTypes =
    [
        typeof(RoleCatalogCurrentStateDocument),
        typeof(ConnectorCatalogCurrentStateDocument),
        typeof(ChatHistoryIndexCurrentStateDocument),
        typeof(ChatConversationCurrentStateDocument),
        typeof(GAgentRegistryCurrentStateDocument),
        typeof(UserMemoryCurrentStateDocument),
        typeof(StreamingProxyParticipantCurrentStateDocument),
        typeof(UserConfigCurrentStateDocument),
    ];

    [Fact]
    public void AddStudioProjectionReadModelProviders_WhenNoProviderConfigured_ShouldDefaultToInMemoryAndRegisterAllReaders()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();

        services.AddStudioProjectionReadModelProviders(configuration);

        foreach (var doc in StudioReadModelDocumentTypes)
        {
            AssertReaderAndWriterRegistered(services, doc);
        }
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_WhenInMemoryExplicitlyEnabled_ShouldRegisterAllReaders()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Projection:Document:Providers:InMemory:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
        });

        services.AddStudioProjectionReadModelProviders(configuration);

        foreach (var doc in StudioReadModelDocumentTypes)
        {
            AssertReaderAndWriterRegistered(services, doc);
        }
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_WhenElasticsearchEnabledWithEndpoints_ShouldRegisterAllReaders()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
            ["Projection:Document:Providers:InMemory:Enabled"] = "false",
        });

        services.AddStudioProjectionReadModelProviders(configuration);

        foreach (var doc in StudioReadModelDocumentTypes)
        {
            AssertReaderAndWriterRegistered(services, doc);
        }
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_WhenElasticsearchEnabledWithoutEndpoints_ShouldThrowOnServiceResolution()
    {
        // BuildElasticsearchDocumentOptions validation is lazy (runs inside the
        // singleton factory), so registration itself succeeds. The endpoints
        // check fires only when the store is resolved from the service provider.
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
            ["Projection:Document:Providers:InMemory:Enabled"] = "false",
        });

        services.AddStudioProjectionReadModelProviders(configuration);

        // Fake the metadata provider the ES store factory requires so we reach the
        // endpoints validation rather than failing earlier on a missing metadata dep.
        services.AddSingleton<IProjectionDocumentMetadataProvider<RoleCatalogCurrentStateDocument>>(
            new FakeRoleCatalogMetadataProvider());

        using var provider = services.BuildServiceProvider();
        Action act = () => provider.GetRequiredService<IProjectionDocumentReader<RoleCatalogCurrentStateDocument, string>>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Elasticsearch*Endpoints*");
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_WhenBothProvidersEnabled_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
            ["Projection:Document:Providers:InMemory:Enabled"] = "true",
        });

        Action act = () => services.AddStudioProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Exactly one document projection provider*");
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_WhenBothProvidersDisabled_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
            ["Projection:Document:Providers:InMemory:Enabled"] = "false",
        });

        Action act = () => services.AddStudioProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Exactly one document projection provider*");
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_WhenInvalidBooleanConfig_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Projection:Document:Providers:InMemory:Enabled"] = "not-a-bool",
        });

        Action act = () => services.AddStudioProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid boolean value*");
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_WhenCalledTwice_ShouldBeIdempotent()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();

        services.AddStudioProjectionReadModelProviders(configuration);
        var countAfterFirstCall = services.Count;
        services.AddStudioProjectionReadModelProviders(configuration);

        // Second call must short-circuit once the full Studio reader set is present.
        services.Count.Should().Be(countAfterFirstCall);
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_WhenPartialRegistrationExists_ShouldFillMissingReaders()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();

        services.AddInMemoryDocumentProjectionStore<RoleCatalogCurrentStateDocument, string>(
            keySelector: static readModel => readModel.ActorId,
            keyFormatter: static key => key,
            defaultSortSelector: static readModel => readModel.UpdatedAt);

        services.AddStudioProjectionReadModelProviders(configuration);

        AssertReaderAndWriterRegistered(services, typeof(ChatConversationCurrentStateDocument));
        AssertReaderAndWriterRegistered(services, typeof(UserConfigCurrentStateDocument));
        services.Count(descriptor => descriptor.ServiceType == typeof(IProjectionDocumentReader<RoleCatalogCurrentStateDocument, string>)).Should().Be(1);
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_WhenPartialRegistrationUsesDifferentProvider_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
            ["Projection:Document:Providers:InMemory:Enabled"] = "false",
        });

        services.AddInMemoryDocumentProjectionStore<RoleCatalogCurrentStateDocument, string>(
            keySelector: static readModel => readModel.ActorId,
            keyFormatter: static key => key,
            defaultSortSelector: static readModel => readModel.UpdatedAt);

        Action act = () => services.AddStudioProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*RoleCatalogCurrentStateDocument*different provider*");
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_WhenElasticsearchEnabled_ShouldNotRegisterInMemoryStore()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
        });

        services.AddStudioProjectionReadModelProviders(configuration);

        // Sanity check: exactly one reader per doc — not double-registered via
        // both branches.
        foreach (var doc in StudioReadModelDocumentTypes)
        {
            var readerType = typeof(IProjectionDocumentReader<,>).MakeGenericType(doc, typeof(string));
            services.Count(descriptor => descriptor.ServiceType == readerType).Should().Be(1);
        }
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_WhenServicesNull_ShouldThrow()
    {
        IServiceCollection? services = null;
        var configuration = BuildConfiguration();

        Action act = () => services!.AddStudioProjectionReadModelProviders(configuration);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_WhenConfigurationNull_ShouldThrow()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddStudioProjectionReadModelProviders(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static IConfiguration BuildConfiguration(IDictionary<string, string?>? overrides = null)
    {
        var builder = new ConfigurationBuilder();
        if (overrides is { Count: > 0 })
        {
            builder.AddInMemoryCollection(overrides);
        }
        return builder.Build();
    }

    private static void AssertReaderAndWriterRegistered(IServiceCollection services, Type documentType)
    {
        var readerType = typeof(IProjectionDocumentReader<,>).MakeGenericType(documentType, typeof(string));
        var writerType = typeof(IProjectionDocumentWriter<>).MakeGenericType(documentType);

        services.Any(descriptor => descriptor.ServiceType == readerType)
            .Should()
            .BeTrue($"reader for {documentType.Name} should be registered");
        services.Any(descriptor => descriptor.ServiceType == writerType)
            .Should()
            .BeTrue($"writer for {documentType.Name} should be registered");
    }

    private sealed class FakeRoleCatalogMetadataProvider : IProjectionDocumentMetadataProvider<RoleCatalogCurrentStateDocument>
    {
        public DocumentIndexMetadata Metadata { get; } = new DocumentIndexMetadata(
            IndexName: "studio-role-catalog-test",
            Mappings: new Dictionary<string, object?>(),
            Settings: new Dictionary<string, object?>(),
            Aliases: new Dictionary<string, object?>());
    }
}
