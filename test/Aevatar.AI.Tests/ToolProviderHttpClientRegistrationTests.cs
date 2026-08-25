using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.ChronoStorage;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class ToolProviderHttpClientRegistrationTests
{
    [Fact]
    public void AddNyxIdTools_RegistersProductionHttpClientsThroughFactory()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        services.ShouldContainTypedHttpClient<NyxIdApiClient>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<NyxIdApiClient>().Should().NotBeNull();
        provider.GetRequiredService<NyxIdServiceInstanceClient>().Should().NotBeNull();
        provider.GetRequiredService<INyxIdApiClientFactory>()
            .CreateClient()
            .Should()
            .NotBeNull();
        provider.GetRequiredService<IRemoteToolApprovalPort>().Should()
            .BeOfType<NyxIdRemoteToolApprovalPort>();
    }

    [Fact]
    public void AddNyxIdTools_DisablesAutomaticRedirectsOnThePrimaryHandler()
    {
        var services = new ServiceCollection();
        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(nameof(NyxIdApiClient));
        while (handler is DelegatingHandler { InnerHandler: { } innerHandler })
            handler = innerHandler;

        handler.Should().BeOfType<HttpClientHandler>()
            .Which.AllowAutoRedirect.Should().BeFalse();
    }

    [Fact]
    public void AddNyxIdTools_GivesTheNyxIdClientRoomForTheLongestCodexRun()
    {
        // The 100s HttpClient default aborts long codex_exec runs before their own deadline
        // reports the failure. The managed request deadline is 300s, and the ingress layer needs
        // at least 315s to return its terminal response.
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        using var provider = services.BuildServiceProvider();
        var timeout = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(NyxIdApiClient))
            .Timeout;

        timeout.Should().BeGreaterThan(TimeSpan.FromSeconds(315));
        timeout.Should().Be(TimeSpan.FromSeconds(NyxIdToolOptions.DefaultMaxRequestDurationSeconds));
    }

    [Fact]
    public void AddNyxIdTools_HonoursAConfiguredNyxIdRequestCeiling()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyx.test";
            options.MaxRequestDurationSeconds = 420;
        });

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(NyxIdApiClient))
            .Timeout
            .Should().Be(TimeSpan.FromSeconds(420));
    }

    [Fact]
    public void AddNyxIdTools_ShouldRegisterFileArtifactIngressOnlyWhenWorkflowIngressExists()
    {
        var withoutWorkflowIngress = new ServiceCollection();
        withoutWorkflowIngress.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        withoutWorkflowIngress.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(INyxIdProxyFileArtifactIngress));

        var withWorkflowIngress = new ServiceCollection();
        withWorkflowIngress.AddSingleton<IFileArtifactIngressPort, StubWorkflowFileIngressPort>();
        withWorkflowIngress.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        withWorkflowIngress.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(INyxIdProxyFileArtifactIngress) &&
            descriptor.ImplementationFactory != null);
        using var provider = withWorkflowIngress.BuildServiceProvider();
        provider.GetRequiredService<INyxIdProxyFileArtifactIngress>()
            .Should()
            .BeOfType<NyxIdProxyWorkflowFileArtifactIngress>();
    }

    [Fact]
    public async Task AddNyxIdTools_ResolvesToolSourceWithoutDeletedCatalogServices()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        services.Any(HttpClientRegistrationAssertions.IsDeletedNyxIdDiscoveryRegistration)
            .Should()
            .BeFalse("AddNyxIdTools must not expose deleted catalog/cache services");

        await using var provider = services.BuildServiceProvider();
        provider.GetServices<IAgentToolSource>().Should().BeEmpty();
        var source = provider.GetRequiredService<NyxIdAgentToolSource>();

        var tools = await source.DiscoverToolsAsync();
        var names = tools.Select(tool => tool.Name).ToList();

        names.Should().Contain("nyxid_proxy");
        names.Should().Contain("nyxid_require_service");
        names.Should().NotContain("nyxid_search_capabilities");
        names.Should().NotContain("nyxid_proxy_execute");
        tools.Should().ContainSingle(tool => tool is NyxIdProxyTool);
        tools.Should().ContainSingle(tool => tool is NyxIdRequireServiceTool);
        tools.Single(tool => tool is NyxIdCatalogTool).Description.Should()
            .Contain("Discovery only")
            .And.Contain("then call nyxid_require_service")
            .And.Contain("do not finish the request with a catalog result");
        var requireService = tools.Single(tool => tool is NyxIdRequireServiceTool);
        requireService.Description.Should()
            .Contain("Final typed readiness gate")
            .And.Contain("connect, add, or authorize")
            .And.Contain("current-turn catalog result")
            .And.Contain("Provider slugs, display names, and remembered values")
            .And.Contain("interactive service.connect handoff");
        requireService.ParametersSchema.Should()
            .Contain("Exact catalog service slug copied from nyxid_catalog in this turn")
            .And.Contain("Do not omit scopes when the entry exposes a scope catalog")
            .And.Contain("\"required\": [\"service_slug\", \"requested_scopes\"]");
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldCreateDeterministicAuthorizationReceipt()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [{ "id": "us-other-alpha", "slug": "api-slack", "status": "active", "is_active": true, "connected": true, "credential_source": { "type": "personal" } }] }""")
        {
            CatalogResponseJson =
                """{"slug":"catalog-finops-alpha","scope_catalog":[{"scope":"repo"},{"scope":"read:org"}]}""",
        };
        var tool = CreateRequireServiceTool(handler);
        const string arguments =
            """{"service_slug":"catalog-finops-alpha","service_label":"FinOps Alpha","resource_uri":"/billing/private?token=bearer-secret","requested_scopes":["repo","read:org","repo"]}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            handler.Requests.Should().NotBeEmpty();
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.AuthorizationRequired);
            receipt.AuthorizationRequired.ServiceSlug.Should().Be("catalog-finops-alpha");
            receipt.AuthorizationRequired.ServiceLabel.Should().Be("FinOps Alpha");
            receipt.AuthorizationRequired.ResourceUri.Should().Be("/billing/private");
            receipt.AuthorizationRequired.ReasonCode.Should().Be("USER_SERVICE_NOT_VISIBLE");
            receipt.AuthorizationRequired.SafeMessage.Should().Be("No caller-visible NyxID UserService matches the requested service.");
            receipt.AuthorizationRequired.RequestedScopes.Should().Equal("repo", "read:org");
            receipt.ToString().Should().NotContain("bearer-secret").And.NotContain("token=");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldRejectUnverifiedCatalogIdentityWithoutCreatingBlocker()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""")
        {
            CatalogStatus = System.Net.HttpStatusCode.NotFound,
        };
        var tool = CreateRequireServiceTool(handler);
        const string arguments = """{"service_slug":"github","requested_scopes":["repo"]}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            handler.Requests.Should().Equal("/api/v1/catalog/github");
            result.Should().Contain("NYXID_REQUIRE_SERVICE_CATALOG_IDENTITY_INVALID");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_CATALOG_IDENTITY_INVALID");
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldRejectEmptyScopesWhenCatalogOffersScopes()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""")
        {
            CatalogResponseJson =
                """{"slug":"api-github","scope_catalog":[{"scope":"repo","label":"Repositories","description":"Repository access","sensitive":true}]}""",
        };
        var tool = CreateRequireServiceTool(handler);
        const string arguments = """{"service_slug":"api-github","requested_scopes":[]}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            handler.Requests.Should().Equal("/api/v1/catalog/api-github");
            result.Should().Contain("NYXID_REQUIRE_SERVICE_SCOPES_REQUIRED");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_SCOPES_REQUIRED");
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldFailClosedWhenCatalogIsUnavailable()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""")
        {
            CatalogStatus = System.Net.HttpStatusCode.ServiceUnavailable,
        };
        var tool = CreateRequireServiceTool(handler);
        const string arguments = """{"service_slug":"api-github","requested_scopes":["repo"]}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            handler.Requests.Should().Equal(
                "/api/v1/catalog/api-github",
                "/api/v1/catalog/api-github");
            result.Should().Contain("NYXID_REQUIRE_SERVICE_CATALOG_UNAVAILABLE");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_CATALOG_UNAVAILABLE");
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldCreateGitHubBlockerFromVerifiedCatalogSlugAndScope()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""")
        {
            CatalogResponseJson =
                """{"slug":"api-github","scope_catalog":[{"scope":"repo","label":"Repositories","description":"Repository access","sensitive":true}]}""",
        };
        var tool = CreateRequireServiceTool(handler);
        const string arguments = """{"service_slug":"api-github","requested_scopes":["repo"]}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            handler.Requests.Should().Equal(
                "/api/v1/catalog/api-github",
                "/api/v1/keys",
                "/api/v1/keys");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.AuthorizationRequired);
            receipt.ResultJson.Should().Be(result);
            receipt.AuthorizationRequired.ServiceSlug.Should().Be("api-github");
            receipt.AuthorizationRequired.RequestedScopes.Should().Equal("repo");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldRejectRequestedScopeOutsideCatalogEntry()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""")
        {
            CatalogResponseJson =
                """{"slug":"api-github","scope_catalog":[{"scope":"repo","label":"Repositories","description":"Repository access","sensitive":true}]}""",
        };
        var tool = CreateRequireServiceTool(handler);
        const string arguments =
            """{"service_slug":"api-github","requested_scopes":["invented-scope"]}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-invalid-scope", tool.Name, arguments, result);

            handler.Requests.Should().Equal("/api/v1/catalog/api-github");
            result.Should().Contain("NYXID_REQUIRE_SERVICE_SCOPES_INVALID");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_SCOPES_INVALID");
            receipt.ResultJson.Should().Be(result);
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldCreateAwsCredentialEntryBlockerWithoutOAuthScopes()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""")
        {
            CatalogResponseJson =
                """{"slug":"aws-cost-explorer","provider_type":"api_key","auth_method":"aws_sigv4","credential_mode":"admin","requires_credential":true}""",
        };
        var tool = CreateRequireServiceTool(handler);
        const string arguments =
            """{"service_slug":"aws-cost-explorer","service_label":"AWS Cost Explorer","requested_scopes":[]}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-aws", tool.Name, arguments, result);

            handler.Requests.Should().Equal(
                "/api/v1/catalog/aws-cost-explorer",
                "/api/v1/keys",
                "/api/v1/keys");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.AuthorizationRequired);
            receipt.ResultJson.Should().Be(result);
            receipt.AuthorizationRequired.ServiceSlug.Should().Be("aws-cost-explorer");
            receipt.AuthorizationRequired.ServiceLabel.Should().Be("AWS Cost Explorer");
            receipt.AuthorizationRequired.RequestedScopes.Should().BeEmpty();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Theory]
    [InlineData("{\"service_slug\":\"api-github\"}")]
    [InlineData("{\"service_slug\":\"api-github\",\"requested_scopes\":\"repo\"}")]
    [InlineData("{\"service_slug\":\"api-github\",\"requested_scopes\":[1]}")]
    [InlineData("{\"service_slug\":\"api-github\",\"requested_scopes\":[\"\"]}")]
    public async Task NyxIdRequireServiceTool_ShouldRejectMalformedRequestedScopes(string arguments)
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""");
        var tool = CreateRequireServiceTool(handler);

        var result = await tool.ExecuteAsync(arguments);
        var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

        handler.Requests.Should().BeEmpty();
        result.Should().Contain("NYXID_REQUIRE_SERVICE_ARGUMENTS_INVALID");
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_ARGUMENTS_INVALID");
        receipt.AuthorizationRequired.Should().BeNull();
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldNotFabricateAuthorization_WhenReadinessSourceIsStale()
    {
        var handler = new StubUserServiceListHandler("""{ "error": true, "status": 503 }""");
        var tool = CreateRequireServiceTool(handler);
        const string arguments = """{"service_slug":"api-github","requested_scopes":[]}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            handler.Requests.Should().NotBeEmpty();
            result.Should().Contain("NYXID_SOURCE_UNAVAILABLE");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.ErrorCode.Should().Be("NYXID_SOURCE_UNAVAILABLE");
            receipt.ResultJson.Should().Be(result);
            var normalized = AgentToolReceiptFactory.CreateResult(
                tool,
                "call-source-stale",
                tool.Name,
                tool.GetCallSafety(arguments),
                result,
                arguments);
            normalized.ResultJson.Should().Be(result);
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldCreateSuccessReceipt_WhenServiceIsAlreadyVisible()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [{ "id": "us-github-alpha", "slug": "github-personal", "catalog_service_slug": "api-github", "status": "active", "is_active": true, "connected": true, "credential_source": { "type": "personal" } }] }""")
        {
            McpResponseJson = """
                {
                  "contract_version": "1.0",
                  "catalog_digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "user_id": "nyx-user-alpha",
                  "services": [
                    {
                      "service_id": "us-github-alpha",
                      "service_name": "GitHub",
                      "service_slug": "api-github",
                      "is_user_service": true,
                      "is_generic_proxy": false,
                      "endpoints": []
                    }
                  ]
                }
                """,
        };
        var tool = CreateRequireServiceTool(handler);
        const string arguments = """{"service_slug":"api-github","requested_scopes":[]}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            result.Should().Contain("\"blocked\":false");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
            receipt.ResultJson.Should().Be(result);
            receipt.ProviderResourceId.Should().Be("us-github-alpha");
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_WhenConnectedServiceIsExcludedFromCurrentBearer_ShouldRequireServiceAccess()
    {
        var handler = new StubUserServiceListHandler("""
            {
              "keys": [
                {
                  "id": "us-github-alpha",
                  "slug": "api-github",
                  "catalog_service_slug": "api-github",
                  "resource_uri": "https://nyx.test/api/v1/proxy/s/api-github",
                  "status": "active",
                  "is_active": true,
                  "connected": true,
                  "credential_source": { "type": "personal" }
                }
              ]
            }
            """)
        {
            CatalogResponseJson = """
                {
                  "slug": "api-github",
                  "resource_uri": "https://nyx.test/api/v1/proxy/s/api-github",
                  "scope_catalog": [{"scope":"repo"}]
                }
                """,
            McpResponseJson = """
                {
                  "contract_version": "1.0",
                  "catalog_digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "user_id": "nyx-user-alpha",
                  "services": []
                }
                """,
        };
        var tool = CreateRequireServiceTool(handler);
        const string arguments =
            """{"service_slug":"api-github","requested_scopes":["repo"]}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-access", tool.Name, arguments, result);

            result.Should().Contain("\"readiness_status\":\"ServiceAccessDenied\"");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.AuthorizationRequired);
            receipt.AuthorizationRequired.Should().NotBeNull();
            receipt.AuthorizationRequired!.ServiceSlug.Should().Be("api-github");
            receipt.AuthorizationRequired.UserServiceId.Should().Be("us-github-alpha");
            receipt.AuthorizationRequired.ResourceUri.Should()
                .Be("https://nyx.test/api/v1/proxy/s/api-github");
            receipt.AuthorizationRequired.ReasonCode.Should().Be("USER_SERVICE_ACCESS_REQUIRED");
            handler.Requests.Should().Contain("/api/v1/mcp/config");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldFailClosed_WhenConnectedCatalogMatchIsAmbiguous()
    {
        var handler = new StubUserServiceListHandler("""
            {
              "keys": [
                {
                  "id": "us-github-alpha",
                  "slug": "github-alpha",
                  "catalog_service_slug": "api-github",
                  "status": "active",
                  "is_active": true,
                  "connected": true,
                  "credential_source": { "type": "personal" }
                },
                {
                  "id": "us-github-beta",
                  "slug": "github-beta",
                  "catalog_service_slug": "api-github",
                  "status": "active",
                  "is_active": true,
                  "connected": true,
                  "credential_source": { "type": "personal" }
                }
              ]
            }
            """);
        var tool = CreateRequireServiceTool(handler);
        const string arguments = """{"service_slug":"api-github","requested_scopes":[]}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-ambiguous", tool.Name, arguments, result);

            result.Should().Contain("NYXID_REQUIRE_SERVICE_INVENTORY_INVALID");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.ProviderResourceId.Should().BeEmpty();
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public void NyxIdRequireServiceTool_ShouldRejectReadyResultWithoutExactUserServiceId()
    {
        var tool = CreateRequireServiceTool(new StubUserServiceListHandler("""{ "keys": [] }"""));
        const string arguments = """{"service_slug":"api-github","requested_scopes":[]}""";

        var receipt = tool.CreateResultReceipt(
            "call-missing-id",
            tool.Name,
            arguments,
            """{"blocked":false,"service_slug":"api-github","user_service_id":"","readiness_status":"Ready","reason_code":"","safe_message":""}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_RESULT_INVALID");
        receipt.ProviderResourceId.Should().BeEmpty();
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldRejectOwnerSubjectWithoutNyxIdAuthority()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""");
        var tool = CreateRequireServiceTool(handler);
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext() with
        {
            NyxIdAuthority = AgentToolNyxIdAuthorityContext.Empty,
        };

        try
        {
            const string arguments = """{"service_slug":"api-github","requested_scopes":[]}""";
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            result.Should().Contain("verified caller identity not available");
            handler.Requests.Should().BeEmpty();
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_CONTEXT_UNAVAILABLE");
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_WithProxyDelegation_ShouldUsePurposeBoundManagementReadAuthority()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""")
        {
            CatalogResponseJson =
                """{"slug":"api-github","scope_catalog":[{"scope":"repo","label":"Repositories","description":"Repository access","sensitive":true}]}""",
        };
        var tool = CreateRequireServiceTool(handler);
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext() with
        {
            Credentials = new AgentToolCredentials(
                "runtime-caller-credential",
                null,
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation),
        };

        try
        {
            const string arguments =
                """{"service_slug":"api-github","requested_scopes":["repo"]}""";
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-delegated", tool.Name, arguments, result);

            result.Should().Contain("USER_SERVICE_NOT_VISIBLE");
            handler.Requests.Should().Equal(
                "/api/v1/catalog/api-github",
                "/api/v1/keys");
            handler.BearerTokens.Should().OnlyContain(token => token == "runtime-caller-credential");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.AuthorizationRequired);
            receipt.ResultJson.Should().Be(result);
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_WithUnspecifiedCredential_ShouldNotReadNyxIdManagementSource()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""");
        var tool = CreateRequireServiceTool(handler);
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext() with
        {
            Credentials = new AgentToolCredentials(
                "runtime-caller-credential",
                null,
                null,
                AgentToolNyxIdCredentialKind.Unspecified),
        };

        try
        {
            var result = await tool.ExecuteAsync(
                """{"service_slug":"api-github","requested_scopes":[]}""");

            result.Should().Contain("NYXID_SOURCE_UNAVAILABLE");
            handler.Requests.Should().BeEmpty();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldReturnTypedFailure_WhenOwnerScopeIsMissing()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""");
        var tool = CreateRequireServiceTool(handler);
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext() with
        {
            Caller = CapabilityContext().Caller with { OwnerScopeId = null },
        };

        try
        {
            const string arguments = """{"service_slug":"catalog-finops-alpha","requested_scopes":[]}""";
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            result.Should().Contain("owner_scope_id not available");
            handler.Requests.Should().BeEmpty();
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_CONTEXT_UNAVAILABLE");
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public void NyxIdRequireServiceTool_ShouldReturnTypedFailure_WhenReadinessResultIsMalformed()
    {
        var tool = CreateRequireServiceTool(new StubUserServiceListHandler("""{ "keys": [] }"""));
        const string arguments = """{"service_slug":"catalog-finops-alpha","requested_scopes":[]}""";

        var receipt = tool.CreateResultReceipt(
            "call-1",
            tool.Name,
            arguments,
            """{"blocked":true,"readiness_status":"ServiceRegistrationRequired"}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_RESULT_INVALID");
        receipt.AuthorizationRequired.Should().BeNull();
    }

    [Fact]
    public void NyxIdRequireServiceTool_ShouldReturnTypedFailure_WhenReadinessFieldsHaveWrongTypes()
    {
        var tool = CreateRequireServiceTool(new StubUserServiceListHandler("""{ "keys": [] }"""));
        const string arguments = """{"service_slug":"catalog-finops-alpha","requested_scopes":[]}""";

        var receipt = tool.CreateResultReceipt(
            "call-1",
            tool.Name,
            arguments,
            """{"blocked":true,"service_slug":42,"readiness_status":[],"reason_code":{},"safe_message":false}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_RESULT_INVALID");
        receipt.AuthorizationRequired.Should().BeNull();
    }

    [Fact]
    public void NyxIdRequireServiceTool_ShouldReturnTypedFailure_WhenReadinessStatusIsNumericText()
    {
        var tool = CreateRequireServiceTool(new StubUserServiceListHandler("""{ "keys": [] }"""));
        const string arguments = """{"service_slug":"catalog-finops-alpha","requested_scopes":[]}""";

        var receipt = tool.CreateResultReceipt(
            "call-1",
            tool.Name,
            arguments,
            """{"blocked":false,"service_slug":"catalog-finops-alpha","readiness_status":"13","reason_code":"","safe_message":""}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_RESULT_INVALID");
        receipt.AuthorizationRequired.Should().BeNull();
    }

    [Fact]
    public void NyxIdRequireServiceTool_ShouldReturnTypedFailure_WhenResultSlugDoesNotMatchArguments()
    {
        var tool = CreateRequireServiceTool(new StubUserServiceListHandler("""{ "keys": [] }"""));
        const string arguments = """{"service_slug":"catalog-finops-alpha","requested_scopes":[]}""";
        const string result =
            """{"blocked":true,"service_slug":"catalog-finops-beta","readiness_status":"ServiceRegistrationRequired","reason_code":"USER_SERVICE_NOT_VISIBLE","safe_message":"No caller-visible NyxID UserService matches the requested service."}""";

        var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_RESULT_INVALID");
        receipt.AuthorizationRequired.Should().BeNull();
    }

    [Fact]
    public async Task NyxIdRequestKeyCreateTool_ShouldEmitTypedRequirementForExactOwnerInventory()
    {
        var handler = new StubUserServiceListHandler(
            """
            { "keys": [
              { "id": "m-github", "slug": "api-github", "status": "active", "is_active": true, "connected": true, "credential_source": { "type": "personal" } },
              { "id": "m-lark", "slug": "api-lark", "status": "active", "is_active": true, "connected": true, "credential_source": { "type": "personal" } }
            ] }
            """);
        var tool = CreateKeyCreateTool(handler);
        const string arguments =
            """{"name":"agent-alpha","platform":"codex","allowed_service_ids":["m-github","m-lark"]}""";
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-key", tool.Name, arguments, result);

            handler.Requests.Should().Equal("/api/v1/keys");
            handler.Methods.Should().OnlyContain(static method => method == HttpMethod.Get);
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.AuthorizationRequired);
            receipt.AuthorizationRequired.ServiceSlug.Should().BeEmpty();
            receipt.AuthorizationRequired.KeyCreate.Name.Should().Be("agent-alpha");
            receipt.AuthorizationRequired.KeyCreate.Platform.Should().Be("codex");
            receipt.AuthorizationRequired.KeyCreate.AllowedServiceIds
                .Should().Equal("m-github", "m-lark");
            receipt.ToString().Should().NotContain("token").And.NotContain("secret");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Theory]
    [InlineData("{\"name\":\"agent-alpha\",\"platform\":\"codex\"}")]
    [InlineData("{\"name\":\"agent-alpha\",\"platform\":\"codex\",\"allowed_service_ids\":[]}")]
    [InlineData("{\"name\":\"agent-alpha\",\"platform\":\"codex\",\"allowed_service_ids\":[\"m-github\",\"m-github\"]}")]
    [InlineData("{\"name\":\"agent-alpha\",\"platform\":\"codex\",\"allowed_service_ids\":[\" m-github\"]}")]
    [InlineData("{\"name\":\"Bearer key-material\",\"platform\":\"codex\",\"allowed_service_ids\":[\"m-github\"]}")]
    [InlineData("{\"name\":\"agent-alpha\",\"platform\":\"codex\",\"allowed_service_ids\":[\"https://service.test/path?token=secret\"]}")]
    public async Task NyxIdRequestKeyCreateTool_ShouldRejectInvalidSelectionBeforeInventoryRead(
        string arguments)
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""");
        var tool = CreateKeyCreateTool(handler);
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-key", tool.Name, arguments, result);

            handler.Requests.Should().BeEmpty();
            result.Should().Contain("NYXID_KEY_CREATE_ARGUMENTS_INVALID");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Theory]
    [InlineData("m-unknown")]
    [InlineData("m-cross-owner")]
    public async Task NyxIdRequestKeyCreateTool_ShouldRejectIdentityOutsideOwnerInventory(
        string selectedId)
    {
        var handler = new StubUserServiceListHandler(
            """{ "keys": [{ "id": "m-owner", "slug": "api-github", "status": "active", "is_active": true, "connected": true, "credential_source": { "type": "personal" } }] }""");
        var tool = CreateKeyCreateTool(handler);
        var arguments = $$"""{"name":"agent-alpha","platform":"codex","allowed_service_ids":["{{selectedId}}"]}""";
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-key", tool.Name, arguments, result);

            handler.Requests.Should().Equal("/api/v1/keys");
            result.Should().Contain("NYXID_KEY_CREATE_SERVICE_IDENTITY_INVALID");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequestKeyRotateTool_ShouldEmitTypedRequirementForExactOwnerKey()
    {
        var handler = new StubUserServiceListHandler(
            """
            {
              "id": "key-alpha",
              "name": "Agent Alpha",
              "scopes": "proxy",
              "platform": "codex",
              "is_active": true,
              "allowed_service_ids": ["m-github"],
              "allow_all_services": false,
              "allowed_node_ids": [],
              "allow_all_nodes": false,
              "created_at": "2026-08-11T08:00:00Z",
              "rotation_predecessor_id": null,
              "state_version": 1,
              "updated_at": "2026-08-11T08:00:00Z"
            }
            """);
        var tool = CreateKeyRotateTool(handler);
        const string arguments = """{"key_id":"key-alpha"}""";
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-key", tool.Name, arguments, result);

            handler.Requests.Should().Equal("/api/v1/api-keys/key-alpha");
            handler.Methods.Should().OnlyContain(static method => method == HttpMethod.Get);
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.AuthorizationRequired);
            receipt.AuthorizationRequired.ServiceSlug.Should().BeEmpty();
            receipt.AuthorizationRequired.KeyRotate.KeyId.Should().Be("key-alpha");
            receipt.ToString().Should().NotContain("token").And.NotContain("secret");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"key_id\":\" key-alpha\"}")]
    [InlineData("{\"key_id\":\"key/alpha\"}")]
    [InlineData("{\"key_id\":\"Bearer secret\"}")]
    public async Task NyxIdRequestKeyRotateTool_ShouldRejectInvalidIdentityBeforeRead(
        string arguments)
    {
        var handler = new StubUserServiceListHandler("{}");
        var tool = CreateKeyRotateTool(handler);
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-key", tool.Name, arguments, result);

            handler.Requests.Should().BeEmpty();
            result.Should().Contain("NYXID_KEY_ROTATE_ARGUMENTS_INVALID");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequestKeyRotateTool_ShouldRejectKeyOutsideExactOwnerRead()
    {
        var handler = new StubUserServiceListHandler(
            """
            {
              "id": "key-other",
              "name": "Other Key",
              "scopes": "proxy",
              "is_active": true,
              "allowed_service_ids": [],
              "allow_all_services": false,
              "allowed_node_ids": [],
              "allow_all_nodes": false,
              "created_at": "2026-08-11T08:00:00Z",
              "rotation_predecessor_id": null,
              "state_version": 1,
              "updated_at": "2026-08-11T08:00:00Z"
            }
            """);
        var tool = CreateKeyRotateTool(handler);
        const string arguments = """{"key_id":"key-alpha"}""";
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-key", tool.Name, arguments, result);

            handler.Requests.Should().Equal("/api/v1/api-keys/key-alpha");
            result.Should().Contain("NYXID_KEY_ROTATE_KEY_UNAVAILABLE");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    private static IAgentTool CreateRequireServiceTool(StubUserServiceListHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        return new NyxIdRequireServiceTool(client);
    }

    private static IAgentTool CreateKeyCreateTool(StubUserServiceListHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        return new NyxIdRequestKeyCreateTool(client);
    }

    private static IAgentTool CreateKeyRotateTool(StubUserServiceListHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        return new NyxIdRequestKeyRotateTool(client);
    }

    private sealed class StubUserServiceListHandler(string responseJson) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string?> BearerTokens { get; } = [];

        public List<HttpMethod> Methods { get; } = [];

        public System.Net.HttpStatusCode CatalogStatus { get; init; } = System.Net.HttpStatusCode.OK;

        public string? CatalogResponseJson { get; init; }

        public string? McpResponseJson { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            BearerTokens.Add(request.Headers.Authorization?.Parameter);
            Methods.Add(request.Method);
            var isCatalogRequest = path.StartsWith("/api/v1/catalog/", StringComparison.Ordinal);
            var response = path == "/api/v1/mcp/config"
                ? McpResponseJson ?? responseJson
                : isCatalogRequest
                    ? CatalogResponseJson ?? System.Text.Json.JsonSerializer.Serialize(new
                {
                    slug = Uri.UnescapeDataString(path["/api/v1/catalog/".Length..]),
                })
                    : responseJson;
            return Task.FromResult(new HttpResponseMessage(
                isCatalogRequest ? CatalogStatus : System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static AgentToolExecutionContext CapabilityContext() =>
        AgentToolExecutionContext.Empty with
        {
            Caller = new AgentToolCallerContext(
                "scope-alpha",
                "caller-alpha",
                null,
                "scope-alpha"),
            Credentials = new AgentToolCredentials(
                "runtime-caller-credential",
                "runtime-organization-credential",
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                string.Empty,
                "nyx-user-alpha"),
        };

    [Fact]
    public void NyxIdProxyTool_AuthorizationError_ShouldCreateCredentialFreeTypedReceipt()
    {
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" });
        var tool = new NyxIdProxyTool(client);
        const string arguments =
            """{"slug":"api-github","path":"/repos/private?access_token=bearer-secret#details"}""";
        const string result =
            """{"error":true,"status":401,"body":"{\"error\":\"unauthorized\",\"error_code\":1001,\"message\":\"expired bearer-secret\"}"}""";

        var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.AuthorizationRequired);
        receipt.AuthorizationRequired.ServiceSlug.Should().Be("api-github");
        receipt.AuthorizationRequired.ResourceUri.Should().Be("/repos/private");
        receipt.AuthorizationRequired.ReasonCode.Should().Be("NYXID_UNAUTHORIZED");
        receipt.ResultJson.Should().Contain("NYXID_UNAUTHORIZED");
        receipt.ToString().Should().NotContain("bearer-secret").And.NotContain("access_token");
    }

    [Fact]
    public void NyxIdProxyTool_ForbiddenError_ShouldRemainCredentialFreeTypedFailure()
    {
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" });
        var tool = new NyxIdProxyTool(client);
        const string arguments =
            """{"slug":"api-github","path":"/repos/private?access_token=bearer-secret#details"}""";
        const string result =
            """{"error":true,"status":403,"body":"{\"error\":\"forbidden\",\"error_code\":1002,\"message\":\"approval timed out bearer-secret\"}"}""";

        var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.AuthorizationRequired.Should().BeNull();
        receipt.ErrorCode.Should().Be("NYXID_PROXY_FORBIDDEN");
        receipt.ResultJson.Should().Contain("NYXID_PROXY_FORBIDDEN");
        receipt.ToString().Should().NotContain("bearer-secret").And.NotContain("access_token");
    }

    [Fact]
    public void NyxIdProxyTool_ServiceScopeForbidden_ShouldPreserveSafeFailureClassification()
    {
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" });
        var tool = new NyxIdProxyTool(client);
        const string arguments =
            """{"slug":"api-calendar","path":"/events/private?access_token=bearer-secret#details"}""";
        const string result =
            """{"error":true,"status":403,"body":"{\"error\":\"api_key_scope_forbidden\",\"error_code\":1042,\"message\":\"service-id-sensitive bearer-secret\"}"}""";

        var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_PROXY_SERVICE_SCOPE_FORBIDDEN");
        receipt.ErrorMessage.Should().Be("The NyxID caller credential is not authorized for this service.");
        receipt.ResultJson.Should().Contain("NYXID_PROXY_SERVICE_SCOPE_FORBIDDEN");
        receipt.ToString().Should()
            .NotContain("service-id-sensitive")
            .And.NotContain("bearer-secret")
            .And.NotContain("access_token");
    }

    [Fact]
    public async Task AddNyxIdTools_WithCodePortWithoutCodexTarget_DiscoversOnlyCodeExecute()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICodeExecutionPort>(new CodeExecutionPortStub());
        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<NyxIdExecutionAgentToolSource>();
        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle(tool => tool is NyxIdCodeExecuteTool);
        tools.Should().NotContain(tool => tool is NyxIdCodexExecTool);
    }

    [Fact]
    public async Task AddNyxIdTools_WithSshOptIn_DiscoversCodeExecuteAndCodexTools()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICodeExecutionPort>(new CodeExecutionPortStub());

        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyx.test";
            options.EnableSshExecTool = true;
        });

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<NyxIdExecutionAgentToolSource>();

        var tools = await source.DiscoverToolsAsync();
        var sshExec = tools.Should().ContainSingle(tool => tool is NyxIdSshExecTool).Subject;
        var codexExec = tools.Should().ContainSingle(tool => tool is NyxIdCodexExecTool).Subject;
        tools.Should().ContainSingle(tool => tool is NyxIdCodeExecuteTool);
        codexExec.Name.Should().Be("codex_exec");
        sshExec.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        sshExec.IsDestructive.Should().BeTrue();
        codexExec.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        codexExec.RequiresApproval("""{"target":{"kind":"private_ssh","private_ssh":{"service":"host","principal":"ubuntu"}},"prompt":"check"}""")
            .Should()
            .BeTrue();
        codexExec.RequiresApproval("""{"target":{"kind":"managed_sandbox"},"workspace":{"kind":"empty_git"},"prompt":"check"}""")
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task AddNyxIdTools_WithManagedPort_DiscoversCodeExecuteAndCodexWithoutSshTool()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICodeExecutionPort>(new CodeExecutionPortStub());
        services.AddSingleton<ICodexExecutionPort>(new ManagedCodexPortStub());
        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyx.test";
            options.EnableManagedCodexExecTool = true;
        });

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<NyxIdExecutionAgentToolSource>();
        var tools = await source.DiscoverToolsAsync();

        tools.Should().NotContain(tool => tool is NyxIdSshExecTool);
        var codexExec = tools.Should().ContainSingle(tool => tool is NyxIdCodexExecTool).Subject;
        tools.Should().ContainSingle(tool => tool is NyxIdCodeExecuteTool);
        codexExec.RequiresApproval("""{"target":{"kind":"managed_sandbox"},"workspace":{"kind":"empty_git"},"prompt":"check"}""")
            .Should().BeFalse();
    }

    [Fact]
    public async Task AddNyxIdTools_WhenManagedEnabledWithoutPort_FailsClosed()
    {
        var services = new ServiceCollection();
        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyx.test";
            options.EnableManagedCodexExecTool = true;
        });

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<NyxIdExecutionAgentToolSource>();

        var act = () => source.DiscoverToolsAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exactly one managed-sandbox ICodexExecutionPort*");
    }

    [Fact]
    public async Task AddNyxIdTools_WithoutCodePort_DoesNotExposeCodeExecute()
    {
        var services = new ServiceCollection();
        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<NyxIdExecutionAgentToolSource>();

        var tools = await source.DiscoverToolsAsync();

        tools.Should().NotContain(tool => tool is NyxIdCodeExecuteTool);
    }

    [Fact]
    public void AddWebTools_RegistersWebApiClientThroughFactory()
    {
        var services = new ServiceCollection();

        services.AddWebTools();

        services.ShouldContainTypedHttpClient<WebApiClient>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<WebApiClient>().Should().NotBeNull();
    }

    [Fact]
    public void AddChronoStorageTools_RegistersChronoStorageApiClientThroughFactory()
    {
        var services = new ServiceCollection();

        services.AddChronoStorageTools(options => options.ApiBaseUrl = "https://storage.test");

        services.ShouldContainTypedHttpClient<ChronoStorageApiClient>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<ChronoStorageApiClient>().Should().NotBeNull();
    }
}

file static class HttpClientRegistrationAssertions
{
    public static void ShouldContainTypedHttpClient<TClient>(
        this IServiceCollection services)
        where TClient : class
    {
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(TClient) &&
            descriptor.Lifetime == ServiceLifetime.Transient);
    }

    public static bool IsDeletedNyxIdDiscoveryRegistration(ServiceDescriptor descriptor)
    {
        var serviceName = descriptor.ServiceType.Name;
        var implementationName = descriptor.ImplementationType?.Name;

        return serviceName is "NyxIdSpecCatalog" or "IServiceDiscoveryCache" ||
               implementationName is "NyxIdSpecCatalog" or "InMemoryServiceDiscoveryCache";
    }

}

file sealed class StubWorkflowFileIngressPort : IFileArtifactIngressPort
{
    public ValueTask<FileArtifactIngressResult> IngestAsync(
        FileArtifactIngressRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new FileArtifactIngressResult(new FileArtifactRef
        {
            FileId = "file-1",
            ArtifactId = "artifact-1",
            SourceKind = request.SourceKind,
        }));
}

file sealed class ManagedCodexPortStub : ICodexExecutionPort
{
    public CodexExecutionTarget.TargetOneofCase TargetKind =>
        CodexExecutionTarget.TargetOneofCase.ManagedSandbox;

    public async IAsyncEnumerable<CodexExecutionEvent> ExecuteAsync(
        CodexExecutionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

file sealed class CodeExecutionPortStub : ICodeExecutionPort
{
    public Task<CodeExecutionOutcome> ExecuteAsync(
        CodeExecutionRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            new CodeExecutionRouteIdentity(
                request.Route.ServiceSlug,
                "svc-code-alpha",
                CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog)));
}
