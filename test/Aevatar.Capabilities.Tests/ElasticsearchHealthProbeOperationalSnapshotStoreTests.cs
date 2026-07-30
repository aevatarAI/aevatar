using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StatusDashboard;
using Aevatar.Mainnet.Host.Api.Status;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Capabilities.Tests;

public sealed class ElasticsearchHealthProbeOperationalSnapshotStoreTests
{
    [Fact]
    public async Task UpsertAndGetAsync_OverwriteExactSlugWithoutIndexLifecycleCalls()
    {
        var snapshot = BuildSnapshot();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"result":"updated"}""");
        handler.Enqueue(HttpStatusCode.OK, BuildHit(snapshot));
        using var store = CreateStore(handler);

        await store.UpsertAsync(snapshot);
        var roundTripped = await store.GetAsync("self-liveness");

        roundTripped.Should().Be(snapshot);
        handler.Requests.Select(static request => $"{request.Method} {request.Path}")
            .Should().Equal(
                "PUT /test-health-probe-operational-snapshots/_doc/self-liveness",
                "GET /test-health-probe-operational-snapshots/_doc/self-liveness");
        JsonParser.Default.Parse<HealthProbeOperationalSnapshot>(handler.Requests[0].Body)
            .Should().Be(snapshot);
    }

    [Fact]
    public async Task GetAsync_WhenDocumentIsMissing_ReturnsNull()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"found":false}""");
        using var store = CreateStore(handler);

        var result = await store.GetAsync("self-liveness");

        result.Should().BeNull();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAsync_WhenIndexIsMissing_FailsHonestly()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(
            HttpStatusCode.NotFound,
            """{"error":{"type":"index_not_found_exception"}}""");
        using var store = CreateStore(handler);

        var act = () => store.GetAsync("self-liveness");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*test-health-probe-operational-snapshots*not found*");
    }

    [Fact]
    public async Task ReconcileIndexAsync_WhenAliasIsMissing_ProvisionsVersionedAlias()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"error":"alias_missing"}""");
        handler.Enqueue(HttpStatusCode.NotFound, """{}""");
        handler.Enqueue(HttpStatusCode.OK, """{"acknowledged":true}""");
        using var store = CreateStore(handler);

        store.Should().BeAssignableTo<IProjectionIndexReconcileTarget>();
        await store.ReconcileIndexAsync();

        handler.Requests.Select(static request => $"{request.Method} {request.Path}")
            .Should().SatisfyRespectively(
                request => request.Should().Be("GET /_alias/test-health-probe-operational-snapshots"),
                request => request.Should().Be("HEAD /test-health-probe-operational-snapshots"),
                request => request.Should().StartWith("PUT /test-health-probe-operational-snapshots-v"));
        using var payload = JsonDocument.Parse(handler.Requests[2].Body);
        payload.RootElement.GetProperty("aliases")
            .TryGetProperty("test-health-probe-operational-snapshots", out _)
            .Should().BeTrue();
        payload.RootElement.GetProperty("mappings").GetProperty("dynamic").GetBoolean()
            .Should().BeTrue();
    }

    private static ElasticsearchHealthProbeOperationalSnapshotStore CreateStore(
        HttpMessageHandler handler) =>
        new(
            ["http://localhost:9200"],
            " Test ",
            requestTimeoutMs: 10_000,
            username: "",
            password: "",
            handler);

    private static HealthProbeOperationalSnapshot BuildSnapshot() => new()
    {
        Target = new HealthProbeTargetDescriptor
        {
            Slug = "self-liveness",
            DisplayName = "Self Liveness",
            ProbeKind = "http_status",
            Enabled = true,
        },
        LastOutcome = new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Ok,
            Detail = "http_200",
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-29T02:00:00Z")),
        },
        LastCheckAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-29T02:00:00Z")),
        LastSuccessAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-29T02:00:00Z")),
        UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-29T02:00:01Z")),
        RecentOutcomes =
        {
            new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Ok,
                Detail = "http_200",
                ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-29T02:00:00Z")),
            },
        },
    };

    private static string BuildHit(HealthProbeOperationalSnapshot snapshot)
    {
        var formatter = new JsonFormatter(
            JsonFormatter.Settings.Default
                .WithPreserveProtoFieldNames(true)
                .WithFormatDefaultValues(true));
        return "{\"_source\":" + formatter.Format(snapshot) + "}";
    }

    private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<CapturedRequest> Requests { get; } = [];

        public void Enqueue(HttpStatusCode status, string body) =>
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri?.PathAndQuery ?? "",
                request.Content == null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (_responses.Count == 0)
                throw new InvalidOperationException($"No response scripted for {request.Method} {request.RequestUri}.");
            return _responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(string Method, string Path, string Body);
}
