using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ElasticsearchProjectionDocumentStoreBehaviorTests
{
    [Fact]
    public async Task UpsertAndGetAsync_WhenLogicalKeyExceedsElasticsearchLimit_ShouldUseStableHashedDocumentId()
    {
        var logicalKey = $"projection.durable.scope:{new string('x', 960)}";
        var expectedStorageId = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(logicalKey)))}";
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"created"}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            $"{{\"_source\":{{\"id\":\"{logicalKey}\",\"actor_id\":\"{logicalKey}\",\"state_version\":\"1\",\"last_event_id\":\"evt-1\",\"updated_at_utc_value\":\"2026-03-16T00:00:00Z\",\"value\":\"ready\"}}}}"));
        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false },
            handler);
        var readModel = new TestStoreReadModel
        {
            Id = logicalKey,
            ActorId = logicalKey,
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = DateTimeOffset.Parse("2026-03-16T00:00:00Z"),
            Value = "ready",
        };

        await store.UpsertAsync(readModel);
        var loaded = await store.GetAsync(logicalKey);

        Encoding.UTF8.GetByteCount(expectedStorageId).Should().BeLessThanOrEqualTo(512);
        handler.CapturedRequests.Select(static request => Uri.UnescapeDataString(request.PathAndQuery))
            .Should().OnlyContain(path => path.EndsWith($"/{expectedStorageId}", StringComparison.Ordinal));
        handler.CapturedRequests[1].Body.Should().Contain($"\"id\":\"{logicalKey}\"");
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(logicalKey);
    }

    [Fact]
    public async Task UpsertAsync_WhenMaintenanceRepublishMatchesExistingVersion_ShouldReplaceStaleDocument()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"_seq_no":7,"_primary_term":3,"_source":{"id":"actor-1","actor_id":"actor-1","state_version":"7","last_event_id":"evt-7","updated_at_utc_value":"2026-03-16T00:00:00Z","value":"stale-running"}}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"updated"}"""));
        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false },
            handler);
        var incoming = new TestStoreReadModel
        {
            Id = "actor-1",
            ActorId = "actor-1",
            StateVersion = 7,
            LastEventId = CommittedStateRepublish.BuildEventId("actor-1", 7),
            UpdatedAt = DateTimeOffset.Parse("2026-03-16T00:01:00Z"),
            Value = "authoritative-terminal",
        };

        var result = await store.UpsertAsync(incoming);

        result.Disposition.Should().Be(ProjectionWriteDisposition.Applied);
        handler.CapturedRequests.Select(static request => request.Method)
            .Should().Equal("GET", "PUT");
        handler.CapturedRequests[1].Body.Should().Contain("authoritative-terminal");
    }

    [Fact]
    public async Task UpsertAsync_WhenOrdinaryWriteFollowsMaintenanceRepublishAtSameVersion_ShouldStayStale()
    {
        var maintenanceEventId = CommittedStateRepublish.BuildEventId("actor-1", 7);
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            $"{{\"_seq_no\":7,\"_primary_term\":3,\"_source\":{{\"id\":\"actor-1\",\"actor_id\":\"actor-1\",\"state_version\":\"7\",\"last_event_id\":\"{maintenanceEventId}\",\"updated_at_utc_value\":\"2026-03-16T00:01:00Z\",\"value\":\"authoritative-terminal\"}}}}"));
        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false },
            handler);
        var incoming = new TestStoreReadModel
        {
            Id = "actor-1",
            ActorId = "actor-1",
            StateVersion = 7,
            LastEventId = "evt-7",
            UpdatedAt = DateTimeOffset.Parse("2026-03-16T00:00:00Z"),
            Value = "stale-running",
        };

        var result = await store.UpsertAsync(incoming);

        result.Disposition.Should().Be(ProjectionWriteDisposition.Stale);
        handler.CapturedRequests.Should().ContainSingle()
            .Which.Method.Should().Be("GET");
    }

    [Fact]
    public void AddElasticsearchDocumentProjectionStore_ShouldRegisterIndexReconcileTarget()
    {
        var services = new ServiceCollection();

        RegisterProjectionStore(services);

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IProjectionIndexReconcileTarget) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType ==
            typeof(IProjectionDocumentMutator<TestStoreReadModel, string>) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public async Task MutateAsync_WhenUncontended_ShouldUseOneReadAndOneConditionalCommit()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"_seq_no":7,"_primary_term":3,"_source":{"id":"actor-1","actor_id":"actor-1","state_version":"1","last_event_id":"evt-1","updated_at_utc_value":"2026-03-16T00:00:00Z","value":"v1"}}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"updated"}"""));
        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false },
            handler);
        var reducerCalls = 0;

        var result = await store.MutateAsync("actor-1", current =>
        {
            reducerCalls++;
            current.Should().NotBeNull();
            current!.StateVersion = 2;
            current.LastEventId = "evt-2";
            current.UpdatedAt = DateTimeOffset.Parse("2026-03-16T00:00:01Z");
            current.Value = "v2";
            return current;
        });

        reducerCalls.Should().Be(1);
        result.WriteResult.Disposition.Should().Be(ProjectionWriteDisposition.Applied);
        result.Document!.Value.Should().Be("v2");
        handler.CapturedRequests.Select(static request => request.Method)
            .Should().Equal("GET", "PUT");
        handler.CapturedRequests[1].PathAndQuery.Should().Contain("if_seq_no=7");
        handler.CapturedRequests[1].PathAndQuery.Should().Contain("if_primary_term=3");
    }

    [Fact]
    public async Task MutateAsync_WhenExactReplay_ShouldReadOnceAndNotWrite()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"_seq_no":7,"_primary_term":3,"_source":{"id":"actor-1","actor_id":"actor-1","state_version":"1","last_event_id":"evt-1","updated_at_utc_value":"2026-03-16T00:00:00Z","value":"v1"}}"""));
        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false },
            handler);

        var result = await store.MutateAsync("actor-1", current => current!);

        result.WriteResult.Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);
        result.Document!.Value.Should().Be("v1");
        handler.CapturedRequests.Should().ContainSingle()
            .Which.Method.Should().Be("GET");
    }

    [Fact]
    public async Task MutateAsync_WhenOccConflicts_ShouldReapplyReducerToLatestDocument()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"_seq_no":7,"_primary_term":3,"_source":{"id":"actor-1","actor_id":"actor-1","state_version":"1","last_event_id":"evt-1","updated_at_utc_value":"2026-03-16T00:00:00Z","value":"v1"}}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.Conflict,
            """{"error":{"type":"version_conflict_engine_exception"},"status":409}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"_seq_no":8,"_primary_term":3,"_source":{"id":"actor-1","actor_id":"actor-1","state_version":"2","last_event_id":"evt-other","updated_at_utc_value":"2026-03-16T00:00:01Z","value":"v2-other"}}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"updated"}"""));
        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false },
            handler);
        var reducerCalls = 0;

        var result = await store.MutateAsync("actor-1", current =>
        {
            reducerCalls++;
            current.Should().NotBeNull();
            current!.StateVersion++;
            current.LastEventId = $"evt-local-{current.StateVersion}";
            current.Value += "|local";
            return current;
        });

        reducerCalls.Should().Be(2);
        result.WriteResult.Disposition.Should().Be(ProjectionWriteDisposition.Applied);
        result.Document!.StateVersion.Should().Be(3);
        result.Document.Value.Should().Be("v2-other|local");
        handler.CapturedRequests.Select(static request => request.Method)
            .Should().Equal("GET", "PUT", "GET", "PUT");
        handler.CapturedRequests[3].PathAndQuery.Should().Contain("if_seq_no=8");
        handler.CapturedRequests[3].Body.Should().Contain("\"value\":\"v2-other|local\"");
        handler.CapturedRequests[3].Body.Should().NotContain("v1|local|local");
    }

    [Fact]
    public void AddElasticsearchDocumentProjectionStore_ShouldNotRegisterRepairStore()
    {
        var services = new ServiceCollection();

        RegisterProjectionStore(services);

        services.Should().NotContain(descriptor =>
            descriptor.ServiceType ==
            typeof(IElasticsearchProjectionDocumentRepairStore<TestStoreReadModel, string>));
        using var provider = services.BuildServiceProvider();
        provider.GetService<
                IElasticsearchProjectionDocumentRepairStore<TestStoreReadModel, string>>()
            .Should()
            .BeNull();
    }

    [Fact]
    public void ConcreteStore_ShouldNotImplementRepairStore()
    {
        typeof(IElasticsearchProjectionDocumentRepairStore<TestStoreReadModel, string>)
            .IsAssignableFrom(
                typeof(ElasticsearchProjectionDocumentStore<TestStoreReadModel, string>))
            .Should()
            .BeFalse();
    }

    [Fact]
    public void AddElasticsearchDocumentProjectionRepairStore_ShouldRegisterSeparateRepairAdapter()
    {
        var services = new ServiceCollection();
        RegisterProjectionStore(services);

        services.AddElasticsearchDocumentProjectionRepairStore<TestStoreReadModel, string>();

        using var provider = services.BuildServiceProvider();
        var repair = provider.GetRequiredService<
            IElasticsearchProjectionDocumentRepairStore<TestStoreReadModel, string>>();
        var concrete = provider.GetRequiredService<
            ElasticsearchProjectionDocumentStore<TestStoreReadModel, string>>();

        repair.Should().NotBeSameAs(concrete);
        repair.Should().NotBeAssignableTo<
            ElasticsearchProjectionDocumentStore<TestStoreReadModel, string>>();
    }

    [Fact]
    public async Task RepairInspectAsync_ReturnsDocumentAndOpaqueRevisionLease()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, ExistingRepairDocumentJson()));
        using var provider = CreateRepairProvider(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        var lease = await RepairStore(provider).InspectAsync("doc-1");

        lease.Should().NotBeNull();
        lease!.Key.Should().Be("doc-1");
        lease.Document.ActorId.Should().Be("actor-1");
        lease.Document.StateVersion.Should().Be(7);
    }

    [Fact]
    public async Task RepairInspectAsync_WhenDocumentMissing_ShouldReturnNull()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            MissingRepairDocumentJson("aevatar-projection-core-tests")));
        using var provider = CreateRepairProvider(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        var lease = await RepairStore(provider).InspectAsync("doc-1");

        lease.Should().BeNull();
    }

    [Fact]
    public async Task RepairDeleteIfUnchangedAsync_UsesConcreteIndexAndOccRevision()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, ExistingRepairDocumentJson()));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"deleted"}"""));
        using var provider = CreateRepairProvider(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);
        var repair = RepairStore(provider);
        var lease = await repair.InspectAsync("doc-1");

        var result = await repair.DeleteIfUnchangedAsync(lease!);

        result.Should().Be(ElasticsearchProjectionDocumentRepairDeleteDisposition.Deleted);
        handler.CapturedRequests[1].PathAndQuery.Should().Be(
            "/aevatar-mainnet-test-v1/_doc/doc-1?if_seq_no=12&if_primary_term=3");
    }

    [Fact]
    public async Task RepairDeleteIfUnchangedAsync_WhenDocumentMissing_ShouldReturnAlreadyAbsent()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, ExistingRepairDocumentJson()));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            DeleteMissingRepairDocumentJson()));
        using var provider = CreateRepairProvider(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);
        var repair = RepairStore(provider);
        var lease = await repair.InspectAsync("doc-1");

        var result = await repair.DeleteIfUnchangedAsync(lease!);

        result.Should().Be(ElasticsearchProjectionDocumentRepairDeleteDisposition.AlreadyAbsent);
    }

    [Fact]
    public async Task RepairDeleteIfUnchangedAsync_WhenRevisionChanged_ShouldReturnRevisionConflict()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, ExistingRepairDocumentJson()));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.Conflict,
            """{"error":{"type":"version_conflict_engine_exception"},"status":409}"""));
        using var provider = CreateRepairProvider(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);
        var repair = RepairStore(provider);
        var lease = await repair.InspectAsync("doc-1");

        var result = await repair.DeleteIfUnchangedAsync(lease!);

        result.Should().Be(ElasticsearchProjectionDocumentRepairDeleteDisposition.RevisionConflict);
    }

    [Fact]
    public async Task RepairDeleteIfUnchangedAsync_WhenDeleteResponseIsLostAndDocumentIsGone_ShouldReturnAlreadyAbsent()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, ExistingRepairDocumentJson()));
        handler.EnqueueResponse(_ => throw new HttpRequestException("delete response lost"));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            MissingRepairDocumentJson()));
        using var provider = CreateRepairProvider(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);
        var repair = RepairStore(provider);
        var lease = await repair.InspectAsync("doc-1");

        var result = await repair.DeleteIfUnchangedAsync(lease!);

        result.Should().Be(ElasticsearchProjectionDocumentRepairDeleteDisposition.AlreadyAbsent);
        handler.CapturedRequests.Select(static request => request.Method)
            .Should().Equal("GET", "DELETE", "GET");
        handler.CapturedRequests[2].PathAndQuery.Should().Be(
            "/aevatar-mainnet-test-v1/_doc/doc-1");
    }

    [Fact]
    public async Task RepairDeleteIfUnchangedAsync_WhenDeleteTimesOutAndDocumentIsGone_ShouldReturnAlreadyAbsent()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, ExistingRepairDocumentJson()));
        handler.EnqueueResponse(_ => throw new TimeoutException("delete timed out"));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            MissingRepairDocumentJson()));
        using var provider = CreateRepairProvider(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);
        var repair = RepairStore(provider);
        var lease = await repair.InspectAsync("doc-1");

        var result = await repair.DeleteIfUnchangedAsync(lease!);

        result.Should().Be(ElasticsearchProjectionDocumentRepairDeleteDisposition.AlreadyAbsent);
        handler.CapturedRequests.Select(static request => request.Method)
            .Should().Equal("GET", "DELETE", "GET");
    }

    [Fact]
    public async Task RepairDeleteIfUnchangedAsync_WhenDeleteReturnsIndexNotFound_ShouldSurfaceProviderFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, ExistingRepairDocumentJson()));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            """{"error":{"type":"index_not_found_exception","index":"aevatar-mainnet-test-v1"},"status":404}"""));
        using var provider = CreateRepairProvider(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);
        var repair = RepairStore(provider);
        var lease = await repair.InspectAsync("doc-1");

        var act = () => repair.DeleteIfUnchangedAsync(lease!);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*repair-delete*");
        handler.CapturedRequests.Select(static request => request.Method)
            .Should().Equal("GET", "DELETE");
    }

    [Fact]
    public async Task RepairDeleteIfUnchangedAsync_WhenReinspectionReturnsIndexNotFound_ShouldSurfaceProviderFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, ExistingRepairDocumentJson()));
        handler.EnqueueResponse(_ => throw new HttpRequestException("delete response lost"));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            """{"error":{"type":"index_not_found_exception","index":"aevatar-mainnet-test-v1"},"status":404}"""));
        using var provider = CreateRepairProvider(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);
        var repair = RepairStore(provider);
        var lease = await repair.InspectAsync("doc-1");

        var act = () => repair.DeleteIfUnchangedAsync(lease!);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*repair-inspect*");
        handler.CapturedRequests.Select(static request => request.Method)
            .Should().Equal("GET", "DELETE", "GET");
    }

    [Theory]
    [InlineData("""{"found":false}""")]
    [InlineData("""{"_index":"wrong-index","_id":"doc-1","found":false}""")]
    [InlineData("""{"_index":"aevatar-mainnet-test-v1","_id":"wrong-doc","found":false}""")]
    [InlineData("<html>not found</html>")]
    public async Task RepairDeleteIfUnchangedAsync_WhenReinspectionReturnsNonExactDocument404_ShouldSurfaceProviderFailure(
        string payload)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, ExistingRepairDocumentJson()));
        handler.EnqueueResponse(_ => throw new HttpRequestException("delete response lost"));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, payload));
        using var provider = CreateRepairProvider(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);
        var repair = RepairStore(provider);
        var lease = await repair.InspectAsync("doc-1");

        var act = () => repair.DeleteIfUnchangedAsync(lease!);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*repair-inspect*");
        handler.CapturedRequests.Select(static request => request.Method)
            .Should().Equal("GET", "DELETE", "GET");
    }

    [Fact]
    public async Task RepairDeleteIfUnchangedAsync_WhenCallerCancelsAfterDeleteIsSent_ShouldStillReinspect()
    {
        using var callerCancellation = new CancellationTokenSource();
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, ExistingRepairDocumentJson()));
        handler.EnqueueResponse(_ =>
        {
            callerCancellation.Cancel();
            throw new HttpRequestException("delete response lost");
        });
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            MissingRepairDocumentJson()));
        using var provider = CreateRepairProvider(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);
        var repair = RepairStore(provider);
        var lease = await repair.InspectAsync("doc-1");

        var result = await repair.DeleteIfUnchangedAsync(lease!, callerCancellation.Token);

        callerCancellation.IsCancellationRequested.Should().BeTrue();
        result.Should().Be(ElasticsearchProjectionDocumentRepairDeleteDisposition.AlreadyAbsent);
        handler.CapturedRequests.Select(static request => request.Method)
            .Should().Equal("GET", "DELETE", "GET");
    }

    [Fact]
    public async Task RepairDeleteIfUnchangedAsync_WhenDeleteResponseIsLostAndDocumentRemains_ShouldSurfaceFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, ExistingRepairDocumentJson()));
        handler.EnqueueResponse(_ => throw new HttpRequestException("delete response lost"));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, ExistingRepairDocumentJson()));
        using var provider = CreateRepairProvider(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);
        var repair = RepairStore(provider);
        var lease = await repair.InspectAsync("doc-1");

        var act = () => repair.DeleteIfUnchangedAsync(lease!);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("delete response lost");
        handler.CapturedRequests.Select(static request => request.Method)
            .Should().Equal("GET", "DELETE", "GET");
    }

    [Fact]
    public async Task RepairDeleteIfUnchangedAsync_WhenCallerAlreadyCanceled_ShouldNotIssueDelete()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, ExistingRepairDocumentJson()));
        using var provider = CreateRepairProvider(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);
        var repair = RepairStore(provider);
        var lease = await repair.InspectAsync("doc-1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => repair.DeleteIfUnchangedAsync(lease!, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.CapturedRequests.Select(static request => request.Method)
            .Should().Equal("GET");
    }

    [Fact]
    public async Task GetAsync_WhenIndexMissingAndAutoCreateDisabled_ShouldThrowByDefault()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            """{"error":{"type":"index_not_found_exception"},"status":404}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
            },
            handler);

        Func<Task> act = () => store.GetAsync("actor-1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*index*not found*");
    }

    [Fact]
    public async Task GetAsync_WhenIndexMissingAndWarnBehaviorEnabled_ShouldReturnNull()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            """{"error":{"type":"index_not_found_exception"},"status":404}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
                MissingIndexBehavior = ElasticsearchMissingIndexBehavior.WarnAndReturnEmpty,
            },
            handler);

        var result = await store.GetAsync("actor-1");

        result.Should().BeNull();
    }

    [Fact]
    public async Task QueryAsync_WhenSortFieldNotConfigured_ShouldUseProjectionDocumentIdAsDeterministicTiebreakSort()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"hits":{"hits":[]}}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
                DefaultSortField = "",
            },
            handler);

        _ = await store.QueryAsync(new ProjectionDocumentQuery());

        var searchRequest = handler.CapturedRequests.Should().ContainSingle().Subject;
        searchRequest.PathAndQuery.Should().EndWith("/_search");
        searchRequest.Body.Should().Contain("\"sort\"");
        searchRequest.Body.Should().Contain("\"CreatedAt\"");
        searchRequest.Body.Should().Contain("\"ProjectionDocumentId\"");
        searchRequest.Body.Should().Contain("\"unmapped_type\":\"keyword\"");
        searchRequest.Body.Should().Contain("\"missing\":\"_last\"");
        searchRequest.Body.Should().NotContain("\"_id\"");
        searchRequest.Body.Should().NotContain("\"Id.keyword\"");
    }

    [Fact]
    public async Task QueryAsync_WhenUsingClrFieldPaths_ShouldTranslateToProtoFieldNames()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"hits":{"hits":[]}}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
            },
            handler);

        _ = await store.QueryAsync(new ProjectionDocumentQuery
        {
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(TestStoreReadModel.ActorId),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString("actor-1"),
                },
            ],
            Sorts =
            [
                new ProjectionDocumentSort
                {
                    FieldPath = nameof(TestStoreReadModel.UpdatedAt),
                    Direction = ProjectionDocumentSortDirection.Desc,
                },
            ],
        });

        var searchRequest = handler.CapturedRequests.Should().ContainSingle().Subject;
        searchRequest.Body.Should().Contain("\"actor_id\":\"actor-1\"");
        searchRequest.Body.Should().Contain("\"updated_at_utc_value\"");
        searchRequest.Body.Should().NotContain("\"ActorId\"");
        searchRequest.Body.Should().NotContain("\"UpdatedAt\"");
    }

    [Fact]
    public async Task QueryAsync_ShouldTranslateAnyOfFiltersToMinimumOneShouldMatch()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"hits":{"hits":[]}}"""));
        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false },
            handler);

        _ = await store.QueryAsync(new ProjectionDocumentQuery
        {
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

        var body = handler.CapturedRequests.Should().ContainSingle().Subject.Body;
        body.Should().Contain("\"should\"");
        body.Should().Contain("\"minimum_should_match\":1");
        body.Should().Contain("\"active\"");
        body.Should().Contain("\"revocation-pending\"");
    }

    [Fact]
    public async Task QueryAsync_ShouldTranslateContainsTextFilterToCaseInsensitiveWildcard()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"hits":{"hits":[]}}"""));
        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false },
            handler);

        _ = await store.QueryAsync(new ProjectionDocumentQuery
        {
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(TestStoreReadModel.Value),
                    Operator = ProjectionDocumentFilterOperator.ContainsText,
                    Value = ProjectionDocumentValue.FromString("run*alpha?"),
                },
            ],
        });

        var body = handler.CapturedRequests.Should().ContainSingle().Subject.Body;
        body.Should().Contain("\"wildcard\"");
        body.Should().Contain("\"value.keyword\"");
        body.Should().Contain("\"value\":\"*run\\\\*alpha\\\\?*\"");
        body.Should().Contain("\"case_insensitive\":true");
    }

    [Fact]
    public async Task UpsertAsync_WhenTimestampDescriptorFieldIsUnmapped_ShouldInitializeItAsDate()
    {
        var handler = CreateSuccessfulUpsertHandler();

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = true,
            },
            handler);

        await store.UpsertAsync(new TestStoreReadModel
        {
            Id = "actor-1",
            ActorId = "actor-1",
            UpdatedAt = DateTimeOffset.Parse("2026-05-15T00:00:00Z"),
        });

        var indexPayload = ParseJson(GetIndexCreateRequest(handler).Body);
        GetMappingType(indexPayload, "updated_at_utc_value").Should().Be("date");
    }

    [Fact]
    public async Task UpsertAsync_WhenStableIdentifierStringDescriptorFieldsAreUnmapped_ShouldInitializeThemAsKeyword()
    {
        var handler = CreateSuccessfulUpsertHandler();

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = true,
            },
            handler);

        await store.UpsertAsync(new TestStoreReadModel
        {
            Id = "actor-1",
            ActorId = "actor-1",
            LastEventId = "event-1",
        });

        var indexPayload = ParseJson(GetIndexCreateRequest(handler).Body);
        GetMappingType(indexPayload, "id").Should().Be("keyword");
        GetMappingType(indexPayload, "actor_id").Should().Be("keyword");
        GetMappingType(indexPayload, "last_event_id").Should().Be("keyword");
        GetProperties(indexPayload).Should().NotContainKey("value");
    }

    [Fact]
    public async Task UpsertAsync_WhenProviderDeclaresExplicitMapping_ShouldPreserveIt()
    {
        var handler = CreateSuccessfulUpsertHandler();
        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            AutoCreateIndex = true,
        };
        options.Endpoints = ["http://localhost:9200"];

        using var store = new ElasticsearchProjectionDocumentStore<TestStoreReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "projection-core-tests",
                Mappings: new Dictionary<string, object?>
                {
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["actor_id"] = new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["analyzer"] = "standard",
                        },
                    },
                },
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            httpMessageHandler: handler);

        await store.UpsertAsync(new TestStoreReadModel
        {
            Id = "actor-1",
            ActorId = "actor-1",
        });

        var indexPayload = ParseJson(GetIndexCreateRequest(handler).Body);
        var actorIdMapping = GetFieldMapping(indexPayload, "actor_id");
        actorIdMapping.GetProperty("type").GetString().Should().Be("text");
        actorIdMapping.GetProperty("analyzer").GetString().Should().Be("standard");
    }

    [Fact]
    public async Task UpsertAsync_WhenDescriptorContainsOpenFields_ShouldOnlySkipOpenMessagesAndRepeatedPrimitives()
    {
        var handler = CreateSuccessfulUpsertHandler();
        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            AutoCreateIndex = true,
        };
        options.Endpoints = ["http://localhost:9200"];

        using var store = new ElasticsearchProjectionDocumentStore<TestRecursiveWellKnownReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "projection-core-tests",
                Mappings: new Dictionary<string, object?>(),
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            httpMessageHandler: handler);

        await store.UpsertAsync(new TestRecursiveWellKnownReadModel
        {
            Id = "actor-1",
            ActorId = "actor-1",
            UpdatedAt = DateTimeOffset.Parse("2026-05-15T00:00:00Z"),
        });

        var indexPayload = ParseJson(GetIndexCreateRequest(handler).Body);
        var properties = GetProperties(indexPayload);
        properties.Should().NotContainKey("fields_value");
        properties.Should().NotContainKey("open_payload");
        properties.Should().NotContainKey("tags");
        GetMappingType(indexPayload, "labels").Should().Be("object");
        GetFieldMapping(indexPayload, "labels").GetProperty("enabled").GetBoolean().Should().BeFalse();
        GetMappingType(indexPayload, "updated_at_utc_value").Should().Be("date");
    }

    [Fact]
    public async Task UpsertAsync_WhenDescriptorContainsProtoMaps_ShouldDisableMapMappingsAndUseObjectParents()
    {
        var handler = CreateSuccessfulUpsertHandler();
        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            AutoCreateIndex = true,
        };
        options.Endpoints = ["http://localhost:9200"];

        using var store = new ElasticsearchProjectionDocumentStore<TestRecursiveWellKnownReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "projection-core-tests",
                Mappings: new Dictionary<string, object?>(),
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            httpMessageHandler: handler);

        await store.UpsertAsync(new TestRecursiveWellKnownReadModel
        {
            Id = "actor-1",
            ActorId = "actor-1",
            UpdatedAt = DateTimeOffset.Parse("2026-05-15T00:00:00Z"),
        });

        var indexCreateRequest = GetIndexCreateRequest(handler);
        var indexPayload = ParseJson(indexCreateRequest.Body);
        var mappings = indexPayload.GetProperty("mappings");
        mappings.TryGetProperty("dynamic", out _).Should().BeFalse();
        indexCreateRequest.Body.Should().NotContain("\"nested\"");

        GetMappingType(indexPayload, "labels").Should().Be("object");
        GetFieldMapping(indexPayload, "labels").GetProperty("enabled").GetBoolean().Should().BeFalse();

        GetMappingType(indexPayload, "primary_entry").Should().Be("object");
        GetMappingType(indexPayload, "primary_entry", "entry_id").Should().Be("keyword");
        GetMappingType(indexPayload, "primary_entry", "attributes").Should().Be("object");
        GetFieldMapping(indexPayload, "primary_entry", "attributes")
            .GetProperty("enabled")
            .GetBoolean()
            .Should()
            .BeFalse();
        GetMappingType(indexPayload, "primary_entry", "leaf").Should().Be("object");
        GetMappingType(indexPayload, "primary_entry", "leaf", "leaf_id").Should().Be("keyword");
        GetFieldMapping(indexPayload, "primary_entry", "leaf", "annotations")
            .GetProperty("enabled")
            .GetBoolean()
            .Should()
            .BeFalse();

        GetMappingType(indexPayload, "entries").Should().Be("object");
        GetFieldMapping(indexPayload, "entries").TryGetProperty("properties", out _).Should().BeTrue();
        GetFieldMapping(indexPayload, "entries", "attributes")
            .GetProperty("enabled")
            .GetBoolean()
            .Should()
            .BeFalse();
        GetMappingType(indexPayload, "child_entries").Should().Be("object");
        GetFieldMapping(indexPayload, "child_entries", "leaf", "annotations")
            .GetProperty("enabled")
            .GetBoolean()
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task UpsertAsync_WhenProviderDeclaresExplicitNestedMapMapping_ShouldPreserveExactPath()
    {
        var handler = CreateSuccessfulUpsertHandler();
        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            AutoCreateIndex = true,
        };
        options.Endpoints = ["http://localhost:9200"];

        using var store = new ElasticsearchProjectionDocumentStore<TestRecursiveWellKnownReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "projection-core-tests",
                Mappings: new Dictionary<string, object?>
                {
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["primary_entry"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["attributes"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "object",
                                    ["enabled"] = true,
                                },
                            },
                        },
                    },
                },
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            httpMessageHandler: handler);

        await store.UpsertAsync(new TestRecursiveWellKnownReadModel
        {
            Id = "actor-1",
            ActorId = "actor-1",
            UpdatedAt = DateTimeOffset.Parse("2026-05-15T00:00:00Z"),
        });

        var indexPayload = ParseJson(GetIndexCreateRequest(handler).Body);
        GetMappingType(indexPayload, "primary_entry").Should().Be("object");
        GetMappingType(indexPayload, "primary_entry", "attributes").Should().Be("object");
        GetFieldMapping(indexPayload, "primary_entry", "attributes")
            .GetProperty("enabled")
            .GetBoolean()
            .Should()
            .BeTrue();
        GetFieldMapping(indexPayload, "primary_entry", "leaf", "annotations")
            .GetProperty("enabled")
            .GetBoolean()
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task UpsertAsync_WhenMetadataOmitsProjectionDocumentId_ShouldInitializeItAsKeyword()
    {
        var handler = CreateSuccessfulUpsertHandler();

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = true,
            },
            handler);

        await store.UpsertAsync(new TestStoreReadModel
        {
            Id = "actor-1",
            ActorId = "actor-1",
        });

        var indexPayload = ParseJson(GetIndexCreateRequest(handler).Body);
        GetMappingType(indexPayload, "ProjectionDocumentId").Should().Be("keyword");
    }

    [Fact]
    public async Task QueryAsync_WhenUsingExplicitTimestampSort_ShouldIncludeMissingAndUnmappedHints()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"hits":{"hits":[]}}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
            },
            handler);

        _ = await store.QueryAsync(new ProjectionDocumentQuery
        {
            Sorts =
            [
                new ProjectionDocumentSort
                {
                    FieldPath = nameof(TestStoreReadModel.UpdatedAt),
                    Direction = ProjectionDocumentSortDirection.Desc,
                },
            ],
        });

        var searchRequest = handler.CapturedRequests.Should().ContainSingle().Subject;
        searchRequest.Body.Should().Contain("\"updated_at_utc_value\"");
        searchRequest.Body.Should().Contain("\"missing\":\"_last\"");
        searchRequest.Body.Should().Contain("\"unmapped_type\":\"date\"");
    }

    [Fact]
    public async Task QueryAsync_WhenUsingExplicitStringSort_ShouldUseKeywordUnmappedHint()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"hits":{"hits":[]}}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
            },
            handler);

        _ = await store.QueryAsync(new ProjectionDocumentQuery
        {
            Sorts =
            [
                new ProjectionDocumentSort
                {
                    FieldPath = nameof(TestStoreReadModel.Value),
                    Direction = ProjectionDocumentSortDirection.Asc,
                },
            ],
        });

        var searchRequest = handler.CapturedRequests.Should().ContainSingle().Subject;
        searchRequest.Body.Should().Contain("\"value\"");
        searchRequest.Body.Should().Contain("\"missing\":\"_last\"");
        searchRequest.Body.Should().Contain("\"unmapped_type\":\"keyword\"");
    }

    [Fact]
    public async Task QueryAsync_WhenFieldHasExplicitKeywordMapping_ShouldNotAppendKeywordSuffix()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"hits":{"hits":[]}}"""));

        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            AutoCreateIndex = false,
        };
        options.Endpoints = ["http://localhost:9200"];

        using var store = new ElasticsearchProjectionDocumentStore<TestStoreReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "projection-core-tests",
                Mappings: new Dictionary<string, object?>
                {
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["value"] = new Dictionary<string, object?>
                        {
                            ["type"] = "keyword",
                        },
                    },
                },
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            httpMessageHandler: handler);

        _ = await store.QueryAsync(new ProjectionDocumentQuery
        {
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(TestStoreReadModel.Value),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString("v1"),
                },
            ],
        });

        var searchRequest = handler.CapturedRequests.Should().ContainSingle().Subject;
        searchRequest.Body.Should().Contain("\"value\":\"v1\"");
        searchRequest.Body.Should().NotContain("\"value.keyword\"");
    }

    [Fact]
    public async Task QueryAsync_WhenFieldHasExplicitTextMappingWithoutKeyword_ShouldNotInventKeywordSuffix()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"hits":{"hits":[]}}"""));

        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            AutoCreateIndex = false,
        };
        options.Endpoints = ["http://localhost:9200"];

        using var store = new ElasticsearchProjectionDocumentStore<TestStoreReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "projection-core-tests",
                Mappings: new Dictionary<string, object?>
                {
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["value"] = new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                        },
                    },
                },
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            httpMessageHandler: handler);

        _ = await store.QueryAsync(new ProjectionDocumentQuery
        {
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(TestStoreReadModel.Value),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString("v1"),
                },
            ],
        });

        var searchRequest = handler.CapturedRequests.Should().ContainSingle().Subject;
        searchRequest.Body.Should().Contain("\"value\":\"v1\"");
        searchRequest.Body.Should().NotContain("\"value.keyword\"");
    }

    [Fact]
    public async Task QueryAsync_WhenDescriptorContainsRecursiveWellKnownType_ShouldAvoidInfiniteRecursion()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"hits":{"hits":[]}}"""));

        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            AutoCreateIndex = false,
        };
        options.Endpoints = ["http://localhost:9200"];

        using var store = new ElasticsearchProjectionDocumentStore<TestRecursiveWellKnownReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "projection-core-tests",
                Mappings: new Dictionary<string, object?>(),
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            httpMessageHandler: handler);

        _ = await store.QueryAsync(new ProjectionDocumentQuery
        {
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(TestRecursiveWellKnownReadModel.Value),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString("v1"),
                },
            ],
        });

        var searchRequest = handler.CapturedRequests.Should().ContainSingle().Subject;
        searchRequest.Body.Should().Contain("\"value.keyword\":\"v1\"");
    }

    [Fact]
    public async Task UpsertAsync_WhenMetadataContainsStructuredObjects_ShouldSendStructuredIndexInitializationPayload()
    {
        var handler = new ScriptedHttpMessageHandler();
        EnqueueGreenfieldLifecycleResponses(handler);
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"result":"created"}"""));

        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            AutoCreateIndex = true,
        };
        options.Endpoints = ["http://localhost:9200"];

        using var store = new ElasticsearchProjectionDocumentStore<TestStoreReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "projection-core-tests",
                Mappings: new Dictionary<string, object?>
                {
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["Value"] = new Dictionary<string, object?>
                        {
                            ["type"] = "keyword",
                        },
                    },
                },
                Settings: new Dictionary<string, object?>
                {
                    ["index"] = new Dictionary<string, object?>
                    {
                        ["number_of_shards"] = 1,
                        ["number_of_replicas"] = 0,
                    },
                },
                Aliases: new Dictionary<string, object?>
                {
                    ["projection-core-tests-alias"] = new Dictionary<string, object?>
                    {
                        ["is_write_index"] = true,
                    },
                }),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            httpMessageHandler: handler);

        await store.UpsertAsync(new TestStoreReadModel
        {
            Id = "actor-1",
            ActorId = "actor-1",
            Value = "v1",
        });

        var indexCreate = GetIndexCreateRequest(handler);
        indexCreate.Method.Should().Be("PUT");
        indexCreate.PathAndQuery.Should().NotContain("/_doc/");
        var docProbe = handler.CapturedRequests.Single(r =>
            r.Method == "GET" && r.PathAndQuery.EndsWith("/aevatar-projection-core-tests/_doc/actor-1", StringComparison.Ordinal));
        var docCreate = handler.CapturedRequests.Single(r =>
            r.PathAndQuery.EndsWith("/aevatar-projection-core-tests/_create/actor-1", StringComparison.Ordinal));
        indexCreate.Body.Should().Contain("\"mappings\"");
        indexCreate.Body.Should().Contain("\"properties\"");
        indexCreate.Body.Should().Contain("\"ProjectionDocumentId\"");
        indexCreate.Body.Should().Contain("\"type\":\"keyword\"");
        indexCreate.Body.Should().Contain("\"Value\"");
        indexCreate.Body.Should().Contain("\"number_of_shards\":1");
        indexCreate.Body.Should().Contain("\"projection-core-tests-alias\"");
        indexCreate.Body.Should().Contain("\"is_write_index\":true");
        docCreate.Body.Should().Contain("\"ProjectionDocumentId\":\"actor-1\"");
        _ = docProbe;
    }

    [Fact]
    public void Constructor_WhenStableSortFieldMappingIsNotKeyword_ShouldThrow()
    {
        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            AutoCreateIndex = false,
        };
        options.Endpoints = ["http://localhost:9200"];

        Action act = () => _ = new ElasticsearchProjectionDocumentStore<TestStoreReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "projection-core-tests",
                Mappings: new Dictionary<string, object?>
                {
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["ProjectionDocumentId"] = new Dictionary<string, object?>
                        {
                            ["type"] = "long",
                        },
                    },
                },
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            httpMessageHandler: new ScriptedHttpMessageHandler());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ProjectionDocumentId*keyword*");
    }

    [Fact]
    public async Task UpsertAsync_WhenExistingDocumentPresent_ShouldUseOptimisticConcurrencyTokens()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"_seq_no":7,"_primary_term":3,"_source":{"id":"actor-1","actor_id":"actor-1","state_version":"1","last_event_id":"evt-1","updated_at_utc_value":"2026-03-16T00:00:00Z","value":"v1"}}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"result":"updated"}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
            },
            handler);

        await store.UpsertAsync(new TestStoreReadModel
        {
            Id = "actor-1",
            ActorId = "actor-1",
            StateVersion = 2,
            LastEventId = "evt-2",
            UpdatedAt = DateTimeOffset.Parse("2026-03-16T00:00:01Z"),
            Value = "v2",
        });

        handler.CapturedRequests.Should().HaveCount(2);
        handler.CapturedRequests[0].PathAndQuery.Should().EndWith("/aevatar-projection-core-tests/_doc/actor-1");
        handler.CapturedRequests[1].PathAndQuery.Should().Contain("if_seq_no=7");
        handler.CapturedRequests[1].PathAndQuery.Should().Contain("if_primary_term=3");
    }

    [Fact]
    public async Task UpsertAsync_WhenIncomingAuthoritativeVersionSkipsAhead_ShouldApply()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"_seq_no":7,"_primary_term":3,"_source":{"id":"actor-gap","actor_id":"actor-gap","state_version":"1","last_event_id":"evt-1","updated_at_utc_value":"2026-06-17T00:00:00Z","value":"v1"}}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"result":"updated"}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
            },
            handler);

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
        handler.CapturedRequests.Should().HaveCount(2);
        handler.CapturedRequests[1].PathAndQuery.Should().Contain("if_seq_no=7");
        handler.CapturedRequests[1].PathAndQuery.Should().Contain("if_primary_term=3");
        handler.CapturedRequests[1].Body.Should().Contain("\"state_version\":\"4\"");
        handler.CapturedRequests[1].Body.Should().Contain("\"last_event_id\":\"evt-4\"");
        handler.CapturedRequests[1].Body.Should().Contain("\"value\":\"v4\"");
    }

    [Fact]
    public async Task UpsertAsync_WhenDelayedWriteFollowsVersionedDelete_ShouldRemainTombstoned()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"_seq_no":7,"_primary_term":3,"_source":{"id":"actor-tombstone","actor_id":"actor-tombstone","state_version":"7","last_event_id":"evt-7","updated_at_utc_value":"2026-07-29T00:00:07Z","value":"live"}}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"result":"updated"}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"_seq_no":8,"_primary_term":3,"_source":{"__projection_tombstone":true,"id":"actor-tombstone","actor_id":"actor-tombstone","state_version":"8","last_event_id":"evt-8-delete","updated_at_utc_value":"2026-07-29T00:00:08Z","__projection_deleted_at_utc":"2026-07-29T00:00:08Z","ProjectionDocumentId":"actor-tombstone"}}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
            },
            handler);

        var deleted = await store.DeleteAsync(new ProjectionDocumentDeleteMarker(
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

        deleted.Disposition.Should().Be(ProjectionWriteDisposition.Applied);
        delayed.Disposition.Should().Be(ProjectionWriteDisposition.Stale);
        handler.CapturedRequests.Should().HaveCount(3);
        handler.CapturedRequests[1].Method.Should().Be("PUT");
        handler.CapturedRequests[1].PathAndQuery.Should().Contain("if_seq_no=7");
        var tombstonePayload = ParseJson(handler.CapturedRequests[1].Body);
        tombstonePayload.GetProperty("__projection_tombstone").GetBoolean().Should().BeTrue();
        tombstonePayload.GetProperty("state_version").GetString().Should().Be("8");
        tombstonePayload.GetProperty("last_event_id").GetString().Should().Be("evt-8-delete");
        var tombstoneRequests = handler.CapturedRequests
            .Where(r => r.Method == "PUT" && HasTombstonePayload(r.Body))
            .ToList();
        tombstoneRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task UpsertAsync_WhenReadModelUsesDynamicIndexScope_ShouldTargetScopeSpecificIndices()
    {
        var handler = new ScriptedHttpMessageHandler();
        // Two dynamic scopes → two greenfield lifecycle probes interleaved with the
        // index create + data ops. The order matches the actual call sequence per
        // upsert: GET _alias (404), HEAD <alias> (404), PUT <physical>, GET _doc, PUT _create.
        EnqueueGreenfieldLifecycleResponses(handler);
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"created"}"""));
        EnqueueGreenfieldLifecycleResponses(handler);
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"created"}"""));

        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            AutoCreateIndex = true,
        };
        options.Endpoints = ["http://localhost:9200"];

        using var store = new ElasticsearchProjectionDocumentStore<TestDynamicStoreReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "script-native-read-models",
                Mappings: new Dictionary<string, object?>(),
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            indexScopeSelector: model => model.DocumentIndexScope,
            httpMessageHandler: handler);

        await store.UpsertAsync(new TestDynamicStoreReadModel
        {
            Id = "actor-1",
            ActorId = "actor-1",
            DocumentIndexScope = "dynamic-alpha",
        });
        await store.UpsertAsync(new TestDynamicStoreReadModel
        {
            Id = "actor-2",
            ActorId = "actor-2",
            DocumentIndexScope = "dynamic-beta",
        });

        // Lifecycle introduces 2 probes per first-touch (GET _alias, HEAD <alias>)
        // before the index create. Data ops still target the alias name.
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.Contains("/aevatar-dynamic-alpha-v", StringComparison.Ordinal) && r.Method == "PUT")
            .Should().BeTrue("the alpha-scope physical index should be created with a fingerprint suffix");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.EndsWith("/aevatar-dynamic-alpha/_doc/actor-1", StringComparison.Ordinal))
            .Should().BeTrue("the alpha doc probe should target the alias");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.EndsWith("/aevatar-dynamic-alpha/_create/actor-1", StringComparison.Ordinal))
            .Should().BeTrue("the alpha doc upsert should target the alias");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.Contains("/aevatar-dynamic-beta-v", StringComparison.Ordinal) && r.Method == "PUT")
            .Should().BeTrue("the beta-scope physical index should be created with a fingerprint suffix");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.EndsWith("/aevatar-dynamic-beta/_doc/actor-2", StringComparison.Ordinal))
            .Should().BeTrue("the beta doc probe should target the alias");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.EndsWith("/aevatar-dynamic-beta/_create/actor-2", StringComparison.Ordinal))
            .Should().BeTrue("the beta doc upsert should target the alias");
    }

    [Fact]
    public async Task GetAsync_WhenReadModelUsesDynamicIndexScope_ShouldThrowUnsupported()
    {
        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            AutoCreateIndex = false,
        };
        options.Endpoints = ["http://localhost:9200"];

        using var store = new ElasticsearchProjectionDocumentStore<TestDynamicStoreReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "script-native-read-models",
                Mappings: new Dictionary<string, object?>(),
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            indexScopeSelector: model => model.DocumentIndexScope,
            httpMessageHandler: new ScriptedHttpMessageHandler());

        Func<Task> act = () => store.GetAsync("actor-1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*dynamically indexed read model*");
    }

    [Fact]
    public async Task DeleteAsync_WhenDocumentDeleted_ShouldReturnApplied()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"deleted"}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        var result = await store.DeleteAsync("actor-1");

        result.IsApplied.Should().BeTrue();
        handler.CapturedRequests.Should().ContainSingle(r =>
            r.Method == "DELETE" && r.PathAndQuery.EndsWith("/_doc/actor-1"));
    }

    [Fact]
    public async Task DeleteAsync_WhenLogicalKeyExceedsElasticsearchLimit_ShouldUseHashedDocumentId()
    {
        var logicalKey = new string('\u754c', 300);
        var expectedStorageId = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(logicalKey)))}";
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"deleted"}"""));
        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        var result = await store.DeleteAsync(logicalKey);

        result.IsApplied.Should().BeTrue();
        var requestPath = Uri.UnescapeDataString(handler.CapturedRequests.Single().PathAndQuery);
        requestPath.Should().EndWith($"/_doc/{expectedStorageId}");
    }

    [Fact]
    public async Task DeleteAsync_WhenDocumentNotFound_ShouldReturnDuplicate()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"not_found"}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        var result = await store.DeleteAsync("actor-ghost");

        result.Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);
    }

    [Fact]
    public async Task DeleteAsync_WhenAutoCreateIndexEnabled_ShouldNotBootstrapIndexBeforeDelete()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            """{"error":{"type":"index_not_found_exception"},"status":404}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = true,
                MissingIndexBehavior = ElasticsearchMissingIndexBehavior.WarnAndReturnEmpty,
            },
            handler);

        var result = await store.DeleteAsync("actor-ghost");

        result.Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);
        handler.CapturedRequests.Should().ContainSingle(r =>
            r.Method == "DELETE" && r.PathAndQuery.EndsWith("/_doc/actor-ghost"));
    }

    [Fact]
    public async Task DeleteAsync_WhenIndexMissingAndWarnBehavior_ShouldReturnDuplicate()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            """{"error":{"type":"index_not_found_exception"},"status":404}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
                MissingIndexBehavior = ElasticsearchMissingIndexBehavior.WarnAndReturnEmpty,
            },
            handler);

        var result = await store.DeleteAsync("actor-ghost");

        result.Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);
    }

    [Fact]
    public async Task DeleteAsync_WhenIndexMissingAndThrowBehavior_ShouldThrow()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.NotFound,
            """{"error":{"type":"index_not_found_exception"},"status":404}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false },
            handler);

        Func<Task> act = () => store.DeleteAsync("actor-1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*index*not found*");
    }

    [Fact]
    public async Task DeleteAsync_WhenIdIsBlank_ShouldThrow()
    {
        var handler = new ScriptedHttpMessageHandler();
        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false },
            handler);

        Func<Task> act = () => store.DeleteAsync("   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DeleteAsync_WhenReadModelUsesDynamicIndexScope_ShouldThrowUnsupported()
    {
        var options = new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false };
        options.Endpoints = ["http://localhost:9200"];

        using var store = new ElasticsearchProjectionDocumentStore<TestDynamicStoreReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "script-native-read-models",
                Mappings: new Dictionary<string, object?>(),
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            indexScopeSelector: model => model.DocumentIndexScope,
            httpMessageHandler: new ScriptedHttpMessageHandler());

        Func<Task> act = () => store.DeleteAsync("actor-1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*dynamically indexed read model*");
    }

    [Fact]
    public async Task DeleteAsync_WhenMalformedResponseBody_ShouldFallBackToApplied()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, "not valid json"));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        var result = await store.DeleteAsync("actor-1");

        // 2xx with unparseable body: treat as Applied (conservative default vs dropping the delete).
        result.IsApplied.Should().BeTrue();
    }

    [Fact]
    public async Task GetAndQueryAsync_ShouldHideProviderOwnedTombstones()
    {
        var getHandler = new ScriptedHttpMessageHandler();
        getHandler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"_seq_no":8,"_primary_term":3,"_source":{"__projection_tombstone":true,"id":"actor-tombstone","actor_id":"actor-tombstone","state_version":"8","last_event_id":"evt-8-delete","updated_at_utc_value":"2026-07-29T00:00:08Z","__projection_deleted_at_utc":"2026-07-29T00:00:08Z","ProjectionDocumentId":"actor-tombstone"}}"""));
        using var getStore = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false },
            getHandler);

        var hidden = await getStore.GetAsync("actor-tombstone");

        hidden.Should().BeNull();

        var queryHandler = new ScriptedHttpMessageHandler();
        queryHandler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"hits":{"hits":[{"_source":{"__projection_tombstone":true,"id":"actor-tombstone","actor_id":"actor-tombstone","state_version":"8","last_event_id":"evt-8-delete","updated_at_utc_value":"2026-07-29T00:00:08Z"}}]}}"""));
        using var queryStore = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false },
            queryHandler);

        var query = await queryStore.QueryAsync(new ProjectionDocumentQuery { Take = 10 });

        query.Items.Should().BeEmpty();
        queryHandler.CapturedRequests.Should().ContainSingle()
            .Which.Body.Should().Contain("\"must_not\"");
        queryHandler.CapturedRequests[0].Body.Should().Contain("\"__projection_tombstone\":true");
    }

    [Fact]
    public async Task UpsertAsync_WhenGreenfield_ShouldCreatePhysicalWithFingerprintSuffixAndInlineAlias()
    {
        // Greenfield: GET _alias → 404, HEAD <alias> → 404, PUT <physical> → 200 (success).
        var handler = new ScriptedHttpMessageHandler();
        EnqueueGreenfieldLifecycleResponses(handler);
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"created"}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        await store.UpsertAsync(new TestStoreReadModel { Id = "actor-1", ActorId = "actor-1" });

        var indexCreate = GetIndexCreateRequest(handler);
        indexCreate.PathAndQuery.Should().Contain("/aevatar-projection-core-tests-v");
        indexCreate.Body.Should().Contain("\"aliases\"");
        indexCreate.Body.Should().Contain("\"aevatar-projection-core-tests\"");
        // Lifecycle probes precede the create
        handler.CapturedRequests[0].Method.Should().Be("GET");
        handler.CapturedRequests[0].PathAndQuery.Should().Contain("/_alias/");
        handler.CapturedRequests[1].Method.Should().Be("HEAD");
    }

    [Fact]
    public async Task UpsertAsync_WhenAliasFingerprintDrifts_ShouldFailClosedWithoutLifecycleMutation()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(req =>
        {
            var alias = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath.Substring("/_alias/".Length));
            return CreateJsonResponse(HttpStatusCode.OK, $"{{\"{alias}-v00000000\":{{\"aliases\":{{\"{alias}\":{{}}}}}}}}");
        });

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        var act = () => store.UpsertAsync(new TestStoreReadModel { Id = "actor-1", ActorId = "actor-1" });

        var exception = await act.Should().ThrowAsync<ProjectionIndexSchemaDriftException>();
        exception.Which.IndexAlias.Should().Be("aevatar-projection-core-tests");
        exception.Which.CurrentPhysicalIndex.Should().Be("aevatar-projection-core-tests-v00000000");
        handler.CapturedRequests.Should().ContainSingle(r =>
            r.Method == "GET" &&
            r.PathAndQuery.StartsWith("/_alias/", StringComparison.Ordinal));
        handler.CapturedRequests
            .Any(r => r.Method is "PUT" or "POST" or "DELETE")
            .Should().BeFalse("projection writes must not repair fingerprint drift");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.Contains("/_doc/", StringComparison.Ordinal))
            .Should().BeFalse("document writes must not run while the index lifecycle is drifted");
    }

    [Fact]
    public async Task CheckIndexConsistencyAsync_WhenAliasFingerprintDrifts_ShouldReportDriftWithoutLifecycleMutation()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(req =>
        {
            var alias = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath.Substring("/_alias/".Length));
            return CreateJsonResponse(HttpStatusCode.OK, $"{{\"{alias}-v00000000\":{{\"aliases\":{{\"{alias}\":{{}}}}}}}}");
        });

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        var result = await store.CheckIndexConsistencyAsync();

        result.Status.Should().Be(ProjectionIndexConsistencyStatus.Drifted);
        result.Provider.Should().Be("Elasticsearch");
        result.IndexAlias.Should().Be("aevatar-projection-core-tests");
        result.CurrentPhysicalIndex.Should().Be("aevatar-projection-core-tests-v00000000");
        result.ExpectedPhysicalIndex.Should().StartWith("aevatar-projection-core-tests-v");
        handler.CapturedRequests.Should().ContainSingle(r =>
            r.Method == "GET" &&
            r.PathAndQuery.StartsWith("/_alias/", StringComparison.Ordinal));
        handler.CapturedRequests
            .Any(r => r.Method is "PUT" or "POST" or "DELETE")
            .Should().BeFalse("the consistency probe must not mutate indices or aliases");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.Contains("_search", StringComparison.Ordinal))
            .Should().BeFalse("the consistency probe must not query read models");
    }

    [Fact]
    public async Task QueryAsync_WhenAliasFingerprintDrifts_ShouldThrowTypedDriftWithoutLifecycleMutation()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(req =>
        {
            var alias = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath.Substring("/_alias/".Length));
            return CreateJsonResponse(HttpStatusCode.OK, $"{{\"{alias}-v00000000\":{{\"aliases\":{{\"{alias}\":{{}}}}}}}}");
        });

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        Func<Task> act = () => store.QueryAsync(new ProjectionDocumentQuery());

        var exception = await act.Should().ThrowAsync<ProjectionIndexSchemaDriftException>();
        exception.Which.IndexAlias.Should().Be("aevatar-projection-core-tests");
        exception.Which.CurrentPhysicalIndex.Should().Be("aevatar-projection-core-tests-v00000000");
        handler.CapturedRequests.Should().ContainSingle(r =>
            r.Method == "GET" &&
            r.PathAndQuery.StartsWith("/_alias/", StringComparison.Ordinal));
        handler.CapturedRequests
            .Any(r => r.Method is "PUT" or "POST" or "DELETE")
            .Should().BeFalse("projection reads must not repair drift through lifecycle mutation");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.Contains("_search", StringComparison.Ordinal))
            .Should().BeFalse("the drifted read path must fail before querying a stale read model");
    }

    [Fact]
    public async Task UpsertAsync_WhenBareIndexExistsWithoutAlias_ShouldWrapItIntoAliasedPhysical()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{}"""));  // GET _alias/<name>
        handler.EnqueueResponse(_ => new HttpResponseMessage(HttpStatusCode.OK));              // HEAD <name>: bare exists
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}""")); // PUT physical
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK,
            """{"took":3,"timed_out":false,"total":1,"updated":0,"created":1,"failures":[]}""")); // POST _reindex
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}""")); // POST _aliases
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}""")); // GET _doc probe
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"created"}""")); // PUT _create

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        await store.UpsertAsync(new TestStoreReadModel { Id = "actor-1", ActorId = "actor-1" });

        var reindex = handler.CapturedRequests.Single(r =>
            r.PathAndQuery.StartsWith("/_reindex", StringComparison.Ordinal));
        reindex.Body.Should().Contain("\"source\"");
        reindex.Body.Should().Contain("\"dest\"");
        reindex.Body.Should().Contain("\"aevatar-projection-core-tests\"");
        reindex.Body.Should().Contain("\"aevatar-projection-core-tests-v");

        var aliasSwap = handler.CapturedRequests.Single(r =>
            r.PathAndQuery == "/_aliases" && r.Method == "POST");
        aliasSwap.Body.Should().Contain("\"add\"");
        aliasSwap.Body.Should().Contain("\"remove_index\"");
        aliasSwap.Body.Should().Contain("\"aevatar-projection-core-tests\"");
    }

    [Fact]
    public async Task UpsertAsync_WhenAliasHasMultipleBackings_ShouldFailClosedWithoutReindexing()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(req =>
        {
            var alias = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath.Substring("/_alias/".Length));
            return CreateJsonResponse(HttpStatusCode.OK,
                $"{{\"{alias}-v00000000\":{{\"aliases\":{{\"{alias}\":{{}}}}}},\"{alias}-v11111111\":{{\"aliases\":{{\"{alias}\":{{}}}}}}}}");
        });

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        var act = () => store.UpsertAsync(new TestStoreReadModel { Id = "actor-1", ActorId = "actor-1" });

        var exception = await act.Should().ThrowAsync<ProjectionIndexSchemaDriftException>();
        exception.Which.IndexAlias.Should().Be("aevatar-projection-core-tests");
        exception.Which.CurrentPhysicalIndex.Should().Contain("aevatar-projection-core-tests-v00000000");
        exception.Which.CurrentPhysicalIndex.Should().Contain("aevatar-projection-core-tests-v11111111");
        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.StartsWith("/_reindex", StringComparison.Ordinal))
            .Should().BeFalse("ambiguous alias backing must fail before data copy");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery == "/_aliases" && r.Method == "POST")
            .Should().BeFalse("ambiguous alias backing must not be swapped automatically");
    }

    [Fact]
    public async Task CheckIndexConsistencyAsync_WhenAliasHasMultipleBackings_ShouldReportDriftWithoutLifecycleMutation()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(req =>
        {
            var alias = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath.Substring("/_alias/".Length));
            return CreateJsonResponse(HttpStatusCode.OK,
                $"{{\"{alias}-v00000000\":{{\"aliases\":{{\"{alias}\":{{}}}}}},\"{alias}-v11111111\":{{\"aliases\":{{\"{alias}\":{{}}}}}}}}");
        });

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        var result = await store.CheckIndexConsistencyAsync();

        result.Status.Should().Be(ProjectionIndexConsistencyStatus.Drifted);
        result.Provider.Should().Be("Elasticsearch");
        result.IndexAlias.Should().Be("aevatar-projection-core-tests");
        result.CurrentPhysicalIndex.Should().Be(
            "aevatar-projection-core-tests-v00000000,aevatar-projection-core-tests-v11111111");
        result.ExpectedPhysicalIndex.Should().StartWith("aevatar-projection-core-tests-v");
        result.Message.Should().Contain("multiple physical indices");
        handler.CapturedRequests.Should().ContainSingle(r =>
            r.Method == "GET" &&
            r.PathAndQuery.StartsWith("/_alias/", StringComparison.Ordinal));
        handler.CapturedRequests
            .Any(r => r.Method is "PUT" or "POST" or "DELETE")
            .Should().BeFalse("the consistency probe must not mutate indices or aliases");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.Contains("_search", StringComparison.Ordinal))
            .Should().BeFalse("the consistency probe must not query read models");
    }

    [Fact]
    public async Task UpsertAsync_WhenAliasFingerprintDrifts_ShouldFailBeforeReindexing()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(req =>
        {
            var alias = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath.Substring("/_alias/".Length));
            return CreateJsonResponse(HttpStatusCode.OK, $"{{\"{alias}-v00000000\":{{\"aliases\":{{\"{alias}\":{{}}}}}}}}");
        });

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        var act = () => store.UpsertAsync(new TestStoreReadModel { Id = "actor-1", ActorId = "actor-1" });

        await act.Should().ThrowAsync<ProjectionIndexSchemaDriftException>();
        handler.CapturedRequests.Should().ContainSingle(r =>
            r.Method == "GET" &&
            r.PathAndQuery.StartsWith("/_alias/", StringComparison.Ordinal));
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.StartsWith("/_reindex", StringComparison.Ordinal))
            .Should().BeFalse("write-path drift must fail before any data copy");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery == "/_aliases" && r.Method == "POST")
            .Should().BeFalse("write-path drift must not swap aliases");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.Contains("/_doc/", StringComparison.Ordinal))
            .Should().BeFalse("document writes must not run while the index lifecycle is drifted");
    }

    [Fact]
    public async Task ReconcileIndexAsync_WhenStaticAliasDrifts_ShouldUseDataSafeLifecycleMigration()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(req =>
        {
            var alias = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath.Substring("/_alias/".Length));
            return CreateJsonResponse(HttpStatusCode.OK, $"{{\"{alias}-v00000000\":{{\"aliases\":{{\"{alias}\":{{}}}}}}}}");
        });
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, "")); // HEAD expected -> missing
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK,
            """{"took":3,"timed_out":false,"total":1,"updated":0,"created":1,"failures":[]}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);
        await store.ReconcileIndexAsync();

        handler.CapturedRequests.Should().Contain(r =>
            r.Method == "PUT" &&
            r.PathAndQuery.Contains("/aevatar-projection-core-tests-v", StringComparison.Ordinal));
        handler.CapturedRequests.Should().ContainSingle(r =>
            r.PathAndQuery.StartsWith("/_reindex", StringComparison.Ordinal));
        handler.CapturedRequests.Should().ContainSingle(r =>
            r.PathAndQuery == "/_aliases" && r.Method == "POST");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.Contains("/_doc/", StringComparison.Ordinal))
            .Should().BeFalse("startup lifecycle must not read or write read-model documents");
    }

    [Fact]
    public async Task ReconcileIndexAsync_WhenAliasHasMultipleBackings_ShouldFailClosedWithoutReindexingOrAliasSwap()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(req =>
        {
            var alias = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath.Substring("/_alias/".Length));
            return CreateJsonResponse(HttpStatusCode.OK,
                $"{{\"{alias}-v00000000\":{{\"aliases\":{{\"{alias}\":{{}}}}}},\"{alias}-v11111111\":{{\"aliases\":{{\"{alias}\":{{}}}}}}}}");
        });

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        var act = async () => await store.ReconcileIndexAsync();

        var exception = await act.Should().ThrowAsync<ProjectionIndexSchemaDriftException>();
        exception.Which.IndexAlias.Should().Be("aevatar-projection-core-tests");
        exception.Which.CurrentPhysicalIndex.Should().Contain("aevatar-projection-core-tests-v00000000");
        exception.Which.CurrentPhysicalIndex.Should().Contain("aevatar-projection-core-tests-v11111111");
        handler.CapturedRequests.Should().ContainSingle(r =>
            r.Method == "GET" &&
            r.PathAndQuery.StartsWith("/_alias/", StringComparison.Ordinal));
        handler.CapturedRequests
            .Any(r => r.Method == "PUT")
            .Should().BeFalse("ambiguous startup reconcile must not create a replacement physical index");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.StartsWith("/_reindex", StringComparison.Ordinal))
            .Should().BeFalse("ambiguous startup reconcile must fail before data copy");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery == "/_aliases" && r.Method == "POST")
            .Should().BeFalse("ambiguous startup reconcile must not swap aliases automatically");
        handler.CapturedRequests
            .Any(r =>
                r.PathAndQuery.Contains("/_doc/", StringComparison.Ordinal) ||
                r.PathAndQuery.Contains("/_create/", StringComparison.Ordinal))
            .Should().BeFalse("startup reconcile must not touch read-model documents");
    }

    [Fact]
    public async Task ReconcileIndexAsync_WhenReadModelUsesDynamicIndexScope_ShouldSkipLifecycle()
    {
        var handler = new ScriptedHttpMessageHandler();
        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            AutoCreateIndex = true,
        };
        options.Endpoints = ["http://localhost:9200"];

        using var store = new ElasticsearchProjectionDocumentStore<TestDynamicStoreReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "script-native-read-models",
                Mappings: new Dictionary<string, object?>(),
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            indexScopeSelector: model => model.DocumentIndexScope,
            httpMessageHandler: handler);
        await store.ReconcileIndexAsync();

        handler.CapturedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_WhenSameMetadataReused_ShouldProduceSameFingerprint()
    {
        // Determinism check: two stores constructed with structurally identical
        // metadata must compute the same physical-index name (same fingerprint).
        async Task<string> CapturePhysicalAsync()
        {
            var handler = new ScriptedHttpMessageHandler();
            EnqueueGreenfieldLifecycleResponses(handler);
            handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
            handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
            handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"created"}"""));

            using var store = CreateStore(
                new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
                handler);
            await store.UpsertAsync(new TestStoreReadModel { Id = "actor-1", ActorId = "actor-1" });

            return GetIndexCreateRequest(handler).PathAndQuery;
        }

        var first = await CapturePhysicalAsync();
        var second = await CapturePhysicalAsync();
        first.Should().Be(second, "fingerprint must be deterministic across constructions");
        first.Should().Contain("-v");
        first.Length.Should().BeGreaterThan("aevatar-projection-core-tests-v".Length);
    }

    [Fact]
    public async Task ReconcileIndexAsync_WhenDriftAndExpectedMissing_ShouldReindexForwardThenAtomicSwap()
    {
        const string oldPhysical = "aevatar-projection-core-tests-vstale01";
        var handler = new ScriptedHttpMessageHandler();
        // GET _alias -> alias points at a stale physical (schema drift).
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"aevatar-projection-core-tests-vstale01":{"aliases":{"aevatar-projection-core-tests":{}}}}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, "")); // HEAD expected -> missing
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}""")); // PUT expected
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"failures":[],"timed_out":false}""")); // POST _reindex
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}""")); // POST _aliases

        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true }, handler);

        await store.ReconcileIndexAsync();

        // Data copied forward via reindex (proves it is NOT an empty CreateFreshAliased shortcut).
        var reindex = handler.CapturedRequests
            .Should().ContainSingle(r => r.Method == "POST" && r.PathAndQuery.Contains("/_reindex")).Subject;
        reindex.Body.Should().Contain(oldPhysical);
        // Atomic alias swap retains the old physical (remove, NOT remove_index).
        var aliases = handler.CapturedRequests
            .Should().ContainSingle(r => r.Method == "POST" && r.PathAndQuery.EndsWith("/_aliases")).Subject;
        aliases.Body.Should().Contain("\"add\"").And.Contain("\"remove\"");
        aliases.Body.Should().NotContain("remove_index");
        aliases.Body.Should().Contain(oldPhysical);
        // Expected physical was created (PUT to a -v physical other than the stale one).
        handler.CapturedRequests.Should().Contain(r =>
            r.Method == "PUT"
            && r.PathAndQuery.Contains("aevatar-projection-core-tests-v")
            && !r.PathAndQuery.Contains(oldPhysical));
    }

    [Fact]
    public async Task ReconcileIndexAsync_WhenDriftAndExpectedExists_ShouldTopUpFromSourceThenSwapAlias()
    {
        // An interrupted earlier heal can leave the expected physical partially filled, so the
        // reconcile must copy source -> destination (overwrite) before it moves the alias.
        const string oldPhysical = "aevatar-projection-core-tests-vstale01";
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            $"{{\"{oldPhysical}\":{{\"aliases\":{{\"aevatar-projection-core-tests\":{{}}}}}}}}"));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, "")); // HEAD expected -> exists
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"took":3,"timed_out":false,"total":2,"updated":1,"created":1,"version_conflicts":0,"failures":[]}""")); // POST _reindex top-up
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}""")); // POST _aliases

        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true }, handler);

        await store.ReconcileIndexAsync();

        var reindex = handler.CapturedRequests
            .Should().ContainSingle(r => r.Method == "POST" && r.PathAndQuery.StartsWith("/_reindex", StringComparison.Ordinal)).Subject;
        reindex.Body.Should().Contain(oldPhysical).And.NotContain("op_type").And.NotContain("conflicts");
        handler.CapturedRequests
            .Should().ContainSingle(r => r.Method == "POST" && r.PathAndQuery.EndsWith("/_aliases")).Subject
            .Body.Should().Contain("\"add\"").And.Contain("\"remove\"").And.Contain(oldPhysical).And.NotContain("remove_index");
    }

    [Fact]
    public async Task ReconcileIndexAsync_WhenDriftAndExpectedExists_AndTopUpMissesDocuments_ShouldNotSwapAlias()
    {
        const string oldPhysical = "aevatar-projection-core-tests-vstale01";
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            $"{{\"{oldPhysical}\":{{\"aliases\":{{\"aevatar-projection-core-tests\":{{}}}}}}}}"));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, "")); // HEAD expected -> exists
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"took":3,"timed_out":false,"total":3,"updated":1,"created":1,"version_conflicts":0,"failures":[]}""")); // POST _reindex top-up short by one

        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true }, handler);

        var act = () => store.ReconcileIndexAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*did not account for every source document*");
        handler.CapturedRequests.Should().NotContain(r => r.PathAndQuery.EndsWith("/_aliases"));
    }

    [Fact]
    public void ReindexRequestTimeout_ShouldOutliveReindexCompletionBudget()
    {
        ElasticsearchIndexLifecycleManager.ReindexRequestTimeout.Should().BeGreaterThan(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task ReconcileIndexAsync_WhenReindexReportsFailures_ShouldThrowAndNotSwapAlias()
    {
        const string oldPhysical = "aevatar-projection-core-tests-vstale01";
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            $"{{\"{oldPhysical}\":{{\"aliases\":{{\"aevatar-projection-core-tests\":{{}}}}}}}}"));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, "")); // HEAD expected -> missing
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}""")); // PUT expected
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"failures":[{"index":"x","cause":{"type":"version_conflict_engine_exception"}}]}""")); // POST _reindex with failures

        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true }, handler);

        var act = async () => await store.ReconcileIndexAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        // The alias was never swapped onto the partially-copied physical.
        handler.CapturedRequests.Should().NotContain(r => r.PathAndQuery.EndsWith("/_aliases"));
    }

    [Fact]
    public async Task ReconcileIndexAsync_WhenAutoCreateDisabled_ShouldBeNoOp()
    {
        var handler = new ScriptedHttpMessageHandler();

        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = false }, handler);

        await store.ReconcileIndexAsync();

        handler.CapturedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileIndexAsync_WhenGreenfield_ShouldCreateFreshAliasedWithoutReindex()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, "{}")); // GET _alias -> none
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, "")); // HEAD bare alias -> none
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}""")); // PUT fresh physical

        using var store = CreateStore(new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true }, handler);

        await store.ReconcileIndexAsync();

        handler.CapturedRequests.Should().NotContain(r => r.PathAndQuery.Contains("/_reindex"));
        handler.CapturedRequests.Should().Contain(r =>
            r.Method == "PUT" && r.PathAndQuery.Contains("aevatar-projection-core-tests-v"));
    }

    [Fact]
    public async Task UpsertAsync_AfterExplicitReconcile_ShouldNotRepeatIndexLifecycleProbe()
    {
        var handler = new ScriptedHttpMessageHandler();
        EnqueueGreenfieldLifecycleResponses(handler);
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"created"}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
            handler);

        await store.ReconcileIndexAsync();
        await store.UpsertAsync(new TestStoreReadModel { Id = "actor-1", ActorId = "actor-1" });

        handler.CapturedRequests.Select(static request => request.Method)
            .Should().Equal("GET", "HEAD", "PUT", "GET", "PUT");
        handler.CapturedRequests.Should().ContainSingle(request =>
            request.PathAndQuery.StartsWith("/_alias/", StringComparison.Ordinal));
    }

    [Fact]
    public void AddElasticsearchDocumentProjectionStore_ForMultipleReadModels_ShouldEnumerateDistinctReconcileTargets()
    {
        var services = new ServiceCollection();
        RegisterStore<TestStoreReadModel>(services, "alias-a");
        RegisterStore<TestRecursiveWellKnownReadModel>(services, "alias-b");

        // Must not throw "indistinguishable ... IProjectionIndexReconcileTarget" at ValidateOnBuild —
        // the regression that crash-looped the host when this used TryAddEnumerable with a factory.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var targets = provider.GetServices<IProjectionIndexReconcileTarget>().ToList();
        targets.Should().HaveCount(2);
        targets.Select(t => t.IndexAlias).Should().OnlyHaveUniqueItems();

        static void RegisterStore<TReadModel>(IServiceCollection services, string indexName)
            where TReadModel : class, IProjectionReadModel<TReadModel>, new()
        {
            services.AddElasticsearchDocumentProjectionStore<TReadModel, string>(
                optionsFactory: _ => new ElasticsearchProjectionDocumentStoreOptions { Endpoints = ["http://localhost:9200"] },
                metadataFactory: _ => new DocumentIndexMetadata(
                    IndexName: indexName,
                    Mappings: new Dictionary<string, object?>(),
                    Settings: new Dictionary<string, object?>(),
                    Aliases: new Dictionary<string, object?>()),
                keySelector: static _ => string.Empty, // never invoked: this test only resolves, never upserts
                keyFormatter: static key => key);
        }
    }

    private static ElasticsearchProjectionDocumentStore<TestStoreReadModel, string> CreateStore(
        ElasticsearchProjectionDocumentStoreOptions options,
        HttpMessageHandler handler)
    {
        options.Endpoints = ["http://localhost:9200"];
        return new ElasticsearchProjectionDocumentStore<TestStoreReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: "projection-core-tests",
                Mappings: new Dictionary<string, object?>(),
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key,
            httpMessageHandler: handler);
    }

    private static ServiceProvider CreateRepairProvider(
        ElasticsearchProjectionDocumentStoreOptions options,
        HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(CreateStore(options, handler));
        services.AddElasticsearchDocumentProjectionRepairStore<TestStoreReadModel, string>();
        return services.BuildServiceProvider();
    }

    private static IElasticsearchProjectionDocumentRepairStore<TestStoreReadModel, string> RepairStore(
        IServiceProvider provider) =>
        provider.GetRequiredService<
            IElasticsearchProjectionDocumentRepairStore<TestStoreReadModel, string>>();

    private static void RegisterProjectionStore(IServiceCollection services)
    {
        services.AddElasticsearchDocumentProjectionStore<TestStoreReadModel, string>(
            _ => new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = true,
                Endpoints = ["http://localhost:9200"],
            },
            _ => new DocumentIndexMetadata(
                IndexName: "projection-core-tests",
                Mappings: new Dictionary<string, object?>(),
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            keySelector: model => model.Id,
            keyFormatter: key => key);
    }

    private static string ExistingRepairDocumentJson()
    {
        return """
               {
                 "_index":"aevatar-mainnet-test-v1",
                 "_seq_no":12,
                 "_primary_term":3,
                 "_source":{
                   "id":"doc-1",
                   "actor_id":"actor-1",
                   "state_version":"7",
                   "last_event_id":"event-7",
                   "updated_at_utc_value":"2026-07-25T00:00:00Z"
                 }
               }
               """;
    }

    private static string MissingRepairDocumentJson(
        string indexName = "aevatar-mainnet-test-v1") =>
        $$"""
        {
          "_index":"{{indexName}}",
          "_id":"doc-1",
          "found":false
        }
        """;

    private static string DeleteMissingRepairDocumentJson() =>
        """
        {
          "_index":"aevatar-mainnet-test-v1",
          "_id":"doc-1",
          "result":"not_found"
        }
        """;

    private static ScriptedHttpMessageHandler CreateSuccessfulUpsertHandler()
    {
        var handler = new ScriptedHttpMessageHandler();
        EnqueueGreenfieldLifecycleResponses(handler);
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"created"}"""));
        return handler;
    }

    private static void EnqueueGreenfieldLifecycleResponses(ScriptedHttpMessageHandler handler)
    {
        // The index lifecycle manager probes alias state before creating an
        // index. For tests that exercise the greenfield path (no pre-existing
        // alias, no pre-existing bare index), both probes return 404 so the
        // manager falls through to the fresh-aliased create branch.
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, ""));
    }

    private static CapturedRequest GetIndexCreateRequest(ScriptedHttpMessageHandler handler)
    {
        return handler.CapturedRequests.First(r =>
            r.Method == "PUT" &&
            !r.PathAndQuery.Contains("/_doc/", StringComparison.Ordinal) &&
            !r.PathAndQuery.Contains("/_create/", StringComparison.Ordinal) &&
            !r.PathAndQuery.Contains("/_aliases", StringComparison.Ordinal));
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool HasTombstonePayload(string json)
    {
        var payload = ParseJson(json);
        return payload.TryGetProperty("__projection_tombstone", out var tombstone) &&
               tombstone.ValueKind == JsonValueKind.True;
    }

    private static IReadOnlyDictionary<string, JsonElement> GetProperties(JsonElement indexPayload)
    {
        return indexPayload
            .GetProperty("mappings")
            .GetProperty("properties")
            .EnumerateObject()
            .ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal);
    }

    private static JsonElement GetFieldMapping(JsonElement indexPayload, params string[] fieldPath)
    {
        fieldPath.Should().NotBeEmpty();

        IReadOnlyDictionary<string, JsonElement> properties = GetProperties(indexPayload);
        JsonElement mapping = default;
        for (var index = 0; index < fieldPath.Length; index++)
        {
            mapping = properties[fieldPath[index]];
            if (index == fieldPath.Length - 1)
                return mapping;

            properties = mapping
                .GetProperty("properties")
                .EnumerateObject()
                .ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal);
        }

        throw new InvalidOperationException("The field path must contain at least one segment.");
    }

    private static string? GetMappingType(JsonElement indexPayload, params string[] fieldPath)
    {
        return GetFieldMapping(indexPayload, fieldPath).GetProperty("type").GetString();
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

        public List<CapturedRequest> CapturedRequests { get; } = [];

        public void EnqueueResponse(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responses.Enqueue(responseFactory);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestBody = request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            CapturedRequests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri?.PathAndQuery ?? "",
                requestBody));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No scripted response available for request '{request.Method} {request.RequestUri}'.");
            }

            return _responses.Dequeue().Invoke(request);
        }
    }

    private sealed record CapturedRequest(string Method, string PathAndQuery, string Body);

}
