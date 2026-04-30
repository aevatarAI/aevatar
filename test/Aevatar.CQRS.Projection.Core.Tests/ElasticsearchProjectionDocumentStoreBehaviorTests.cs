using System.Net;
using System.Text;
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
        searchRequest.Body.Should().Contain("\"actor_id.keyword\":\"actor-1\"");
        searchRequest.Body.Should().Contain("\"updated_at_utc_value\"");
        searchRequest.Body.Should().NotContain("\"ActorId\"");
        searchRequest.Body.Should().NotContain("\"UpdatedAt\"");
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
