using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdAssistantToolSourceTests
{
    private static readonly string[] PinnedAssistantToolNames =
    [
        "nyxid_account",
        "nyxid_status",
        "nyxid_sessions",
        "nyxid_catalog",
        "nyxid_llm_status",
        "nyxid_require_service",
        "nyxid_proxy",
        "nyxid_profile",
        "nyxid_mfa",
        "nyxid_services",
        "nyxid_api_keys",
        "nyxid_nodes",
        "nyxid_node_credentials",
        "nyxid_service_pools",
        "nyxid_approvals",
        "nyxid_endpoints",
        "nyxid_external_keys",
        "nyxid_notifications",
        "nyxid_providers",
        "nyxid_orgs",
    ];

    private static readonly string[] ClosedEmptyArgumentToolNames =
    [
        "nyxid_account",
        "nyxid_status",
        "nyxid_sessions",
    ];

    private static readonly string[] ManagementReadToolNames =
    [
        "nyxid_profile",
        "nyxid_mfa",
        "nyxid_services",
        "nyxid_api_keys",
        "nyxid_nodes",
        "nyxid_node_credentials",
        "nyxid_service_pools",
        "nyxid_approvals",
        "nyxid_endpoints",
        "nyxid_external_keys",
        "nyxid_notifications",
        "nyxid_providers",
        "nyxid_orgs",
    ];

    [Fact]
    public async Task DiscoverToolsAsync_ShouldExposePinnedAssistantSurfaceOnly()
    {
        using var client = CreateClient(new RecordingHandler());
        var source = new NyxIdAssistantToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            client);

        var tools = await source.DiscoverToolsAsync();
        var names = tools.Select(static tool => tool.Name).ToArray();

        names.Should().Equal(PinnedAssistantToolNames);

        foreach (var name in ManagementReadToolNames)
        {
            var tool = tools.Single(candidate => candidate.Name == name);
            tool.IsReadOnly.Should().BeTrue();
            tool.IsDestructive.Should().BeFalse();
            tool.Should().BeAssignableTo<IAgentToolCapabilityDescriptor>()
                .Which.Capabilities.Should().Contain(AgentToolCapabilities.RequiresHumanSession);
            tool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
            tool.ParametersSchema.Should()
                .NotMatchRegex("(?i)(authorization|api[-_]?key|token|secret|password|credential|cookie)");
            using var schema = JsonDocument.Parse(tool.ParametersSchema);
            schema.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        }

        foreach (var name in ClosedEmptyArgumentToolNames)
        {
            var tool = tools.Single(candidate => candidate.Name == name);
            using var schema = JsonDocument.Parse(tool.ParametersSchema);
            schema.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
            tool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
        }

        using var servicesSchema = JsonDocument.Parse(
            tools.Single(static tool => tool.Name == "nyxid_services").ParametersSchema);
        servicesSchema.RootElement.GetProperty("properties").GetProperty("action")
            .GetProperty("enum").EnumerateArray().Select(static item => item.GetString())
            .Should().Equal("list", "show");
    }

    [Fact]
    public async Task ManagementReadTool_ShouldRejectWritesAndUndeclaredSecretFieldsBeforeHttp()
    {
        var handler = new RecordingHandler();
        using var client = CreateClient(handler);
        var source = new NyxIdAssistantToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            client);
        var services = (await source.DiscoverToolsAsync())
            .Single(static tool => tool.Name == "nyxid_services");

        var writeResult = await services.ExecuteAsync(
            """{"action":"delete","id":"service-alpha"}""");
        var secretResult = await services.ExecuteAsync(
            """{"action":"show","id":"service-alpha","credential":"must-not-pass"}""");

        writeResult.Should().Contain("not callable from the assistant");
        secretResult.Should().Contain("not callable from the assistant");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ManagementReadTool_ShouldCallPinnedReadApiWithRequestToken()
    {
        var handler = new RecordingHandler();
        using var client = CreateClient(handler);
        var source = new NyxIdAssistantToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            client);
        var services = (await source.DiscoverToolsAsync())
            .Single(static tool => tool.Name == "nyxid_services");

        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "request-token",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
        });
        var result = await services.ExecuteAsync(
            """{"action":"show","id":"service-alpha"}""");

        result.Should().Be("{}");
        handler.RequestCount.Should().Be(1);
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastPathAndQuery.Should().Be("/api/v1/keys/service-alpha");
        handler.LastBearerToken.Should().Be("request-token");
    }

    [Fact]
    public async Task NewParityReads_ShouldUseSourceBearerAndExactNyxIdRoutes()
    {
        var handler = new RecordingHandler(static request =>
            request.RequestUri?.AbsolutePath.Contains("credentials/pending", StringComparison.Ordinal) == true
                ? """{"pending_credentials":[]}"""
                : request.RequestUri?.AbsolutePath == "/api/v1/service-pools"
                    ? """{"pools":[]}"""
                    : """{"id":"pool-alpha","members":[]}""");
        using var client = CreateClient(handler);
        var source = new NyxIdAssistantToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            client);
        var tools = await source.DiscoverToolsAsync();

        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "proxy-delegation",
                null,
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation,
                "source-readable-bearer"),
        });

        await tools.Single(static tool => tool.Name == "nyxid_node_credentials")
            .ExecuteAsync("""{"action":"list","node_id":"node/alpha","include_history":true}""");
        await tools.Single(static tool => tool.Name == "nyxid_node_credentials")
            .ExecuteAsync("""{"node_id":"node-beta"}""");
        await tools.Single(static tool => tool.Name == "nyxid_service_pools")
            .ExecuteAsync("""{"action":"list","org_id":"org/alpha"}""");
        await tools.Single(static tool => tool.Name == "nyxid_service_pools")
            .ExecuteAsync("""{"action":"show","id":"pool/alpha"}""");

        handler.Requests.Select(static request => request.PathAndQuery).Should().Equal(
            "/api/v1/nodes/node%2Falpha/credentials/pending?include_history=true",
            "/api/v1/nodes/node-beta/credentials/pending",
            "/api/v1/service-pools?org_id=org%2Falpha",
            "/api/v1/service-pools/pool%2Falpha");
        handler.Requests.Should().OnlyContain(static request =>
            request.Method == HttpMethod.Get && request.BearerToken == "source-readable-bearer");
    }

    [Fact]
    public async Task NewParityReads_ShouldProjectOnlyPinnedMetadataFields()
    {
        var handler = new RecordingHandler(static request =>
            request.RequestUri?.AbsolutePath.Contains("credentials/pending", StringComparison.Ordinal) == true
                ? """
                  {
                    "pending_credentials": [{
                      "id": "pending-alpha",
                      "node_id": "node-alpha",
                      "service_slug": "calendar",
                      "is_active": true,
                      "target_url": "https://callback.test/path?token=query-secret#fragment-secret",
                      "label": "label-secret",
                      "created_by_user_id": "creator-secret",
                      "owner_user_id": "owner-secret",
                      "node_pubkey": "must-not-pass",
                      "ciphertext": "must-not-pass",
                      "secret": "must-not-pass"
                    }],
                    "secret": "must-not-pass"
                  }
                  """
                : """
                  {
                    "pools": [{
                      "id": "pool-alpha",
                      "slug": "primary",
                      "name": "sk-pool-secret",
                      "members": [{
                        "user_service_id": "service-alpha",
                        "weight": 1,
                        "enabled": true,
                        "secret": "must-not-pass"
                      }],
                      "secret": "must-not-pass"
                    }],
                    "secret": "must-not-pass"
                  }
                  """);
        using var client = CreateClient(handler);
        var tools = await new NyxIdAssistantToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            client).DiscoverToolsAsync();
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "source-readable-bearer",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
        });

        var credentials = await tools.Single(static tool => tool.Name == "nyxid_node_credentials")
            .ExecuteAsync("""{"node_id":"node-alpha"}""");
        var pools = await tools.Single(static tool => tool.Name == "nyxid_service_pools")
            .ExecuteAsync("""{"action":"list"}""");

        credentials.Should().Contain("pending-alpha").And.Contain("node-alpha");
        pools.Should().Contain("pool-alpha").And.Contain("service-alpha");
        credentials.Should().NotMatchRegex("(?i)(pubkey|ciphertext|secret|must-not-pass)");
        pools.Should().NotMatchRegex("(?i)(secret|must-not-pass)");
    }

    [Fact]
    public async Task NewParityReads_ShouldStripSensitiveUpstreamErrorDetails()
    {
        var handler = new RecordingHandler(_ => """
            {
              "error": true,
              "status": 403,
              "retry_after_seconds": 5,
              "body": "{\"error\":\"org_role_insufficient\",\"session_token\":\"must-not-pass\",\"approve_url\":\"https://secret.test\"}"
            }
            """);
        using var client = CreateClient(handler);
        var tools = await new NyxIdAssistantToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            client).DiscoverToolsAsync();
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "source-readable-bearer",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
        });

        var result = await tools.Single(static tool => tool.Name == "nyxid_service_pools")
            .ExecuteAsync("""{"action":"list"}""");

        result.Should().Be("{\"error\":true,\"status\":403,\"retry_after_seconds\":5}");
        result.Should().NotMatchRegex("(?i)(body|session_token|approve_url|must-not-pass|secret\\.test)");
    }

    [Fact]
    public async Task ManagementReads_ShouldFailClosedForRawDelegationAndUndeclaredArguments()
    {
        var handler = new RecordingHandler();
        using var client = CreateClient(handler);
        var source = new NyxIdAssistantToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            client);
        var tools = await source.DiscoverToolsAsync();

        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "proxy-delegation",
                null,
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation),
        });

        var accountResult = await tools.Single(static tool => tool.Name == "nyxid_account")
            .ExecuteAsync("{}");
        var poolResult = await tools.Single(static tool => tool.Name == "nyxid_service_pools")
            .ExecuteAsync("""{"action":"list"}""");
        var secretArgumentResult = await tools.Single(static tool => tool.Name == "nyxid_node_credentials")
            .ExecuteAsync("""{"action":"list","node_id":"node-alpha","credential":"must-not-pass"}""");
        var nonObjectResult = await tools.Single(static tool => tool.Name == "nyxid_service_pools")
            .ExecuteAsync("[]");
        var objectNodeIdResult = await tools.Single(static tool => tool.Name == "nyxid_node_credentials")
            .ExecuteAsync("""{"node_id":{"secret":"must-not-pass"}}""");
        var stringHistoryResult = await tools.Single(static tool => tool.Name == "nyxid_node_credentials")
            .ExecuteAsync("""{"node_id":"node-alpha","include_history":"true"}""");
        var listWithIdResult = await tools.Single(static tool => tool.Name == "nyxid_service_pools")
            .ExecuteAsync("""{"action":"list","id":"pool-alpha"}""");
        var showWithOrgResult = await tools.Single(static tool => tool.Name == "nyxid_service_pools")
            .ExecuteAsync("""{"action":"show","id":"pool-alpha","org_id":"org-alpha"}""");
        var accountWithSecretResult = await tools.Single(static tool => tool.Name == "nyxid_account")
            .ExecuteAsync("""{"secret":"must-not-pass"}""");
        var statusWithSecretResult = await tools.Single(static tool => tool.Name == "nyxid_status")
            .ExecuteAsync("""{"secret":"must-not-pass"}""");
        var sessionsWithSecretResult = await tools.Single(static tool => tool.Name == "nyxid_sessions")
            .ExecuteAsync("""{"secret":"must-not-pass"}""");

        accountResult.Should().Contain("No NyxID access token available");
        poolResult.Should().Contain("No source-readable NyxID user bearer");
        secretArgumentResult.Should().Contain("invalid_arguments");
        nonObjectResult.Should().Contain("invalid_arguments");
        objectNodeIdResult.Should().Contain("invalid_arguments");
        stringHistoryResult.Should().Contain("invalid_arguments");
        listWithIdResult.Should().Contain("invalid_arguments");
        showWithOrgResult.Should().Contain("invalid_arguments");
        accountWithSecretResult.Should().Contain("invalid_arguments");
        statusWithSecretResult.Should().Contain("invalid_arguments");
        sessionsWithSecretResult.Should().Contain("invalid_arguments");
        handler.RequestCount.Should().Be(0);
    }

    private static NyxIdApiClient CreateClient(HttpMessageHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(handler));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, string? BearerToken);

        public List<RecordedRequest> Requests { get; } = [];
        public int RequestCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastPathAndQuery { get; private set; }
        public string? LastBearerToken { get; private set; }

        private readonly Func<HttpRequestMessage, string> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, string>? responseFactory = null) =>
            _responseFactory = responseFactory ?? (_ => "{}");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            LastMethod = request.Method;
            LastPathAndQuery = request.RequestUri?.PathAndQuery;
            LastBearerToken = request.Headers.Authorization?.Parameter;
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.Parameter));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseFactory(request)),
            });
        }
    }
}
