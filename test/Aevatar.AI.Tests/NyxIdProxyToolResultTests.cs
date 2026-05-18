using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.Tests;

public sealed class NyxIdProxyToolResultTests
{
    [Fact]
    public void ClassifyProxyResponse_NyxIdErrorEnvelope_ReturnsTypedToolError()
    {
        var result = NyxIdProxyTool.ClassifyProxyResponse(
            """{"error":true,"status":401,"body":"{\"message\":\"bad token\"}"}""",
            "api-github",
            "/user");

        result.ToolStatus.Should().Be("error");
        result.StatusCode.Should().Be(401);
        result.Error.Should().Be("upstream_http_401");
        result.Detail.Should().Be("bad token");
        result.Data.Should().BeNull();

        using var doc = JsonDocument.Parse(result.ToJson());
        doc.RootElement.GetProperty("tool_status").GetString().Should().Be("error");
        doc.RootElement.GetProperty("status_code").GetInt32().Should().Be(401);
        doc.RootElement.GetProperty("slug").GetString().Should().Be("api-github");
        doc.RootElement.GetProperty("path").GetString().Should().Be("/user");
    }

    [Fact]
    public void ClassifyProxyResponse_SuccessJson_ReturnsTypedOkWithData()
    {
        var result = NyxIdProxyTool.ClassifyProxyResponse(
            """{"login":"octocat","id":1}""",
            "api-github",
            "/user");

        result.ToolStatus.Should().Be("ok");
        result.StatusCode.Should().BeNull();
        result.Error.Should().BeNull();
        result.Data.Should().NotBeNull();
        result.Data!.Value.GetProperty("login").GetString().Should().Be("octocat");

        using var doc = JsonDocument.Parse(result.ToJson());
        doc.RootElement.GetProperty("tool_status").GetString().Should().Be("ok");
        doc.RootElement.GetProperty("data").GetProperty("login").GetString().Should().Be("octocat");
    }

    [Fact]
    public void ClassifyProxyResponse_NonJsonSuccess_ReturnsTypedOkWithStringData()
    {
        var result = NyxIdProxyTool.ClassifyProxyResponse(
            "service temporarily unavailable",
            "api-github",
            "/user");

        result.ToolStatus.Should().Be("ok");
        result.StatusCode.Should().BeNull();
        result.Error.Should().BeNull();
        result.Detail.Should().BeNull();
        result.Data.Should().NotBeNull();
        result.Data!.Value.GetString().Should().Be("service temporarily unavailable");

        using var doc = JsonDocument.Parse(result.ToJson());
        doc.RootElement.GetProperty("tool_status").GetString().Should().Be("ok");
        doc.RootElement.GetProperty("data").GetString().Should().Be("service temporarily unavailable");
    }

    [Fact]
    public void ClassifyProxyResponse_UnrecognizedJsonErrorBody_DoesNotLeakRawJsonToDetail()
    {
        var result = NyxIdProxyTool.ClassifyProxyResponse(
            """{"error":true,"status":403,"body":"{\"nested\":{\"secret\":\"token\"}}"}""",
            "api-github",
            "/user");

        result.ToolStatus.Should().Be("error");
        result.StatusCode.Should().Be(403);
        result.Detail.Should().Be("upstream_error_body_unclassified");
    }
}
