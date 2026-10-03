using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.CatalogSkills;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.GAgentService.Abstractions.CatalogSkills;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class CatalogRecommendedSkillGenerationTests
{
    [Fact]
    public async Task GenerateAsync_ResponseSchemaDetailsAreNotPublished()
    {
        var nestedSchema = JsonNode.Parse("""
            {"type":"object","properties":{"response_private_field":{"type":"string"}},
             "x-response-private":"aevatar-local-diag-catalog",
             "x-instance-id":"fcc6c5c6-9661-4dae-ac17-b57ac42de847"}
            """)!;
        var handler = new CatalogHandler { Document = DocumentWithResponseSchema(nestedSchema) };
        using var client = CreateClient(handler);

        var content = await new NyxIdRecommendedSkillGenerator(client)
            .GenerateAsync("authorized-reader", "catalog-aevatar", CancellationToken.None);

        content.InstructionsMarkdown.Should().Contain("GET /items")
            .And.Contain("response: `200`")
            .And.Contain("media_type: `application/json`")
            .And.NotContain("response_private_field")
            .And.NotContain("x-response-private")
            .And.NotContain("aevatar-local-diag-catalog")
            .And.NotContain("fcc6c5c6-9661-4dae-ac17-b57ac42de847");
    }

    [Fact]
    public async Task ReadAsync_SchemaDataObjects_PreserveBusinessFieldNamesAndLiteralValues()
    {
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{
              "examples":{"type":"string"},"x-value":{"type":"string"},"service_slug":{"type":"string"}},
             "default":{"examples":["business-default"],"x-value":"business-extension","service_slug":"business-service"},
             "enum":[{"examples":["business-enum"],"x-value":"business-value","service_slug":"business-selection"}],
             "dependencies":{"service_slug":["examples","x-value"]},
             "dependentRequired":{"service_slug":["examples"]}}
            """)!;
        var handler = new CatalogHandler { Document = DocumentWithResponseSchema(schema) };
        using var client = CreateClient(handler);
        var reader = new NyxIdCatalogSkillContentReader(new NyxIdCatalogSkillClient(client), NullLogger.Instance);

        var input = await reader.ReadAsync("authorized-reader", "catalog-aevatar", CancellationToken.None);

        var actual = JsonNode.Parse(JsonFormatter.Default.Format(input.ApiContract.Operations.Single().Responses.Single().Content.Single().Schema!));
        JsonNode.DeepEquals(actual, schema).Should().BeTrue();
    }

    [Theory]
    [InlineData("9007199254740993")]
    [InlineData("9223372036854775807")]
    [InlineData("1e309")]
    [InlineData("1e-400")]
    [InlineData("0.10000000000000000001")]
    public async Task GenerateAsync_UnrepresentableSchemaNumber_FailsWithTypedContractError(string number)
    {
        var schema = JsonNode.Parse("{\"type\":\"number\",\"const\":" + number + "}")!;
        var handler = new CatalogHandler { Document = DocumentWithResponseSchema(schema) };
        using var client = CreateClient(handler);
        var generator = new NyxIdRecommendedSkillGenerator(client);

        var act = () => generator.GenerateAsync("authorized-reader", "catalog-aevatar", CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<CatalogRecommendedSkillUpdateException>()).Which;
        failure.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.ContractInvalid);
        failure.Stage.Should().Be(CatalogRecommendedSkillUpdateStage.ReadContract);
        failure.Message.Should().NotContain(number);
    }

    [Fact]
    public async Task GenerateAsync_ResponseSchemaChangesRevisionWithoutPublishingSchema()
    {
        var firstHandler = new CatalogHandler
        {
            Document = DocumentWithResponseSchema(JsonNode.Parse("{\"type\":\"number\",\"const\":1.2500e2}")!),
        };
        var secondHandler = new CatalogHandler
        {
            Document = DocumentWithResponseSchema(JsonNode.Parse("{\"type\":\"number\",\"const\":125.01}")!),
        };
        using var firstClient = CreateClient(firstHandler);
        using var secondClient = CreateClient(secondHandler);

        var first = await new NyxIdRecommendedSkillGenerator(firstClient)
            .GenerateAsync("authorized-reader", "catalog-aevatar", CancellationToken.None);
        var second = await new NyxIdRecommendedSkillGenerator(secondClient)
            .GenerateAsync("authorized-reader", "catalog-aevatar", CancellationToken.None);

        first.InstructionsMarkdown.Should().Contain("response: `200`")
            .And.Contain("media_type: `application/json`")
            .And.NotContain("const")
            .And.NotContain("1.2500e2");
        second.InstructionsMarkdown.Should().NotContain("125.01");
        first.Revision.Should().NotBe(second.Revision);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("slug")]
    public async Task GenerateAsync_WhitespaceCatalogField_FailsAtCatalogBoundary(string field)
    {
        var catalog = JsonNode.Parse(CatalogHandler.Catalog)!;
        catalog[field] = " \t";
        if (field == "slug")
            catalog["catalog_service_slug"] = " \t";
        var handler = new CatalogHandler { CatalogResponse = catalog.ToJsonString() };
        using var client = CreateClient(handler);

        var act = () => new NyxIdRecommendedSkillGenerator(client)
            .GenerateAsync("authorized-reader", "catalog-aevatar", CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<CatalogRecommendedSkillUpdateException>()).Which;
        failure.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.ContractInvalid);
        failure.Stage.Should().Be(CatalogRecommendedSkillUpdateStage.ReadContract);
        handler.Paths.Should().Equal("/api/v1/keys/catalog-aevatar");
    }

    [Fact]
    public async Task GenerateAsync_UsesAuthoritativeCatalogWithoutPublishingReadingInstanceIdentity()
    {
        var handler = new CatalogHandler();
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));
        var instance = new NyxIdServiceInstance
        {
            UserServiceId = "fcc6c5c6-9661-4dae-ac17-b57ac42de847",
            DisplaySlug = "aevatar-local-diag-catalog",
            Label = "Aevatar Local Diagnostic Catalog",
            CatalogServiceId = "catalog-aevatar",
            CatalogServiceSlug = "aevatar",
            OpenapiDocumentUrl = "https://nyx.test/api/v1/proxy/services/fcc6c5c6-9661-4dae-ac17-b57ac42de847/openapi.json",
        };

        var content = await new NyxIdRecommendedSkillGenerator(client)
            .GenerateAsync("authorized-reader", instance, CancellationToken.None);

        content.Should().NotBeNull();
        var publicContent = content!.InstructionsMarkdown + content.Description + string.Join(',', content.Tags);
        publicContent.Should().NotContain(instance.UserServiceId)
            .And.NotContain(instance.DisplaySlug).And.NotContain(instance.Label);
        content.InstructionsMarkdown.Should().Contain("GET /items").And.Contain("service_slug");
        handler.Paths.Should().Equal("/api/v1/keys/catalog-aevatar",
            "/api/v1/catalog-curation/services/catalog-aevatar/openapi.json");
    }

    private static NyxIdApiClient CreateClient(CatalogHandler handler) =>
        new(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));

    private static string DocumentWithResponseSchema(JsonNode schema)
    {
        var document = JsonNode.Parse(CatalogHandler.DefaultDocument)!;
        document["paths"]!["/items"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"] = schema;
        return document.ToJsonString();
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public const string Catalog = """
            {"resource_type":"catalog_service","id":"catalog-aevatar","catalog_service_id":"catalog-aevatar",
             "slug":"aevatar","catalog_service_slug":"aevatar","name":"Aevatar"}
            """;
        public const string DefaultDocument = """
            {"openapi":"3.1.1","info":{"title":"Aevatar Local Diagnostic Catalog"},
            "servers":[{"url":"https://private-instance.invalid"}],"paths":{"/items":{"get":{
              "parameters":[{"in":"query","name":"service_slug","schema":{"type":"string"},
                "example":"aevatar-local-diag-catalog"}],
              "responses":{"200":{"content":{"application/json":{"schema":{"type":"object"}}}}}
            }}}}
            """;
        public List<string> Paths { get; } = [];
        public string CatalogResponse { get; init; } = Catalog;
        public string Document { get; init; } = DefaultDocument;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var body = path == "/api/v1/keys/catalog-aevatar"
                ? CatalogResponse : Document;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
