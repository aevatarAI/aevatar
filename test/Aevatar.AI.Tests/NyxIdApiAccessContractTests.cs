using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdApiAccessContractTests
{
    [Fact]
    public async Task Client_ShouldUsePublishedUserServicesAndScopePlanRoutes()
    {
        var handler = new RecordingHandler();
        using var client = new NyxIdApiClient(
            new NyxIdToolOptions
            {
                BaseUrl = "http://nyxid.internal:3001/",
                InternalApiBaseUrl = "http://nyxid.internal:3001/",
                ApiBaseUrl = "https://nyx.example/",
            },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

        await client.ListUserServicesAsync("bearer-secret", CancellationToken.None);
        await client.PlanApiKeyScopeAsync(
            "bearer-secret",
            ["service-a", "service-b"],
            "org-alpha",
            CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Uri.Should().Be("https://nyx.example/api/v1/user-services");
        handler.Requests[0].Authorization.Should().Be("Bearer bearer-secret");
        handler.Requests[0].Body.Should().BeNull();

        handler.Requests[1].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].Uri.Should().Be("https://nyx.example/api/v1/api-keys/scope-plan");
        handler.Requests[1].Authorization.Should().Be("Bearer bearer-secret");
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        body.RootElement.GetProperty("selected_service_ids")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .Should()
            .Equal("service-a", "service-b");
        body.RootElement.GetProperty("target_org_id").GetString().Should().Be("org-alpha");
    }

    [Fact]
    public async Task Client_WithSplitHosts_ShouldKeepControlPlanePublicAndTransportCallsInternal()
    {
        var handler = new RecordingHandler();
        using var client = new NyxIdApiClient(
            new NyxIdToolOptions
            {
                BaseUrl = "http://nyxid.internal:3001/transport/",
                InternalApiBaseUrl = "http://nyxid.internal:3001/transport/",
                ApiBaseUrl = "https://nyx.example/public/",
                PublicTransportFallbackBaseUrl = "https://nyx.example/public/",
            },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

        await client.GetCurrentUserAsync("token", CancellationToken.None);
        await client.ListCatalogAsync("token", CancellationToken.None);
        await client.CreateServiceAsync("token", """{"slug":"calendar"}""", CancellationToken.None);
        await client.DeleteServiceAsync("token", "service-calendar", CancellationToken.None);
        await client.GetLlmServicesAsync("token", CancellationToken.None);
        await client.GetLlmRouteModelsBoundedAsync(
            "token",
            Aevatar.AI.Abstractions.LLMRouteKind.Gateway,
            verifiedUserServiceId: null,
            verifiedServiceSlug: null,
            maxBytes: 1024,
            ct: CancellationToken.None);
        await client.ProxyRequestAsync(
            "token",
            "calendar",
            "/v1/events",
            HttpMethod.Get.Method,
            body: null,
            extraHeaders: null,
            ct: CancellationToken.None);
        await client.SshExecAsync(
            "token",
            "ssh-service",
            """{"command":"true"}""",
            CancellationToken.None);

        handler.Requests.Select(static request => request.Uri).Should().Equal(
            "https://nyx.example/public/api/v1/users/me",
            "https://nyx.example/public/api/v1/catalog",
            "https://nyx.example/public/api/v1/keys",
            "https://nyx.example/public/api/v1/keys/service-calendar",
            "https://nyx.example/public/api/v1/llm/services",
            "https://nyx.example/public/api/v1/llm/gateway/v1/models",
            "http://nyxid.internal:3001/transport/api/v1/proxy/s/calendar/v1/events",
            "http://nyxid.internal:3001/transport/api/v1/ssh/ssh-service/exec");
    }

    [Fact]
    public async Task PlanApiKeyScopeAsync_WhenTargetOrganizationIsAbsent_ShouldOmitOptionalField()
    {
        var handler = new RecordingHandler();
        using var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

        await client.PlanApiKeyScopeAsync(
            "token",
            ["service-a"],
            null,
            CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Requests.Single().Body!);
        body.RootElement.TryGetProperty("target_org_id", out _).Should().BeFalse();
    }

    [Fact]
    public void ParseUserServices_ShouldMapPublishedCredentialSourceUnionAndIgnoreUnrelatedFields()
    {
        const string response = """
            {
              "services": [
                {
                  "id": "service-personal",
                  "slug": "api-github",
                  "label": "GitHub",
                  "catalog_service_name": "GitHub API",
                  "catalog_service_id": "catalog-github",
                  "is_active": true,
                  "forward_access_token": false,
                  "inject_delegation_token": true,
                  "delegation_token_scope": "sandbox:execute",
                  "credential_source": { "type": "personal" },
                  "default_model": "gpt-5.5",
                  "endpoint_id": "ignored"
                },
                {
                  "id": "service-org",
                  "slug": "api-linear",
                  "label": null,
                  "is_active": false,
                  "defaultModel": "claude-opus-4-6",
                  "credential_source": {
                    "type": "org",
                    "org_id": "org-alpha",
                    "org_name": "Alpha",
                    "avatar_url": null,
                    "role": "admin",
                    "allowed": true
                  }
                }
              ],
              "future_field": true
            }
            """;

        var result = NyxIdApiAccessResponseParser.ParseUserServices(response);

        result.Succeeded.Should().BeTrue();
        result.Failure.Should().BeNull();
        result.Value!.Services.Should().HaveCount(2);
        result.Value.Services[0].Should().BeEquivalentTo(new NyxIdUserService(
            "service-personal",
            "api-github",
            "GitHub",
            "GitHub API",
            true,
            new NyxIdUserServiceCredentialSource(
                NyxIdUserServiceCredentialSourceKind.Personal),
            "gpt-5.5"));
        result.Value.Services[1].CredentialSource.Should().BeEquivalentTo(
            new NyxIdUserServiceCredentialSource(
                NyxIdUserServiceCredentialSourceKind.Organization,
                "org-alpha",
                "Alpha",
                null,
                NyxIdOrganizationRole.Admin,
                true));
        result.Value.Services[1].DefaultModel.Should().Be("claude-opus-4-6");
    }

    [Fact]
    public void ParseUserServiceRoutes_ShouldMapRouteContractWithoutChangingOrdinaryParser()
    {
        const string response = """
            {
              "services": [{
                "id": "service-code",
                "slug": "chrono-sandbox",
                "catalog_service_id": "catalog-chrono-sandbox",
                "is_active": true,
                "forward_access_token": false,
                "inject_delegation_token": true,
                "delegation_token_scope": "sandbox:execute",
                "credential_source": { "type": "personal" }
              }]
            }
            """;

        var ordinary = NyxIdApiAccessResponseParser.ParseUserServices(response);
        var routes = NyxIdApiAccessResponseParser.ParseUserServiceRoutes(response);

        ordinary.Succeeded.Should().BeTrue();
        ordinary.Value!.Services.Single().Should().BeEquivalentTo(new
        {
            CatalogServiceId = (string?)null,
            ForwardAccessToken = (bool?)null,
            InjectDelegationToken = (bool?)null,
            DelegationTokenScope = (string?)null,
        });
        routes.Succeeded.Should().BeTrue();
        routes.Value!.Services.Single().Should().BeEquivalentTo(new
        {
            CatalogServiceId = "catalog-chrono-sandbox",
            ForwardAccessToken = (bool?)false,
            InjectDelegationToken = (bool?)true,
            DelegationTokenScope = "sandbox:execute",
            AutoConnected = false,
        });
    }

    [Fact]
    public void ParseUserServiceRoutes_ShouldIgnorePhantomAutoConnected()
    {
        const string response = """
            {
              "services": [{
                "id": "service-code",
                "slug": "chrono-sandbox",
                "catalog_service_id": "catalog-chrono-sandbox",
                "is_active": true,
                "auto_connected": true,
                "forward_access_token": true,
                "inject_delegation_token": true,
                "delegation_token_scope": "proxy:*",
                "credential_source": { "type": "personal" }
              }]
            }
            """;

        var routes = NyxIdApiAccessResponseParser.ParseUserServiceRoutes(response);

        routes.Succeeded.Should().BeTrue();
        routes.Value!.Services.Single().AutoConnected.Should().BeFalse();
    }

    [Fact]
    public void ParseUserServiceKeys_ShouldMapAutoConnectedOwnership()
    {
        const string response = """
            {
              "keys": [{
                "id": "service-code",
                "slug": "chrono-sandbox",
                "catalog_service_id": "catalog-chrono-sandbox",
                "catalog_service_slug": "chrono-sandbox",
                "status": "active",
                "is_active": true,
                "connected": true,
                "auto_connected": true,
                "credential_source": { "type": "personal" }
              }]
            }
            """;

        var result = NyxIdApiAccessResponseParser.ParseUserServiceKeys(response);

        result.Succeeded.Should().BeTrue();
        result.Value!.Services.Single().AutoConnected.Should().BeTrue();
    }

    [Fact]
    public void ParseUserServices_WhenCodeExecutionOnlyFieldIsMalformed_ShouldRemainCompatible()
    {
        const string response = """
            {"services":[{
              "id":"service-github",
              "slug":"api-github",
              "is_active":true,
              "forward_access_token":"invalid",
              "credential_source":{"type":"personal"}
            }]}
            """;

        NyxIdApiAccessResponseParser.ParseUserServices(response).Succeeded.Should().BeTrue();
        NyxIdApiAccessResponseParser.ParseUserServiceRoutes(response).Succeeded
            .Should().BeFalse();
    }

    [Fact]
    public void ParseUserServices_ShouldReadPublishedUnderscoreIdIdentity()
    {
        const string response = """
            {"services":[{"_id":"service-a","slug":"api-a","is_active":true,"credential_source":{"type":"personal"}}]}
            """;

        var result = NyxIdApiAccessResponseParser.ParseUserServices(response);

        result.Succeeded.Should().BeTrue();
        result.Value!.Services.Should().ContainSingle()
            .Which.Id.Should().Be("service-a");
    }

    [Fact]
    public void ParseUserServiceKeys_ShouldMapPublishedExecutionAndAuthorityFacts()
    {
        const string response = """
            {
              "keys": [
                {
                  "id": "service-direct",
                  "slug": "api-github",
                  "catalog_service_id": "catalog-api-github",
                  "catalog_service_slug": "api-github",
                  "label": "GitHub",
                  "catalog_service_name": "GitHub API",
                  "status": "active",
                  "is_active": true,
                  "credential_source": { "type": "personal" },
                  "endpoint_id": "endpoint-direct",
                  "endpoint_url": "https://example.invalid",
                  "connected": true
                },
                {
                  "id": "service-node",
                  "slug": "api-linear",
                  "label": "Linear",
                  "catalog_service_name": null,
                  "status": "pending_auth",
                  "is_active": true,
                  "node_id": "node-alpha",
                  "node_status": "online",
                  "credential_source": {
                    "type": "org",
                    "org_id": "org-alpha",
                    "org_name": "Alpha",
                    "avatar_url": null,
                    "role": "member",
                    "allowed": true
                  },
                  "endpoint_id": "endpoint-node",
                  "endpoint_url": "https://node.invalid",
                  "connected": true
                }
              ],
              "future_field": true
            }
            """;

        var result = NyxIdApiAccessResponseParser.ParseUserServiceKeys(response);

        result.Succeeded.Should().BeTrue();
        result.Failure.Should().BeNull();
        result.Value!.Services.Should().Equal(
            new NyxIdUserServiceKey(
                "service-direct",
                "api-github",
                "GitHub",
                "GitHub API",
                true,
                NyxIdUserServiceCredentialStatus.Active,
                null,
                NyxIdUserServiceNodeStatus.NotBound,
                new NyxIdUserServiceCredentialSource(
                    NyxIdUserServiceCredentialSourceKind.Personal),
                "catalog-api-github",
                "api-github",
                true),
            new NyxIdUserServiceKey(
                "service-node",
                "api-linear",
                "Linear",
                null,
                true,
                NyxIdUserServiceCredentialStatus.PendingAuthorization,
                "node-alpha",
                NyxIdUserServiceNodeStatus.Online,
                new NyxIdUserServiceCredentialSource(
                    NyxIdUserServiceCredentialSourceKind.Organization,
                    "org-alpha",
                    "Alpha",
                    null,
                    NyxIdOrganizationRole.Member,
                    true),
                null,
                null,
                true));
    }

    [Fact]
    public void ParseUserServiceAuthorization_ShouldPreserveExactScopeAndAuthorizationEvidence()
    {
        const string response = """
            {
              "id": "service-alpha",
              "api_key_id": "credential-alpha",
              "status": "active",
              "is_active": true,
              "connected": true,
              "connection_status": "active",
              "granted_scopes": ["read:user", "repo"],
              "last_authorized_at": "2026-08-10T07:00:00Z"
            }
            """;

        var result = NyxIdApiAccessResponseParser.ParseUserServiceAuthorization(response);

        result.Succeeded.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new NyxIdUserServiceAuthorizationEvidence(
            "service-alpha",
            "credential-alpha",
            true,
            NyxIdUserServiceCredentialStatus.Active,
            NyxIdOAuthConnectionStatus.Active,
            ["read:user", "repo"],
            DateTimeOffset.Parse("2026-08-10T07:00:00Z"),
            null));
    }

    [Fact]
    public void ParseUserServiceAuthorization_ProjectionContract_ShouldPreserveMonotonicStateVersion()
    {
        const string response = """
            {
              "id": "service-alpha",
              "api_key_id": "credential-alpha",
              "status": "active",
              "is_active": true,
              "connection_status": "active",
              "granted_scopes": ["repo"],
              "last_authorized_at": "2026-08-10T07:00:00Z",
              "node_id": null,
              "rotation_predecessor_id": null,
              "state_version": 7,
              "updated_at": "2026-08-10T07:00:05Z"
            }
            """;

        var result = NyxIdApiAccessResponseParser.ParseUserServiceAuthorization(response);

        result.Succeeded.Should().BeTrue();
        result.Value!.StateVersion.Should().Be(7);

        var zeroVersion = NyxIdApiAccessResponseParser.ParseUserServiceAuthorization(
            response.Replace("\"state_version\": 7", "\"state_version\": 0"));
        zeroVersion.Succeeded.Should().BeFalse();
        zeroVersion.Failure!.Code.Should().Be(
            "nyxid_user_service_authorization_response_malformed");
    }

    [Theory]
    [InlineData("\"expired\"", NyxIdOAuthConnectionStatus.Expired)]
    [InlineData("null", NyxIdOAuthConnectionStatus.Unspecified)]
    public void ParseUserServiceAuthorization_ShouldPreserveNonActiveConnectionStatus(
        string connectionStatus,
        NyxIdOAuthConnectionStatus expected)
    {
        var response = $$"""
            {
              "id": "service-alpha",
              "api_key_id": "credential-alpha",
              "status": "active",
              "is_active": true,
              "connected": true,
              "connection_status": {{connectionStatus}},
              "granted_scopes": ["repo"],
              "last_authorized_at": "2026-08-10T07:00:00Z"
            }
            """;

        var result = NyxIdApiAccessResponseParser.ParseUserServiceAuthorization(response);

        result.Succeeded.Should().BeTrue();
        result.Value!.OAuthConnectionStatus.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"connection_status\":\"unknown\"")]
    public void ParseUserServiceAuthorization_WithoutTypedConnectionStatus_ShouldFailClosed(
        string injectedConnectionStatus)
    {
        var response = $$"""
            {
              "id": "service-alpha",
              "api_key_id": "credential-alpha",
              "status": "active",
              "is_active": true,
              "connected": true,
              "granted_scopes": ["repo"],
              "last_authorized_at": "2026-08-10T07:00:00Z"
              {{injectedConnectionStatus}}
            }
            """;

        var result = NyxIdApiAccessResponseParser.ParseUserServiceAuthorization(response);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(new NyxIdApiAccessFailure(
            NyxIdApiAccessFailureKind.MalformedResponse,
            "nyxid_user_service_authorization_response_malformed"));
    }

    [Fact]
    public void ParseAgentApiKey_CurrentReadContract_ShouldPreserveFactsWithoutInventingLineage()
    {
        const string response = """
            {
              "id": "key-alpha",
              "name": "Codex Key",
              "scopes": "proxy account:read",
              "platform": "codex",
              "is_active": true,
              "allowed_service_ids": ["service-alpha"],
              "allow_all_services": false,
              "allowed_node_ids": [],
              "allow_all_nodes": false,
              "created_at": "2026-08-10T07:00:00Z",
              "future_safe_field": { "authority_source": "api_keys" },
              "note": "Bearer",
              "short_key_hint": "nyxid_ag_short"
            }
            """;

        var result = NyxIdApiAccessResponseParser.ParseAgentApiKey(response);

        result.Succeeded.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new NyxIdAgentApiKeyEvidence(
            "key-alpha",
            ["proxy", "account:read"],
            "codex",
            true,
            ["service-alpha"],
            false,
            [],
            false,
            DateTimeOffset.Parse("2026-08-10T07:00:00Z"),
            null));
    }

    [Fact]
    public void ParseAgentApiKey_ProjectionDisplayName_ShouldBeExemptFromScanAndNeverRead()
    {
        // The authorization projection intentionally retains the display name;
        // a user naming their key "Bearer Bot" must not fail the evidence read,
        // and the name never appears in the typed evidence.
        const string response = """
            {
              "id": "key-alpha",
              "name": "Bearer Bot",
              "scopes": "proxy",
              "platform": "codex",
              "is_active": true,
              "allowed_service_ids": ["service-alpha"],
              "allow_all_services": false,
              "allowed_node_ids": [],
              "allow_all_nodes": false,
              "created_at": "2026-08-10T07:00:00Z"
            }
            """;

        var result = NyxIdApiAccessResponseParser.ParseAgentApiKey(response);

        result.Succeeded.Should().BeTrue();
        result.Value!.Id.Should().Be("key-alpha");
        typeof(NyxIdAgentApiKeyEvidence).GetProperties()
            .Select(static property => property.Name)
            .Should().NotContain("Name");
    }

    [Fact]
    public void ParseAgentApiKey_LineageWithNullUpdatedAt_ShouldKeepStateVersionAuthoritative()
    {
        // The api-key projection wraps updated_at as an inner-nullable; a
        // lineage row without an update timestamp keeps its monotonic version.
        const string response = """
            {
              "id": "key-beta",
              "name": "Codex Key",
              "scopes": "proxy",
              "platform": "codex",
              "is_active": true,
              "allowed_service_ids": [],
              "allow_all_services": false,
              "allowed_node_ids": [],
              "allow_all_nodes": false,
              "created_at": "2026-08-10T07:00:00Z",
              "rotation_predecessor_id": "key-alpha",
              "state_version": 2,
              "updated_at": null
            }
            """;

        var result = NyxIdApiAccessResponseParser.ParseAgentApiKey(response);

        result.Succeeded.Should().BeTrue();
        result.Value!.VersionEvidence.Should().Be(new NyxIdApiKeyVersionEvidence(
            "key-alpha",
            2,
            null));
    }

    [Fact]
    public void ParseAgentApiKey_DirectCreateVersionContract_ShouldAcceptNullPredecessor()
    {
        const string response = """
            {
              "id": "key-alpha",
              "name": "Codex Key",
              "scopes": "proxy",
              "platform": "codex",
              "is_active": true,
              "allowed_service_ids": ["service-alpha"],
              "allow_all_services": false,
              "allowed_node_ids": [],
              "allow_all_nodes": false,
              "created_at": "2026-08-10T07:00:00Z",
              "rotation_predecessor_id": null,
              "state_version": 1,
              "updated_at": "2026-08-10T07:00:00Z"
            }
            """;

        var result = NyxIdApiAccessResponseParser.ParseAgentApiKey(response);

        result.Succeeded.Should().BeTrue();
        result.Value!.VersionEvidence.Should().Be(new NyxIdApiKeyVersionEvidence(
            null,
            1,
            DateTimeOffset.Parse("2026-08-10T07:00:00Z")));
    }

    [Fact]
    public void ParseAgentApiKey_VersionedRotationContract_ShouldPreserveTypedLineage()
    {
        const string response = """
            {
              "id": "key-beta",
              "name": "Codex Key",
              "scopes": "proxy",
              "platform": "codex",
              "is_active": true,
              "allowed_service_ids": [],
              "allow_all_services": false,
              "allowed_node_ids": [],
              "allow_all_nodes": false,
              "created_at": "2026-08-10T07:00:00Z",
              "rotation_predecessor_id": "key-alpha",
              "state_version": 2,
              "updated_at": "2026-08-10T07:00:01Z"
            }
            """;

        var result = NyxIdApiAccessResponseParser.ParseAgentApiKey(response);

        result.Succeeded.Should().BeTrue();
        result.Value!.VersionEvidence.Should().Be(new NyxIdApiKeyVersionEvidence(
            "key-alpha",
            2,
            DateTimeOffset.Parse("2026-08-10T07:00:01Z")));
    }

    [Theory]
    [InlineData("\"full_key\":\"nyxid_ag_secret\",")]
    [InlineData("\"ignored\":{\"access_token\":\"nested-secret\"},")]
    [InlineData("\"ignored\":{\"AccessToken\":\"nested-secret\"},")]
    [InlineData("\"Authorization\":\"Bearer nested-secret\",")]
    [InlineData("\"note\":\"Bearer secret-in-innocuous-field\",")]
    [InlineData("\"note\":\"nyxid_ag_1234567890abcdef\",")]
    [InlineData("\"ignored\":{\"note\":\"Bearer nested-secret-value\"},")]
    [InlineData("\"ignored\":{\"name\":\"Bearer nested-name-value\"},")]
    [InlineData("\"ignored\":[\"safe\",\"nyxid_ag_1234567890abcdef\"],")]
    [InlineData("\"api_key\":\"nested-secret\",")]
    [InlineData("\"token\":\"nested-secret\",")]
    [InlineData("\"rotation_predecessor_id\":\"key-old\",")]
    public void ParseAgentApiKey_SecretOrPartialLineage_ShouldFailClosed(string injectedField)
    {
        var response = $$"""
            {
              {{injectedField}}
              "id": "key-alpha",
              "name": "Codex Key",
              "scopes": "proxy",
              "is_active": true,
              "allowed_service_ids": [],
              "allow_all_services": false,
              "allowed_node_ids": [],
              "allow_all_nodes": false,
              "created_at": "2026-08-10T07:00:00Z"
            }
            """;

        var result = NyxIdApiAccessResponseParser.ParseAgentApiKey(response);

        result.Succeeded.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Failure.Should().Be(new NyxIdApiAccessFailure(
            NyxIdApiAccessFailureKind.MalformedResponse,
            "nyxid_agent_api_key_response_malformed"));
        result.ToString().Should().NotContain("nyxid_ag_secret");
    }

    [Fact]
    public async Task Client_ExactActionEvidenceReads_ShouldUseAuthorizationProjectionRoutes()
    {
        var handler = new RecordingHandler();
        using var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example/" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

        await client.GetServiceAuthorizationAsync("bearer-secret", "service-alpha", CancellationToken.None);
        await client.GetApiKeyAuthorizationAsync("bearer-secret", "key-alpha", CancellationToken.None);

        handler.Requests.Select(static request => (request.Method, request.Uri)).Should().Equal(
            (HttpMethod.Get, "https://nyx.example/api/v1/keys/service-alpha/authorization"),
            (HttpMethod.Get, "https://nyx.example/api/v1/api-keys/key-alpha/authorization"));
        handler.Requests.Should().OnlyContain(static request =>
            request.Authorization == "Bearer bearer-secret" && request.Body == null);
    }

    [Fact]
    public async Task EvidenceReadPort_ShouldReadOnlySecretFreeAuthorizationProjections()
    {
        var handler = new RoutedRecordingHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v1/keys/service-alpha/authorization"] = """
                {
                  "id": "service-alpha",
                  "api_key_id": "credential-alpha",
                  "status": "active",
                  "is_active": true,
                  "connection_status": "active",
                  "granted_scopes": ["repo"],
                  "last_authorized_at": "2026-08-10T07:00:00Z",
                  "node_id": null,
                  "rotation_predecessor_id": null,
                  "state_version": 7,
                  "updated_at": "2026-08-10T07:00:05Z"
                }
                """,
            ["/api/v1/api-keys/key-alpha/authorization"] = """
                {
                  "id": "key-alpha",
                  "name": "Bearer Bot",
                  "scopes": "proxy",
                  "platform": "codex",
                  "is_active": true,
                  "allowed_service_ids": ["service-alpha"],
                  "allow_all_services": false,
                  "allowed_node_ids": [],
                  "allow_all_nodes": false,
                  "created_at": "2026-08-10T07:00:00Z"
                }
                """,
        });
        var port = new NyxIdActionEvidenceReadPort(new StaticApiClientFactory(handler));

        var service = await port.GetUserServiceAuthorizationAsync(
            "bearer-secret",
            "service-alpha",
            CancellationToken.None);
        var key = await port.GetAgentApiKeyAsync(
            "bearer-secret",
            "key-alpha",
            CancellationToken.None);

        service.Succeeded.Should().BeTrue();
        service.Value!.StateVersion.Should().Be(7);
        key.Succeeded.Should().BeTrue();
        key.Value!.Id.Should().Be("key-alpha");
        handler.RequestPaths.Should().Equal(
            "/api/v1/keys/service-alpha/authorization",
            "/api/v1/api-keys/key-alpha/authorization");
    }

    [Theory]
    [MemberData(nameof(MalformedUserServiceKeys))]
    public void ParseUserServiceKeys_ShouldRejectMalformedExecutionInventory(string response)
    {
        var result = NyxIdApiAccessResponseParser.ParseUserServiceKeys(response);

        result.Succeeded.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Failure.Should().Be(new NyxIdApiAccessFailure(
            NyxIdApiAccessFailureKind.MalformedResponse,
            "nyxid_user_service_keys_response_malformed"));
    }

    [Fact]
    public void ParseScopePlan_ShouldMapCompletePublishedContract()
    {
        var result = NyxIdApiAccessResponseParser.ParseScopePlan(ValidScopePlanJson());

        result.Succeeded.Should().BeTrue();
        result.Failure.Should().BeNull();
        var plan = result.Value!;
        plan.Authority.Should().Be("nyxid");
        plan.ContractVersion.Should().Be("1");
        plan.PolicyVersion.Should().Be("api-key-scope-v1");
        plan.AuthenticatedActor.Should().Be(new NyxIdScopePlanPrincipal(
            "actor-alpha",
            NyxIdScopePlanPrincipalKind.Personal));
        plan.IntendedKeyOwner.Should().Be(new NyxIdScopePlanPrincipal(
            "org-alpha",
            NyxIdScopePlanPrincipalKind.Organization));
        plan.Services.Should().HaveCount(2);
        plan.Services[0].NodeGrant.Should().BeEquivalentTo(new NyxIdScopePlanNodeGrant(
            NyxIdScopePlanNodeGrantKind.NotRequired,
            []));
        plan.Services[1].ResourceOwner.Should().Be(new NyxIdScopePlanPrincipal(
            "org-alpha",
            NyxIdScopePlanPrincipalKind.Organization));
        plan.Services[1].NodeGrant.Should().BeEquivalentTo(new NyxIdScopePlanNodeGrant(
            NyxIdScopePlanNodeGrantKind.Required,
            ["node-a", "node-b"]));
        plan.AllowedServiceIds.Should().Equal("service-a", "service-b");
        plan.AllowedNodeIds.Should().Equal("node-a", "node-b");
        plan.EvaluatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        plan.NormalizedGrantDigest.Should().Be("sha256:" + new string('a', 64));
        plan.Freshness.Should().Be(new NyxIdScopePlanFreshness(
            NyxIdScopePlanFreshnessMode.MutationRevalidatedSnapshot,
            "scope_plan_digest",
            NyxIdScopePlanPostCreationDrift.FailClosed));
        plan.Completeness.Should().Be(new NyxIdScopePlanCompleteness(
            true,
            true,
            NyxIdScopePlanRouteCandidateBasis.ActiveConfiguredRoutes,
            true));
    }

    [Theory]
    [MemberData(nameof(MalformedScopePlans))]
    public void ParseScopePlan_ShouldRejectMalformedOrInconsistentContract(string response)
    {
        var result = NyxIdApiAccessResponseParser.ParseScopePlan(response);

        result.Succeeded.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Failure.Should().Be(new NyxIdApiAccessFailure(
            NyxIdApiAccessFailureKind.MalformedResponse,
            "nyxid_scope_plan_response_malformed"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("org")]
    public void ParseUserServices_ShouldRejectUnknownCredentialSource(string sourceType)
    {
        const string template = """
            {"services":[{"id":"service-a","slug":"api-a","is_active":true,"credential_source":{"type":"SOURCE_TYPE"}}]}
            """;
        var response = template.Replace("SOURCE_TYPE", sourceType, StringComparison.Ordinal);

        var result = NyxIdApiAccessResponseParser.ParseUserServices(response);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(new NyxIdApiAccessFailure(
            NyxIdApiAccessFailureKind.MalformedResponse,
            "nyxid_user_services_response_malformed"));
    }

    [Fact]
    public void ParseUserServices_ShouldRejectDuplicateServiceIds()
    {
        const string response = """
            {"services":[
              {"id":"service-a","slug":"api-a","is_active":true,"credential_source":{"type":"personal"}},
              {"_id":"service-a","slug":"api-b","is_active":true,"credential_source":{"type":"personal"}}
            ]}
            """;

        NyxIdApiAccessResponseParser.ParseUserServices(response).Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    public void ParseResponses_ShouldFailClosedForEmptyOrInvalidJson(string? response)
    {
        NyxIdApiAccessResponseParser.ParseUserServices(response!)
            .Failure
            .Should()
            .Be(new NyxIdApiAccessFailure(
                NyxIdApiAccessFailureKind.MalformedResponse,
                "nyxid_user_services_response_malformed"));
        NyxIdApiAccessResponseParser.ParseUserServiceKeys(response!)
            .Failure
            .Should()
            .Be(new NyxIdApiAccessFailure(
                NyxIdApiAccessFailureKind.MalformedResponse,
                "nyxid_user_service_keys_response_malformed"));
        NyxIdApiAccessResponseParser.ParseScopePlan(response!)
            .Failure
            .Should()
            .Be(new NyxIdApiAccessFailure(
                NyxIdApiAccessFailureKind.MalformedResponse,
                "nyxid_scope_plan_response_malformed"));
    }

    [Fact]
    public void ParseScopePlan_ShouldReturnSanitizedTypedProviderError()
    {
        const string response = """
            {"error":true,"status":403,"body":"{\"error\":\"api_key_scope_plan_denied\",\"error_code\":9004,\"message\":\"denied bearer-secret\"}"}
            """;

        var result = NyxIdApiAccessResponseParser.ParseScopePlan(response);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(new NyxIdApiAccessFailure(
            NyxIdApiAccessFailureKind.Forbidden,
            "api_key_scope_plan_denied",
            403,
            9004));
        result.ToString().Should().NotContain("bearer-secret").And.NotContain("message");
    }

    [Fact]
    public void ParseScopePlan_ShouldNotPropagateUntrustedErrorText()
    {
        const string response = """
            {"error":true,"status":503,"body":"{\"error\":\"secret/token?bearer-secret\",\"error_code\":1006,\"message\":\"credential-secret\"}"}
            """;

        var result = NyxIdApiAccessResponseParser.ParseScopePlan(response);

        result.Failure.Should().Be(new NyxIdApiAccessFailure(
            NyxIdApiAccessFailureKind.Transient,
            "nyxid_scope_plan_failed",
            503,
            1006));
        result.ToString().Should()
            .NotContain("bearer-secret")
            .And.NotContain("credential-secret")
            .And.NotContain("secret/token");
    }

    [Fact]
    public void AddNyxIdApiAccess_ShouldUseApiBaseUrlPrecedenceAndRegisterFactory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:InternalApiBaseUrl"] = " ",
                ["Aevatar:NyxId:ApiBaseUrl"] = " https://api.nyx.test/ ",
                ["Aevatar:NyxId:Authority"] = "https://authority.nyx.test",
                ["Cli:App:NyxId:Authority"] = "https://cli.nyx.test",
                ["Aevatar:Authentication:Authority"] = "https://auth.test",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddNyxIdApiAccess(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<NyxIdToolOptions>().BaseUrl.Should().Be("https://api.nyx.test/");
        provider.GetRequiredService<NyxIdApiClient>().Should().NotBeNull();
        provider.GetRequiredService<INyxIdApiClientFactory>().CreateClient().Should().NotBeNull();
        provider.GetRequiredService<INyxIdActionEvidenceReadPort>()
            .Should().BeOfType<NyxIdActionEvidenceReadPort>();
    }

    [Fact]
    public async Task ServiceAccessEvidence_WhenCatalogContainsDuplicateIdentity_ShouldFailClosed()
    {
        const string response = """
            {
              "contract_version": "1.0",
              "catalog_digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "user_id": "nyx-user-alpha",
              "services": [
                {
                  "service_id": "service-alpha",
                  "service_name": "GitHub",
                  "service_slug": "api-github",
                  "is_user_service": true,
                  "is_generic_proxy": false,
                  "endpoints": [{
                    "endpoint_id": "github-list-issues",
                    "name": "list_issues",
                    "method": "GET",
                    "path": "/issues",
                    "parameters": [],
                    "request_body_schema": null,
                    "request_content_type": null,
                    "request_body_required": false,
                    "response": {
                      "content_types": ["application/json"],
                      "binary_artifact": false
                    }
                  }]
                },
                {
                  "service_id": "service-duplicate",
                  "service_name": "Linear A",
                  "service_slug": "api-linear-a",
                  "is_user_service": true,
                  "is_generic_proxy": false,
                  "endpoints": []
                },
                {
                  "service_id": "service-duplicate",
                  "service_name": "Linear B",
                  "service_slug": "api-linear-b",
                  "is_user_service": true,
                  "is_generic_proxy": false,
                  "endpoints": []
                }
              ]
            }
            """;
        var handler = new StaticResponseHandler(response);
        var port = new NyxIdActionEvidenceReadPort(new StaticApiClientFactory(handler));

        var result = await port.GetServiceAccessAsync(
            "review-bearer",
            "service-alpha",
            "api-github");

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().BeEquivalentTo(new NyxIdApiAccessFailure(
            NyxIdApiAccessFailureKind.Conflict,
            "nyxid_service_access_conflict"));
    }

    [Fact]
    public async Task McpOperationCatalogReader_WhenCatalogIsValid_ShouldUseInjectedClock()
    {
        var now = new DateTimeOffset(2026, 8, 16, 8, 0, 0, TimeSpan.Zero);
        var reader = new NyxIdMcpOperationCatalogReader(
            new StaticApiClientFactory(new StaticResponseHandler(McpCatalog("service-alpha"))),
            new FixedTimeProvider(now));

        var result = await reader.ReadAsync("current-bearer");

        result.Succeeded.Should().BeTrue();
        result.Failure.Should().BeNull();
        result.Catalog.Should().NotBeNull();
        result.Catalog!.Source.ObservedAt.ToDateTimeOffset().Should().Be(now);
        result.Catalog.Source.FreshUntil.ToDateTimeOffset().Should().Be(now.AddMinutes(5));
    }

    [Fact]
    public async Task McpOperationCatalogReader_WhenCatalogContainsAmbiguousIdentity_ShouldReturnTypedFailure()
    {
        var reader = new NyxIdMcpOperationCatalogReader(
            new StaticApiClientFactory(
                new StaticResponseHandler(McpCatalog("service-alpha", "service-alpha"))),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 16, 8, 0, 0, TimeSpan.Zero)));

        var result = await reader.ReadAsync("current-bearer");

        result.Succeeded.Should().BeFalse();
        result.Catalog.Should().NotBeNull();
        result.Failure.Should().BeEquivalentTo(new NyxIdMcpOperationCatalogReadFailure(
            NyxIdMcpOperationCatalogReadFailureKind.AmbiguousServiceIdentity));
    }

    [Fact]
    public async Task ActionContinuationCredentialVisibility_WhenExactUserServiceIsPublished_ShouldBeVisible()
    {
        var handler = new StaticResponseHandler(McpCatalog("service-alpha"));
        var port = new NyxIdActionContinuationCredentialVisibilityPort(
            new StaticApiClientFactory(handler));

        var result = await port.InspectUserServiceAsync(
            "current-bearer",
            "service-alpha");

        result.Status.Should().Be(
            NyxIdActionContinuationCredentialVisibilityStatus.Visible);
        result.UserServiceId.Should().Be("service-alpha");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("access-denied")]
    public async Task ActionContinuationCredentialVisibility_WhenBearerCannotSeeUserService_ShouldRequireRefresh(
        string condition)
    {
        var response = condition == "access-denied"
            ? "{\"error\":true,\"status\":401,\"body\":\"{}\"}"
            : McpCatalog("service-other");
        var port = new NyxIdActionContinuationCredentialVisibilityPort(
            new StaticApiClientFactory(new StaticResponseHandler(response)));

        var result = await port.InspectUserServiceAsync(
            "stale-bearer",
            "service-alpha");

        result.Status.Should().Be(
            NyxIdActionContinuationCredentialVisibilityStatus.CredentialRefreshRequired);
        result.UserServiceId.Should().Be("service-alpha");
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    public async Task ActionContinuationCredentialVisibility_WhenCatalogIsUntrustworthy_ShouldFailClosed(
        string condition)
    {
        var response = condition == "duplicate"
            ? McpCatalog("service-alpha", "service-alpha")
            : "not-json";
        var port = new NyxIdActionContinuationCredentialVisibilityPort(
            new StaticApiClientFactory(new StaticResponseHandler(response)));

        var result = await port.InspectUserServiceAsync(
            "current-bearer",
            "service-alpha");

        result.Status.Should().Be(
            NyxIdActionContinuationCredentialVisibilityStatus.SourceUnavailable);
        result.UserServiceId.Should().Be("service-alpha");
    }

    [Fact]
    public void AddNyxIdApiAccess_ShouldSeparateInternalTransportPublicApiAndAuthority()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:InternalApiBaseUrl"] = " http://nyxid.internal:3001/ ",
                ["Aevatar:NyxId:ApiBaseUrl"] = "https://api.nyx.test",
                ["Aevatar:NyxId:Authority"] = "https://authority.nyx.test",
                [NyxIdTransportFallbackPolicy.TimeoutSecondsConfigurationKey] = "7",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddNyxIdApiAccess(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<NyxIdToolOptions>();
        options.BaseUrl.Should().Be("http://nyxid.internal:3001/");
        options.InternalApiBaseUrl.Should().Be("http://nyxid.internal:3001/");
        options.ApiBaseUrl.Should().Be("https://api.nyx.test");
        options.Authority.Should().Be("https://authority.nyx.test");
        options.PublicTransportFallbackBaseUrl.Should().Be("https://api.nyx.test");
        options.InternalApiFallbackTimeoutSeconds.Should().Be(7);
        options.EffectiveTransportBaseUrl.Should().Be("http://nyxid.internal:3001/");
        options.EffectiveApiBaseUrl.Should().Be("https://api.nyx.test");
        options.EffectiveAuthority.Should().Be("https://authority.nyx.test");
    }

    [Fact]
    public void AddNyxIdApiAccess_WithInternalAndAuthorityButNoApi_ShouldFailClosedForPublicRest()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:InternalApiBaseUrl"] = "http://nyxid.internal:3001",
                ["Aevatar:NyxId:Authority"] = "https://authority.nyx.test",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddNyxIdApiAccess(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<NyxIdToolOptions>();
        options.InternalApiBaseUrl.Should().Be("http://nyxid.internal:3001");
        options.EffectiveTransportBaseUrl.Should().Be("http://nyxid.internal:3001");
        options.ApiBaseUrl.Should().BeNull();
        options.EffectiveApiBaseUrl.Should().BeNull();
        options.Authority.Should().Be("https://authority.nyx.test");
        options.EffectiveAuthority.Should().Be("https://authority.nyx.test");
        options.PublicTransportFallbackBaseUrl.Should().BeNull();
    }

    [Fact]
    public void NyxIdToolOptions_WithDedicatedInternalTransport_ShouldNotExposeItAsPublicEndpoint()
    {
        var options = new NyxIdToolOptions
        {
            BaseUrl = "http://legacy-public.example.test",
            InternalApiBaseUrl = "http://nyxid.internal:3001",
        };

        options.EffectiveTransportBaseUrl.Should().Be("http://nyxid.internal:3001");
        options.EffectiveApiBaseUrl.Should().BeNull();
        options.EffectiveAuthority.Should().BeNull();
    }

    [Fact]
    public void AddNyxIdApiAccess_ShouldTreatPublicBaseUrlPathAsCaseSensitive()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:InternalApiBaseUrl"] = "https://nyx.example.test/Internal",
                ["Aevatar:NyxId:ApiBaseUrl"] = "https://nyx.example.test/internal",
                ["Aevatar:NyxId:Authority"] = "https://authority.nyx.test",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddNyxIdApiAccess(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<NyxIdToolOptions>().PublicTransportFallbackBaseUrl.Should()
            .Be("https://nyx.example.test/internal");
    }

    [Fact]
    public void AddNyxIdTools_WithoutInternalTransport_ShouldUsePublicApiWithoutFallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:ApiBaseUrl"] = " https://api.nyx.test/ ",
                ["Aevatar:NyxId:Authority"] = " https://authority.nyx.test/ ",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddNyxIdTools(configuration, options =>
            options.ProxyFileArtifactMaxBytes = 12_345);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<NyxIdToolOptions>();
        options.BaseUrl.Should().Be("https://api.nyx.test/");
        options.ApiBaseUrl.Should().Be("https://api.nyx.test/");
        options.Authority.Should().Be("https://authority.nyx.test/");
        options.PublicTransportFallbackBaseUrl.Should().BeNull();
        options.ProxyFileArtifactMaxBytes.Should().Be(12_345);
    }

    [Theory]
    [InlineData("Aevatar:NyxId:Authority", "https://app-authority.test")]
    [InlineData("Cli:App:NyxId:Authority", "https://cli-authority.test")]
    [InlineData("Aevatar:Authentication:Authority", "https://authentication-authority.test")]
    public void AddNyxIdApiAccess_ShouldFallBackThroughAuthorityAliases(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();
        var services = new ServiceCollection();

        services.AddNyxIdApiAccess(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<NyxIdToolOptions>();
        options.BaseUrl.Should().Be(value);
        options.ApiBaseUrl.Should().Be(value);
        options.Authority.Should().Be(value);
    }

    [Fact]
    public void AddNyxIdApiAccess_AfterTools_ShouldOverrideOnlyExplicitApiSettings()
    {
        var services = new ServiceCollection();
        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://tools-first.test";
            options.EnableSshExecTool = true;
            options.ProxyFileArtifactMaxBytes = 42_000_000;
        });

        services.AddNyxIdApiAccess(ConfigurationWithApiBaseUrl("https://api-later.test"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<NyxIdToolOptions>();
        options.BaseUrl.Should().Be("https://api-later.test");
        options.EnableSshExecTool.Should().BeTrue();
        options.ProxyFileArtifactMaxBytes.Should().Be(42_000_000);
        AssertApiAccessRegistrationsAreSingle(services);
    }

    [Fact]
    public void AddNyxIdTools_AfterApiAccess_ShouldPreserveEarlierApiSettings()
    {
        var services = new ServiceCollection();
        services.AddNyxIdApiAccess(ConfigurationWithApiBaseUrl("https://api-first.test"));

        services.AddNyxIdTools(options =>
        {
            options.EnableManagedCodexExecTool = true;
            options.ProxyFileArtifactMaxBytes = 37_000_000;
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<NyxIdToolOptions>();
        options.BaseUrl.Should().Be("https://api-first.test");
        options.EnableManagedCodexExecTool.Should().BeTrue();
        options.ProxyFileArtifactMaxBytes.Should().Be(37_000_000);
        AssertApiAccessRegistrationsAreSingle(services);
    }

    [Fact]
    public void AddNyxIdApiAccess_AfterTypeBasedDefaults_ShouldNormalizeAndComposeHttpAccess()
    {
        var services = new ServiceCollection();
        services.TryAddSingleton<NyxIdToolOptions>();
        services.TryAddSingleton<NyxIdApiClient>();

        services.AddNyxIdApiAccess(ConfigurationWithApiBaseUrl("https://api-after-defaults.test"));
        services.AddNyxIdApiAccess();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var createClient = () => provider.GetRequiredService<INyxIdApiClientFactory>().CreateClient();
        createClient.Should().NotThrow();
        provider.GetServices<NyxIdToolOptions>().Should().ContainSingle()
            .Which.BaseUrl.Should().Be("https://api-after-defaults.test");
        services.Count(static descriptor => descriptor.ServiceType == typeof(NyxIdApiClient))
            .Should().Be(2);
        services.Count(static descriptor => descriptor.ServiceType == typeof(INyxIdApiClientFactory))
            .Should().Be(1);
        services.Count(static descriptor =>
                descriptor.ServiceType == typeof(INyxIdActionEvidenceReadPort))
            .Should().Be(1);
    }

    public static TheoryData<string> MalformedScopePlans()
    {
        var valid = ValidScopePlanJson();
        return new TheoryData<string>
        {
            valid.Replace("\"type\": \"personal\"", "\"type\": \"person\"", StringComparison.Ordinal),
            valid.Replace("2026-07-21T08:09:10.123456789Z", "21 July 2026", StringComparison.Ordinal),
            valid.Replace("sha256:" + new string('a', 64), "sha256:ABC", StringComparison.Ordinal),
            valid.Replace("\"mode\": \"mutation_revalidated_snapshot\"", "\"mode\": \"live\"", StringComparison.Ordinal),
            valid.Replace("\"precondition_field\": \"scope_plan_digest\"", "\"precondition_field\": \"etag\"", StringComparison.Ordinal),
            valid.Replace("\"list_complete\": true", "\"list_complete\": false", StringComparison.Ordinal),
            valid.Replace("\"allowed_service_ids\": [\"service-a\", \"service-b\"]", "\"allowed_service_ids\": [\"service-a\", \"service-a\"]", StringComparison.Ordinal),
            valid.Replace("\"allowed_node_ids\": [\"node-a\", \"node-b\"]", "\"allowed_node_ids\": [\"node-b\", \"node-a\"]", StringComparison.Ordinal),
            valid.Replace("\"allowed_node_ids\": [\"node-a\", \"node-b\"]", "\"allowed_node_ids\": [\"node-a\"]", StringComparison.Ordinal),
            valid.Replace("\"node_ids\": [\"node-a\", \"node-b\"]", "\"node_ids\": []", StringComparison.Ordinal),
        };
    }

    public static TheoryData<string> MalformedUserServiceKeys() => new()
    {
        """
        {"services":[{"id":"service-a","slug":"api-a","status":"active","is_active":true,"credential_source":{"type":"personal"}}]}
        """,
        """
        {"keys":[
          {"id":"service-a","slug":"api-a","status":"active","is_active":true,"credential_source":{"type":"personal"}},
          {"id":"service-a","slug":"api-b","status":"active","is_active":true,"credential_source":{"type":"personal"}}
        ]}
        """,
        """
        {"keys":[{"id":"service-a","slug":"api-a","status":"mystery","is_active":true,"credential_source":{"type":"personal"}}]}
        """,
        """
        {"keys":[{"id":"service-a","slug":"api-a","status":"active","is_active":true,"node_id":"node-a","credential_source":{"type":"personal"}}]}
        """,
        """
        {"keys":[{"id":"service-a","slug":"api-a","status":"active","is_active":true,"node_id":"node-a","node_status":"mystery","credential_source":{"type":"personal"}}]}
        """,
    };

    private static string ValidScopePlanJson() => $$"""
        {
          "authority": "nyxid",
          "contract_version": "1",
          "policy_version": "api-key-scope-v1",
          "authenticated_actor": { "id": "actor-alpha", "type": "personal" },
          "intended_key_owner": { "id": "org-alpha", "type": "organization" },
          "services": [
            {
              "user_service_id": "service-a",
              "resource_owner": { "id": "actor-alpha", "type": "personal" },
              "node_grant": { "type": "not_required" }
            },
            {
              "user_service_id": "service-b",
              "resource_owner": { "id": "org-alpha", "type": "organization" },
              "node_grant": { "type": "required", "node_ids": ["node-a", "node-b"] }
            }
          ],
          "allowed_service_ids": ["service-a", "service-b"],
          "allowed_node_ids": ["node-a", "node-b"],
          "evaluated_at": "2026-07-21T08:09:10.123456789Z",
          "normalized_grant_digest": "sha256:{{new string('a', 64)}}",
          "freshness": {
            "mode": "mutation_revalidated_snapshot",
            "precondition_field": "scope_plan_digest",
            "post_creation_drift": "fail_closed"
          },
          "completeness": {
            "list_complete": true,
            "no_duplicates": true,
            "route_candidate_basis": "active_configured_routes",
            "transient_node_state_excluded": true
          },
          "ignored_future_field": "allowed"
        }
        """;

    private static IConfiguration ConfigurationWithApiBaseUrl(string baseUrl) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:ApiBaseUrl"] = baseUrl,
            })
            .Build();

    private static void AssertApiAccessRegistrationsAreSingle(IServiceCollection services)
    {
        services.Count(static descriptor => descriptor.ServiceType == typeof(NyxIdToolOptions))
            .Should().Be(1);
        services.Count(static descriptor => descriptor.ServiceType == typeof(NyxIdApiClient))
            .Should().Be(1);
        services.Count(static descriptor => descriptor.ServiceType == typeof(INyxIdApiClientFactory))
            .Should().Be(1);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content == null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StaticApiClientFactory(HttpMessageHandler handler)
        : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example/" },
            new HttpClient(handler, disposeHandler: false),
            NullLogger<NyxIdApiClient>.Instance);
    }

    private sealed class RoutedRecordingHandler(IReadOnlyDictionary<string, string> responsesByPath)
        : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            RequestPaths.Add(path);
            return Task.FromResult(responsesByPath.TryGetValue(path, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class StaticResponseHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string McpCatalog(params string[] userServiceIds) =>
        JsonSerializer.Serialize(new
        {
            contract_version = "1.0",
            catalog_digest = $"sha256:{new string('a', 64)}",
            user_id = "nyx-user-alpha",
            services = userServiceIds.Select((userServiceId, index) => new
            {
                service_id = userServiceId,
                service_name = $"Service {index}",
                service_slug = $"service-{index}",
                is_user_service = true,
                is_generic_proxy = false,
                endpoints = new[]
                {
                    new
                    {
                        endpoint_id = $"endpoint-{index}",
                        name = $"read_{index}",
                        method = "GET",
                        path = "/items",
                        parameters = Array.Empty<object>(),
                        request_body_schema = (object?)null,
                        request_content_type = (string?)null,
                        request_body_required = false,
                        response = new
                        {
                            content_types = new[] { "application/json" },
                            binary_artifact = false,
                        },
                    },
                },
            }),
        });

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string? Authorization,
        string? Body);
}
