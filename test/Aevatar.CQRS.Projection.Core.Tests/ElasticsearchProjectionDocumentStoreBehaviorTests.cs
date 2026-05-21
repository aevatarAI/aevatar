using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ElasticsearchProjectionDocumentStoreBehaviorTests
{
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

        var searchRequest = handler.CapturedRequests
            .Should().ContainSingle(r => r.PathAndQuery.EndsWith("/_search")).Subject;
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
        handler.SetMappingResponse(_ => CreateMappingResponse(
            """{"actor_id":{"type":"keyword"}}"""));
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

        var searchRequest = handler.CapturedRequests
            .Should().ContainSingle(r => r.PathAndQuery.EndsWith("/_search")).Subject;
        searchRequest.Body.Should().Contain("\"actor_id\":\"actor-1\"");
        searchRequest.Body.Should().Contain("\"updated_at_utc_value\"");
        searchRequest.Body.Should().NotContain("\"ActorId\"");
        searchRequest.Body.Should().NotContain("\"UpdatedAt\"");
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

        var indexPayload = ParseJson(handler.CapturedRequests[0].Body);
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

        var indexPayload = ParseJson(handler.CapturedRequests[0].Body);
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

        var indexPayload = ParseJson(handler.CapturedRequests[0].Body);
        var actorIdMapping = GetFieldMapping(indexPayload, "actor_id");
        actorIdMapping.GetProperty("type").GetString().Should().Be("text");
        actorIdMapping.GetProperty("analyzer").GetString().Should().Be("standard");
    }

    [Fact]
    public async Task UpsertAsync_WhenDescriptorContainsOpenFields_ShouldNotInitializeStaticMappingsForThem()
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

        var indexPayload = ParseJson(handler.CapturedRequests[0].Body);
        var properties = GetProperties(indexPayload);
        properties.Should().NotContainKey("fields_value");
        properties.Should().NotContainKey("open_payload");
        properties.Should().NotContainKey("labels");
        properties.Should().NotContainKey("entries");
        properties.Should().NotContainKey("tags");
        GetMappingType(indexPayload, "updated_at_utc_value").Should().Be("date");
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

        var indexPayload = ParseJson(handler.CapturedRequests[0].Body);
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

        var searchRequest = handler.CapturedRequests
            .Should().ContainSingle(r => r.PathAndQuery.EndsWith("/_search")).Subject;
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

        var searchRequest = handler.CapturedRequests
            .Should().ContainSingle(r => r.PathAndQuery.EndsWith("/_search")).Subject;
        searchRequest.Body.Should().Contain("\"value\"");
        searchRequest.Body.Should().Contain("\"missing\":\"_last\"");
        searchRequest.Body.Should().Contain("\"unmapped_type\":\"keyword\"");
    }

    [Fact]
    public async Task QueryAsync_WhenLiveIndexMappingIsKeyword_ShouldNotAppendKeywordSuffix()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.SetMappingResponse(_ => CreateMappingResponse(
            """{"value":{"type":"keyword"}}"""));
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
                    FieldPath = nameof(TestStoreReadModel.Value),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString("v1"),
                },
            ],
        });

        var searchRequest = handler.CapturedRequests
            .Should().ContainSingle(r => r.PathAndQuery.EndsWith("/_search")).Subject;
        searchRequest.Body.Should().Contain("\"value\":\"v1\"");
        searchRequest.Body.Should().NotContain("\"value.keyword\"");
    }

    [Fact]
    public async Task QueryAsync_WhenLiveIndexMappingIsTextWithoutKeyword_ShouldNotInventKeywordSuffix()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.SetMappingResponse(_ => CreateMappingResponse(
            """{"value":{"type":"text"}}"""));
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
                    FieldPath = nameof(TestStoreReadModel.Value),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString("v1"),
                },
            ],
        });

        var searchRequest = handler.CapturedRequests
            .Should().ContainSingle(r => r.PathAndQuery.EndsWith("/_search")).Subject;
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

        var searchRequest = handler.CapturedRequests
            .Should().ContainSingle(r => r.PathAndQuery.EndsWith("/_search")).Subject;
        searchRequest.Body.Should().Contain("\"value.keyword\":\"v1\"");
    }

    [Fact]
    public async Task QueryAsync_WhenLiveIndexMapsAugmentedKeywordFieldAsTextMultiField_ShouldTargetKeywordSubfield()
    {
        // Regression for #743: `actor_id` is an `_id`-suffix field, so descriptor augmentation
        // declares it `keyword` in code-side metadata. A projection index created before that
        // augmentation shipped still carries ES's dynamic `text` + `.keyword` multi-field mapping.
        // The exact-match term filter must target the physical `.keyword` sub-field, otherwise the
        // term query hits the analyzed `text` field and never matches a UUID-shaped value.
        var handler = new ScriptedHttpMessageHandler();
        handler.SetMappingResponse(_ => CreateMappingResponse(
            """{"actor_id":{"type":"text","fields":{"keyword":{"type":"keyword","ignore_above":256}}}}"""));
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
                    Value = ProjectionDocumentValue.FromString("801667e9-772d-4bdf-8000-717ce331746c"),
                },
            ],
        });

        handler.CapturedRequests[0].Method.Should().Be("GET");
        handler.CapturedRequests[0].PathAndQuery.Should().EndWith("/aevatar-projection-core-tests/_mapping");
        var searchRequest = handler.CapturedRequests
            .Should().ContainSingle(r => r.PathAndQuery.EndsWith("/_search")).Subject;
        searchRequest.Body.Should().Contain(
            "\"actor_id.keyword\":\"801667e9-772d-4bdf-8000-717ce331746c\"");
        searchRequest.Body.Should().NotContain("\"actor_id\":\"801667e9");
    }

    [Fact]
    public async Task QueryAsync_WhenLiveIndexMappingProbeFails_ShouldFallBackToDeclaredMetadata()
    {
        // When the `_mapping` probe cannot read physical truth (here: an Elasticsearch 500), the
        // resolver falls back to declared/augmented metadata — `actor_id` is augmented to `keyword`,
        // so the term targets the bare field. This preserves pre-#743 behaviour on probe failure.
        var handler = new ScriptedHttpMessageHandler();
        handler.SetMappingResponse(_ => CreateJsonResponse(
            HttpStatusCode.InternalServerError,
            """{"error":"mapping unavailable"}"""));
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
        });

        var searchRequest = handler.CapturedRequests
            .Should().ContainSingle(r => r.PathAndQuery.EndsWith("/_search")).Subject;
        searchRequest.Body.Should().Contain("\"actor_id\":\"actor-1\"");
        searchRequest.Body.Should().NotContain("\"actor_id.keyword\"");
    }

    [Fact]
    public async Task QueryAsync_WhenCalledRepeatedly_ShouldProbeLiveIndexMappingOnce()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.SetMappingResponse(_ => CreateMappingResponse(
            """{"value":{"type":"keyword"}}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"hits":{"hits":[]}}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"hits":{"hits":[]}}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
            },
            handler);

        _ = await store.QueryAsync(new ProjectionDocumentQuery());
        _ = await store.QueryAsync(new ProjectionDocumentQuery());

        handler.CapturedRequests.Count(r => r.PathAndQuery.EndsWith("/_mapping")).Should().Be(1);
        handler.CapturedRequests.Count(r => r.PathAndQuery.EndsWith("/_search")).Should().Be(2);
    }

    [Fact]
    public async Task UpsertAsync_WhenMetadataContainsStructuredObjects_ShouldSendStructuredIndexInitializationPayload()
    {
        var handler = new ScriptedHttpMessageHandler();
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

        handler.CapturedRequests.Should().HaveCount(3);
        handler.CapturedRequests[0].Method.Should().Be("PUT");
        handler.CapturedRequests[0].PathAndQuery.Should().NotContain("/_doc/");
        handler.CapturedRequests[1].Method.Should().Be("GET");
        handler.CapturedRequests[1].PathAndQuery.Should().EndWith("/aevatar-projection-core-tests/_doc/actor-1");
        handler.CapturedRequests[2].PathAndQuery.Should().EndWith("/aevatar-projection-core-tests/_create/actor-1");
        handler.CapturedRequests[0].Body.Should().Contain("\"mappings\"");
        handler.CapturedRequests[0].Body.Should().Contain("\"properties\"");
        handler.CapturedRequests[0].Body.Should().Contain("\"ProjectionDocumentId\"");
        handler.CapturedRequests[0].Body.Should().Contain("\"type\":\"keyword\"");
        handler.CapturedRequests[0].Body.Should().Contain("\"Value\"");
        handler.CapturedRequests[0].Body.Should().Contain("\"number_of_shards\":1");
        handler.CapturedRequests[0].Body.Should().Contain("\"projection-core-tests-alias\"");
        handler.CapturedRequests[0].Body.Should().Contain("\"is_write_index\":true");
        handler.CapturedRequests[2].Body.Should().Contain("\"ProjectionDocumentId\":\"actor-1\"");
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
    public async Task UpsertAsync_WhenReadModelUsesDynamicIndexScope_ShouldTargetScopeSpecificIndices()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"created"}"""));
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

        handler.CapturedRequests.Should().HaveCount(6);
        handler.CapturedRequests[0].PathAndQuery.Should().EndWith("/aevatar-dynamic-alpha");
        handler.CapturedRequests[1].PathAndQuery.Should().EndWith("/aevatar-dynamic-alpha/_doc/actor-1");
        handler.CapturedRequests[2].PathAndQuery.Should().EndWith("/aevatar-dynamic-alpha/_create/actor-1");
        handler.CapturedRequests[3].PathAndQuery.Should().EndWith("/aevatar-dynamic-beta");
        handler.CapturedRequests[4].PathAndQuery.Should().EndWith("/aevatar-dynamic-beta/_doc/actor-2");
        handler.CapturedRequests[5].PathAndQuery.Should().EndWith("/aevatar-dynamic-beta/_create/actor-2");
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

    private static ScriptedHttpMessageHandler CreateSuccessfulUpsertHandler()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"created"}"""));
        return handler;
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, JsonElement> GetProperties(JsonElement indexPayload)
    {
        return indexPayload
            .GetProperty("mappings")
            .GetProperty("properties")
            .EnumerateObject()
            .ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal);
    }

    private static JsonElement GetFieldMapping(JsonElement indexPayload, string fieldName)
    {
        return GetProperties(indexPayload)[fieldName];
    }

    private static string? GetMappingType(JsonElement indexPayload, string fieldName)
    {
        return GetFieldMapping(indexPayload, fieldName).GetProperty("type").GetString();
    }

    // CreateStore / the explicit-metadata stores all resolve to this concrete index name
    // (default "aevatar" prefix + "projection-core-tests" scope).
    private const string TestIndexName = "aevatar-projection-core-tests";

    // Builds an Elasticsearch `GET <index>/_mapping` response body. `propertiesJson` is the raw
    // JSON object placed under `mappings.properties` (e.g. {"value":{"type":"keyword"}}).
    private static HttpResponseMessage CreateMappingResponse(string propertiesJson)
    {
        return CreateJsonResponse(
            HttpStatusCode.OK,
            "{\"" + TestIndexName + "\":{\"mappings\":{\"properties\":" + propertiesJson + "}}}");
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
        private Func<HttpRequestMessage, HttpResponseMessage>? _mappingResponseFactory;

        public List<CapturedRequest> CapturedRequests { get; } = [];

        public void EnqueueResponse(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responses.Enqueue(responseFactory);
        }

        // `GET <index>/_mapping` is a transparent, idempotent probe issued by the query path to
        // resolve keyword/text field paths from physical index truth. It is served from a dedicated
        // slot so scripted `_search` sequences stay focused on the operation under test. Tests that
        // exercise field-path resolution set an explicit mapping; the rest get an empty mapping.
        public void SetMappingResponse(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _mappingResponseFactory = responseFactory;
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

            if (request.Method == HttpMethod.Get &&
                (request.RequestUri?.PathAndQuery ?? "").EndsWith("/_mapping", StringComparison.Ordinal))
            {
                return (_mappingResponseFactory ?? DefaultMappingResponse).Invoke(request);
            }

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No scripted response available for request '{request.Method} {request.RequestUri}'.");
            }

            return _responses.Dequeue().Invoke(request);
        }

        private static HttpResponseMessage DefaultMappingResponse(HttpRequestMessage request)
        {
            return CreateMappingResponse("{}");
        }
    }

    private sealed record CapturedRequest(string Method, string PathAndQuery, string Body);

}
