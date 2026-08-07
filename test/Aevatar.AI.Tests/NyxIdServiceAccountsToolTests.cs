using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdServiceAccountsToolTests
{
    [Fact]
    public async Task ListAndShow_ShouldUseExactRoutesAndProjectStrictAllowlist()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri?.AbsolutePath.EndsWith("sa%2Falpha", StringComparison.Ordinal) == true
                ? ServiceAccountJson("sa-show", rateLimitOverride: "null")
                : $$"""
                  {
                    "service_accounts": [{{ServiceAccountJson("sa-list", rateLimitOverride: "25")}}],
                    "total": 21,
                    "page": 2,
                    "per_page": 20,
                    "unknown": {"secret":"must-not-pass"}
                  }
                  """);
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client);
        using var _ = PushSourceReadableBearer();

        var list = await tool.ExecuteAsync(
            """{"action":"list","org_id":"org/alpha","page":2}""");
        var show = await tool.ExecuteAsync("""{"action":"show","sa_id":"sa/alpha"}""");

        handler.Requests.Select(static request => request.PathAndQuery).Should().Equal(
            "/api/v1/admin/service-accounts?page=2&per_page=20&org_id=org%2Falpha",
            "/api/v1/admin/service-accounts/sa%2Falpha");
        handler.Requests.Should().OnlyContain(static request =>
            request.Method == HttpMethod.Get && request.BearerToken == "source-readable-bearer");
        AssertProjection(list, expectCollection: true, expectedId: "sa-list");
        AssertProjection(show, expectCollection: false, expectedId: "sa-show");

        using var listDocument = JsonDocument.Parse(list);
        listDocument.RootElement.GetProperty("total").GetUInt64().Should().Be(21);
        listDocument.RootElement.GetProperty("page").GetUInt64().Should().Be(2);
        listDocument.RootElement.GetProperty("per_page").GetUInt64().Should().Be(20);
        listDocument.RootElement.GetProperty("returned").GetInt32().Should().Be(1);
        listDocument.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        listDocument.RootElement.GetProperty("service_accounts").EnumerateArray().Single()
            .GetProperty("rate_limit_override").GetUInt64().Should().Be(25);
        using var showDocument = JsonDocument.Parse(show);
        showDocument.RootElement.GetProperty("rate_limit_override").ValueKind.Should()
            .Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Tool_ShouldRejectWrongActionFieldsKindsAndSecretArgumentsBeforeHttp()
    {
        var handler = new RecordingHandler();
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client);
        using var _ = PushSourceReadableBearer();

        string[] results =
        [
            await tool.ExecuteAsync("""{"action":"list","sa_id":"sa-alpha"}"""),
            await tool.ExecuteAsync("""{"action":"show","sa_id":"sa-alpha","org_id":"org-alpha"}"""),
            await tool.ExecuteAsync("""{"action":"show","sa_id":{"secret":"must-not-pass"}}"""),
            await tool.ExecuteAsync("""{"action":"list","org_id":" "}"""),
            await tool.ExecuteAsync("""{"action":"list","page":0}"""),
            await tool.ExecuteAsync("""{"action":"list","page":"2"}"""),
            await tool.ExecuteAsync("""{"action":"show","sa_id":"sa-alpha","page":2}"""),
            await tool.ExecuteAsync("""{"action":"list","client_secret":"must-not-pass"}"""),
        ];

        results.Should().OnlyContain(static result =>
            result == "{\"error\":\"invalid_arguments\"}");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Show_ShouldRequireServiceAccountIdBeforeHttp()
    {
        var handler = new RecordingHandler();
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client);
        using var _ = PushSourceReadableBearer();

        var result = await tool.ExecuteAsync("""{"action":"show"}""");

        result.Should().Be("{\"error\":\"'sa_id' is required for show\"}");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Tool_ShouldRequireSourceReadableBearerBeforeHttp()
    {
        var handler = new RecordingHandler();
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client);
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "proxy-delegation",
                null,
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation),
        });

        var result = await tool.ExecuteAsync("""{"action":"list"}""");

        result.Should().Contain("No source-readable NyxID user bearer");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Tool_ShouldExposeClosedReadOnlyHumanSessionCatalogAndSuccessReceipt()
    {
        var handler = new RecordingHandler();
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client);

        tool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
        tool.IsReadOnly.Should().BeTrue();
        tool.IsDestructive.Should().BeFalse();
        tool.Should().BeAssignableTo<IAgentToolCapabilityDescriptor>().Which.Capabilities
            .Should().Contain(AgentToolCapabilities.RequiresHumanSession);
        tool.ParametersSchema.Should().NotMatchRegex(
            "(?i)(authorization|api[-_]?key|token|secret|password|credential|cookie)");
        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        schema.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        tool.CreateResultReceipt("call-alpha", tool.Name, "{}", "{}")!.Status.Should()
            .Be(AgentToolReceiptStatus.Success);
        var invalidResponseReceipt = tool.CreateResultReceipt(
            "call-beta",
            tool.Name,
            "{}",
            "{\"error\":\"invalid_nyxid_response\"}");
        invalidResponseReceipt.Should().NotBeNull();
        invalidResponseReceipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        invalidResponseReceipt.ErrorCode.Should().Be("nyxid_request_failed");
        invalidResponseReceipt.ResultJson.Should()
            .Be("{\"error\":\"nyxid_request_failed\",\"message\":\"The NyxID request failed.\"}");
    }

    [Fact]
    public async Task Tool_ShouldStripUpstreamErrorsAndRejectMalformedAllowlistFields()
    {
        var response = """
            {
              "error": true,
              "status": 403,
              "body": "{\"client_secret\":\"must-not-pass\"}",
              "details": "https://secret.test"
            }
            """;
        var handler = new RecordingHandler(_ => response);
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client);
        using var _ = PushSourceReadableBearer();

        var error = await tool.ExecuteAsync("""{"action":"list"}""");

        error.Should().Be("{\"error\":true,\"status\":403}");
        error.Should().NotMatchRegex("(?i)(body|details|client_secret|must-not-pass|secret\\.test)");

        handler.ResponseFactory = _ =>
            "{\"service_accounts\":[{\"id\":\"sa-alpha\",\"client_id\":\"client-alpha\",\"allowed_scopes\":\"account:read\",\"role_ids\":[{\"secret\":\"must-not-pass\"}],\"is_active\":true,\"rate_limit_override\":null,\"created_at\":\"2026-08-07T01:02:03Z\",\"updated_at\":\"2026-08-07T02:03:04Z\",\"last_authenticated_at\":null}],\"total\":1,\"page\":1,\"per_page\":20}";
        (await tool.ExecuteAsync("""{"action":"list"}""")).Should()
            .Be("{\"error\":\"invalid_nyxid_response\"}");

        handler.ResponseFactory = _ =>
            "{\"service_accounts\":[],\"total\":\"one\",\"page\":1,\"per_page\":20}";
        (await tool.ExecuteAsync("""{"action":"list"}""")).Should()
            .Be("{\"error\":\"invalid_nyxid_response\"}");

        handler.ResponseFactory = _ =>
            "{\"service_accounts\":[],\"total\":0,\"page\":0,\"per_page\":20}";
        (await tool.ExecuteAsync("""{"action":"list"}""")).Should()
            .Be("{\"error\":\"invalid_nyxid_response\"}");

        handler.ResponseFactory = _ =>
            "{\"service_accounts\":[],\"total\":0,\"page\":1,\"per_page\":0}";
        (await tool.ExecuteAsync("""{"action":"list"}""")).Should()
            .Be("{\"error\":\"invalid_nyxid_response\"}");

        handler.ResponseFactory = _ => $$"""
            {
              "service_accounts": [{{ServiceAccountJson("sa-impossible", "null")}}],
              "total": 0,
              "page": 1,
              "per_page": 20
            }
            """;
        (await tool.ExecuteAsync("""{"action":"list"}""")).Should()
            .Be("{\"error\":\"invalid_nyxid_response\"}");

        handler.ResponseFactory = _ =>
            "{\"service_accounts\":[],\"total\":0,\"page\":2,\"per_page\":20}";
        (await tool.ExecuteAsync("""{"action":"list"}""")).Should()
            .Be("{\"error\":\"invalid_nyxid_response\"}");

        var tooManyAccounts = Enumerable.Range(0, 21)
            .Select(index => ServiceAccountJson($"sa-overflow-{index:D2}", "null"));
        handler.ResponseFactory = _ => $$"""
            {
              "service_accounts": [{{string.Join(',', tooManyAccounts)}}],
              "total": 21,
              "page": 1,
              "per_page": 20
            }
            """;
        (await tool.ExecuteAsync("""{"action":"list"}""")).Should()
            .Be("{\"error\":\"invalid_nyxid_response\"}");
    }

    [Fact]
    public async Task Tool_ShouldBoundListCountAndBytesAndRejectOversizedShow()
    {
        var accounts = Enumerable.Range(0, 20)
            .Select(index => ServiceAccountJson($"sa-{index:D2}", rateLimitOverride: "null"))
            .ToArray();
        var listResponse = $$"""
            {
              "service_accounts": [{{string.Join(',', accounts)}}],
              "total": 21,
              "page": 1,
              "per_page": 20
            }
            """;
        var handler = new RecordingHandler(_ => listResponse);
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client);
        using var _ = PushSourceReadableBearer();

        var list = await tool.ExecuteAsync("""{"action":"list"}""");

        using var listDocument = JsonDocument.Parse(list);
        listDocument.RootElement.GetProperty("service_accounts").GetArrayLength().Should().Be(20);
        listDocument.RootElement.GetProperty("total").GetInt32().Should().Be(21);
        listDocument.RootElement.GetProperty("returned").GetInt32().Should().Be(20);
        listDocument.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();

        var oversizedRoleId = new string('x', 40_000);
        handler.ResponseFactory = request =>
        {
            var account = ServiceAccountJson("sa-oversized", rateLimitOverride: "null")
                .Replace("role-reader", oversizedRoleId, StringComparison.Ordinal);
            return request.RequestUri?.AbsolutePath.EndsWith("sa-oversized", StringComparison.Ordinal) == true
                ? account
                : $$"""
                  {
                    "service_accounts": [{{account}}],
                    "total": 1,
                    "page": 1,
                    "per_page": 20
                  }
                  """;
        };

        var oversizedList = await tool.ExecuteAsync("""{"action":"list"}""");
        var oversizedShow = await tool.ExecuteAsync(
            """{"action":"show","sa_id":"sa-oversized"}""");

        using var oversizedListDocument = JsonDocument.Parse(oversizedList);
        oversizedListDocument.RootElement.GetProperty("service_accounts").GetArrayLength()
            .Should().Be(0);
        oversizedListDocument.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        System.Text.Encoding.UTF8.GetByteCount(oversizedList).Should().BeLessThan(32 * 1024);
        oversizedShow.Should().Be("{\"error\":\"invalid_nyxid_response\"}");
    }

    private static string ServiceAccountJson(string id, string rateLimitOverride) => $$"""
        {
          "id": "{{id}}",
          "name": "sensitive-account-name",
          "description": "sensitive-account-description",
          "client_id": "client-{{id}}",
          "secret_prefix": "must-not-pass",
          "client_secret": "must-not-pass",
          "allowed_scopes": "account:read proxy:read",
          "role_ids": ["role-reader"],
          "is_active": true,
          "rate_limit_override": {{rateLimitOverride}},
          "created_by": "sensitive-creator-id",
          "created_at": "2026-08-07T01:02:03Z",
          "updated_at": "2026-08-07T02:03:04Z",
          "last_authenticated_at": null,
          "unknown": {"secret":"must-not-pass"}
        }
        """;

    private static void AssertProjection(string result, bool expectCollection, string expectedId)
    {
        using var document = JsonDocument.Parse(result);
        var serviceAccount = expectCollection
            ? document.RootElement.GetProperty("service_accounts").EnumerateArray().Single()
            : document.RootElement;
        serviceAccount.GetProperty("id").GetString().Should().Be(expectedId);
        serviceAccount.EnumerateObject().Select(static property => property.Name).Should()
            .BeEquivalentTo(
                "id",
                "client_id",
                "allowed_scopes",
                "role_ids",
                "is_active",
                "rate_limit_override",
                "created_at",
                "updated_at",
                "last_authenticated_at");
        result.Should().NotMatchRegex(
            "(?i)(name|description|secret_prefix|client_secret|created_by|unknown|must-not-pass|sensitive)");
    }

    private static async Task<IAgentTool> DiscoverToolAsync(NyxIdApiClient client) =>
        (await new NyxIdAssistantToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            client).DiscoverToolsAsync()).Single(tool => tool.Name == "nyxid_service_accounts");

    private static IDisposable PushSourceReadableBearer() =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "source-readable-bearer",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
        });

    private static NyxIdApiClient CreateClient(HttpMessageHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(handler));

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, string>? responseFactory = null) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        public Func<HttpRequestMessage, string> ResponseFactory { get; set; } =
            responseFactory ?? (_ => "{}");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.Parameter));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseFactory(request)),
            });
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string PathAndQuery,
        string? BearerToken);
}
