using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging;

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
    public void Constructor_NullClient_Throws()
    {
        var act = () => new NyxIdSshExecTool(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("client");
    }

    [Fact]
    public void Metadata_DescribesSshExecutionContract()
    {
        var tool = new NyxIdSshExecTool(CreateDummyClient());

        tool.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        tool.IsDestructive.Should().BeTrue();
        tool.Description.Should().Contain("ssh://");
        tool.Description.Should().Contain("nyxid_proxy");
        tool.Description.Should().Contain("nyxid_services");
        tool.Description.Should().NotContain("(no slug)");
        tool.ParametersSchema.Should().Contain("\"service\"");
        tool.ParametersSchema.Should().Contain("\"timeout_secs\"");
    }

    [Fact]
    public void ApprovalPolicy_AlwaysRequiresDurableGrant()
    {
        var tool = new NyxIdSshExecTool(CreateDummyClient());

        tool.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        tool.IsDestructive.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NoToken_ReturnsError()
    {
        var tool = new NyxIdSshExecTool(CreateDummyClient());
        AgentToolRequestContext.Current = null;

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
    public async Task ExecuteAsync_InvalidJson_ReturnsParseError()
    {
        var tool = new NyxIdSshExecTool(CreateDummyClient());
        SetMetadata("test-token");
        try
        {
            var result = await tool.ExecuteAsync("""{"service":""");

            result.Should().Contain("Failed to parse tool arguments");
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
    public async Task ExecuteAsync_AcceptsLegacySlugArgument()
    {
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, "/api/v1/keys/sg-alias",
            $$"""{"id":"u","slug":"sg-alias","catalog_service_id":"{{CatalogId}}"}""");
        handler.Map(HttpMethod.Post, $"/api/v1/ssh/{CatalogId}/exec", SshOk);

        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            var result = await tool.ExecuteAsync(
                """{"slug":"sg-alias","command":"whoami","principal":"ubuntu"}""");

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
    public async Task ExecuteAsync_LogsWarning_WhenListLookupFallbackCannotParse()
    {
        var rawCatalogId = "catalog-from-caller";
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, $"/api/v1/keys/{rawCatalogId}", """{"id":"u"}""");
        handler.Map(HttpMethod.Get, "/api/v1/keys", "not-json");
        handler.Map(HttpMethod.Post, $"/api/v1/ssh/{rawCatalogId}/exec", SshOk);
        var logger = new RecordingLogger();

        var tool = new NyxIdSshExecTool(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
                new HttpClient(handler)),
            logger: logger);
        SetMetadata("test-token");
        try
        {
            var result = await tool.ExecuteAsync(
                $$"""{"service":"{{rawCatalogId}}","command":"uname -a","principal":"ubuntu"}""");

            result.Should().Contain("\"exit_code\":0");
            logger.Entries.Should().Contain(entry =>
                entry.Level == LogLevel.Warning &&
                entry.Message.Contains("/keys list lookup failed", StringComparison.Ordinal));
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackToArrayListServices_WhenWrappedListDoesNotMatch()
    {
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, "/api/v1/keys/edge-router", """[]""");
        handler.Map(HttpMethod.Get, "/api/v1/keys",
            $$"""[42,{"id":"other","catalog_service_id":"ignored"},{"service_slug":"edge-router","catalog_service_id":"{{CatalogId}}"}]""");
        handler.Map(HttpMethod.Post, $"/api/v1/ssh/{CatalogId}/exec", SshOk);

        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            var result = await tool.ExecuteAsync(
                """{"service":"edge-router","command":"uptime","principal":"admin"}""");

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
    public async Task ExecuteAsync_FallsBackToRawCatalogId_WhenLookupsDoNotResolveCatalogId()
    {
        var rawCatalogId = "raw-catalog-id";
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, $"/api/v1/keys/{rawCatalogId}", """{"error":true}""");
        handler.Map(HttpMethod.Get, "/api/v1/keys", """{"keys":[{"slug":"other","catalog_service_id":"other-catalog"}]}""");
        handler.Map(HttpMethod.Post, $"/api/v1/ssh/{rawCatalogId}/exec", SshOk);

        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            var result = await tool.ExecuteAsync(
                $$"""{"service":"{{rawCatalogId}}","command":"hostname","principal":"ubuntu"}""");

            result.Should().Contain("\"exit_code\":0");
            handler.Recorded.Should().Contain(r =>
                r.Method == HttpMethod.Post && r.Path == $"/api/v1/ssh/{rawCatalogId}/exec");
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_HardTimesOut_WhenNyxIdHangsOnSshPost()
    {
        // Production incident 2026-05-08: NyxID's /api/v1/ssh/{id}/exec hung well past
        // the user-supplied timeout_secs, dragging the LLM run to its turn budget. The
        // tool now caps the wall-clock at timeout_secs + 15s and returns ssh_timeout so
        // the LLM can summarize a degraded but real answer rather than the runtime's
        // generic "took too long" fallback.
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, "/api/v1/keys/sg-office",
            $$"""{"id":"u","slug":"sg-office","catalog_service_id":"{{CatalogId}}"}""");
        handler.MapHanging(HttpMethod.Post, $"/api/v1/ssh/{CatalogId}/exec");

        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            // timeout_secs=1 → wall-clock cap = 1 + 15 = 16s. Use a very short timeout
            // so the test exits fast; the production cap is timeout_secs + 15 regardless.
            var result = await tool.ExecuteAsync(
                """{"service":"sg-office","command":"sleep 30","principal":"ubuntu","timeout_secs":1}""");

            result.Should().Contain("\"error\":\"ssh_timeout\"");
            result.Should().Contain("16s");
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackToRawCatalogId_WhenDirectLookupIsEmpty()
    {
        var rawCatalogId = "catalog-from-empty-direct";
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, $"/api/v1/keys/{rawCatalogId}", string.Empty);
        handler.Map(HttpMethod.Get, "/api/v1/keys", "{}");
        handler.Map(HttpMethod.Post, $"/api/v1/ssh/{rawCatalogId}/exec", SshOk);

        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            var result = await tool.ExecuteAsync(
                $$"""{"service":"{{rawCatalogId}}","command":"date","principal":"ubuntu"}""");

            result.Should().Contain("\"exit_code\":0");
            handler.Recorded.Should().Contain(r =>
                r.Method == HttpMethod.Post && r.Path == $"/api/v1/ssh/{rawCatalogId}/exec");
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackToRawCatalogId_WhenDirectLookupIsInvalidJson()
    {
        var rawCatalogId = "catalog-from-invalid-direct";
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, $"/api/v1/keys/{rawCatalogId}", "not-json");
        handler.Map(HttpMethod.Get, "/api/v1/keys", "{}");
        handler.Map(HttpMethod.Post, $"/api/v1/ssh/{rawCatalogId}/exec", SshOk);

        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            var result = await tool.ExecuteAsync(
                $$"""{"service":"{{rawCatalogId}}","command":"date","principal":"ubuntu"}""");

            result.Should().Contain("\"exit_code\":0");
            handler.Recorded.Should().Contain(r =>
                r.Method == HttpMethod.Post && r.Path == $"/api/v1/ssh/{rawCatalogId}/exec");
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_IgnoresBlankCatalogServiceIdFromMatchedListEntry()
    {
        var rawCatalogId = "catalog-from-blank-list-match";
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, $"/api/v1/keys/{rawCatalogId}", """{"id":"u"}""");
        handler.Map(HttpMethod.Get, "/api/v1/keys",
            $$"""{"keys":[{"slug":"{{rawCatalogId}}","catalog_service_id":""}]}""");
        handler.Map(HttpMethod.Post, $"/api/v1/ssh/{rawCatalogId}/exec", SshOk);

        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            var result = await tool.ExecuteAsync(
                $$"""{"service":"{{rawCatalogId}}","command":"date","principal":"ubuntu"}""");

            result.Should().Contain("\"exit_code\":0");
            handler.Recorded.Should().Contain(r =>
                r.Method == HttpMethod.Post && r.Path == $"/api/v1/ssh/{rawCatalogId}/exec");
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackToRawService_WhenListResponseIsInvalidJson()
    {
        var rawCatalogId = "catalog-from-caller";
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, $"/api/v1/keys/{rawCatalogId}", """{"id":"u"}""");
        handler.Map(HttpMethod.Get, "/api/v1/keys", "not-json");
        handler.Map(HttpMethod.Post, $"/api/v1/ssh/{rawCatalogId}/exec", SshOk);

        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            var result = await tool.ExecuteAsync(
                $$"""{"service":"{{rawCatalogId}}","command":"pwd","principal":"ubuntu"}""");

            result.Should().Contain("\"exit_code\":0");
            handler.Recorded.Should().Contain(r =>
                r.Method == HttpMethod.Post && r.Path == $"/api/v1/ssh/{rawCatalogId}/exec");
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsConfiguredBaseUrlError_AfterResolverLookupsFail()
    {
        var tool = new NyxIdSshExecTool(new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = null }));
        SetMetadata("test-token");
        try
        {
            var act = () => tool.ExecuteAsync(
                """{"service":"raw-catalog","command":"date","principal":"ubuntu"}""");

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("NyxID base URL is not configured.");
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_MatchesUnderscoreIdAndClampsTimeoutToMinimum()
    {
        var handler = new PathHandler();
        handler.Map(HttpMethod.Get, "/api/v1/keys/user-service-id", """{"id":"user-service-id"}""");
        handler.Map(HttpMethod.Get, "/api/v1/keys",
            $$"""{"keys":[{"_id":"user-service-id","catalog_service_id":"{{CatalogId}}"}]}""");
        handler.Map(HttpMethod.Post, $"/api/v1/ssh/{CatalogId}/exec", SshOk);

        var tool = new NyxIdSshExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        SetMetadata("test-token");
        try
        {
            await tool.ExecuteAsync(
                """{"service":"user-service-id","command":"id","principal":"ubuntu","timeout_secs":0}""");

            var exec = handler.Recorded.Last(r => r.Method == HttpMethod.Post);
            using var doc = JsonDocument.Parse(exec.Body!);
            doc.RootElement.GetProperty("timeout_secs").GetInt32().Should().Be(1);
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_DefaultsTimeoutWhenValueIsNotAnInteger()
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
                """{"service":"sg","command":"sleep 1","principal":"ubuntu","timeout_secs":"soon"}""");

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
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = token,
        });
    }

    private static void ClearMetadata() => AgentToolRequestContext.Current = null;

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Body, string? Authorization);

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class PathHandler : HttpMessageHandler
    {
        private readonly Dictionary<(HttpMethod Method, string Path), string> _routes = new();
        private readonly HashSet<(HttpMethod Method, string Path)> _hangingRoutes = new();
        public List<RecordedRequest> Recorded { get; } = new();

        public void Map(HttpMethod method, string path, string responseBody)
        {
            _routes[(method, path)] = responseBody;
        }

        /// <summary>
        /// Mark a route to "hang" — the handler awaits the cancellation token instead of
        /// returning a response, simulating a NyxID gateway that never replies. Used to
        /// pin the ssh_exec tool's hard wall-clock cap.
        /// </summary>
        public void MapHanging(HttpMethod method, string path)
        {
            _hangingRoutes.Add((method, path));
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

            if (_hangingRoutes.Contains((request.Method, path)))
            {
                // Block until the caller's wall-clock cap fires — exactly what the production
                // incident looked like (NyxID accepted the POST but never responded).
                var pendingResponse = new TaskCompletionSource<HttpResponseMessage>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var cancellationRegistration = cancellationToken.Register(() =>
                    pendingResponse.TrySetCanceled(cancellationToken));

                return await pendingResponse.Task;
            }

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
