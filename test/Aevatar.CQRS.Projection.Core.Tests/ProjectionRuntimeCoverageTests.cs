using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;
namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionRuntimeCoverageTests
{
    [Fact]
    public async Task LoggingProjectionStoreDispatchCompensator_WhenContextIsNull_ShouldThrowArgumentNullException()
    {
        var compensator = new LoggingProjectionStoreDispatchCompensator<TestReadModel>();

        Func<Task> act = () => compensator.CompensateAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task LoggingProjectionStoreDispatchCompensator_WhenTokenCanceled_ShouldThrowOperationCanceledException()
    {
        var compensator = new LoggingProjectionStoreDispatchCompensator<TestReadModel>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => compensator.CompensateAsync(CreateContext(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task LoggingProjectionStoreDispatchCompensator_ShouldCompleteForValidContext()
    {
        var compensator = new LoggingProjectionStoreDispatchCompensator<TestReadModel>();

        await compensator.CompensateAsync(CreateContext());
    }

    private static ProjectionStoreDispatchCompensationContext<TestReadModel> CreateContext() =>
        new()
        {
            Operation = "upsert",
            FailedStore = "Graph",
            SucceededStores = ["Document"],
            ReadModel = new TestReadModel { Id = "id-1" },
            Exception = new InvalidOperationException("dispatch failed"),
        };

    private sealed class TestReadModel : IProjectionReadModel
    {
        public string Id { get; init; } = string.Empty;

        public string ActorId => Id;

        public long StateVersion { get; init; }

        public string LastEventId { get; init; } = string.Empty;

        public DateTimeOffset UpdatedAt { get; init; }
    }
}
