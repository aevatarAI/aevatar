using System.Reflection;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Value = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.AI.Tests;

public sealed class CatalogSkillContentRendererTests
{
    [Fact]
    public void Render_UsesCatalogIdentityAndSelectsCurrentInstanceAtExecution()
    {
        var content = new CatalogSkillContentRenderer().Render(Input());

        content.Name.Should().Be("aevatar-connected-service");
        content.DisplayName.Should().Be("Aevatar");
        content.Description.Should().Contain("Aevatar").And.Contain("Workflow and agent services.");
        content.Tags.Should().Equal("aevatar");
        content.ToolList.Should().Equal("nyxid_invoke_operation");
        content.RecommendationName.Should().Be(content.Name);
        content.InstructionsMarkdown.Should().Contain("catalog-aevatar")
            .And.Contain("nyxid_service_inventory")
            .And.Contain("explicit catalog association")
            .And.Contain("Do not silently select the first")
            .And.Contain("Prefer invoking with only `user_service_id`")
            .And.Contain("same selected inventory record")
            .And.Contain("Never use the catalog slug as an invocation selector")
            .And.Contain(CatalogSkillContentRenderer.DocumentRequestInstructionMarker)
            .And.Contain("document_request.skill_ref")
            .And.NotContain("- service_slug:")
            .And.NotContain("user_service_id` =")
            .And.NotContain("service_slug` =");
    }

    [Fact]
    public void Render_InputBoundaryCannotReceiveCallerInstanceOrPublishingVersion()
    {
        typeof(CatalogSkillContentInput).GetProperties().Select(static property => property.Name)
            .Should().BeEquivalentTo("CatalogServiceId", "CatalogServiceSlug", "CatalogName", "CatalogDescription", "ApiContract");
        typeof(CatalogApiContract).GetProperties().Select(static property => property.Name)
            .Should().Equal("Operations");
        typeof(CatalogApiOperation).GetProperties().Select(static property => property.Name)
            .Should().BeEquivalentTo("OperationId", "Name", "Method", "RelativePath", "Risk", "Parameters", "RequestBody", "Responses");
        typeof(GeneratedCatalogSkillContent).GetProperty("Version").Should().BeNull();
        typeof(CatalogSkillContentRenderer).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Should().ContainSingle().Which.GetParameters()
            .Should().ContainSingle().Which.ParameterType.Should().Be(typeof(CatalogSkillContentInput));
    }

    [Fact]
    public void Render_IntegerQueryParameterDocumentsStringTransportEncoding()
    {
        var operation = Operation("list_items", "/items", "GET", NyxIdOperationRisk.ReadOnly) with
        {
            Parameters = [new("limit", ParameterLocation.Query, false,
                Value.Parser.ParseJson("""{"type":"integer","minimum":1,"maximum":100}"""), "Maximum items to return.")],
        };

        var content = new CatalogSkillContentRenderer().Render(Input(operation));

        content.InstructionsMarkdown.Should().Contain("query.limit optional")
            .And.Contain("Maximum items to return.")
            .And.Contain("\"type\": \"integer\"")
            .And.Contain("`query` and `headers` are transport string maps")
            .And.Contain("Encode every query and header value as a JSON string")
            .And.Contain("Preserve native JSON types only in `body`")
            .And.Contain("""`"query":{"limit":"100","include_inactive":"false"}`""");
    }

    [Fact]
    public void Render_PreservesBusinessServiceSlugAndCompleteRequestSchemaAndResponseSummary()
    {
        var description = new string('x', 1800);
        var operation = Operation() with
        {
            Parameters = [new("service_slug", ParameterLocation.Query, false,
                Value.Parser.ParseJson("""{"type":"string","enum":["business-service"]}"""), "A business service filter.")],
            RequestBody = new(true, [new("application/json", Value.Parser.ParseJson(
                "{\"type\":\"object\",\"description\":\"" + description + "\",\"properties\":{\"service_slug\":{\"type\":\"string\"},\"last_field\":{\"type\":\"integer\",\"minimum\":2}},\"required\":[\"service_slug\"]}"))]),
            Responses = [new("202", "Accepted for processing.", [new("application/json", Value.Parser.ParseJson(
                """{"type":"object","properties":{"state_version":{"type":"integer"},"service_slug":{"type":"string"}}}"""))])],
        };

        var content = new CatalogSkillContentRenderer().Render(Input(operation));

        content.InstructionsMarkdown.Should().Contain("query.service_slug optional")
            .And.Contain("business-service")
            .And.Contain(description)
            .And.Contain("last_field")
            .And.Contain("minimum")
            .And.Contain("202")
            .And.Contain("Accepted for processing.")
            .And.Contain("media_type: `application/json`")
            .And.Contain("request_body_required: `true`")
            .And.Contain("application/json")
            .And.NotContain("state_version")
            .And.NotContain("response_media_types:")
            .And.NotContain("fcc6c5c6-9661-4dae-ac17-b57ac42de847")
            .And.NotContain("aevatar-local-diag-catalog")
            .And.NotContain("Aevatar Local Diagnostic Catalog");
    }

