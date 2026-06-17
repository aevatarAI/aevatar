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

        var indexPayload = ParseJson(GetIndexCreateRequest(handler).Body);
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
    public async Task UpsertAsync_WhenAliasFingerprintDrifts_ShouldThrowWithoutReindexing()
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

        // Refactor (iter98/cluster-743): Old pattern: drifted alias fingerprints
        // triggered PUT physical + _reindex + _aliases repair. New principle:
        // lifecycle drift is a configuration error and projection refuses writes.
        var act = () => store.UpsertAsync(new TestStoreReadModel { Id = "actor-1", ActorId = "actor-1" });

        var exception = await act.Should().ThrowAsync<ProjectionIndexSchemaDriftException>();
        exception.Which.Provider.Should().Be("Elasticsearch");
        exception.Which.IndexAlias.Should().Be("aevatar-projection-core-tests");
        exception.Which.CurrentPhysicalIndex.Should().Be("aevatar-projection-core-tests-v00000000");
        exception.Which.ExpectedPhysicalIndex.Should().StartWith("aevatar-projection-core-tests-v");
        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests
            .Any(r => r.PathAndQuery.StartsWith("/_reindex", StringComparison.Ordinal))
            .Should().BeFalse("drift must fail loud instead of repairing through reindex");
        handler.CapturedRequests
            .Any(r => r.PathAndQuery == "/_aliases" && r.Method == "POST")
            .Should().BeFalse("drift must not swap aliases from the projection write path");
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
        // This is the exact prod scenario behind the Lark relay outage on 2026-05-20:
        // `aevatar-mainnet-channel-bot-registrations` existed as a bare index from
        // 2026-04-22 with dynamic mappings, never wrapped into an alias. The lifecycle
        // manager must detect this and migrate: create v<fingerprint> with the
        // explicit augmented mapping, reindex from bare → physical, atomically
        // (add alias + remove_index bare) in one _aliases call.
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
