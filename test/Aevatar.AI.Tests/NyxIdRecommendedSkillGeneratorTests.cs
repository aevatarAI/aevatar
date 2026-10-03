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
    public async Task GenerateAsync_DifferentCallersAndInstances_ProduceSameAuthoritativeCatalogContent()
    {
        var handler = new CatalogDocumentHandler();
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));
        var firstInstance = CatalogInstance();
        var secondInstance = new NyxIdServiceInstance
        {
            UserServiceId = "us-second-account",
            DisplaySlug = "another-private-instance",
            Label = "Another Account Private Label",
            CatalogServiceId = "catalog-aevatar",
            CatalogServiceSlug = "untrusted-instance-catalog-slug",
            OpenapiDocumentUrl = "https://outside.invalid/second-private-document.json",
            OpenapiSpecUrl = "/private/openapi.json",
            EndpointUrl = "https://private-instance.invalid",
        };
        var original = firstInstance.Clone();
        var generator = new NyxIdRecommendedSkillGenerator(client);

        var first = await generator.GenerateAsync("first-caller-token", firstInstance, CancellationToken.None);
        var second = await generator.GenerateAsync("second-caller-token", secondInstance, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeEquivalentTo(first, options => options.WithStrictOrdering());
        first!.Name.Should().Be("aevatar-connected-service");
        first.DisplayName.Should().Be("Aevatar");
        first.Tags.Should().Equal("aevatar");
        first.Description.Should().Contain("Shared workflow APIs.");
        var publicContent = first.InstructionsMarkdown + first.Description + first.DisplayName + string.Join(',', first.Tags);
        publicContent.Should().NotContain(firstInstance.UserServiceId).And.NotContain(firstInstance.DisplaySlug)
            .And.NotContain(firstInstance.Label).And.NotContain(secondInstance.Label)
            .And.NotContain("private-instance.invalid").And.NotContain("first-caller-token")
            .And.NotContain("untrusted-instance-catalog-slug");
        firstInstance.Should().Be(original);
        handler.Requests.Should().Equal(
            (CatalogDocumentHandler.CatalogPath, "first-caller-token"),
            (CatalogDocumentHandler.DocumentPath, "first-caller-token"),
            (CatalogDocumentHandler.CatalogPath, "second-caller-token"),
            (CatalogDocumentHandler.DocumentPath, "second-caller-token"));
    }

    [Fact]
    public async Task GenerateAsync_AuthoritativeLocalReferences_PreservesOptionalRequestSchemaAndResponseSummary()
    {
        var handler = new CatalogDocumentHandler { DocumentBody = ReferencedOpenApi };
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));

        var skill = await new NyxIdRecommendedSkillGenerator(client)
            .GenerateAsync("caller-token", CatalogInstance(), CancellationToken.None);

        skill.Should().NotBeNull();
        skill!.InstructionsMarkdown.Should().Contain("POST /registrations")
            .And.Contain("service_ids").And.Contain("default_skill")
            .And.Contain("response: `404` - Registration not found")
            .And.Contain("media_type: `application/json`")
            .And.NotContain("error").And.NotContain("$ref");
        var schemaBlock = skill.InstructionsMarkdown.Split("- request_body_schema:\n```json\n", StringSplitOptions.None)[1]
            .Split("\n```", StringSplitOptions.None)[0];
        var schema = JsonNode.Parse(schemaBlock)!;
        schema["required"].Should().BeNull();
        schema["properties"]!["service_ids"]!["default"].Should().BeNull();
        schema["properties"]!["service_ids"]!["type"]!.GetValue<string>().Should().Be("array");
        handler.Requests.Should().Equal(
            (CatalogDocumentHandler.CatalogPath, "caller-token"),
            (CatalogDocumentHandler.DocumentPath, "caller-token"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task GenerateAsync_MissingCatalogIdentity_DoesNotReadInstanceDocument(string catalogId)
    {
        var handler = new CatalogDocumentHandler();
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));
        var instance = CatalogInstance();
        instance.CatalogServiceId = catalogId;

        (await new NyxIdRecommendedSkillGenerator(client).GenerateAsync("caller-token", instance, CancellationToken.None))
            .Should().BeNull();

        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("resource_type", "user_service")]
    [InlineData("resource_type", "")]
    [InlineData("id", "catalog-other")]
    [InlineData("catalog_service_id", "catalog-other")]
    [InlineData("catalog_service_slug", "another-catalog")]
    [InlineData("slug", "")]
    [InlineData("name", "")]
    public async Task GenerateAsync_InvalidCatalogAuthority_DoesNotReadDocument(string field, string value)
    {
        var catalog = JsonNode.Parse(CatalogDocumentHandler.CatalogBody)!;
        catalog[field] = value;
        var handler = new CatalogDocumentHandler { CatalogResponseBody = catalog.ToJsonString() };
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));

        (await new NyxIdRecommendedSkillGenerator(client).GenerateAsync("caller-token", CatalogInstance(), CancellationToken.None))
            .Should().BeNull();

        handler.Requests.Should().Equal((CatalogDocumentHandler.CatalogPath, "caller-token"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GenerateAsync_CatalogReadFailure_DoesNotReadDocumentOrLeakResponse(HttpStatusCode status)
    {
        var handler = new CatalogDocumentHandler { CatalogStatus = status, CatalogResponseBody = "provider-secret" };
        var logger = new RecordingLogger();
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));

        (await new NyxIdRecommendedSkillGenerator(client, logger).GenerateAsync("caller-token", CatalogInstance(), CancellationToken.None))
            .Should().BeNull();

        handler.Requests.Should().Equal((CatalogDocumentHandler.CatalogPath, "caller-token"));
        string.Join('\n', logger.Messages).Should().NotContain("provider-secret").And.NotContain("caller-token");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GenerateAsync_CatalogDocumentFailure_DoesNotFallBackToInstanceOverride(HttpStatusCode status)
    {
        var handler = new CatalogDocumentHandler { DocumentStatus = status, DocumentBody = "provider-secret" };
        var logger = new RecordingLogger();
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));

        (await new NyxIdRecommendedSkillGenerator(client, logger).GenerateAsync("caller-token", CatalogInstance(), CancellationToken.None))
            .Should().BeNull();

        handler.Requests.Should().Equal(
            (CatalogDocumentHandler.CatalogPath, "caller-token"),
            (CatalogDocumentHandler.DocumentPath, "caller-token"));
        string.Join('\n', logger.Messages).Should().NotContain("provider-secret").And.NotContain("caller-token");
    }

    [Theory]
    [InlineData("{provider-secret")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"openapi\":\"3.1.1\",\"paths\":{}}")]
    public async Task GenerateAsync_InvalidCatalogDocument_DoesNotPublishPartialGuidance(string document)
    {
        var handler = new CatalogDocumentHandler { DocumentBody = document };
        var logger = new RecordingLogger();
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));

        (await new NyxIdRecommendedSkillGenerator(client, logger).GenerateAsync("caller-token", CatalogInstance(), CancellationToken.None))
            .Should().BeNull();

        handler.Requests.Should().Equal(
            (CatalogDocumentHandler.CatalogPath, "caller-token"),
            (CatalogDocumentHandler.DocumentPath, "caller-token"));
        string.Join('\n', logger.Messages).Should().NotContain("provider-secret");
    }

    [Fact]
    public async Task ReadAsync_LocalReferences_PreservesOptionalNestedRequestSchema()
    {
        var handler = new DocumentHandler { DocumentBody = ReferencedOpenApi };
        var services = await ReadAsync(handler, new RecordingLogger());

        services.Should().NotBeEmpty();
        var endpoint = services.Should().ContainSingle().Subject.Endpoints.Should().ContainSingle().Subject;
        endpoint.Method.Should().Be("POST");
        endpoint.PathTemplate.Should().Be("/registrations");
        var request = endpoint.RequestBodySchema!;
        request.ToJsonString().Should().Contain("service_ids").And.Contain("default_skill").And.NotContain("$ref");
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
    public async Task ReadAsync_InvalidReference_RejectsOnlyAffectedOperation(string reference)
    {
        var document = JsonNode.Parse(ReferencedOpenApi)!;
        document["components"]!["schemas"]!["Update"]!["properties"]!["runtime_config"] =
            new JsonObject { ["$ref"] = reference };
        document["paths"]!["/healthy"] = JsonNode.Parse("""
            {"get":{"responses":{"200":{"content":{"application/json":{"schema":{"type":"string"}}}}}}}
            """);
        var handler = new DocumentHandler { DocumentBody = document.ToJsonString() };
        var services = await ReadAsync(handler, new RecordingLogger());

        services.Should().NotBeEmpty();
        services.SelectMany(static service => service.Endpoints).Should().ContainSingle()
            .Which.PathTemplate.Should().Be("/healthy");
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
    public async Task ReadAsync_GatewayOnlyInventory_ReadsDocumentWithoutDownstreamEndpoint()
    {
        var handler = new DocumentHandler();
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));
        var inventory = await new NyxIdConnectedServiceInventoryReader(new NyxIdServiceInstanceClient(client))
            .ReadAsync("caller-token", organizationToken: null);
        var instance = inventory.Instances.Should().ContainSingle().Subject;

        var services = await new NyxIdOpenApiDocumentReader(client, new RecordingLogger())
            .ReadAsync("caller-token", instance, "document-test", CancellationToken.None);

        services.Should().NotBeEmpty();
        services.SelectMany(static service => service.Endpoints).Select(static endpoint => endpoint.PathTemplate)
            .Should().BeEquivalentTo("/api/channels/me", "/api/channels/registrations");
        handler.Requests.Should().Equal(
            ("/api/v1/keys", "caller-token"),
            ("/api/v1/proxy/services/us-aevatar/openapi.json", "caller-token"));
    }

    [Theory]
    [InlineData("https://nyx.test/api/v1/proxy/services/catalog-aevatar/openapi.json", "/api/v1/proxy/services/catalog-aevatar/openapi.json")]
    [InlineData("/api/v1/proxy/services/us-aevatar/openapi.json", "/api/v1/proxy/services/us-aevatar/openapi.json")]
    public async Task ReadAsync_GatewayDocument_UsesExplicitCatalogOrUserServiceIdentity(string url, string expectedPath)
    {
        var handler = new DocumentHandler { InventoryJson = WithUrls(url), DocumentPath = expectedPath };
        var services = await ReadAsync(handler, new RecordingLogger());

        services.Should().NotBeEmpty();
        handler.Requests.Last().Should().Be((expectedPath, "caller-token"));
    }

    [Theory]
    [InlineData("/api/openapi.json", null)]
    [InlineData("api/openapi.json", null)]
    [InlineData("https://aevatar.test/api/openapi.json", "https://aevatar.test")]
    public async Task ReadAsync_DownstreamSpec_UsesExactInstanceProxy(string specUrl, string? endpointUrl)
    {
        var handler = new DocumentHandler
        {
            InventoryJson = WithUrls(null, specUrl, endpointUrl),
            DocumentPath = "/api/v1/proxy/s/aevatar/api/openapi.json",
        };

        var services = await ReadAsync(handler, new RecordingLogger());

        services.Should().NotBeEmpty();
        handler.Requests.Last().Should().Be((handler.DocumentPath + "?_nyxid_via=us-aevatar", "caller-token"));
    }

    [Fact]
    public async Task ReadAsync_BothDocumentSources_UsesPublishedGatewayWithoutEndpointUrl()
    {
        var handler = new DocumentHandler
        {
            InventoryJson = WithUrls(
                "https://nyx.test/api/v1/proxy/services/us-aevatar/openapi.json",
                "https://aevatar.test/api/openapi.json"),
        };

        (await ReadAsync(handler, new RecordingLogger())).Should().NotBeEmpty();

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
    public async Task ReadAsync_UnsafeOrMissingAddress_ReportsResolutionFailureWithoutHttp(
        string? documentUrl, string? specUrl, string? endpointUrl, string failureCode)
    {
        var handler = new DocumentHandler { InventoryJson = WithUrls(documentUrl, specUrl, endpointUrl) };
        var logger = new RecordingLogger();

        var services = await ReadAsync(handler, logger);

        services.Should().BeEmpty();
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
    public async Task ReadAsync_HttpFailure_ReportsStatusWithoutLeakingResponse(HttpStatusCode status, string failureCode)
    {
        var handler = new DocumentHandler { DocumentStatus = status, DocumentBody = "provider-secret" };
        var logger = new RecordingLogger();

        var services = await ReadAsync(handler, logger);

        services.Should().BeEmpty();
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
    public async Task ReadAsync_InvalidOrEmptyContract_ReportsPreciseStage(string body, string stage, string failureCode)
    {
        var handler = new DocumentHandler { DocumentBody = body };
        var logger = new RecordingLogger();

        var services = await ReadAsync(handler, logger);

        services.Should().BeEmpty();
        logger.Messages.Should().ContainSingle(message => message.Contains($"stage={stage} code={failureCode}", StringComparison.Ordinal));
        string.Join('\n', logger.Messages).Should().NotContain("provider-secret");
    }

    [Fact]
    public async Task ReadAsync_OversizedDocument_ReportsBoundedReadFailure()
    {
        var handler = new DocumentHandler { DocumentBody = new string('a', 1024 * 1024 + 1) };
        var logger = new RecordingLogger();

        (await ReadAsync(handler, logger)).Should().BeEmpty();

        logger.Messages.Should().ContainSingle(message => message.Contains("stage=Fetch code=ResponseTooLarge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_TransportFailure_LogsOnlyFailureCode()
    {
        var handler = new DocumentHandler { DocumentException = new HttpRequestException("provider-secret") };
        var logger = new RecordingLogger();

        (await ReadAsync(handler, logger)).Should().BeEmpty();

        logger.Messages.Should().ContainSingle(message => message.Contains("stage=Fetch code=TransportFailure", StringComparison.Ordinal));
        string.Join('\n', logger.Messages).Should().NotContain("provider-secret").And.NotContain("caller-token");
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_PropagatesWithoutUnavailableDiagnostic()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new DocumentHandler { CancelDocumentWith = cancellation };
        var logger = new RecordingLogger();

        var act = () => ReadAsync(handler, logger, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        logger.Messages.Should().BeEmpty();
    }

    private static async Task<IReadOnlyList<NyxIdMcpService>> ReadAsync(
        DocumentHandler handler, RecordingLogger logger, CancellationToken ct = default)
    {
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));
        var inventory = await new NyxIdConnectedServiceInventoryReader(new NyxIdServiceInstanceClient(client))
            .ReadAsync("caller-token", organizationToken: null);
        return await new NyxIdOpenApiDocumentReader(client, logger)
            .ReadAsync("caller-token", inventory.Instances.Single(), "document-test", ct);
    }

    private static NyxIdServiceInstance CatalogInstance() => new()
    {
        UserServiceId = "us-diagnostic-instance",
        DisplaySlug = "aevatar-local-diag-catalog",
        Label = "Aevatar Local Diagnostic Catalog",
        CatalogServiceId = "catalog-aevatar",
        CatalogServiceSlug = "aevatar",
        OpenapiDocumentUrl = "https://nyx.test/api/v1/proxy/services/us-diagnostic-instance/openapi.json",
        OpenapiSpecUrl = "/private/openapi.json",
    };

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

    private sealed class CatalogDocumentHandler : HttpMessageHandler
    {
        public const string CatalogPath = "/api/v1/keys/catalog-aevatar";
        public const string DocumentPath = "/api/v1/catalog-curation/services/catalog-aevatar/openapi.json";
        public const string CatalogBody = """
            {"resource_type":"catalog_service","id":"catalog-aevatar","catalog_service_id":"catalog-aevatar",
             "slug":"aevatar","catalog_service_slug":"aevatar","name":"Aevatar","description":"Shared workflow APIs."}
            """;

        public List<(string Path, string? Token)> Requests { get; } = [];
        public string CatalogResponseBody { get; init; } = CatalogBody;
        public HttpStatusCode CatalogStatus { get; init; } = HttpStatusCode.OK;
        public string DocumentBody { get; init; } = OpenApi;
        public HttpStatusCode DocumentStatus { get; init; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.PathAndQuery, request.Headers.Authorization?.Parameter));
            var (status, body) = request.RequestUri.AbsolutePath switch
            {
                CatalogPath => (CatalogStatus, CatalogResponseBody),
                DocumentPath => (DocumentStatus, DocumentBody),
                _ => (HttpStatusCode.Forbidden, "instance_document_must_not_be_read"),
            };
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
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
