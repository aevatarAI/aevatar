using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class InMemoryProjectionDocumentStoreBehaviorTests
{
    [Fact]
    public async Task QueryAsync_ShouldApplyAnyOfFiltersBeforeCountAndPaging()
    {
        var store = new InMemoryProjectionDocumentStore<TestStoreReadModel, string>(
            keySelector: model => model.Id);
        foreach (var (id, value) in new[]
                 {
                     ("item-a", "active"),
                     ("item-b", "revocation-pending"),
                     ("item-c", "deleted"),
                 })
        {
            await store.UpsertAsync(new TestStoreReadModel
            {
                Id = id,
                ActorId = id,
                StateVersion = 1,
                LastEventId = $"event-{id}",
                UpdatedAt = DateTimeOffset.UnixEpoch,
                Value = value,
            });
        }

        var result = await store.QueryAsync(new ProjectionDocumentQuery
        {
            Take = 1,
            IncludeTotalCount = true,
            AnyOfFilters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(TestStoreReadModel.Value),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString("active"),
                },
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(TestStoreReadModel.Value),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString("revocation-pending"),
                },
            ],
        });

        result.Items.Should().ContainSingle();
        result.Items[0].Value.Should().BeOneOf("active", "revocation-pending");
        result.TotalCount.Should().Be(2);
        result.NextCursor.Should().NotBeNullOrWhiteSpace();
    }

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

    [Fact]
    public async Task UpsertAsync_WhenIncomingAuthoritativeVersionSkipsAhead_ShouldApply()
    {
        var store = new InMemoryProjectionDocumentStore<TestStoreReadModel, string>(
            keySelector: model => model.Id);
        await store.UpsertAsync(new TestStoreReadModel
        {
            Id = "actor-gap",
            ActorId = "actor-gap",
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = DateTimeOffset.Parse("2026-06-17T00:00:00Z"),
            Value = "v1",
        });

        var result = await store.UpsertAsync(new TestStoreReadModel
        {
            Id = "actor-gap",
            ActorId = "actor-gap",
            StateVersion = 4,
            LastEventId = "evt-4",
            UpdatedAt = DateTimeOffset.Parse("2026-06-17T00:00:04Z"),
            Value = "v4",
        });

        result.Disposition.Should().Be(ProjectionWriteDisposition.Applied);
        var stored = await store.GetAsync("actor-gap");
        stored.Should().NotBeNull();
        stored!.StateVersion.Should().Be(4);
        stored.LastEventId.Should().Be("evt-4");
        stored.Value.Should().Be("v4");
    }
}
