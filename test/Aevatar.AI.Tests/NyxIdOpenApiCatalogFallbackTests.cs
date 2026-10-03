using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Tests;

public sealed class NyxIdOpenApiCatalogFallbackTests
{
    private const string InstancePath = "/api/v1/proxy/services/us-aevatar/openapi.json";
    private const string CatalogPath = "/api/v1/proxy/services/catalog-aevatar/openapi.json";
    private const string MissingDocumentation = """
        {"error":"not_found","error_code":1003,"message":"Not found: Service has no documentation spec configured"}
        """;
    private const string Inventory = """
        {"keys":[{
          "id":"us-aevatar","slug":"aevatar","catalog_service_id":"catalog-aevatar",
          "catalog_service_slug":"aevatar","is_active":true,"connected":true,"status":"active",
          "credential_source":{"type":"personal"},
          "openapi_url":"https://nyx.test/api/v1/proxy/services/us-aevatar/openapi.json"
        }]}
        """;
    private const string OpenApi = """
        {"openapi":"3.1.1","servers":[{"url":"https://nyx.test/api/v1/proxy/catalog-aevatar/"}],"paths":{
          "/api/channels/me":{"get":{"operationId":"get_channel_scope","responses":{
            "200":{"content":{"application/json":{"schema":{"type":"object"}}}}
          }}}
        }}
        """;

