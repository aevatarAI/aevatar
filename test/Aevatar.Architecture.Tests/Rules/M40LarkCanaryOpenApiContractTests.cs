using System.Text.Json;

namespace Aevatar.Architecture.Tests.Rules;

public sealed class M40LarkCanaryOpenApiContractTests
{
    private const string ApprovalCreatePath = "/open-apis/approval/v4/instances";
    private const string ApprovalExactReadPath = "/open-apis/approval/v4/instances/{instance_id}";
    private const string BitableRecordsPath =
        "/open-apis/bitable/v1/apps/{app_token}/tables/{table_id}/records";
    private const string BitableExactRecordPath =
        "/open-apis/bitable/v1/apps/{app_token}/tables/{table_id}/records/{record_id}";

    [Fact]
    public void ApprovalCreateRequiresCallerOwnedUuidAndExactReadUsesItAsPathIdentity()
    {
        using var document = LoadOverlay();
        var paths = document.RootElement.GetProperty("paths");
        var create = paths.GetProperty(ApprovalCreatePath).GetProperty("post");
        var createSchema = JsonSchema(create);

        Assert.Equal("lark_create_approval_instance", create.GetProperty("operationId").GetString());
        Assert.False(createSchema.GetProperty("additionalProperties").GetBoolean());
        AssertRequiredProperties(createSchema, "approval_code", "user_id", "form", "uuid");
        Assert.Equal("string", createSchema.GetProperty("properties").GetProperty("uuid")
            .GetProperty("type").GetString());

        var exactRead = paths.GetProperty(ApprovalExactReadPath).GetProperty("get");
        Assert.Equal("lark_get_approval_instance", exactRead.GetProperty("operationId").GetString());
        AssertParameter(exactRead, "instance_id", "path", required: true, schemaType: "string");
    }

    [Fact]
    public void BitableListPublishesTheCompletePaginationAndExactRecordIdentityContract()
    {
        using var document = LoadOverlay();
        var list = document.RootElement.GetProperty("paths")
            .GetProperty(BitableRecordsPath)
            .GetProperty("get");

        Assert.Equal("bitable_records_list", list.GetProperty("operationId").GetString());
        AssertParameter(list, "page_size", "query", required: false, schemaType: "integer");
        AssertParameter(list, "page_token", "query", required: false, schemaType: "string");

        var responseData = JsonSchema(list.GetProperty("responses").GetProperty("200"))
            .GetProperty("properties")
            .GetProperty("data");
        var responseProperties = responseData.GetProperty("properties");

        Assert.Equal("boolean", responseProperties.GetProperty("has_more").GetProperty("type").GetString());
        Assert.Equal("string", responseProperties.GetProperty("page_token").GetProperty("type").GetString());

        var itemProperties = responseProperties.GetProperty("items")
            .GetProperty("items")
            .GetProperty("properties");
        Assert.Equal("string", itemProperties.GetProperty("record_id").GetProperty("type").GetString());
    }

    [Fact]
    public void BitableCleanupDeletesOnlyAnExactRecordPath()
    {
        using var document = LoadOverlay();
        var paths = document.RootElement.GetProperty("paths");
        var delete = paths.GetProperty(BitableExactRecordPath).GetProperty("delete");

        Assert.Equal("bitable_record_delete", delete.GetProperty("operationId").GetString());
        AssertParameter(delete, "app_token", "path", required: true, schemaType: "string");
        AssertParameter(delete, "table_id", "path", required: true, schemaType: "string");
        AssertParameter(delete, "record_id", "path", required: true, schemaType: "string");
    }

    [Fact]
    public void OverlayDoesNotPublishGenericProxyOperations()
    {
        using var document = LoadOverlay();
        var operations = document.RootElement.GetProperty("paths")
            .EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Where(method => method.Value.ValueKind == JsonValueKind.Object &&
                             method.Value.TryGetProperty("operationId", out _))
            .Select(method => method.Value.GetProperty("operationId").GetString())
            .ToArray();

        Assert.NotEmpty(operations);
        Assert.DoesNotContain(operations, operationId =>
            operationId is null ||
            operationId.Contains("proxy", StringComparison.OrdinalIgnoreCase) ||
            operationId.Contains("request", StringComparison.OrdinalIgnoreCase) ||
            operationId.Contains("invoke", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonDocument LoadOverlay()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "operations",
            "2026-08-09-m40-lark-canary-openapi.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static JsonElement JsonSchema(JsonElement operationOrResponse)
    {
        var contentOwner = operationOrResponse.TryGetProperty("requestBody", out var requestBody)
            ? requestBody
            : operationOrResponse;
        return contentOwner.GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
    }

    private static void AssertRequiredProperties(JsonElement schema, params string[] expected)
    {
        var required = schema.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var property in expected)
            Assert.Contains(property, required);
    }

    private static void AssertParameter(
        JsonElement operation,
        string name,
        string location,
        bool required,
        string schemaType)
    {
        var parameter = operation.GetProperty("parameters")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == name);

        Assert.Equal(location, parameter.GetProperty("in").GetString());
        Assert.Equal(required, parameter.GetProperty("required").GetBoolean());
        Assert.Equal(schemaType, parameter.GetProperty("schema").GetProperty("type").GetString());
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
            current = current.Parent;

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
