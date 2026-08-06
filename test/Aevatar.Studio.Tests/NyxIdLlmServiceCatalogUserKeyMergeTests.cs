using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.LlmCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Verifies how NyxID unified keys (<c>GET /api/v1/keys</c>) are merged into the LLM route catalog.
/// Regression context: <c>/api/v1/proxy/services</c> reports legacy connection state, so an active
/// unified key could previously be misclassified as "not allowed for this user".
/// </summary>
public sealed class NyxIdLlmServiceCatalogUserKeyMergeTests
{
    [Fact]
    public void ParseServicesResult_WithEmptyModels_ShouldReturnNotVerifiable()
    {
        var result = NyxIdLlmServiceCatalogParser.ParseServicesResult(
            """{"services":[{"user_service_id":"diag-alpha","service_slug":"chrono","route_value":"/api/v1/llm/gateway/v1","status":"ready","source":"gateway_provider","allowed":true,"models":[]}]}""");

        result.Services.Single().ModelCatalog.Certainty
            .Should().Be(LLMModelCatalogCertainty.NotVerifiable);
        result.Services.Single().ModelCatalog.DiagnosticKind
            .Should().Be(LLMModelCatalogDiagnosticKind.NotPublished);
    }

    [Theory]
    [InlineData("gpt-*", LLMModelCatalogDiagnosticKind.PatternOnly)]
    [InlineData(" gpt-5.5", LLMModelCatalogDiagnosticKind.ResponseInvalid)]
    [InlineData("gpt-5.5\u0001", LLMModelCatalogDiagnosticKind.ResponseInvalid)]
    public void ParseServicesResult_WithInvalidModel_ShouldReturnTypedDiagnostic(
        string model,
        LLMModelCatalogDiagnosticKind expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            services = new[]
            {
                new
                {
                    user_service_id = "diag-alpha",
                    service_slug = "gateway",
                    route_value = UserConfigLlmRouteDefaults.Gateway,
                    status = "ready",
                    source = "gateway_provider",
                    allowed = true,
                    models = new[] { model },
                },
            },
        });

        var catalog = NyxIdLlmServiceCatalogParser.ParseServicesResult(json)
            .Services.Single().ModelCatalog;

        catalog.Certainty.Should().Be(LLMModelCatalogCertainty.NotVerifiable);
        catalog.DiagnosticKind.Should().Be(expected);
        catalog.ModelIds.Should().BeEmpty();
    }

    [Fact]
    public void ParseServicesResult_WithTooManyModels_ShouldReturnResponseTooLarge()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            services = new[]
            {
                new
                {
                    user_service_id = "diag-alpha",
                    service_slug = "gateway",
                    route_value = UserConfigLlmRouteDefaults.Gateway,
                    status = "ready",
                    source = "gateway_provider",
                    allowed = true,
                    models = Enumerable.Range(0, LLMSelectionPolicy.MaxModelsPerCatalog + 1)
                        .Select(index => $"model-{index:D4}").ToArray(),
                },
            },
        });

        var catalog = NyxIdLlmServiceCatalogParser.ParseServicesResult(json)
            .Services.Single().ModelCatalog;

        catalog.Certainty.Should().Be(LLMModelCatalogCertainty.NotVerifiable);
        catalog.DiagnosticKind.Should().Be(LLMModelCatalogDiagnosticKind.ResponseTooLarge);
    }

    [Fact]
    public void ParseServicesResult_ShouldUseOrdinalModelIdentityAndRejectDefaultOutsideList()
    {
        var exact = NyxIdLlmServiceCatalogParser.ParseServicesResult("""
            {"services":[{"user_service_id":"diag-alpha","service_slug":"gateway","route_value":"/api/v1/llm/gateway/v1","status":"ready","source":"gateway_provider","allowed":true,"default_model":"model-a","models":["model-a","MODEL-A"]}]}
            """).Services.Single().ModelCatalog;
        var invalidDefault = NyxIdLlmServiceCatalogParser.ParseServicesResult("""
            {"services":[{"user_service_id":"diag-alpha","service_slug":"gateway","route_value":"/api/v1/llm/gateway/v1","status":"ready","source":"gateway_provider","allowed":true,"default_model":"missing","models":["model-a"]}]}
            """).Services.Single().ModelCatalog;

        exact.ModelIds.Should().Equal("MODEL-A", "model-a");
        invalidDefault.Certainty.Should().Be(LLMModelCatalogCertainty.NotVerifiable);
        invalidDefault.DiagnosticKind.Should().Be(LLMModelCatalogDiagnosticKind.ResponseInvalid);
    }

    [Theory]
    [InlineData("ready", false, LLMModelCatalogDiagnosticKind.AccessDenied)]
    [InlineData("not_connected", true, LLMModelCatalogDiagnosticKind.RouteNotReady)]
    public void ParseServicesResult_WithUnavailableRoute_ShouldReturnUnavailable(
        string status,
        bool allowed,
        LLMModelCatalogDiagnosticKind expected)
    {
        var json = $$"""
            {"services":[{"user_service_id":"diag-alpha","service_slug":"gateway","route_value":"/api/v1/llm/gateway/v1","status":"{{status}}","source":"gateway_provider","allowed":{{allowed.ToString().ToLowerInvariant()}},"models":["model-a"]}]}
            """;

        var catalog = NyxIdLlmServiceCatalogParser.ParseServicesResult(json)
            .Services.Single().ModelCatalog;

        catalog.Certainty.Should().Be(LLMModelCatalogCertainty.Unavailable);
        catalog.DiagnosticKind.Should().Be(expected);
        catalog.ModelIds.Should().BeEmpty();
    }

    [Fact]
    public void ComposeUserServiceInventory_ShouldRetainGatewayAndExactInventoryIdentity()
    {
        var diagnostics = new NyxIdLlmServicesResult(
            [
                new NyxIdLlmService(
                    null,
                    "gateway",
                    "Gateway",
                    UserConfigLlmRouteDefaults.Gateway,
                    new LLMModelCatalog
                    {
                        Certainty = LLMModelCatalogCertainty.Enumerated,
                        DefaultModelId = "gateway-model",
                        ModelIds = { "gateway-model" },
                    },
                    UserLlmRouteStatus.Ready,
                    NyxIdLlmProviderSource.GatewayProvider,
                    true,
                    null),
                Diagnostic("diag-alpha", "shared"),
            ],
            null);

        var result = NyxIdLlmServiceCatalogParser.ComposeUserServiceInventory(
            diagnostics,
            new NyxIdUserServices([Inventory("us-alpha", "shared")]));

        result.Services.Should().ContainSingle(service =>
            service.Source == NyxIdLlmProviderSource.GatewayProvider &&
            service.ModelCatalog.Certainty == LLMModelCatalogCertainty.Enumerated);
        result.Services.Should().ContainSingle(service =>
            service.Identity != null &&
            service.Identity.NyxIdUserServiceId == "us-alpha" &&
            service.ModelCatalog.ModelIds.Contains("gpt-5.5"));
    }

    [Fact]
    public void ComposeUserServiceInventory_WithoutGatewayDiagnostic_ShouldNotSynthesizeGateway()
    {
        var result = NyxIdLlmServiceCatalogParser.ComposeUserServiceInventory(
            new NyxIdLlmServicesResult([Diagnostic("diag-alpha", "shared")], null),
            new NyxIdUserServices([Inventory("us-alpha", "shared")]));

        result.Services.Should().NotContain(service =>
            service.Source == NyxIdLlmProviderSource.GatewayProvider);
    }

    [Fact]
    public void ParseProvisionedService_ShouldKeepResponseIdDiagnosticOnly()
    {
        var service = NyxIdLlmServiceCatalogParser.ParseProvisionedService("""
            {
              "service": {
                "user_service_id": "us-provisioned",
                "service_slug": "chrono-llm",
                "display_name": "Chrono LLM",
                "route_value": "/api/v1/proxy/s/chrono-llm",
                "status": "ready",
                "source": "user_service",
                "allowed": true
              }
            }
            """);

        service.CatalogEntryId.Should().Be("us-provisioned");
        service.Identity.Should().BeNull();
    }

    [Fact]
    public void ComposeInventory_ShouldMintOnlyInventoryIdsAndPreserveDuplicateSlugs()
    {
        var diagnostics = new NyxIdLlmServicesResult(
            [Diagnostic("key-alpha", "chrono-llm-public")],
            null);
        var inventory = new NyxIdUserServices(
        [
            Inventory("us-alpha", "chrono-llm-public"),
            Inventory("us-beta", "chrono-llm-public"),
        ]);

        var result = NyxIdLlmServiceCatalogParser.ComposeUserServiceInventory(diagnostics, inventory);

        result.Services.Should().HaveCount(2);
        result.Services.Select(service => service.Identity!.NyxIdUserServiceId)
            .Should()
            .Equal("us-alpha", "us-beta");
        result.Services.Should().OnlyContain(service =>
            service.Identity!.Authority == UserLlmIdentityAuthority.NyxIdUserServicesInventory);
        result.Services.Should().NotContain(service =>
            service.Identity!.NyxIdUserServiceId == "key-alpha");
    }

    [Fact]
    public void ComposeInventory_ShouldIncludeOnlyActiveAuthorizedCredentialSources()
    {
        var inventory = new NyxIdUserServices(
        [
            Inventory("us-personal", "personal-llm"),
            Inventory("us-inactive", "inactive-llm", isActive: false),
            Inventory("us-org-allowed", "org-allowed-llm", organizationAllowed: true),
            Inventory("us-org-denied", "org-denied-llm", organizationAllowed: false),
        ]);

        var result = NyxIdLlmServiceCatalogParser.ComposeUserServiceInventory(
            new NyxIdLlmServicesResult([], null),
            inventory);

        result.Services.Select(service => service.Identity!.NyxIdUserServiceId)
            .Should()
            .Equal("us-org-allowed", "us-personal");
    }

    [Fact]
    public void ToOption_ShouldCopyOnlyExplicitInventoryIdentity()
    {
        var identity = new UserLlmServiceIdentity(
            UserLlmIdentityAuthority.NyxIdUserServicesInventory,
            "us-alpha");
        var proven = Diagnostic("legacy-diagnostic-id", "chrono-llm") with
        {
            Identity = identity,
        };
        var unproven = Diagnostic("source-derived-id", "other-llm") with
        {
            Source = NyxIdLlmProviderSource.UserService,
        };

        NyxIdLlmServiceMapping.ToOption(proven).Identity.Should().Be(identity);
        NyxIdLlmServiceMapping.ToOption(unproven).Identity.Should().BeNull();
    }

    private static NyxIdLlmService NotConnectedProxyService(string slug = "chrono-llm") => new(
        CatalogEntryId: "svc-catalog-id",
        ServiceSlug: slug,
        DisplayName: "Chrono LLM",
        RouteValue: $"/api/v1/proxy/s/{slug}",
        ModelCatalog: new LLMModelCatalog
        {
            Certainty = LLMModelCatalogCertainty.Unavailable,
            DiagnosticKind = LLMModelCatalogDiagnosticKind.RouteNotReady,
        },
        Status: "not_connected",
        Source: NyxIdLlmProviderSource.ProxyService,
        Allowed: false,
        Description: "Shared LLM route");

    private static NyxIdLlmServicesResult ResultWith(params NyxIdLlmService[] services) =>
        new(services, null);

    private static NyxIdLlmService Diagnostic(string diagnosticId, string slug) => new(
        CatalogEntryId: diagnosticId,
        ServiceSlug: slug,
        DisplayName: "Chrono LLM",
        RouteValue: $"/api/v1/proxy/s/{slug}",
        ModelCatalog: new LLMModelCatalog
        {
            Certainty = LLMModelCatalogCertainty.Enumerated,
            DefaultModelId = "gpt-5.5",
            ModelIds = { "gpt-5.5" },
        },
        Status: UserLlmRouteStatus.Ready,
        Source: NyxIdLlmProviderSource.ProxyService,
        Allowed: true,
        Description: null)
    {
        Identity = null,
    };

    private static NyxIdUserService Inventory(
        string id,
        string slug,
        bool isActive = true,
        bool? organizationAllowed = null) => new(
        Id: id,
        Slug: slug,
        Label: $"Inventory {id}",
        CatalogServiceName: "Chrono LLM",
        IsActive: isActive,
        CredentialSource: organizationAllowed is { } allowed
            ? new NyxIdUserServiceCredentialSource(
                NyxIdUserServiceCredentialSourceKind.Organization,
                OrganizationId: "org-1",
                OrganizationName: "Org",
                OrganizationRole: NyxIdOrganizationRole.Member,
                Allowed: allowed)
            : new NyxIdUserServiceCredentialSource(
                NyxIdUserServiceCredentialSourceKind.Personal));

    [Fact]
    public void ActiveKeyReplacesNotConnectedProxyEntryAsSelectable()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(NotConnectedProxyService()),
            """
            {
              "keys": [
                {
                  "id": "key-1",
                  "label": "Chrono LLM",
                  "slug": "chrono-llm",
                  "endpoint_url": "https://llm.test/v1",
                  "status": "active",
                  "catalog_service_slug": "chrono-llm",
                  "catalog_service_name": "Chrono LLM",
                  "service_type": "http",
                  "is_active": true
                }
              ]
            }
            """);

        var service = merged.Services.Should().ContainSingle().Subject;
        service.Allowed.Should().BeTrue();
        service.Status.Should().Be("ready");
        service.CatalogEntryId.Should().Be("key-1");
        service.RouteValue.Should().Be("/api/v1/proxy/s/chrono-llm");
        service.Source.Should().Be(NyxIdLlmProviderSource.UserService);
    }

    [Fact]
    public void CatalogSlugWinsOverPerKeySlug_ForCatalogBackedKeys()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(NotConnectedProxyService()),
            """
            {
              "keys": [
                {
                  "id": "key-1",
                  "label": "Chrono LLM / personal key",
                  "slug": "chrono-llm-personal-key",
                  "endpoint_url": "https://llm.test/v1",
                  "status": "active",
                  "catalog_service_slug": "chrono-llm",
                  "catalog_service_name": "Chrono LLM",
                  "service_type": "http",
                  "is_active": true
                }
              ]
            }
            """);

        var service = merged.Services.Should().ContainSingle().Subject;
        service.ServiceSlug.Should().Be("chrono-llm");
        service.RouteValue.Should().Be("/api/v1/proxy/s/chrono-llm");
        service.CatalogEntryId.Should().Be("key-1");
    }

    [Fact]
    public void OrgViewerKeyDoesNotMakeServiceSelectable()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(NotConnectedProxyService()),
            """
            {
              "keys": [
                {
                  "id": "key-1",
                  "label": "Chrono LLM",
                  "slug": "chrono-llm",
                  "endpoint_url": "https://llm.test/v1",
                  "status": "active",
                  "catalog_service_slug": "chrono-llm",
                  "catalog_service_name": "Chrono LLM",
                  "service_type": "http",
                  "is_active": true,
                  "credential_source": {
                    "type": "org",
                    "org_id": "org-1",
                    "org_name": "Org",
                    "avatar_url": null,
                    "role": "viewer",
                    "allowed": false
                  }
                }
              ]
            }
            """);

        var service = merged.Services.Should().ContainSingle().Subject;
        service.Source.Should().Be(NyxIdLlmProviderSource.UserService);
        service.Status.Should().Be("ready");
        service.Allowed.Should().BeFalse();
    }

    [Fact]
    public void UserKeyRemainsAuthoritative_WhenProxyAlsoLooksReady()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeProxyRouteCandidates(
            NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
                ResultWith(),
                """
                {
                  "keys": [
                    {
                      "id": "key-1",
                      "label": "Chrono LLM",
                      "slug": "chrono-llm",
                      "endpoint_url": "https://llm.test/v1",
                      "status": "active",
                      "catalog_service_slug": "chrono-llm",
                      "catalog_service_name": "Chrono LLM",
                      "service_type": "http",
                      "is_active": true
                    }
                  ]
                }
                """),
            """
            {
              "services": [
                {
                  "id": "svc-catalog-id",
                  "slug": "chrono-llm",
                  "name": "Chrono LLM",
                  "description": "Shared LLM route",
                  "connected": true,
                  "requires_connection": false,
                  "proxy_url_slug": "https://nyx.test/api/v1/proxy/s/chrono-llm/{path}"
                }
              ]
            }
            """);

        var service = merged.Services.Should().ContainSingle().Subject;
        service.Source.Should().Be(NyxIdLlmProviderSource.UserService);
        service.CatalogEntryId.Should().Be("key-1");
    }

    [Fact]
    public void InactiveKeyDoesNotMakeServiceSelectable()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(NotConnectedProxyService()),
            """{"keys":[{"id":"key-1","slug":"chrono-llm","status":"active","is_active":false,"service_type":"http"}]}""");

        var service = merged.Services.Should().ContainSingle().Subject;
        service.Allowed.Should().BeFalse();
    }

    [Fact]
    public void PendingAuthKeyStatusPropagatesAsNotSelectable()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(),
            """{"keys":[{"id":"key-1","slug":"my-llm-endpoint","status":"pending_auth","is_active":true,"service_type":"http"}]}""");

        var service = merged.Services.Should().ContainSingle().Subject;
        service.Status.Should().Be("pending_auth");
        service.Allowed.Should().BeFalse();
    }

    [Fact]
    public void KeysWithoutLlmSignalsAreFilteredOut()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(),
            """
            {
              "keys": [
                {"id":"key-lark","slug":"api-lark-bot","label":"Lark Bot","status":"active","is_active":true,"service_type":"http"},
                {"id":"key-storage","slug":"chrono-storage-service","label":"Storage","status":"active","is_active":true,"service_type":"http"}
              ]
            }
            """);

        merged.Services.Should().BeEmpty();
    }

    [Fact]
    public void NonHttpKeysAreFilteredOut()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(),
            """{"keys":[{"id":"key-ssh","slug":"my-llm-box","label":"LLM box over ssh","status":"active","is_active":true,"service_type":"ssh"}]}""");

        merged.Services.Should().BeEmpty();
    }

    [Fact]
    public void StandaloneLlmKeyIsAppendedWhenCatalogHasNoMatchingEntry()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(NotConnectedProxyService("other-llm")),
            """{"keys":[{"id":"key-1","slug":"my-custom-llm","status":"active","is_active":true,"service_type":"http"}]}""");

        merged.Services.Should().HaveCount(2);
        merged.Services.Should().Contain(service =>
            service.ServiceSlug == "my-custom-llm" &&
            service.Allowed &&
            service.RouteValue == "/api/v1/proxy/s/my-custom-llm");
    }

    [Fact]
    public void BareArrayResponseIsTolerated()
    {
        var merged = NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(),
            """[{"id":"key-1","slug":"chrono-llm","status":"active","is_active":true,"service_type":"http"}]""");

        merged.Services.Should().ContainSingle(service => service.ServiceSlug == "chrono-llm");
    }

    [Fact]
    public void ErrorEnvelopeThrowsSoCallersCanDegradeSoftly()
    {
        var act = () => NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(
            ResultWith(),
            """{"error":true,"status":401,"body":"unauthorized"}""");

        act.Should().Throw<InvalidOperationException>();
    }
}
