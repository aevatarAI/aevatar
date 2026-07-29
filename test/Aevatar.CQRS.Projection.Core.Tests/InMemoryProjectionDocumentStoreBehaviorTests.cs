using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class InMemoryProjectionDocumentStoreBehaviorTests
{
    [Fact]
    public async Task QueryAsync_ShouldResolveProtoFieldNames()
    {
        var store = new InMemoryProjectionDocumentStore<TestStoreReadModel, string>(
            keySelector: model => model.Id);
        await store.UpsertAsync(new TestStoreReadModel
        {
            Id = "item-1",
            ActorId = "actor-1",
            StateVersion = 1,
            LastEventId = "event-1",
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        var result = await store.QueryAsync(new ProjectionDocumentQuery
        {
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = "actor_id",
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString("actor-1"),
                },
            ],
        });

        result.Items.Should().ContainSingle().Which.Id.Should().Be("item-1");
    }

    [Fact]
    public async Task QueryAsync_ShouldMatchScalarEqualityAgainstRepeatedFieldElement()
    {
        var store = new InMemoryProjectionDocumentStore<TestRecursiveWellKnownReadModel, string>(
            keySelector: model => model.Id);
        await store.UpsertAsync(new TestRecursiveWellKnownReadModel
        {
            Id = "item-1",
            ActorId = "actor-1",
            StateVersion = 1,
            LastEventId = "event-1",
            UpdatedAt = DateTimeOffset.UnixEpoch,
            Tags = { "reader-1", "reader-2" },
        });

        var result = await store.QueryAsync(new ProjectionDocumentQuery
        {
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = "tags",
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString("reader-1"),
                },
            ],
        });

        result.Items.Should().ContainSingle().Which.Id.Should().Be("item-1");
    }

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
    public async Task DeleteAsync_WithAuthoritativeMarker_ShouldRejectDelayedOlderUpsert()
    {
        var store = new InMemoryProjectionDocumentStore<TestStoreReadModel, string>(
            keySelector: model => model.Id);
        await store.UpsertAsync(new TestStoreReadModel
        {
            Id = "actor-tombstone",
            ActorId = "actor-tombstone",
            StateVersion = 7,
            LastEventId = "evt-7",
            UpdatedAt = DateTimeOffset.Parse("2026-07-29T00:00:07Z"),
            Value = "live",
        });

        var delete = await store.DeleteAsync(new ProjectionDocumentDeleteMarker(
            "actor-tombstone",
            "actor-tombstone",
            8,
            "evt-8-delete",
            DateTimeOffset.Parse("2026-07-29T00:00:08Z")));
        var delayed = await store.UpsertAsync(new TestStoreReadModel
        {
            Id = "actor-tombstone",
            ActorId = "actor-tombstone",
            StateVersion = 7,
            LastEventId = "evt-7",
            UpdatedAt = DateTimeOffset.Parse("2026-07-29T00:00:07Z"),
            Value = "delayed",
        });

        delete.Disposition.Should().Be(ProjectionWriteDisposition.Applied);
        delayed.Disposition.Should().Be(ProjectionWriteDisposition.Stale);
        (await store.GetAsync("actor-tombstone")).Should().BeNull();
        var query = await store.QueryAsync(new ProjectionDocumentQuery { Take = 10 });
        query.Items.Should().NotContain(x => x.Id == "actor-tombstone");
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
