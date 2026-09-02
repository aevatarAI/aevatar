using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
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
            new NyxIdToolOptions { BaseUrl = "https://nyx.example/" },
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
                  "is_active": true,
                  "credential_source": { "type": "personal" },
                  "endpoint_id": "ignored"
                },
                {
                  "id": "service-org",
                  "slug": "api-linear",
                  "label": null,
                  "is_active": false,
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
                NyxIdUserServiceCredentialSourceKind.Personal)));
        result.Value.Services[1].CredentialSource.Should().BeEquivalentTo(
            new NyxIdUserServiceCredentialSource(
                NyxIdUserServiceCredentialSourceKind.Organization,
                "org-alpha",
                "Alpha",
                null,
                NyxIdOrganizationRole.Admin,
                true));
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
                    NyxIdUserServiceCredentialSourceKind.Personal)),
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
                    true)));
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
        provider.GetRequiredService<NyxIdToolOptions>().BaseUrl.Should().Be(value);
    }

    [Fact]
    public void AddNyxIdApiAccess_AfterTools_ShouldOverrideOnlyExplicitApiSettings()
    {
        var services = new ServiceCollection();
        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://tools-first.test";
            options.SandboxServiceSlug = "sandbox-tools-first";
            options.EnableSshExecTool = true;
            options.ProxyFileArtifactMaxBytes = 42_000_000;
        });

        services.AddNyxIdApiAccess(ConfigurationWithApiBaseUrl("https://api-later.test"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<NyxIdToolOptions>();
        options.BaseUrl.Should().Be("https://api-later.test");
        options.SandboxServiceSlug.Should().Be("sandbox-tools-first");
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
            options.SandboxServiceSlug = "sandbox-tools-later";
            options.EnableManagedCodexExecTool = true;
            options.ProxyFileArtifactMaxBytes = 37_000_000;
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<NyxIdToolOptions>();
        options.BaseUrl.Should().Be("https://api-first.test");
        options.SandboxServiceSlug.Should().Be("sandbox-tools-later");
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

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string? Authorization,
        string? Body);
}
