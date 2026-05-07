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
    private const string CatalogId = "69b3fbd6-bb62-40ec-9b42-88457a9c75d0";
    private const string SshOk = """{"exit_code":0,"stdout":"ok","stderr":"","duration_ms":42,"timed_out":false}""";

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
    public async Task ExecuteAsync_ResolvesSlugToCatalogServiceId_AndPostsToCorrectSshPath()
    {
        // The /api/v1/ssh/{id}/exec route keys on catalog_service_id, NOT on the user-service
        // slug or its uuid. Tool must hop GET /keys/{slug} → take catalog_service_id → POST.
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, "/api/v1/keys/sg-office-network",
            $$"""{"id":"70f053b1-9185-4794-a135-5536c7608c19","slug":"sg-office-network","catalog_service_id":"{{CatalogId}}"}""");
        handler.Map(HttpMethod.Post, $"/api/v1/ssh/{CatalogId}/exec", SshOk);

        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            var result = await tool.ExecuteAsync(
                """{"service":"sg-office-network","command":"uname -a","principal":"ubuntu","timeout_secs":15}""");

            result.Should().Contain("\"exit_code\":0");

            handler.Recorded.Should().Contain(r =>
                r.Method == HttpMethod.Post &&
                r.Path == $"/api/v1/ssh/{CatalogId}/exec");

            var execRequest = handler.Recorded.Last(r => r.Method == HttpMethod.Post);
            execRequest.Authorization.Should().Be("Bearer test-token");

            using var doc = JsonDocument.Parse(execRequest.Body!);
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
    public async Task ExecuteAsync_FallsBackToListServices_WhenDirectKeyLookupMissesCatalogId()
    {
        // /keys/{slug} can return a wrapper without `catalog_service_id` surfaced (e.g. some
        // builds nest it). The list endpoint always carries it, so the resolver falls back.
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, "/api/v1/keys/sg-office-network",
            """{"id":"70f053b1-9185-4794-a135-5536c7608c19","slug":"sg-office-network"}""");
        handler.Map(HttpMethod.Get, "/api/v1/keys",
            $$"""{"keys":[{"id":"70f053b1-9185-4794-a135-5536c7608c19","slug":"sg-office-network","catalog_service_id":"{{CatalogId}}"}]}""");
        handler.Map(HttpMethod.Post, $"/api/v1/ssh/{CatalogId}/exec", SshOk);

        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            var result = await tool.ExecuteAsync(
                """{"service":"sg-office-network","command":"uname -a","principal":"ubuntu"}""");

            result.Should().Contain("\"exit_code\":0");
            handler.Recorded.Should().Contain(r =>
                r.Method == HttpMethod.Post && r.Path == $"/api/v1/ssh/{CatalogId}/exec");
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_DefaultsTimeoutTo30_WhenOmitted()
    {
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, "/api/v1/keys/sg-office",
            $$"""{"id":"u","slug":"sg-office","catalog_service_id":"{{CatalogId}}"}""");
        handler.Map(HttpMethod.Post, $"/api/v1/ssh/{CatalogId}/exec", SshOk);

        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            await tool.ExecuteAsync(
                """{"service":"sg-office","command":"echo hi","principal":"ubuntu"}""");
            var exec = handler.Recorded.Last(r => r.Method == HttpMethod.Post);
            using var doc = JsonDocument.Parse(exec.Body!);
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
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, "/api/v1/keys/sg",
            $$"""{"id":"u","slug":"sg","catalog_service_id":"{{CatalogId}}"}""");
        handler.Map(HttpMethod.Post, $"/api/v1/ssh/{CatalogId}/exec", SshOk);

        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            await tool.ExecuteAsync(
                """{"service":"sg","command":"sleep 1","principal":"ubuntu","timeout_secs":9999}""");
            var exec = handler.Recorded.Last(r => r.Method == HttpMethod.Post);
            using var doc = JsonDocument.Parse(exec.Body!);
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

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Body, string? Authorization);

    private sealed class PathHandler : HttpMessageHandler
    {
        private readonly Dictionary<(HttpMethod Method, string Path), string> _routes = new();
        public List<RecordedRequest> Recorded { get; } = new();

        public void Map(HttpMethod method, string path, string responseBody)
        {
            _routes[(method, path)] = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content is not null)
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Recorded.Add(new RecordedRequest(
                request.Method,
                path,
                body,
                request.Headers.Authorization?.ToString()));

            if (_routes.TryGetValue((request.Method, path), out var responseBodyText))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBodyText, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":"not_found"}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
