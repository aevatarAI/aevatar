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
    public async Task QueryAsync_WhenSortFieldNotConfigured_ShouldUseDeterministicDefaultSort()
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
        searchRequest.Body.Should().Contain("\"_id\"");
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

        using var store = new ElasticsearchProjectionDocumentStore<StoreReadModel, string>(
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

        await store.UpsertAsync(new StoreReadModel
        {
            Id = "actor-1",
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
        handler.CapturedRequests[0].Body.Should().Contain("\"Value\"");
        handler.CapturedRequests[0].Body.Should().Contain("\"number_of_shards\":1");
        handler.CapturedRequests[0].Body.Should().Contain("\"projection-core-tests-alias\"");
        handler.CapturedRequests[0].Body.Should().Contain("\"is_write_index\":true");
    }

    [Fact]
    public async Task UpsertAsync_WhenExistingDocumentPresent_ShouldUseOptimisticConcurrencyTokens()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"_seq_no":7,"_primary_term":3,"_source":{"Id":"actor-1","ActorId":"actor-1","StateVersion":1,"LastEventId":"evt-1","UpdatedAt":"2026-03-16T00:00:00Z","Value":"v1"}}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(
            HttpStatusCode.OK,
            """{"result":"updated"}"""));

        using var store = CreateStore(
            new ElasticsearchProjectionDocumentStoreOptions
            {
                AutoCreateIndex = false,
            },
            handler);

        await store.UpsertAsync(new StoreReadModel
        {
            Id = "actor-1",
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

        using var store = new ElasticsearchProjectionDocumentStore<DynamicStoreReadModel, string>(
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

        await store.UpsertAsync(new DynamicStoreReadModel
        {
            Id = "actor-1",
            DocumentIndexScope = "dynamic-alpha",
        });
        await store.UpsertAsync(new DynamicStoreReadModel
        {
            Id = "actor-2",
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

        using var store = new ElasticsearchProjectionDocumentStore<DynamicStoreReadModel, string>(
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

    private static ElasticsearchProjectionDocumentStore<StoreReadModel, string> CreateStore(
        ElasticsearchProjectionDocumentStoreOptions options,
        HttpMessageHandler handler)
    {
        options.Endpoints = ["http://localhost:9200"];
        return new ElasticsearchProjectionDocumentStore<StoreReadModel, string>(
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

    private sealed class StoreReadModel : IProjectionReadModel
    {
        public string Id { get; set; } = "";

        public string ActorId => Id;

        public long StateVersion { get; set; }

        public string LastEventId { get; set; } = "";

        public DateTimeOffset UpdatedAt { get; set; }

        public string Value { get; set; } = "";
    }

    private sealed class DynamicStoreReadModel : IProjectionReadModel
    {
        public string Id { get; set; } = string.Empty;

        public string ActorId => Id;

        public long StateVersion { get; set; }

        public string LastEventId { get; set; } = string.Empty;

        public DateTimeOffset UpdatedAt { get; set; }

        public string DocumentIndexScope { get; set; } = string.Empty;
    }
}
