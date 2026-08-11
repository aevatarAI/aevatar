using System.Text.Json;

namespace Aevatar.Architecture.Tests.Rules;

public sealed class M42LarkExactReadOpenApiContractTests
{
    private const string MessageCreatePath = "/open-apis/im/v1/messages";
    private const string MessageExactReadPath = "/open-apis/im/v1/messages/{message_id}";

    [Fact]
    public void ExactMessageReadPreservesCreateAndPublishesReadOnlyProviderIdentityPath()
    {
        using var document = LoadOverlay();
        var paths = document.RootElement.GetProperty("paths");

        var create = paths.GetProperty(MessageCreatePath).GetProperty("post");
        Assert.Equal("im_message_create", create.GetProperty("operationId").GetString());

        var exactRead = paths.GetProperty(MessageExactReadPath).GetProperty("get");
        Assert.Equal("im_message_get", exactRead.GetProperty("operationId").GetString());
        var tool = exactRead.GetProperty("x-aevatar-tool");
        Assert.True(tool.GetProperty("readOnly").GetBoolean());
        Assert.False(tool.GetProperty("destructive").GetBoolean());
        Assert.False(tool.GetProperty("requiresApproval").GetBoolean());

        var parameter = exactRead.GetProperty("parameters")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "message_id");
        Assert.Equal("path", parameter.GetProperty("in").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
        Assert.Equal("string", parameter.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public void ExactMessageCleanupIsDestructiveApprovalProtectedAndIdentityBound()
    {
        using var document = LoadOverlay();
        Assert.Equal("m42-canary-v3",
            document.RootElement.GetProperty("info").GetProperty("version").GetString());
        var exactDelete = document.RootElement.GetProperty("paths")
            .GetProperty(MessageExactReadPath)
            .GetProperty("delete");

        Assert.Equal("im_message_delete", exactDelete.GetProperty("operationId").GetString());
        var tool = exactDelete.GetProperty("x-aevatar-tool");
        Assert.False(tool.GetProperty("readOnly").GetBoolean());
        Assert.True(tool.GetProperty("destructive").GetBoolean());
        Assert.True(tool.GetProperty("requiresApproval").GetBoolean());

        var parameters = exactDelete.GetProperty("parameters").EnumerateArray().ToArray();
        var parameter = Assert.Single(parameters);
        Assert.Equal("message_id", parameter.GetProperty("name").GetString());
        Assert.Equal("path", parameter.GetProperty("in").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
        Assert.Equal("string", parameter.GetProperty("schema").GetProperty("type").GetString());
    }

    private static JsonDocument LoadOverlay()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "operations",
            "2026-08-12-m42-lark-exact-readback-openapi.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
            current = current.Parent;

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