    [Fact]
    public async Task ReadAsync_MissingInstanceDocument_ReadsAssociatedCatalogAndKeepsInstanceIdentity()
    {
        var handler = new DocumentHandler();
        var logger = new RecordingLogger();

        var services = await ReadAsync(handler, logger);

        var service = services.Should().ContainSingle().Subject;
        service.UserServiceId.Should().Be("us-aevatar");
        service.ServiceSlug.Should().Be("aevatar");
        service.Endpoints.Should().ContainSingle().Which.PathTemplate.Should().Be("/api/channels/me");
        AssertCatalogRequests(handler);
        logger.Messages.Should().Contain(message => message.Contains("code=DocumentNotConfigured", StringComparison.Ordinal));
        logger.Messages.Should().Contain(message => message.Contains("source=Catalog operationCount=1", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(message => message.Contains("contract read failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_MissingInstanceDocument_DoesNotMutateOriginalInstance()
    {
        var handler = new DocumentHandler();
        using var client = CreateClient(handler);
        var instance = await ReadInstanceAsync(client);
        var original = instance.Clone();

        var services = await new NyxIdOpenApiDocumentReader(client, new RecordingLogger())
            .ReadAsync("caller-token", instance, "catalog-test", CancellationToken.None);

        var service = services.Should().ContainSingle().Subject;
        service.UserServiceId.Should().Be("us-aevatar");
        service.Endpoints.Should().ContainSingle().Which.PathTemplate.Should().Be("/api/channels/me");
        instance.Should().Be(original);
        AssertCatalogRequests(handler);
    }

    [Fact]
    public async Task ReadAsync_InstanceDocumentAvailable_DoesNotReadCatalog()
    {
        var handler = new DocumentHandler { InstanceStatus = HttpStatusCode.OK, InstanceBody = OpenApi };

        (await ReadAsync(handler, new RecordingLogger())).Should().ContainSingle();

        AssertInstanceOnly(handler);
    }

    [Theory]
    [InlineData("provider-secret")]
    [InlineData("{provider-secret")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"error\":\"not_found\",\"error_code\":1003,\"message\":\"Not found: Service not found\"}")]
    [InlineData("{\"error\":\"not_found\",\"error_code\":1003,\"message\":\"Service has no documentation spec configured\"}")]
    [InlineData("{\"error\":\"forbidden\",\"error_code\":1003,\"message\":\"Not found: Service has no documentation spec configured\"}")]
    [InlineData("{\"error\":\"not_found\",\"error_code\":1002,\"message\":\"Not found: Service has no documentation spec configured\"}")]
    [InlineData("{\"error\":\"not_found\",\"error_code\":\"1003\",\"message\":\"Not found: Service has no documentation spec configured\"}")]
    [InlineData("{\"error\":\"not_found\",\"message\":\"Not found: Service has no documentation spec configured\"}")]
    [InlineData("{\"error\":\"forbidden\",\"error\":\"not_found\",\"error_code\":1003,\"message\":\"Not found: Service has no documentation spec configured\"}")]
    [InlineData("{\"error\":\"not_found\",\"error_code\":1002,\"error_code\":1003,\"message\":\"Not found: Service has no documentation spec configured\"}")]
    [InlineData("{\"error\":\"not_found\",\"error_code\":1003,\"message\":\"Not found: Service not found\",\"message\":\"Not found: Service has no documentation spec configured\"}")]
    [InlineData("{\"error\":\"not_found\",\"error_code\":1003,\"message\":\"Not found: Service has no documentation spec configured\",\"message\":\"Not found: Service has no documentation spec configured\"}")]
    public async Task ReadAsync_UnrecognizedNotFound_DoesNotReadCatalog(string body)
    {
        var handler = new DocumentHandler { InstanceBody = body };
        var logger = new RecordingLogger();

        (await ReadAsync(handler, logger)).Should().BeEmpty();

        AssertInstanceOnly(handler);
        logger.Messages.Should().ContainSingle(message => message.Contains("stage=Fetch code=NotFound", StringComparison.Ordinal));
        string.Join('\n', logger.Messages).Should().NotContain("provider-secret").And.NotContain("caller-token");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AuthenticationRequired")]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied")]
    [InlineData(HttpStatusCode.TooManyRequests, "HttpError")]
    [InlineData(HttpStatusCode.InternalServerError, "HttpError")]
    [InlineData(HttpStatusCode.Redirect, "HttpError")]
    public async Task ReadAsync_OtherHttpFailure_DoesNotReadCatalog(HttpStatusCode status, string code)
    {
        var handler = new DocumentHandler { InstanceStatus = status };
        var logger = new RecordingLogger();

        (await ReadAsync(handler, logger)).Should().BeEmpty();

        AssertInstanceOnly(handler);
        logger.Messages.Should().ContainSingle(message => message.Contains($"stage=Fetch code={code}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("us-aevatar")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../keys")]
    public async Task ReadAsync_NoSafeDistinctCatalogIdentity_DoesNotGuessAnotherDocument(string? catalogId)
    {
        var handler = new DocumentHandler
        {
            InventoryJson = WithInstance(instance => instance["catalog_service_id"] = catalogId),
        };

        (await ReadAsync(handler, new RecordingLogger())).Should().BeEmpty();

        AssertInstanceOnly(handler);
    }

    [Fact]
    public async Task ReadAsync_ExplicitInstanceOverride_DoesNotReadCatalog()
    {
        var handler = new DocumentHandler
        {
            InventoryJson = WithInstance(instance => instance["openapi_spec_url"] = "/custom/openapi.json"),
        };

        (await ReadAsync(handler, new RecordingLogger())).Should().BeEmpty();

        AssertInstanceOnly(handler);
    }

    [Fact]
    public async Task ReadAsync_DownstreamFailure_DoesNotReadCatalog()
    {
        var handler = new DocumentHandler
        {
            InventoryJson = WithInstance(instance =>
            {
                instance.Remove("openapi_url");
                instance["openapi_spec_url"] = "/custom/openapi.json";
            }),
            PrimaryPath = "/api/v1/proxy/s/aevatar/custom/openapi.json",
        };

        (await ReadAsync(handler, new RecordingLogger())).Should().BeEmpty();

        handler.Requests.Should().Equal(
            ("/api/v1/keys", "caller-token"),
            (handler.PrimaryPath + "?_nyxid_via=us-aevatar", "caller-token"));
    }

    [Fact]
    public async Task ReadAsync_CatalogIsPrimaryDocument_DoesNotRequestItAgain()
    {
        var handler = new DocumentHandler
        {
            InventoryJson = WithInstance(instance => instance["openapi_url"] = "https://nyx.test" + CatalogPath),
            PrimaryPath = CatalogPath,
        };

        (await ReadAsync(handler, new RecordingLogger())).Should().BeEmpty();

        handler.Requests.Should().Equal(("/api/v1/keys", "caller-token"), (CatalogPath, "caller-token"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AuthenticationRequired")]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied")]
    [InlineData(HttpStatusCode.NotFound, "DocumentNotConfigured")]
    [InlineData(HttpStatusCode.InternalServerError, "HttpError")]
    public async Task ReadAsync_CatalogHttpFailure_StopsAfterOneFallback(HttpStatusCode status, string code)
    {
        var handler = new DocumentHandler { CatalogStatus = status, CatalogBody = MissingDocumentation };
        var logger = new RecordingLogger();

        (await ReadAsync(handler, logger)).Should().BeEmpty();

        AssertCatalogRequests(handler);
        logger.Messages.Should().ContainSingle(message => message.Contains($"stage=Fetch code={code}", StringComparison.Ordinal)
            && message.Contains($"source=Catalog httpStatus={(int)status}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{provider-secret", "Parse", "InvalidDocument")]
    [InlineData("{\"openapi\":\"3.1.1\",\"paths\":{}}", "OperationSelection", "NoOperations")]
    [InlineData("{\"openapi\":\"3.1.1\",\"paths\":{\"/test\":{\"get\":false}}}", "OperationSelection", "NoAdmissibleOperations")]
    public async Task ReadAsync_InvalidCatalog_ReportsCatalogStage(string body, string stage, string code)
    {
        var handler = new DocumentHandler { CatalogBody = body };
        var logger = new RecordingLogger();

        (await ReadAsync(handler, logger)).Should().BeEmpty();

        AssertCatalogRequests(handler);
        logger.Messages.Should().ContainSingle(message => message.Contains($"stage={stage} code={code}", StringComparison.Ordinal)
            && message.Contains("source=Catalog", StringComparison.Ordinal));
        string.Join('\n', logger.Messages).Should().NotContain("provider-secret").And.NotContain("caller-token");
    }

    [Fact]
    public async Task ReadAsync_OversizedCatalog_PreservesDocumentLimit()
    {
        var handler = new DocumentHandler { CatalogBody = new string('a', 1024 * 1024 + 1) };
        var logger = new RecordingLogger();

        (await ReadAsync(handler, logger)).Should().BeEmpty();

        AssertCatalogRequests(handler);
        logger.Messages.Should().ContainSingle(message => message.Contains("stage=Fetch code=ResponseTooLarge", StringComparison.Ordinal)
            && message.Contains("source=Catalog", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_CatalogTransportFailure_DoesNotLeakException()
    {
        var handler = new DocumentHandler { CatalogException = new HttpRequestException("provider-secret") };
        var logger = new RecordingLogger();

        (await ReadAsync(handler, logger)).Should().BeEmpty();

        AssertCatalogRequests(handler);
        logger.Messages.Should().ContainSingle(message => message.Contains("stage=Fetch code=TransportFailure", StringComparison.Ordinal)
            && message.Contains("source=Catalog", StringComparison.Ordinal));
        string.Join('\n', logger.Messages).Should().NotContain("provider-secret").And.NotContain("caller-token");
    }

    [Fact]
    public async Task ReadAsync_CatalogCancellation_PropagatesWithoutFailureDiagnostic()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new DocumentHandler { CancelCatalogWith = cancellation };
        var logger = new RecordingLogger();

        var act = () => ReadAsync(handler, logger, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        AssertCatalogRequests(handler);
        logger.Messages.Should().NotContain(message => message.Contains("contract read failed", StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<NyxIdMcpService>> ReadAsync(
        DocumentHandler handler, RecordingLogger logger, CancellationToken ct = default)
    {
        using var client = CreateClient(handler);
        var instance = await ReadInstanceAsync(client);
        return await new NyxIdOpenApiDocumentReader(client, logger).ReadAsync("caller-token", instance, "catalog-test", ct);
    }

    private static NyxIdApiClient CreateClient(DocumentHandler handler) =>
        new(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));

    private static async Task<NyxIdServiceInstance> ReadInstanceAsync(NyxIdApiClient client)
    {
        var inventory = await new NyxIdConnectedServiceInventoryReader(new NyxIdServiceInstanceClient(client))
            .ReadAsync("caller-token", organizationToken: null);
        return inventory.Instances.Should().ContainSingle().Subject;
    }

    private static string WithInstance(Action<JsonObject> change)
    {
        var json = JsonNode.Parse(Inventory)!;
        change(json["keys"]![0]!.AsObject());
        return json.ToJsonString();
    }

    private static void AssertInstanceOnly(DocumentHandler handler) =>
        handler.Requests.Should().Equal(("/api/v1/keys", "caller-token"), (InstancePath, "caller-token"));

    private static void AssertCatalogRequests(DocumentHandler handler) =>
        handler.Requests.Should().Equal(
            ("/api/v1/keys", "caller-token"), (InstancePath, "caller-token"), (CatalogPath, "caller-token"));

    private sealed class RecordingLogger : ILogger<NyxIdRecommendedSkillGenerator>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class DocumentHandler : HttpMessageHandler
    {
        public List<(string Path, string? Token)> Requests { get; } = [];
        public string InventoryJson { get; init; } = Inventory;
        public string PrimaryPath { get; init; } = InstancePath;
        public HttpStatusCode InstanceStatus { get; init; } = HttpStatusCode.NotFound;
        public string InstanceBody { get; init; } = MissingDocumentation;
        public HttpStatusCode CatalogStatus { get; init; } = HttpStatusCode.OK;
        public string CatalogBody { get; init; } = OpenApi;
        public Exception? CatalogException { get; init; }
        public CancellationTokenSource? CancelCatalogWith { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.GetLeftPart(UriPartial.Authority).Should().Be("https://nyx.test");
            Requests.Add((request.RequestUri.PathAndQuery, request.Headers.Authorization?.Parameter));
            var path = request.RequestUri.AbsolutePath;
            if (path == "/api/v1/keys")
                return Respond(HttpStatusCode.OK, InventoryJson);
            if (path == PrimaryPath)
                return Respond(InstanceStatus, InstanceBody);
            path.Should().Be(CatalogPath);
            CancelCatalogWith?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (CatalogException is not null)
                throw CatalogException;
            return Respond(CatalogStatus, CatalogBody);
        }

        private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
