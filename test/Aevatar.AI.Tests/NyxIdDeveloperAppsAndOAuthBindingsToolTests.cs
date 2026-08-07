using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdDeveloperAppsAndOAuthBindingsToolTests
{
    private static readonly string BindingHashAlpha = new('a', 64);
    private static readonly string BindingHashBeta = new('b', 64);

    public static TheoryData<string> InvalidBindingSelectors => new()
    {
        new string('A', 64),
        new string('a', 8),
        "bnd_" + new string('a', 64),
        new string('a', 64) + "-suffix",
        new string('a', 63),
        new string('a', 65),
        " " + new string('a', 64),
    };

    [Fact]
    public async Task DeveloperApps_ShouldUseExactRoutesAndProjectIndependentAllowlist()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri?.AbsolutePath.EndsWith("oauth-client%2Falpha", StringComparison.Ordinal) == true
                ? DeveloperAppJson("oauth-client-alpha")
                : $$"""
                  {"clients":[{{DeveloperAppJson("oauth-client-list")}}]}
                  """);
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client, "nyxid_developer_apps");
        using var _ = PushSourceReadableBearer();

        var list = await tool.ExecuteAsync("""{"action":"list","org_id":"org/alpha"}""");
        var show = await tool.ExecuteAsync(
            """{"action":"show","client_id":"oauth-client/alpha"}""");

        handler.Requests.Select(static request => request.PathAndQuery).Should().Equal(
            "/api/v1/developer/oauth-clients?org_id=org%2Falpha",
            "/api/v1/developer/oauth-clients/oauth-client%2Falpha");
        handler.Requests.Should().OnlyContain(static request =>
            request.Method == HttpMethod.Get && request.BearerToken == "source-readable-bearer");
        AssertDeveloperProjection(list, expectCollection: true);
        AssertDeveloperProjection(show, expectCollection: false);
    }

    [Fact]
    public async Task OAuthBindings_Show_ShouldGetListOnceAndExactFilterLocally()
    {
        var handler = new RecordingHandler(_ => BindingListJson(
            BindingJson(BindingHashAlpha, "oauth-client-alpha", "first-client-secret"),
            BindingJson(BindingHashBeta, "oauth-client-beta", "target-client-secret")));
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client, "nyxid_oauth_bindings");
        using var _ = PushSourceReadableBearer();

        var result = await tool.ExecuteAsync(
            $$"""{"action":"show","binding_hash":"{{BindingHashBeta}}"}""");

        handler.Requests.Should().ContainSingle().Which.Should().Be(new RecordedRequest(
            HttpMethod.Get,
            "/api/v1/users/me/broker-bindings",
            "source-readable-bearer"));
        handler.Requests.Should().NotContain(static request =>
            request.PathAndQuery.Contains("/api/v1/oauth/bindings/", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("binding_hash").GetString().Should().Be(BindingHashBeta);
        document.RootElement.GetProperty("client_id").GetString().Should().Be("oauth-client-beta");
        document.RootElement.EnumerateObject().Select(static property => property.Name).Should()
            .BeEquivalentTo(
                "binding_hash",
                "client_id",
                "external_subject",
                "scopes",
                "created_at",
                "last_used_at");
        document.RootElement.GetProperty("external_subject").EnumerateObject()
            .Select(static property => property.Name).Should()
            .BeEquivalentTo("platform", "tenant", "external_user_id");
        result.Should().NotMatchRegex(
            "(?i)(client_name|refresh|encrypted|cnf|revok|upstream|body|details|unknown|must-not-pass|target-client-secret)");
    }

    [Fact]
    public async Task OAuthBindings_List_ShouldUseUserScopedRouteAndProjectIndependentAllowlist()
    {
        var handler = new RecordingHandler(_ => BindingListJson(
            BindingJson(BindingHashAlpha, "oauth-client-alpha", "client-name-secret")));
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client, "nyxid_oauth_bindings");
        using var _ = PushSourceReadableBearer();

        var result = await tool.ExecuteAsync("""{"action":"list"}""");

        handler.Requests.Should().ContainSingle().Which.PathAndQuery.Should()
            .Be("/api/v1/users/me/broker-bindings");
        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("total").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("returned").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        var binding = document.RootElement.GetProperty("bindings").EnumerateArray().Single();
        binding.GetProperty("binding_hash").GetString().Should().Be(BindingHashAlpha);
        result.Should().NotMatchRegex(
            "(?i)(client_name|refresh|encrypted|cnf|revok|upstream|body|details|unknown|must-not-pass|client-name-secret)");
    }

    [Fact]
    public async Task OAuthBindings_List_ShouldAcceptNullableExternalSubjectTenant()
    {
        var binding = BindingJson(BindingHashAlpha, "oauth-client-alpha", "client-name-secret")
            .Replace("\"tenant\": \"tenant-alpha\"", "\"tenant\": null", StringComparison.Ordinal);
        var handler = new RecordingHandler(_ => BindingListJson(binding));
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client, "nyxid_oauth_bindings");
        using var _ = PushSourceReadableBearer();

        var result = await tool.ExecuteAsync("""{"action":"list"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("bindings").EnumerateArray().Single()
            .GetProperty("external_subject").GetProperty("tenant").ValueKind.Should()
            .Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task OAuthBindings_List_ShouldBoundResultsWhileShowScansTheFullCollection()
    {
        var hashes = Enumerable.Range(0, 21)
            .Select(static index => index.ToString("x64"))
            .ToArray();
        var response = BindingListJson(hashes
            .Select(hash => BindingJson(hash, $"oauth-client-{hash[^2..]}", "client-name-secret"))
            .ToArray());
        var handler = new RecordingHandler(_ => response);
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client, "nyxid_oauth_bindings");
        using var _ = PushSourceReadableBearer();

        var list = await tool.ExecuteAsync("""{"action":"list"}""");
        var show = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { action = "show", binding_hash = hashes[^1] }));

        using var listDocument = JsonDocument.Parse(list);
        listDocument.RootElement.GetProperty("bindings").GetArrayLength().Should().Be(20);
        listDocument.RootElement.GetProperty("total").GetInt32().Should().Be(21);
        listDocument.RootElement.GetProperty("returned").GetInt32().Should().Be(20);
        listDocument.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        list.Should().NotContain(hashes[^1]);
        using var showDocument = JsonDocument.Parse(show);
        showDocument.RootElement.GetProperty("binding_hash").GetString().Should().Be(hashes[^1]);
        handler.Requests.Should().HaveCount(2).And.OnlyContain(static request =>
            request.PathAndQuery == "/api/v1/users/me/broker-bindings");
    }

    [Fact]
    public async Task OAuthBindings_ShouldBoundSerializedListAndRejectOversizedShow()
    {
        var oversizedExternalUserId = new string('x', 40_000);
        var response = BindingListJson(
            BindingJson(BindingHashAlpha, "oauth-client-alpha", "client-name-secret")
                .Replace("external-user-alpha", oversizedExternalUserId, StringComparison.Ordinal));
        var handler = new RecordingHandler(_ => response);
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client, "nyxid_oauth_bindings");
        using var _ = PushSourceReadableBearer();

        var list = await tool.ExecuteAsync("""{"action":"list"}""");
        var show = await tool.ExecuteAsync(
            $$"""{"action":"show","binding_hash":"{{BindingHashAlpha}}"}""");

        using var listDocument = JsonDocument.Parse(list);
        listDocument.RootElement.GetProperty("bindings").GetArrayLength().Should().Be(0);
        listDocument.RootElement.GetProperty("total").GetInt32().Should().Be(1);
        listDocument.RootElement.GetProperty("returned").GetInt32().Should().Be(0);
        listDocument.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        System.Text.Encoding.UTF8.GetByteCount(list).Should().BeLessThan(32 * 1024);
        show.Should().Be("{\"error\":\"invalid_nyxid_response\"}");
    }

    [Theory]
    [MemberData(nameof(InvalidBindingSelectors))]
    public async Task OAuthBindings_Show_ShouldRejectNonCanonicalSelectorBeforeHttp(string selector)
    {
        var handler = new RecordingHandler();
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client, "nyxid_oauth_bindings");
        using var _ = PushSourceReadableBearer();

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { action = "show", binding_hash = selector }));

        result.Should().Be("{\"error\":\"invalid_arguments\"}");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(InvalidBindingSelectors))]
    public async Task OAuthBindings_List_ShouldRejectNonCanonicalUpstreamBindingHash(string bindingHash)
    {
        var handler = new RecordingHandler(_ => BindingListJson(
            BindingJson(bindingHash, "oauth-client-alpha", "client-name-secret")));
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client, "nyxid_oauth_bindings");
        using var _ = PushSourceReadableBearer();

        var result = await tool.ExecuteAsync("""{"action":"list"}""");

        result.Should().Be("{\"error\":\"invalid_nyxid_response\"}");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task OAuthBindings_Show_ShouldReturnDeterministicNotFound()
    {
        var handler = new RecordingHandler(_ => BindingListJson(
            BindingJson(BindingHashAlpha, "oauth-client-alpha", "client-name-secret")));
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client, "nyxid_oauth_bindings");
        using var _ = PushSourceReadableBearer();

        var result = await tool.ExecuteAsync(
            $$"""{"action":"show","binding_hash":"{{BindingHashBeta}}"}""");

        result.Should().Be("{\"error\":\"oauth_binding_not_found\"}");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task OAuthBindings_Show_ShouldRejectDuplicateExactMatch()
    {
        var handler = new RecordingHandler(_ => BindingListJson(
            BindingJson(BindingHashAlpha, "oauth-client-alpha", "client-name-one"),
            BindingJson(BindingHashAlpha, "oauth-client-beta", "client-name-two")));
        using var client = CreateClient(handler);
        var tool = await DiscoverToolAsync(client, "nyxid_oauth_bindings");
        using var _ = PushSourceReadableBearer();

        var result = await tool.ExecuteAsync(
            $$"""{"action":"show","binding_hash":"{{BindingHashAlpha}}"}""");

        result.Should().Be("{\"error\":\"invalid_nyxid_response\"}");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Tools_ShouldRejectWrongActionFieldsKindsAndSecretsBeforeHttp()
    {
        var handler = new RecordingHandler();
        using var client = CreateClient(handler);
        var developerApps = await DiscoverToolAsync(client, "nyxid_developer_apps");
        var bindings = await DiscoverToolAsync(client, "nyxid_oauth_bindings");
        using var _ = PushSourceReadableBearer();

        string[] results =
        [
            await developerApps.ExecuteAsync("""{"action":"list","client_id":"oauth-client-alpha"}"""),
            await developerApps.ExecuteAsync("""{"action":"show","client_id":"oauth-client-alpha","org_id":"org-alpha"}"""),
            await developerApps.ExecuteAsync("""{"action":"show","client_id":{"secret":"must-not-pass"}}"""),
            await developerApps.ExecuteAsync("""{"action":"list","client_secret":"must-not-pass"}"""),
            await bindings.ExecuteAsync($$"""{"action":"list","binding_hash":"{{BindingHashAlpha}}"}"""),
            await bindings.ExecuteAsync("""{"action":"show","binding_hash":{"secret":"must-not-pass"}}"""),
            await bindings.ExecuteAsync("""{"action":"list","refresh_token":"must-not-pass"}"""),
        ];

        results.Should().OnlyContain(static result =>
            result == "{\"error\":\"invalid_arguments\"}");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Tools_ShouldRequireSourceReadableBearerBeforeHttp()
    {
        var handler = new RecordingHandler();
        using var client = CreateClient(handler);
        var developerApps = await DiscoverToolAsync(client, "nyxid_developer_apps");
        var bindings = await DiscoverToolAsync(client, "nyxid_oauth_bindings");
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "proxy-delegation",
                null,
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation),
        });

        var developerResult = await developerApps.ExecuteAsync("""{"action":"list"}""");
        var bindingResult = await bindings.ExecuteAsync("""{"action":"list"}""");

        developerResult.Should().Contain("No source-readable NyxID user bearer");
        bindingResult.Should().Contain("No source-readable NyxID user bearer");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Tools_ShouldExposeClosedReadOnlyHumanSessionCatalogAndSuccessReceipts()
    {
        var handler = new RecordingHandler();
        using var client = CreateClient(handler);
        var tools = await new NyxIdAssistantToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            client).DiscoverToolsAsync();

        foreach (var name in new[] { "nyxid_developer_apps", "nyxid_oauth_bindings" })
        {
            var tool = tools.Single(candidate => candidate.Name == name);
            tool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
            tool.IsReadOnly.Should().BeTrue();
            tool.IsDestructive.Should().BeFalse();
            tool.Should().BeAssignableTo<IAgentToolCapabilityDescriptor>().Which.Capabilities
                .Should().Contain(AgentToolCapabilities.RequiresHumanSession);
            tool.ParametersSchema.Should().NotMatchRegex(
                "(?i)(authorization|api[-_]?key|token|secret|password|credential|cookie)");
            using var schema = JsonDocument.Parse(tool.ParametersSchema);
            schema.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
            tool.CreateResultReceipt("call-alpha", name, "{}", "{}")!.Status.Should()
                .Be(AgentToolReceiptStatus.Success);
        }

        using var bindingSchema = JsonDocument.Parse(
            tools.Single(static tool => tool.Name == "nyxid_oauth_bindings").ParametersSchema);
        bindingSchema.RootElement.GetProperty("properties").GetProperty("binding_hash")
            .GetProperty("pattern").GetString().Should().Be("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task Tools_ShouldStripUpstreamErrorsAndRejectMalformedAllowlistFields()
    {
        var response = """
            {
              "error": true,
              "status": 429,
              "retry_after_seconds": 7,
              "body": "{\"access_token\":\"must-not-pass\"}",
              "details": "https://secret.test"
            }
            """;
        var handler = new RecordingHandler(_ => response);
        using var client = CreateClient(handler);
        var developerApps = await DiscoverToolAsync(client, "nyxid_developer_apps");
        var bindings = await DiscoverToolAsync(client, "nyxid_oauth_bindings");
        using var _ = PushSourceReadableBearer();

        var developerError = await developerApps.ExecuteAsync("""{"action":"list"}""");
        var bindingError = await bindings.ExecuteAsync("""{"action":"list"}""");

        developerError.Should().Be("{\"error\":true,\"status\":429,\"retry_after_seconds\":7}");
        bindingError.Should().Be(developerError);
        developerError.Should().NotMatchRegex("(?i)(body|details|access_token|must-not-pass|secret\\.test)");

        handler.ResponseFactory = request =>
            request.RequestUri?.AbsolutePath.Contains("developer", StringComparison.Ordinal) == true
                ? "{\"clients\":[{\"id\":\"oauth-client-alpha\",\"allowed_scopes\":{\"secret\":\"must-not-pass\"}}]}"
                : $$"""{"bindings":[{"binding_hash":"{{BindingHashAlpha}}","client_id":"oauth-client-alpha","scopes":[{"secret":"must-not-pass"}]}]}""";
        (await developerApps.ExecuteAsync("""{"action":"list"}""")).Should()
            .Be("{\"error\":\"invalid_nyxid_response\"}");
        (await bindings.ExecuteAsync("""{"action":"list"}""")).Should()
            .Be("{\"error\":\"invalid_nyxid_response\"}");
    }

    private static string DeveloperAppJson(string id) => $$"""
        {
          "id": "{{id}}",
          "client_name": "arbitrary-client-name-secret",
          "client_type": "confidential",
          "redirect_uris": ["https://redirect.test/callback?secret=must-not-pass"],
          "allowed_scopes": "openid profile",
          "delegation_scopes": "account:read",
          "broker_capability_enabled": true,
          "revocation_webhook_url": "https://revocation.test/must-not-pass",
          "connection_webhook_url": "https://connection.test/must-not-pass",
          "connection_webhook_enabled": true,
          "is_active": true,
          "default_service_catalog_slugs": ["github"],
          "client_secret": "must-not-pass",
          "created_at": "2026-08-07T01:02:03Z",
          "unknown": {"secret": "must-not-pass"}
        }
        """;

    private static string BindingJson(string bindingHash, string clientId, string clientName) => $$"""
        {
          "binding_hash": "{{bindingHash}}",
          "client_id": "{{clientId}}",
          "client_name": "{{clientName}}",
          "external_subject": {
            "platform": "github",
            "tenant": "tenant-alpha",
            "external_user_id": "external-user-alpha",
            "refresh_token": "must-not-pass"
          },
          "scopes": ["openid", "account:read"],
          "created_at": "2026-08-07T01:02:03Z",
          "last_used_at": null,
          "refresh_token_jti": "must-not-pass",
          "refresh_token_encrypted": "must-not-pass",
          "cnf": {"jkt": "must-not-pass"},
          "revoked": false,
          "revoked_at": "must-not-pass",
          "revoke_reason": "must-not-pass",
          "upstream_body": "must-not-pass",
          "details": "must-not-pass",
          "unknown": {"secret": "must-not-pass"}
        }
        """;

    private static string BindingListJson(params string[] bindings) =>
        "{\"bindings\":[" + string.Join(',', bindings) + "]}";

    private static void AssertDeveloperProjection(string result, bool expectCollection)
    {
        using var document = JsonDocument.Parse(result);
        var client = expectCollection
            ? document.RootElement.GetProperty("clients").EnumerateArray().Single()
            : document.RootElement;
        client.EnumerateObject().Select(static property => property.Name).Should().BeEquivalentTo(
            "id",
            "client_type",
            "allowed_scopes",
            "delegation_scopes",
            "broker_capability_enabled",
            "connection_webhook_enabled",
            "is_active",
            "default_service_catalog_slugs",
            "created_at");
        result.Should().NotMatchRegex(
            "(?i)(client_name|client_secret|redirect|webhook_url|revocation|unknown|must-not-pass|arbitrary-client-name-secret)");
    }

    private static async Task<IAgentTool> DiscoverToolAsync(
        NyxIdApiClient client,
        string name) =>
        (await new NyxIdAssistantToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            client).DiscoverToolsAsync()).Single(tool => tool.Name == name);

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
