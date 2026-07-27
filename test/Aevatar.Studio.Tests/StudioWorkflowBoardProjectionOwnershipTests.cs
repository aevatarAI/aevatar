using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Projection.DependencyInjection;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class StudioWorkflowBoardProjectionOwnershipTests
{
    [Fact]
    public void StudioProjection_ShouldOwnWorkflowBoardReadModelContract()
    {
        var boardDocumentType = ResolveStudioBoardDocumentType();

        boardDocumentType.Should().NotBeNull();
        boardDocumentType!.Namespace.Should().Be(typeof(StudioMemberCurrentStateDocument).Namespace);
        boardDocumentType.Assembly.FullName.Should().Be(typeof(StudioMemberCurrentStateDocument).Assembly.FullName);
    }

    [Fact]
    public void AddStudioProjectionComponents_ShouldRegisterWorkflowBoardMaterializerAndMetadataProvider()
    {
        var services = new ServiceCollection();

        services.AddStudioProjectionComponents();

        var boardDocumentType = ResolveStudioBoardDocumentType();
        boardDocumentType.Should().NotBeNull();
        services.Should().Contain(descriptor =>
            descriptor.ServiceType.IsGenericType &&
            descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IProjectionDocumentMetadataProvider<>) &&
            descriptor.ServiceType.GetGenericArguments()[0] == boardDocumentType!);
        services.Any(descriptor =>
                descriptor.ImplementationType != null &&
                descriptor.ImplementationType.Name == "WorkflowExecutionBoardMaterializer")
            .Should()
            .BeTrue();
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_ShouldRegisterWorkflowBoardDocumentStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddStudioProjectionComponents();
        services.AddStudioProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        var boardDocumentType = ResolveStudioBoardDocumentType();
        boardDocumentType.Should().NotBeNull();
        var boardDocument = boardDocumentType!;
        var readerType = typeof(IProjectionDocumentReader<,>).MakeGenericType(boardDocument, typeof(string));
        var writerType = typeof(IProjectionDocumentWriter<>).MakeGenericType(boardDocument);

        provider.GetRequiredService(readerType)
            .Should()
            .BeOfType(typeof(InMemoryProjectionDocumentStore<,>).MakeGenericType(boardDocument, typeof(string)));
        provider.GetRequiredService(writerType)
            .Should()
            .BeOfType(typeof(InMemoryProjectionDocumentStore<,>).MakeGenericType(boardDocument, typeof(string)));
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_ShouldInferElasticsearchWorkflowBoardDocumentStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
                ["Projection:Document:Providers:InMemory:Enabled"] = "false",
            })
            .Build();

        services.AddStudioProjectionComponents();
        services.AddStudioProjectionReadModelProviders(configuration);

        var boardDocumentType = ResolveStudioBoardDocumentType();
        boardDocumentType.Should().NotBeNull();
        var boardDocument = boardDocumentType!;
        var elasticsearchStoreType = typeof(ElasticsearchProjectionDocumentStore<,>)
            .MakeGenericType(boardDocument, typeof(string));
        var readerType = typeof(IProjectionDocumentReader<,>).MakeGenericType(boardDocument, typeof(string));
        var writerType = typeof(IProjectionDocumentWriter<>).MakeGenericType(boardDocument);

        services.Should().Contain(descriptor => descriptor.ServiceType == elasticsearchStoreType);
        services.Should().Contain(descriptor => descriptor.ServiceType == readerType);
        services.Should().Contain(descriptor => descriptor.ServiceType == writerType);
    }

    [Fact]
    public async Task AddStudioProjectionReadModelProviders_ShouldKeyCreateRecoveryDocumentByRecoveryId()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var recoveryId = ChatHistoryCreateRecoveryIds.FromScopeAndCommandId("scope-a", "create-command-1");

        services.AddStudioProjectionComponents();
        services.AddStudioProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<IProjectionDocumentWriter<ChatHistoryCreateRecoveryCurrentStateDocument>>();
        var reader = provider.GetRequiredService<IProjectionDocumentReader<ChatHistoryCreateRecoveryCurrentStateDocument, string>>();

        await writer.UpsertAsync(new ChatHistoryCreateRecoveryCurrentStateDocument
        {
            Id = recoveryId,
            ActorId = "chat-history-delivery:actor",
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.Parse("2026-07-21T01:00:00Z")),
            ScopeId = "scope-a",
            WorkflowCommandId = "create-command-1",
            Status = "reserved",
        });

        var document = await reader.GetAsync(recoveryId);

        document.Should().NotBeNull();
        document!.Id.Should().Be(recoveryId);
    }

    private static Type? ResolveStudioBoardDocumentType() =>
        typeof(StudioMemberCurrentStateDocument).Assembly.GetType(
            "Aevatar.Studio.Projection.ReadModels.WorkflowExecutionBoardDocument",
            throwOnError: false);
}
