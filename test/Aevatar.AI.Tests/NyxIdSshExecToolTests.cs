using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class NyxIdSshExecToolTests
{
    [Fact]
    public void Name_IsSshExec()
    {
        var tool = new NyxIdSshExecTool(CreateDummyClient());
        tool.Name.Should().Be("ssh_exec");
    }

    [Fact]
    public void RequiresApproval_AlwaysTrue()
    {
        var tool = new NyxIdSshExecTool(CreateDummyClient());
        tool.RequiresApproval(
            """{"service":"sg-office","command":"uname -a","principal":"ubuntu"}""")
            .Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NoToken_ReturnsError()
    {
        var tool = new NyxIdSshExecTool(CreateDummyClient());
        AgentToolRequestContext.CurrentMetadata = null;

        var result = await tool.ExecuteAsync(
            """{"service":"sg-office","command":"uname -a","principal":"ubuntu"}""");

        result.Should().Contain("No NyxID access token");
    }

    [Theory]
    [InlineData("""{"command":"uname -a","principal":"ubuntu"}""")]   // missing service
    [InlineData("""{"service":"sg-office","principal":"ubuntu"}""")]  // missing command
    [InlineData("""{"service":"sg-office","command":"uname -a"}""")]  // missing principal
    public async Task ExecuteAsync_MissingRequiredField_ReturnsError(string args)
    {
        var tool = new NyxIdSshExecTool(CreateDummyClient());
        SetMetadata("test-token");
        try
        {
            var result = await tool.ExecuteAsync(args);
            result.Should().Contain("'service', 'command', and 'principal' are required");
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_RoutesToCorrectSshEndpoint_AndForwardsBody()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"exit_code":0,"stdout":"ok","stderr":"","duration_ms":42,"timed_out":false}""",
                Encoding.UTF8, "application/json"),
        });
        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            var result = await tool.ExecuteAsync(
                """{"service":"sg-office-network","command":"uname -a","principal":"ubuntu","timeout_secs":15}""");

            result.Should().Contain("\"exit_code\":0");
            handler.LastRequest.Should().NotBeNull();
            handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
            handler.LastRequest.RequestUri!.AbsoluteUri.Should()
                .Be("https://nyx.example/api/v1/ssh/sg-office-network/exec");
            handler.LastRequest.Headers.Authorization
                .Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "test-token"));

            using var doc = JsonDocument.Parse(handler.LastBody!);
            doc.RootElement.GetProperty("command").GetString().Should().Be("uname -a");
            doc.RootElement.GetProperty("principal").GetString().Should().Be("ubuntu");
            doc.RootElement.GetProperty("timeout_secs").GetInt32().Should().Be(15);
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_DefaultsTimeoutTo30_WhenOmitted()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"exit_code":0,"stdout":"","stderr":"","duration_ms":1,"timed_out":false}""",
                Encoding.UTF8, "application/json"),
        });
        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            await tool.ExecuteAsync("""{"service":"sg-office","command":"echo hi","principal":"ubuntu"}""");
            using var doc = JsonDocument.Parse(handler.LastBody!);
            doc.RootElement.GetProperty("timeout_secs").GetInt32().Should().Be(30);
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_ClampsTimeoutToServerMax()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"exit_code":0,"stdout":"","stderr":"","duration_ms":1,"timed_out":false}""",
                Encoding.UTF8, "application/json"),
        });
        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            await tool.ExecuteAsync(
                """{"service":"sg","command":"sleep 1","principal":"ubuntu","timeout_secs":9999}""");
            using var doc = JsonDocument.Parse(handler.LastBody!);
            doc.RootElement.GetProperty("timeout_secs").GetInt32().Should().Be(300);
        }
        finally
        {
            ClearMetadata();
        }
    }

    private static NyxIdApiClient CreateDummyClient() =>
        new(new NyxIdToolOptions { BaseUrl = "https://test.example.com" });

    private static void SetMetadata(string token)
    {
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = token,
        };
    }

    private static void ClearMetadata() => AgentToolRequestContext.CurrentMetadata = null;

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
