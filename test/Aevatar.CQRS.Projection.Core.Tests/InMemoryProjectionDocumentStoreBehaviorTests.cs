using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class InMemoryProjectionDocumentStoreBehaviorTests
{
    [Fact]
    public async Task DeleteAsync_WhenKeyExists_ShouldReturnApplied_AndRemoveItem()
    {
        var store = new InMemoryProjectionDocumentStore<TestStoreReadModel, string>(
            keySelector: model => model.Id);
        var readModel = new TestStoreReadModel
        {
            Id = "actor-1",
            ActorId = "actor-1",
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await store.UpsertAsync(readModel);

        var result = await store.DeleteAsync("actor-1");

        result.IsApplied.Should().BeTrue();
        (await store.GetAsync("actor-1")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WhenKeyMissing_ShouldReturnDuplicate()
    {
        var store = new InMemoryProjectionDocumentStore<TestStoreReadModel, string>(
            keySelector: model => model.Id);

        var result = await store.DeleteAsync("does-not-exist");

        result.Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);
    }

    [Fact]
    public async Task DeleteAsync_ShouldTrimKey_BeforeLookup()
    {
        var store = new InMemoryProjectionDocumentStore<TestStoreReadModel, string>(
            keySelector: model => model.Id);
        await store.UpsertAsync(new TestStoreReadModel
        {
            Id = "actor-trim",
            ActorId = "actor-trim",
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var result = await store.DeleteAsync("  actor-trim  ");

        result.IsApplied.Should().BeTrue();
        (await store.GetAsync("actor-trim")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WhenIdIsBlank_ShouldThrowArgumentException()
    {
        var store = new InMemoryProjectionDocumentStore<TestStoreReadModel, string>(
            keySelector: model => model.Id);

        Func<Task> act = () => store.DeleteAsync("   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DeleteAsync_WhenCancellationRequested_ShouldThrow()
    {
        var store = new InMemoryProjectionDocumentStore<TestStoreReadModel, string>(
            keySelector: model => model.Id);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => store.DeleteAsync("actor-1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotent_AcrossRepeatedCalls()
    {
        var store = new InMemoryProjectionDocumentStore<TestStoreReadModel, string>(
            keySelector: model => model.Id);
        await store.UpsertAsync(new TestStoreReadModel
        {
            Id = "actor-idem",
            ActorId = "actor-idem",
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var first = await store.DeleteAsync("actor-idem");
        var second = await store.DeleteAsync("actor-idem");

        first.Disposition.Should().Be(ProjectionWriteDisposition.Applied);
        second.Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);
    }
}
