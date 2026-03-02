using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionRuntimeCoverageTests
{
    [Fact]
    public void ProjectionDocumentMetadataResolver_ShouldResolveMetadataFromProvider()
    {
        var expected = new DocumentIndexMetadata(
            IndexName: "test-index",
            Mappings: new Dictionary<string, object?> { ["dynamic"] = true },
            Settings: new Dictionary<string, object?>(),
            Aliases: new Dictionary<string, object?>());

        var services = new ServiceCollection();
        services.AddSingleton<IProjectionDocumentMetadataProvider<TestReadModel>>(new TestMetadataProvider(expected));
        using var provider = services.BuildServiceProvider();

        var resolver = new ProjectionDocumentMetadataResolver(provider);

        var actual = resolver.Resolve<TestReadModel>();

        actual.Should().BeSameAs(expected);
    }

    [Fact]
    public void ProjectionDocumentMetadataResolver_WhenProviderMissing_ShouldThrowInvalidOperationException()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var resolver = new ProjectionDocumentMetadataResolver(provider);

        Action act = () => resolver.Resolve<TestReadModel>();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task LoggingProjectionStoreDispatchCompensator_WhenContextIsNull_ShouldThrowArgumentNullException()
    {
        var compensator = new LoggingProjectionStoreDispatchCompensator<TestReadModel, string>();

        Func<Task> act = () => compensator.CompensateAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task LoggingProjectionStoreDispatchCompensator_WhenTokenCanceled_ShouldThrowOperationCanceledException()
    {
        var compensator = new LoggingProjectionStoreDispatchCompensator<TestReadModel, string>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => compensator.CompensateAsync(CreateContext(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task LoggingProjectionStoreDispatchCompensator_ShouldCompleteForValidContext()
    {
        var compensator = new LoggingProjectionStoreDispatchCompensator<TestReadModel, string>();

        await compensator.CompensateAsync(CreateContext());
    }

    private static ProjectionStoreDispatchCompensationContext<TestReadModel, string> CreateContext() =>
        new()
        {
            Operation = "upsert",
            FailedStore = "Graph",
            SucceededStores = ["Document"],
            ReadModel = new TestReadModel { Id = "id-1" },
            Exception = new InvalidOperationException("dispatch failed"),
            Key = "id-1",
        };

    private sealed class TestReadModel : IProjectionReadModel
    {
        public string Id { get; init; } = string.Empty;
    }

    private sealed class TestMetadataProvider(DocumentIndexMetadata metadata) : IProjectionDocumentMetadataProvider<TestReadModel>
    {
        public DocumentIndexMetadata Metadata { get; } = metadata;
    }
}
