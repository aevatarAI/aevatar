using System.Net;
using System.Text;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.Scripting.Projection.Metadata;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptCatalogEntryMetadataProviderTests
{
    [Fact]
    public async Task ElasticsearchIndexInitialization_ShouldIncludeSortableTimestampMappings()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"acknowledged":true}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.NotFound, """{"found":false}"""));
        handler.EnqueueResponse(_ => CreateJsonResponse(HttpStatusCode.OK, """{"result":"created"}"""));

        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            AutoCreateIndex = true,
        };
        options.Endpoints = ["http://localhost:9200"];

        using var store = new ElasticsearchProjectionDocumentStore<ScriptCatalogEntryDocument, string>(
            options,
            new ScriptCatalogEntryDocumentMetadataProvider().Metadata,
            keySelector: document => document.Id,
            keyFormatter: key => key,
            httpMessageHandler: handler);

        await store.UpsertAsync(new ScriptCatalogEntryDocument
        {
            Id = "catalog:script-1",
            CatalogActorId = "catalog",
            ScriptId = "script-1",
            StateVersion = 1,
            LastEventId = "event-1",
            CreatedAt = DateTimeOffset.Parse("2026-04-30T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-04-30T00:00:01Z"),
        });

        handler.CapturedRequests.Should().HaveCount(3);
        var indexInitialization = handler.CapturedRequests[0];
        indexInitialization.Method.Should().Be("PUT");
        indexInitialization.PathAndQuery.Should().Be("/aevatar-script-catalog-entries");
        indexInitialization.Body.Should().Contain("\"created_at_utc_value\":{\"type\":\"date\"}");
        indexInitialization.Body.Should().Contain("\"updated_at_utc_value\":{\"type\":\"date\"}");
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