    [Fact]
    public void Render_UnorderedContractProducesIdenticalContentAndRevision()
    {
        var firstOperation = Operation() with
        {
            Parameters =
            [
                new("z", ParameterLocation.Query, false, Value.Parser.ParseJson("""{"type":"string","minLength":1}"""), null),
                new("a", ParameterLocation.Path, true, Value.Parser.ParseJson("""{"type":"integer"}"""), null),
            ],
            RequestBody = new(false,
            [
                new("text/plain", Value.ForBool(true)),
                new("application/json", Value.Parser.ParseJson("""{"type":"object","properties":{"z":{"type":"integer"},"a":{"type":"string"}}}""")),
            ]),
            Responses =
            [
                new("404", "Missing.", []),
                new("200", "Success.", [new("text/plain", Value.ForBool(true)), new("application/json", Value.ForBool(false))]),
            ],
        };
        var secondOperation = Operation("read_other", "/other", "GET", NyxIdOperationRisk.ReadOnly);
        var reordered = firstOperation with
        {
            Parameters =
            [
                firstOperation.Parameters[1],
                firstOperation.Parameters[0] with { Schema = Value.Parser.ParseJson("""{"minLength":1,"type":"string"}""") },
            ],
            RequestBody = new(false,
            [
                new("application/json", Value.Parser.ParseJson("""{"properties":{"a":{"type":"string"},"z":{"type":"integer"}},"type":"object"}""")),
                firstOperation.RequestBody!.Content[0],
            ]),
            Responses =
            [
                firstOperation.Responses[1] with { Content = firstOperation.Responses[1].Content.Reverse().ToArray() },
                firstOperation.Responses[0],
            ],
        };
        var renderer = new CatalogSkillContentRenderer();

        var first = renderer.Render(Input(firstOperation, secondOperation));
        var second = renderer.Render(Input(secondOperation, reordered));

        first.InstructionsMarkdown.Should().NotBeEmpty();
        first.Should().BeEquivalentTo(second, options => options.WithStrictOrdering());
        first.Revision.Should().StartWith("generated-catalog-v1-");
    }

    [Fact]
    public void Render_ResponseContractChangeChangesRevision()
    {
        var operation = Operation() with
        {
            Responses = [new("200", "Success.", [new("application/json", Value.Parser.ParseJson("""{"type":"integer","minimum":1}"""))])],
        };
        var changedOperation = operation with
        {
            Responses = [new("200", "Success.", [new("application/json", Value.Parser.ParseJson("""{"type":"integer","minimum":2}"""))])],
        };
        var renderer = new CatalogSkillContentRenderer();

        renderer.Render(Input(operation)).Revision.Should().NotBe(renderer.Render(Input(changedOperation)).Revision);
    }

    [Fact]
    public void Render_LimitsOperationsDeterministicallyButRevisesForUnlistedContractChanges()
    {
        var operations = Enumerable.Range(0, 41)
            .Select(index => Operation($"read_{index:00}", $"/resource/{index:00}", "GET", NyxIdOperationRisk.ReadOnly))
            .ToArray();
        var renderer = new CatalogSkillContentRenderer();
        var content = renderer.Render(Input(operations.Reverse().ToArray()));

        content.InstructionsMarkdown.Should().Contain("read_00").And.Contain("read_39")
            .And.NotContain("read_40").And.Contain("Only the first 40 operations");
        var changed = operations.Select(operation => operation.OperationId == "read_40"
            ? operation with { RelativePath = "/resource/changed" }
            : operation).ToArray();
        content.Revision.Should().NotBe(renderer.Render(Input(changed)).Revision);
    }

    [Fact]
    public void Render_EmptyContractIsRejectedBeforeCreatingPublicContent()
    {
        var act = () => new CatalogSkillContentRenderer().Render(Input() with { ApiContract = new([]) });

        act.Should().Throw<ArgumentException>().WithMessage("*operation*");
    }

    private static CatalogSkillContentInput Input(params CatalogApiOperation[] operations) => new(
        "catalog-aevatar", "aevatar", "Aevatar", "Workflow and agent services.",
        new(operations.Length == 0 ? [Operation()] : operations));

    private static CatalogApiOperation Operation(
        string id = "create_registration", string path = "/registrations", string method = "POST",
        NyxIdOperationRisk risk = NyxIdOperationRisk.Write) =>
        new(id, "Manage registrations", method, path, risk, [], null, []);
}
