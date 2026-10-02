using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Tests;

public sealed class NyxIdRecommendedSkillGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_LocalReferences_PreservesOptionalNestedRequestSchema()
    {
        var handler = new DocumentHandler { DocumentBody = ReferencedOpenApi };
        var skill = await GenerateAsync(handler, new RecordingLogger());

        skill.Should().NotBeNull();
        var instructions = skill!.InstructionsMarkdown;
        instructions.Should().Contain("POST /registrations")
            .And.Contain("service_ids").And.Contain("default_skill")
            .And.NotContain("$ref");

        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));
        var inventory = await new NyxIdConnectedServiceInventoryReader(new NyxIdServiceInstanceClient(client))
            .ReadAsync("caller-token", organizationToken: null);
        var parsed = NyxIdMcpOperationCatalog.ParseCustomOpenApi(ReferencedOpenApi, inventory.Instances.Single(),
            "test", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        var request = parsed.Services.Single().Endpoints.Single().RequestBodySchema!;
        request["required"].Should().BeNull("omitted update fields must remain optional");
        request["properties"]!["service_ids"]!["default"].Should().BeNull("resolution must not insert empty selections");
        request["properties"]!["service_ids"]!["type"]!.GetValue<string>().Should().Be("array");
        request["properties"]!["runtime_config"]!["properties"]!["default_skill"]!["properties"]!["name"]!["type"]!
            .GetValue<string>().Should().Be("string");
    }

    [Theory]
    [InlineData("#/components/schemas/Missing")]
    [InlineData("https://outside.test/schema.json")]
    [InlineData("#/components/schemas/Update")]
    public async Task GenerateAsync_InvalidReference_RejectsOnlyAffectedOperation(string reference)
    {
        var document = JsonNode.Parse(ReferencedOpenApi)!;
        document["components"]!["schemas"]!["Update"]!["properties"]!["runtime_config"] =
            new JsonObject { ["$ref"] = reference };
        document["paths"]!["/healthy"] = JsonNode.Parse("""
            {"get":{"responses":{"200":{"content":{"application/json":{"schema":{"type":"string"}}}}}}}
            """);
        var handler = new DocumentHandler { DocumentBody = document.ToJsonString() };
        var skill = await GenerateAsync(handler, new RecordingLogger());

        skill.Should().NotBeNull();
        skill!.InstructionsMarkdown.Should().Contain("GET /healthy").And.NotContain("POST /registrations");
        handler.Requests.Should().HaveCount(2, "references must not trigger network fetches");
    }

    private const string ReferencedOpenApi = """
        {"openapi":"3.1.1","paths":{"/registrations":{"post":{
          "description":"Update a registration and read back the committed state.",
          "requestBody":{"$ref":"#/components/requestBodies/Update"},
          "responses":{
            "202":{"description":"Accepted","content":{"application/json":{"schema":{"type":"object"}}}},
            "404":{"description":"Registration not found","content":{"application/json":{
              "schema":{"type":"object","properties":{"error":{"type":"string"}}}}}}
          }
        }}},"components":{
          "requestBodies":{"Update":{"required":true,"content":{"application/json":{"schema":{"$ref":"#/components/schemas/Update"}}}}},
          "schemas":{
            "Update":{"type":"object","properties":{
              "service_ids":{"type":"array","items":{"type":"string"}},
              "runtime_config":{"$ref":"#/components/schemas/Runtime"}}},
            "Runtime":{"type":"object","properties":{"default_skill":{"$ref":"#/components/schemas/Skill~1Name~0"}}},
            "Skill/Name~":{"type":"object","properties":{"name":{"type":"string"}}}
          }
        }}
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
        {"openapi":"3.1.1","paths":{
          "/api/channels/me":{"get":{"operationId":"get_channel_scope","responses":{
            "200":{"content":{"application/json":{"schema":{"type":"object"}}}}
          }}},
          "/api/channels/registrations":{"get":{"operationId":"list_channel_registrations","responses":{
            "200":{"content":{"application/json":{"schema":{"type":"array","items":{"type":"object"}}}}}
          }}}
        }}
        """;

    [Fact]
    public async Task GenerateAsync_GatewayOnlyInventory_ReadsDocumentWithoutDownstreamEndpoint()
    {
        var handler = new DocumentHandler();
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));
        var inventory = await new NyxIdConnectedServiceInventoryReader(new NyxIdServiceInstanceClient(client))
            .ReadAsync("caller-token", organizationToken: null);
        var instance = inventory.Instances.Should().ContainSingle().Subject;

        var skill = await new NyxIdRecommendedSkillGenerator(client)
            .GenerateAsync("caller-token", instance, CancellationToken.None);

        skill.Should().NotBeNull();
        skill!.InstructionsMarkdown.Should().Contain("GET /api/channels/me")
            .And.Contain("GET /api/channels/registrations");
        handler.Requests.Should().Equal(
            ("/api/v1/keys", "caller-token"),
            ("/api/v1/proxy/services/us-aevatar/openapi.json", "caller-token"));
    }

    [Theory]
    [InlineData("https://nyx.test/api/v1/proxy/services/catalog-aevatar/openapi.json", "/api/v1/proxy/services/catalog-aevatar/openapi.json")]
    [InlineData("/api/v1/proxy/services/us-aevatar/openapi.json", "/api/v1/proxy/services/us-aevatar/openapi.json")]
    public async Task GenerateAsync_GatewayDocument_UsesExplicitCatalogOrUserServiceIdentity(string url, string expectedPath)
    {
        var handler = new DocumentHandler { InventoryJson = WithUrls(url), DocumentPath = expectedPath };
        var skill = await GenerateAsync(handler, new RecordingLogger());

        skill.Should().NotBeNull();
        handler.Requests.Last().Should().Be((expectedPath, "caller-token"));
    }

    [Theory]
    [InlineData("/api/openapi.json", null)]
    [InlineData("api/openapi.json", null)]
    [InlineData("https://aevatar.test/api/openapi.json", "https://aevatar.test")]
    public async Task GenerateAsync_DownstreamSpec_UsesExactInstanceProxy(string specUrl, string? endpointUrl)
    {
        var handler = new DocumentHandler
        {
            InventoryJson = WithUrls(null, specUrl, endpointUrl),
            DocumentPath = "/api/v1/proxy/s/aevatar/api/openapi.json",
        };

        var skill = await GenerateAsync(handler, new RecordingLogger());

        skill.Should().NotBeNull();
        handler.Requests.Last().Should().Be((handler.DocumentPath + "?_nyxid_via=us-aevatar", "caller-token"));
    }

    [Fact]
    public async Task GenerateAsync_BothDocumentSources_UsesPublishedGatewayWithoutEndpointUrl()
    {
        var handler = new DocumentHandler
        {
            InventoryJson = WithUrls(
                "https://nyx.test/api/v1/proxy/services/us-aevatar/openapi.json",
                "https://aevatar.test/api/openapi.json"),
        };

        (await GenerateAsync(handler, new RecordingLogger())).Should().NotBeNull();

        handler.Requests.Should().Equal(
            ("/api/v1/keys", "caller-token"),
            (handler.DocumentPath, "caller-token"));
    }

    [Theory]
    [InlineData("https://outside.test/api/v1/proxy/services/us-aevatar/openapi.json", null, null, "GatewayOriginMismatch")]
    [InlineData("https://outside.test/api/v1/proxy/services/us-aevatar/openapi.json", "/api/openapi.json", null, "GatewayOriginMismatch")]
    [InlineData("https://nyx.test/api/v1/proxy/services/another-user/openapi.json", null, null, "GatewayServiceIdentityMismatch")]
    [InlineData("https://nyx.test/api/v1/keys", null, null, "GatewayServiceIdentityMismatch")]
    [InlineData("https://nyx.test/api/v1/proxy/services/us-aevatar/openapi.json?secret=provider-secret", null, null, "InvalidDocumentUrl")]
    [InlineData("https://provider-secret@nyx.test/api/v1/proxy/services/us-aevatar/openapi.json", null, null, "InvalidDocumentUrl")]
    [InlineData("https://nyx.test/api/v1/proxy/services/us-aevatar/openapi.json#provider-secret", null, null, "InvalidDocumentUrl")]
    [InlineData(null, "https://aevatar.test/api/openapi.json", null, "EndpointUrlMissing")]
    [InlineData(null, "https://outside.test/openapi.json?secret=provider-secret", "https://aevatar.test", "SpecOriginMismatch")]
    [InlineData(null, "//outside.test/openapi.json", null, "UnsafeProxyPath")]
    [InlineData(null, "/../api/openapi.json", null, "UnsafeProxyPath")]
    [InlineData(null, "/%2e%2e/api/openapi.json", null, "UnsafeProxyPath")]
    [InlineData(null, null, null, "DocumentUrlMissing")]
    public async Task GenerateAsync_UnsafeOrMissingAddress_ReportsResolutionFailureWithoutHttp(
        string? documentUrl, string? specUrl, string? endpointUrl, string failureCode)
    {
        var handler = new DocumentHandler { InventoryJson = WithUrls(documentUrl, specUrl, endpointUrl) };
        var logger = new RecordingLogger();

        var skill = await GenerateAsync(handler, logger);

        skill.Should().BeNull();
        handler.Requests.Should().ContainSingle().Which.Path.Should().Be("/api/v1/keys");
        logger.Messages.Should().ContainSingle(message => message.Contains($"stage=AddressResolution code={failureCode}", StringComparison.Ordinal));
        string.Join('\n', logger.Messages).Should().NotContain("provider-secret").And.NotContain("caller-token");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AuthenticationRequired")]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied")]
    [InlineData(HttpStatusCode.NotFound, "NotFound")]
    [InlineData(HttpStatusCode.InternalServerError, "HttpError")]
    [InlineData(HttpStatusCode.Redirect, "HttpError")]
    public async Task GenerateAsync_HttpFailure_ReportsStatusWithoutLeakingResponse(HttpStatusCode status, string failureCode)
    {
        var handler = new DocumentHandler { DocumentStatus = status, DocumentBody = "provider-secret" };
        var logger = new RecordingLogger();

        var skill = await GenerateAsync(handler, logger);

        skill.Should().BeNull();
        logger.Messages.Should().ContainSingle(message => message.Contains($"stage=Fetch code={failureCode}", StringComparison.Ordinal)
            && message.Contains($"httpStatus={(int)status}", StringComparison.Ordinal));
        string.Join('\n', logger.Messages).Should().NotContain("provider-secret").And.NotContain("caller-token");
    }

    [Theory]
    [InlineData("{provider-secret", "Parse", "InvalidDocument")]
    [InlineData("{\"error\":\"provider-secret\"}", "Parse", "InvalidDocument")]
    [InlineData("{\"openapi\":\"3.1.1\",\"paths\":{\"/test\":{\"get\":{\"operationId\":\"read_test\",\"parameters\":[{\"name\":\"q\",\"in\":\"query\",\"schema\":{\"type\":\"string\",\"type\":\"string\"}}],\"responses\":{\"200\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\"}}}}}}}}}", "Parse", "InvalidDocument")]
    [InlineData("{\"openapi\":\"3.1.1\",\"paths\":{}}", "OperationSelection", "NoOperations")]
    [InlineData("{\"openapi\":\"3.1.1\",\"paths\":{\"/test\":{\"get\":false}}}", "OperationSelection", "NoAdmissibleOperations")]
    public async Task GenerateAsync_InvalidOrEmptyContract_ReportsPreciseStage(string body, string stage, string failureCode)
    {
        var handler = new DocumentHandler { DocumentBody = body };
        var logger = new RecordingLogger();

        var skill = await GenerateAsync(handler, logger);

        skill.Should().BeNull();
        logger.Messages.Should().ContainSingle(message => message.Contains($"stage={stage} code={failureCode}", StringComparison.Ordinal));
        string.Join('\n', logger.Messages).Should().NotContain("provider-secret");
    }

    [Fact]
    public async Task GenerateAsync_OversizedDocument_ReportsBoundedReadFailure()
    {
        var handler = new DocumentHandler { DocumentBody = new string('a', 1024 * 1024 + 1) };
        var logger = new RecordingLogger();

        (await GenerateAsync(handler, logger)).Should().BeNull();

        logger.Messages.Should().ContainSingle(message => message.Contains("stage=Fetch code=ResponseTooLarge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsync_TransportFailure_LogsOnlyFailureCode()
    {
        var handler = new DocumentHandler { DocumentException = new HttpRequestException("provider-secret") };
        var logger = new RecordingLogger();

        (await GenerateAsync(handler, logger)).Should().BeNull();

        logger.Messages.Should().ContainSingle(message => message.Contains("stage=Fetch code=TransportFailure", StringComparison.Ordinal));
        string.Join('\n', logger.Messages).Should().NotContain("provider-secret").And.NotContain("caller-token");
    }

    [Fact]
    public async Task GenerateAsync_CallerCancellation_PropagatesWithoutUnavailableDiagnostic()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new DocumentHandler { CancelDocumentWith = cancellation };
        var logger = new RecordingLogger();

        var act = () => GenerateAsync(handler, logger, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        logger.Messages.Should().BeEmpty();
    }

    private static async Task<NyxIdGeneratedRecommendedSkill?> GenerateAsync(
        DocumentHandler handler, RecordingLogger logger, CancellationToken ct = default)
    {
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));
        var inventory = await new NyxIdConnectedServiceInventoryReader(new NyxIdServiceInstanceClient(client))
            .ReadAsync("caller-token", organizationToken: null);
        return await new NyxIdRecommendedSkillGenerator(client, logger)
            .GenerateAsync("caller-token", inventory.Instances.Single(), ct);
    }

    private static string WithUrls(string? documentUrl, string? specUrl = null, string? endpointUrl = null)
    {
        var json = JsonNode.Parse(Inventory)!;
        var instance = json["keys"]![0]!.AsObject();
        instance.Remove("openapi_url");
        if (documentUrl is not null)
            instance["openapi_url"] = documentUrl;
        if (specUrl is not null)
            instance["openapi_spec_url"] = specUrl;
        if (endpointUrl is not null)
            instance["endpoint_url"] = endpointUrl;
        return json.ToJsonString();
    }

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
        public string DocumentPath { get; init; } = "/api/v1/proxy/services/us-aevatar/openapi.json";
        public string DocumentBody { get; init; } = OpenApi;
        public HttpStatusCode DocumentStatus { get; init; } = HttpStatusCode.OK;
        public Exception? DocumentException { get; init; }
        public CancellationTokenSource? CancelDocumentWith { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.PathAndQuery, request.Headers.Authorization?.Parameter));
            var isInventory = request.RequestUri.AbsolutePath == "/api/v1/keys";
            if (!isInventory && request.RequestUri.AbsolutePath != DocumentPath)
                throw new InvalidOperationException("unexpected_document_route");
            if (!isInventory)
            {
                CancelDocumentWith?.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                if (DocumentException is not null)
                    throw DocumentException;
            }
            return Task.FromResult(new HttpResponseMessage(isInventory ? HttpStatusCode.OK : DocumentStatus)
            {
                Content = new StringContent(isInventory ? InventoryJson : DocumentBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
