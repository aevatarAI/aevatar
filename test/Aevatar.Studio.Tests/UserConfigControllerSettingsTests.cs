using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Hosting.Controllers;
using Aevatar.Studio.Hosting.NyxId;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Tests;

public sealed class UserConfigControllerSettingsTests
{
    [Fact]
    public async Task GetLlmSettings_ShouldReturnCanonicalSettingsView()
    {
        var httpHandler = new RecordingHttpHandler(
            (HttpStatusCode.OK, """
            {
              "services": [
                {
                  "user_service_id": "svc-openai",
                  "service_slug": "openai-work",
                  "display_name": "OpenAI Work",
                  "route_value": "/api/v1/proxy/s/openai-work",
                  "default_model": "gpt-5.4",
                  "models": ["gpt-5.4", "gpt-5.5"],
                  "status": "ready",
                  "source": "user",
                  "allowed": true
                }
              ]
            }
            """),
            (HttpStatusCode.OK, """{"keys":[]}"""),
            (HttpStatusCode.OK, """{"services":[]}"""))
            .RespondToUserServicesWith(PersonalUserServicesJson("us-openai", "openai-work", "OpenAI Work", "gpt-5.5"));
        var controller = CreateController(
            current: UserServiceConfig("gpt-5.4", "openai-work", "us-openai"),
            httpHandler: httpHandler,
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.CatalogStatus.Should().Be(UserLlmCatalogStatus.Ready);
        payload.SavedSelection!.RouteKind.Should().Be("nyx_id_user_service");
        payload.SavedSelection.RouteValue.Should().Be("/api/v1/proxy/s/openai-work");
        payload.SavedSelection.ModelSelection!.Kind.Should().Be("explicit_model");
        payload.SavedSelection.ModelSelection.ModelId.Should().Be("gpt-5.4");
        payload.SelectionStatus.Should().Be("ready");
        payload.RouteOptions.Should().Contain(option => option.RouteValue == UserConfigLlmRouteDefaults.Gateway);
        payload.RouteOptions.Should().Contain(option =>
            option.UserServiceId == "us-openai" &&
            option.Ready &&
            option.ModelCatalog.DefaultModelId == "gpt-5.5");
        payload.RouteOptions.Should()
            .ContainSingle(option => option.RouteValue == UserConfigLlmRouteDefaults.Gateway)
            .Which.ModelCatalog.Certainty.Should().Be("unavailable");
        payload.ModelGroupsByRoute.Should()
            .Contain(group => group.RouteValue == "/api/v1/proxy/s/openai-work" && group.Models.Contains("gpt-5.4"));
        httpHandler.Requests.Select(request => request.Path)
            .Should()
            .Equal(
                "/api/v1/llm/services",
                "/api/v1/keys",
                "/api/v1/proxy/services?per_page=100",
                "/api/v1/user-services");
    }

    [Fact]
    public async Task GetLlmSettings_WhenInventoryLacksModelCatalog_ShouldProbeProxyModelsAndPreferGpt55Default()
    {
        var httpHandler = new RecordingHttpHandler(
            (HttpStatusCode.OK, """{"services":[]}"""),
            (HttpStatusCode.OK, """{"keys":[]}"""),
            (HttpStatusCode.OK, """{"services":[]}"""))
            .RespondToUserServicesWith(PersonalUserServicesJson(
                "us-chrono-public",
                "chrono-llm-public",
                "Chrono LLM (public)"))
            .RespondToPathWith(
                "/api/v1/proxy/s/chrono-llm-public/models?_nyxid_via=us-chrono-public",
                """
                {
                  "object": "list",
                  "data": [
                    { "id": "gpt-5.4", "object": "model" },
                    { "id": "gpt-5.5", "object": "model" }
                  ]
                }
                """);
        var controller = CreateController(
            current: UserServiceConfig("", "chrono-llm-public", "us-chrono-public"),
            httpHandler: httpHandler,
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        var option = payload.RouteOptions.Should()
            .ContainSingle(candidate => candidate.UserServiceId == "us-chrono-public")
            .Subject;
        option.ModelCatalog.DefaultModelId.Should().Be("gpt-5.5");
        option.ModelCatalog.ModelIds.Should().Equal("gpt-5.4", "gpt-5.5");
        payload.ModelGroupsByRoute.Should()
            .ContainSingle(group => group.RouteValue == "/api/v1/proxy/s/chrono-llm-public")
            .Which.Models.Should().Equal("gpt-5.4", "gpt-5.5");
        httpHandler.Requests.Select(request => request.Path)
            .Should()
            .Equal(
                "/api/v1/llm/services",
                "/api/v1/keys",
                "/api/v1/proxy/services?per_page=100",
                "/api/v1/user-services",
                "/api/v1/proxy/s/chrono-llm-public/models?_nyxid_via=us-chrono-public");
    }

    [Fact]
    public async Task GetServicesAsync_ShouldMintOnlyStrictInventoryIdentities()
    {
        var httpHandler = new RecordingHttpHandler(
            (HttpStatusCode.OK, """
            {
              "services": [
                {
                  "user_service_id": "llm-diagnostic-id",
                  "service_slug": "chrono-llm",
                  "display_name": "Chrono LLM",
                  "route_value": "/api/v1/proxy/s/chrono-llm",
                  "status": "ready",
                  "source": "user_service",
                  "allowed": true
                }
              ]
            }
            """),
            (HttpStatusCode.OK, """
            {
              "keys": [
                {
                  "id": "key-alpha",
                  "slug": "chrono-llm",
                  "catalog_service_slug": "chrono-llm",
                  "catalog_service_name": "Chrono LLM",
                  "status": "active",
                  "service_type": "http",
                  "is_active": true
                }
              ]
            }
            """),
            (HttpStatusCode.OK, """
            {
              "services": [
                {
                  "id": "catalog-alpha",
                  "slug": "chrono-llm",
                  "name": "Chrono LLM",
                  "connected": true,
                  "requires_connection": false
                }
              ]
            }
            """))
            .RespondToUserServicesWith("""
            {
              "services": [
                {
                  "id": "us-beta",
                  "slug": "chrono-llm",
                  "label": "Chrono Beta",
                  "catalog_service_name": "Chrono LLM",
                  "is_active": true,
                  "credential_source": { "type": "personal" }
                },
                {
                  "id": "us-alpha",
                  "slug": "chrono-llm",
                  "label": "Chrono Alpha",
                  "catalog_service_name": "Chrono LLM",
                  "is_active": true,
                  "credential_source": { "type": "personal" }
                }
              ]
            }
            """)
            .RespondToPathWith(
                "/api/v1/proxy/s/chrono-llm/models?_nyxid_via=us-alpha",
                """{"data":[{"id":"model-alpha"}]}""")
            .RespondToPathWith(
                "/api/v1/proxy/s/chrono-llm/models?_nyxid_via=us-beta",
                """{"data":[{"id":"model-beta"}]}""");
        var catalog = CreateCatalogPort(httpHandler);

        var result = await catalog.GetServicesAsync("user-token-1", CancellationToken.None);

        result.Services.Select(service => service.Identity!.NyxIdUserServiceId)
            .Should()
            .Equal("us-alpha", "us-beta");
        result.Services.Should().OnlyContain(service =>
            service.Identity!.Authority == UserLlmIdentityAuthority.NyxIdUserServicesInventory);
        result.Services.Select(service => service.Identity!.NyxIdUserServiceId)
            .Should()
            .NotContain(["llm-diagnostic-id", "key-alpha", "catalog-alpha"]);
        result.Services.Single(service => service.Identity!.NyxIdUserServiceId == "us-alpha")
            .ModelCatalog.ModelIds.Should().Equal("model-alpha");
        result.Services.Single(service => service.Identity!.NyxIdUserServiceId == "us-beta")
            .ModelCatalog.ModelIds.Should().Equal("model-beta");
        httpHandler.Requests.Select(request => request.Path).Should().Contain("/api/v1/user-services");
        httpHandler.Requests
            .Select(request => request.Path)
            .Where(path => path.Contains("/models", StringComparison.Ordinal))
            .Should()
            .Equal(
                "/api/v1/proxy/s/chrono-llm/models?_nyxid_via=us-alpha",
                "/api/v1/proxy/s/chrono-llm/models?_nyxid_via=us-beta");
    }

    [Fact]
    public async Task GetServicesAsync_WhenUserServicesResponseIsMalformed_ShouldRejectCatalog()
    {
        var httpHandler = new RecordingHttpHandler(
            (HttpStatusCode.OK, """{"services":[]}"""),
            (HttpStatusCode.OK, """{"keys":[]}"""),
            (HttpStatusCode.OK, """{"services":[]}"""))
            .RespondToUserServicesWith("""{"services":[{"id":"us-alpha","slug":"chrono-llm"}]}""");
        var catalog = CreateCatalogPort(httpHandler);

        var act = () => catalog.GetServicesAsync("user-token-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        httpHandler.Requests.Select(request => request.Path).Should().Contain("/api/v1/user-services");
    }

    [Fact]
    public async Task GetServicesAsync_WithSplitNyxIdHosts_ShouldUseOnlyPublicApiBaseUrl()
    {
        var httpHandler = new RecordingHttpHandler(
            (HttpStatusCode.OK, """{"services":[]}"""),
            (HttpStatusCode.OK, """{"keys":[]}"""),
            (HttpStatusCode.OK, """{"services":[]}"""))
            .RespondToUserServicesWith("""{"services":[]}""");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:InternalApiBaseUrl"] = "http://nyxid.internal:3001",
                ["Aevatar:NyxId:ApiBaseUrl"] = "https://nyx-api.example.test",
                ["Aevatar:NyxId:Authority"] = "https://nyx-authority.example.test",
            })
            .Build();
        var catalog = CreateCatalogPort(httpHandler, configuration);

        await catalog.GetServicesAsync("user-token-1", CancellationToken.None);

        httpHandler.RequestUris.Should().HaveCount(4)
            .And.OnlyContain(uri => uri.StartsWith("https://nyx-api.example.test/", StringComparison.Ordinal));
        catalog.ResolveGatewayUrl().Should().Be("https://nyx-api.example.test/api/v1/llm/gateway/v1");
    }

    [Fact]
    public async Task GetLlmSettings_ShouldUseConfiguredGatewayRouteLabel()
    {
        var controller = CreateController(
            current: new UserConfig(string.Empty, string.Empty),
            httpHandler: new RecordingHttpHandler("""{"services":[]}"""),
            bearerToken: "user-token-1",
            llmSettingsOptions: new UserLlmSettingsOptions
            {
                GatewayRouteLabel = "Aevatar Gateway",
            });

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.SavedSelection.Should().BeNull();
        payload.SavedRouteLabel.Should().BeEmpty();
        payload.SelectionStatus.Should().Be("system_default");
        payload.RouteOptions.Should()
            .ContainSingle(option => option.RouteValue == UserConfigLlmRouteDefaults.Gateway)
            .Which.Label.Should().Be("Aevatar Gateway");
    }

    [Fact]
    public async Task GetLlmSettings_WhenCatalogFails_ShouldReturnDegradedView()
    {
        var controller = CreateController(
            current: UserServiceConfig("gpt-5.4", "openai-work", "us-openai"),
            httpHandler: new RecordingHttpHandler((HttpStatusCode.BadGateway, """{"error":"offline"}""")),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.CatalogStatus.Should().Be(UserLlmCatalogStatus.Unavailable);
        payload.SavedSelection!.RouteValue.Should().Be("/api/v1/proxy/s/openai-work");
        payload.SelectionStatus.Should().Be("verification_unavailable");
        payload.CatalogDiagnostic.Should().Be("observation_unavailable");
        payload.Remediation.Should().Be("retry_catalog");
        payload.RouteOptions.Should().ContainSingle().Which.Ready.Should().BeFalse();
        payload.Capabilities.CanSave.Should().BeFalse();
        payload.Capabilities.CanRetryCatalog.Should().BeTrue();
    }

    [Fact]
    public async Task GetLlmSettings_WhenSavedRouteUnavailable_ShouldRequireReplacement()
    {
        var controller = CreateController(
            current: UserServiceConfig("gpt-5.4", "missing", "us-missing"),
            httpHandler: new RecordingHttpHandler(SingleReadyServiceJson()),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.SavedSelection!.RouteValue.Should().Be("/api/v1/proxy/s/missing");
        payload.SelectionStatus.Should().Be("needs_repair");
        payload.Remediation.Should().Be("choose_replacement");
    }

    [Fact]
    public async Task GetLlmSettings_WhenCatalogIsEmpty_ShouldExposeClosedEmptyCapabilities()
    {
        var controller = CreateController(
            current: GatewayConfig("gpt-5.4"),
            httpHandler: new RecordingHttpHandler("""{"services":[]}"""),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.CatalogStatus.Should().Be(UserLlmCatalogStatus.Empty);
        payload.RouteOptions.Should().ContainSingle(option => option.RouteValue == UserConfigLlmRouteDefaults.Gateway);
        payload.ModelGroupsByRoute.Should().BeEmpty();
        payload.Capabilities.CanEditRoute.Should().BeFalse();
        payload.Capabilities.CanEditModel.Should().BeFalse();
        payload.Capabilities.CanSave.Should().BeFalse();
        payload.Capabilities.CanRetryCatalog.Should().BeFalse();
    }

    [Fact]
    public async Task GetLlmSettings_EligibleInventory_ShouldIgnoreDiagnosticAllowedFlag()
    {
        var controller = CreateController(
            current: UserServiceConfig("gpt-5.4", "openai-work", "us-openai"),
            httpHandler: new RecordingHttpHandler("""
            {
              "services": [
                {
                  "user_service_id": "svc-openai",
                  "service_slug": "openai-work",
                  "display_name": "OpenAI Work",
                  "route_value": "/api/v1/proxy/s/openai-work",
                  "default_model": "gpt-5.4",
                  "models": ["gpt-5.4"],
                  "status": "ready",
                  "source": "user",
                  "allowed": false
                }
              ]
            }
            """)
                .RespondToUserServicesWith(PersonalUserServicesJson("us-openai", "openai-work", "OpenAI Work")),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.SelectionStatus.Should().Be("needs_repair");
        payload.CatalogDiagnostic.Should().Be("access_denied");
        payload.RouteOptions.Should()
            .ContainSingle(option => option.RouteValue == "/api/v1/proxy/s/openai-work")
            .Which.Should().Match<UserLlmRouteOptionResponse>(option =>
                option.Status == UserLlmRouteStatus.Ready &&
                option.Allowed &&
                option.Ready &&
                option.UserServiceId == "us-openai");
        payload.Capabilities.CanSave.Should().BeFalse();
    }

    [Fact]
    public async Task GetLlmSettings_ShouldNotLeakUnknownExternalRouteStatusOrSource()
    {
        var controller = CreateController(
            current: new UserConfig("gpt-5.4", "/api/v1/proxy/s/openai-work"),
            httpHandler: new RecordingHttpHandler("""
            {
              "services": [
                {
                  "user_service_id": "svc-openai",
                  "service_slug": "openai-work",
                  "display_name": "OpenAI Work",
                  "route_value": "/api/v1/proxy/s/openai-work",
                  "default_model": "gpt-5.4",
                  "models": ["gpt-5.4"],
                  "status": "nyxid_custom_state",
                  "source": "external_vendor_source",
                  "allowed": true
                }
              ]
            }
            """)
                .RespondToUserServicesWith(PersonalUserServicesJson("us-openai", "openai-work", "OpenAI Work")),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.RouteOptions.Should().NotContain(option =>
            option.Status == "nyxid_custom_state" ||
            option.Source == "external_vendor_source");
        var allowedStatuses = new HashSet<string>(StringComparer.Ordinal)
        {
            UserLlmRouteStatus.Ready,
            UserLlmRouteStatus.Unavailable,
            UserLlmRouteStatus.Unknown,
        };
        var allowedSources = new HashSet<string>(StringComparer.Ordinal)
        {
            UserLlmRouteSource.GatewayProvider,
            UserLlmRouteSource.UserService,
            UserLlmRouteSource.ProxyService,
            UserLlmRouteSource.Unknown,
        };
        payload.RouteOptions.Select(option => option.Status).Should().OnlyContain(status => allowedStatuses.Contains(status));
        payload.RouteOptions.Select(option => option.Source).Should().OnlyContain(source => allowedSources.Contains(source));
    }

    [Fact]
    public async Task GetLlmSettings_LegacyStatusWithoutSource_ShouldRemainGatewayProviderAndNotBecomeUserServiceRoute()
    {
        var httpHandler = new RecordingHttpHandler(
            (HttpStatusCode.NotFound, """{"message":"not found"}"""),
            (HttpStatusCode.OK, """
            {
              "providers": [
                {
                  "provider_slug": "openai",
                  "provider_name": "OpenAI Gateway",
                  "status": "ready",
                  "proxy_url": "/api/v1/llm/openai/v1"
                }
              ],
              "supported_models": ["gpt-5.4"]
            }
            """),
            (HttpStatusCode.OK, """{"keys":[]}"""),
            (HttpStatusCode.OK, """{"services":[]}"""));
        var controller = CreateController(
            current: new UserConfig("gpt-5.4", "/api/v1/llm/openai/v1"),
            httpHandler: httpHandler,
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.RouteOptions.Should().NotContain(option => option.RouteValue == "/api/v1/llm/openai/v1");
        payload.RouteOptions.Should()
            .ContainSingle(option => option.RouteValue == UserConfigLlmRouteDefaults.Gateway)
            .Which.Should().Match<UserLlmRouteOptionResponse>(option =>
                option.Source == UserLlmRouteSource.GatewayProvider &&
                option.Status == UserLlmRouteStatus.Ready &&
                option.Allowed &&
                option.Ready);
        payload.ModelGroupsByRoute.Should().ContainSingle(group =>
            group.RouteValue == UserConfigLlmRouteDefaults.Gateway &&
            group.Models.Contains("gpt-5.4"));
        payload.SelectionStatus.Should().Be("legacy_repair_required");
        httpHandler.Requests.Select(request => request.Path)
            .Should()
            .Equal(
                "/api/v1/llm/services",
                "/api/v1/llm/status",
                "/api/v1/keys",
                "/api/v1/proxy/services?per_page=100",
                "/api/v1/user-services");
    }

    [Fact]
    public async Task GetLlmSettings_EligibleInventory_ShouldUseKeyReadinessOverMisleadingProxyServices()
    {
        var httpHandler = new RecordingHttpHandler(
            (HttpStatusCode.OK, """{"services":[]}"""),
            (HttpStatusCode.OK, """
            {
              "keys": [
                {
                  "id": "key-chrono",
                  "label": "Chrono LLM",
                  "slug": "chrono-llm-personal",
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
            (HttpStatusCode.OK, """
            {
              "services": [
                {
                  "id": "svc-chrono",
                  "slug": "chrono-llm",
                  "name": "Chrono LLM",
                  "description": "Shared LLM route",
                  "connected": false,
                  "requires_connection": true,
                  "proxy_url_slug": "https://nyxid.example/api/v1/proxy/s/chrono-llm/{path}"
                }
              ]
            }
            """))
            .RespondToUserServicesWith(PersonalUserServicesJson("us-chrono", "chrono-llm", "Chrono LLM"))
            .RespondToPathWith(
                "/api/v1/proxy/s/chrono-llm/models?_nyxid_via=us-chrono",
                """{"data":[]}""");
        var controller = CreateController(
            current: UserServiceConfig("gpt-5.5", "chrono-llm", "us-chrono"),
            httpHandler: httpHandler,
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        var chrono = payload.RouteOptions.Should()
            .ContainSingle(option => option.ServiceSlug == "chrono-llm")
            .Subject;
        chrono.UserServiceId.Should().Be("us-chrono");
        chrono.Source.Should().Be(UserLlmRouteSource.UserService);
        chrono.Ready.Should().BeTrue();
        payload.SavedSelection!.RouteValue.Should().Be("/api/v1/proxy/s/chrono-llm");
        payload.SelectionStatus.Should().Be("needs_repair");
        httpHandler.Requests.Select(request => request.Path)
            .Should()
            .Equal(
                "/api/v1/llm/services",
                "/api/v1/keys",
                "/api/v1/proxy/services?per_page=100",
                "/api/v1/user-services",
                "/api/v1/proxy/s/chrono-llm/models?_nyxid_via=us-chrono");
    }

    [Fact]
    public async Task GetLlmSettings_ShouldExcludeDisallowedOrganizationInventoryEvenWhenKeyIsActive()
    {
        var httpHandler = new RecordingHttpHandler(
            (HttpStatusCode.OK, """{"services":[]}"""),
            (HttpStatusCode.OK, """
            {
              "keys": [
                {
                  "id": "key-chrono",
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
            """),
            (HttpStatusCode.OK, """{"services":[]}"""))
            .RespondToUserServicesWith("""
            {
              "services": [
                {
                  "id": "us-chrono",
                  "slug": "chrono-llm",
                  "label": "Chrono LLM",
                  "catalog_service_name": "Chrono LLM",
                  "is_active": true,
                  "credential_source": {
                    "type": "org",
                    "org_id": "org-1",
                    "org_name": "Org",
                    "role": "viewer",
                    "allowed": false
                  }
                }
              ]
            }
            """);
        var controller = CreateController(
            current: new UserConfig("gpt-5.5", "/api/v1/proxy/s/chrono-llm"),
            httpHandler: httpHandler,
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.RouteOptions.Should().NotContain(option => option.ServiceSlug == "chrono-llm");
        payload.SelectionStatus.Should().Be("legacy_repair_required");
    }

    [Fact]
    public async Task GetLlmSettings_ServicesArrayWithoutInventory_ShouldNotMintUserServiceRouteFromSourceDefault()
    {
        var controller = CreateController(
            current: new UserConfig("gpt-5.4", "/api/v1/proxy/s/openai-work"),
            httpHandler: new RecordingHttpHandler("""
            {
              "services": [
                {
                  "user_service_id": "svc-openai",
                  "service_slug": "openai-work",
                  "display_name": "OpenAI Work",
                  "route_value": "/api/v1/proxy/s/openai-work",
                  "default_model": "gpt-5.4",
                  "models": ["gpt-5.4"],
                  "status": "ready",
                  "allowed": true
                }
              ]
            }
            """),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.RouteOptions.Should().NotContain(option =>
            option.RouteValue == "/api/v1/proxy/s/openai-work");
        payload.SelectionStatus.Should().Be("legacy_repair_required");
        payload.CatalogStatus.Should().Be(UserLlmCatalogStatus.Empty);
        payload.Capabilities.CanSave.Should().BeFalse();
    }

    [Fact]
    public async Task GetLlmSettings_ServicesArrayWithoutInventory_ShouldNotMintUserServiceRouteFromMissingAllowed()
    {
        var controller = CreateController(
            current: new UserConfig("gpt-5.4", "/api/v1/proxy/s/openai-work"),
            httpHandler: new RecordingHttpHandler("""
            {
              "services": [
                {
                  "user_service_id": "svc-openai",
                  "service_slug": "openai-work",
                  "display_name": "OpenAI Work",
                  "route_value": "/api/v1/proxy/s/openai-work",
                  "default_model": "gpt-5.4",
                  "models": ["gpt-5.4"],
                  "status": "ready"
                }
              ]
            }
            """),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.RouteOptions.Should().NotContain(option =>
            option.RouteValue == "/api/v1/proxy/s/openai-work");
        payload.SelectionStatus.Should().Be("legacy_repair_required");
    }

    [Fact]
    public async Task SaveLlmSettings_WithReset_ShouldPersistUnspecifiedWithoutReadOrCatalog()
    {
        var commandService = new RecordingUserConfigCommandService();
        var queryPort = new StubUserConfigQueryPort(new UserConfig("old-model", "/api/v1/proxy/s/old"));
        var httpHandler = new RecordingHttpHandler("""{"services":[]}""");
        var controller = CreateController(
            queryPort: queryPort,
            commandService: commandService,
            httpHandler: httpHandler,
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmSettings(
            new SaveUserLlmSettingsRequest("reset"),
            CancellationToken.None);

        var accepted = response.Result.Should().BeOfType<AcceptedResult>().Subject;
        var payload = accepted.Value.Should().BeOfType<UserConfigSaveReceiptResponse>().Subject;
        payload.Accepted.Should().BeTrue();
        payload.AckStage.Should().Be(UserConfigCommandAckStage.Accepted);
        var update = commandService.Updates.Should().ContainSingle().Which.Update;
        update.LlmSelection!.RouteKind.Should().Be(LLMRouteKind.Unspecified);
        update.LlmSelection.ModelSelection.Kind.Should().Be(LLMModelSelectionKind.Unspecified);
        queryPort.ReadCount.Should().Be(0);
        httpHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void SaveLlmSettingsWireContract_ShouldMapEachClosedAction()
    {
        new SaveUserLlmSettingsRequest("reset").ToIntent()
            .Should().BeOfType<ResetUserLlmPreferenceIntent>();
        new SaveUserLlmSettingsRequest(
                "select_gateway",
                Gateway: new SelectGatewayRequest(new UserLlmModelSelectionRequest("provider_default")))
            .ToIntent().Should().BeOfType<SelectGatewayUserLlmPreferenceIntent>();
        new SaveUserLlmSettingsRequest(
                "select_user_service",
                UserService: new SelectUserServiceRequest(
                    "us-alpha",
                    new UserLlmModelSelectionRequest("explicit_model", "gpt-5.5")))
            .ToIntent().Should().BeEquivalentTo(new SelectUserServiceUserLlmPreferenceIntent(
                "us-alpha",
                new LLMModelSelection
                {
                    Kind = LLMModelSelectionKind.ExplicitModel,
                    ModelId = "gpt-5.5",
                }));
        new SaveUserLlmSettingsRequest(
                "activate_preset",
                Preset: new ActivatePresetRequest("chrono"))
            .ToIntent().Should().BeEquivalentTo(new ActivateUserLlmPresetIntent("chrono"));
    }

    [Fact]
    public async Task GetLlmSettings_ResponseJson_ShouldNotExposeFallbackFields()
    {
        var controller = CreateController(
            current: UserServiceConfig("gpt-5.4", "openai-work", "us-openai"),
            httpHandler: new RecordingHttpHandler(SingleReadyServiceJson())
                .RespondToUserServicesWith(PersonalUserServicesJson("us-openai", "openai-work", "OpenAI Work")),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContain("effectiveRoute");
        json.Should().NotContain("effectiveRouteLabel");
        json.Should().NotContain("routeFallbackActive");
        json.Should().NotContain("fallbackReason");
    }

    [Fact]
    public void SaveReceipt_ResponseJson_ShouldDescribeAcceptedDispatchOnly()
    {
        var response = UserConfigSaveReceiptResponse.FromApplication(new UserConfigSaveReceipt(
            Accepted: true,
            CommandId: "command-alpha",
            AckStage: UserConfigCommandAckStage.Accepted,
            ActorId: "actor-alpha",
            CorrelationId: "corr-alpha",
            AckedAtUtc: DateTimeOffset.UnixEpoch));

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        response.AckStage.Should().Be("accepted");
        json.Should().NotContain("committed");
        json.Should().NotContain("saved");
        json.Should().NotContain("active");
    }

    [Fact]
    public void SaveUserConfigRequest_ShouldRejectDefaultModelJsonMember()
    {
        var act = () => JsonSerializer.Deserialize<UserConfigController.SaveUserConfigRequest>(
            """{"defaultModel":"gpt-5.5"}""");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public async Task SaveLlmSettings_WhenAdmissionIsRejected_ShouldReturnReceiptWithTracingFields()
    {
        var ackedAt = new DateTimeOffset(2026, 5, 26, 12, 30, 0, TimeSpan.Zero);
        var commandService = new RecordingUserConfigCommandService
        {
            NextReceipt = new UserConfigSaveReceipt(
                Accepted: false,
                CommandId: "rejected-command",
                AckStage: UserConfigCommandAckStage.AdmissionRejected,
                ActorId: "user-config-scope-1",
                CorrelationId: "corr-1",
                AckedAtUtc: ackedAt),
        };
        var controller = CreateController(
            current: new UserConfig("old-model", "/api/v1/proxy/s/old"),
            commandService: commandService,
            httpHandler: new RecordingHttpHandler("""{"services":[]}"""),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmSettings(
            new SaveUserLlmSettingsRequest("reset"),
            CancellationToken.None);

        var accepted = response.Result.Should().BeOfType<AcceptedResult>().Subject;
        var payload = accepted.Value.Should().BeOfType<UserConfigSaveReceiptResponse>().Subject;
        payload.Accepted.Should().BeFalse();
        payload.CommandId.Should().Be("rejected-command");
        payload.AckStage.Should().Be(UserConfigCommandAckStage.AdmissionRejected);
        payload.ActorId.Should().Be("user-config-scope-1");
        payload.CorrelationId.Should().Be("corr-1");
        payload.AckedAtUtc.Should().Be(ackedAt);
    }

    [Fact]
    public async Task SaveLlmSettings_WhenCommandIsEmpty_ShouldReturnBadRequest()
    {
        var controller = CreateController(
            current: new UserConfig(string.Empty),
            httpHandler: new RecordingHttpHandler("""{"services":[]}"""),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmSettings(
            new SaveUserLlmSettingsRequest("unknown"),
            CancellationToken.None);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SaveLlmSettings_WhenRequestBodyIsMissing_ShouldReturnBadRequest()
    {
        var controller = CreateController(
            current: new UserConfig(string.Empty),
            httpHandler: new RecordingHttpHandler("""{"services":[]}"""),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmSettings(null, CancellationToken.None);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SaveLlmSettings_WithMixedPayload_ShouldReturnBadRequestBeforeCatalog()
    {
        var controller = CreateController(
            current: new UserConfig(string.Empty),
            httpHandler: new RecordingHttpHandler(SingleReadyServiceJson()),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmSettings(
            new SaveUserLlmSettingsRequest(
                "reset",
                Gateway: new SelectGatewayRequest(
                    new UserLlmModelSelectionRequest("provider_default"))),
            CancellationToken.None);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
        controller.HttpContext.Should().NotBeNull();
    }

    [Fact]
    public async Task GetRuntime_ShouldReturnBackendRuntimeContract()
    {
        var controller = CreateController(
            current: new UserConfig(
                DefaultModel: string.Empty,
                RuntimeMode: "REMOTE",
                LocalRuntimeBaseUrl: "http://127.0.0.1:5080/",
                RemoteRuntimeBaseUrl: "https://runtime.example.com/"));

        var response = await controller.GetRuntime(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserConfigRuntimeView>().Subject;
        payload.RuntimeMode.Should().Be(UserConfigRuntimeDefaults.RemoteMode);
        payload.ActiveRuntimeBaseUrl.Should().Be("https://runtime.example.com");
        payload.LocalRuntimeBaseUrl.Should().Be("http://127.0.0.1:5080");
        payload.RuntimeDefaults.LocalRuntimeBaseUrl.Should().Be(UserConfigRuntimeDefaults.LocalRuntimeBaseUrl);
    }

    private static UserConfigController CreateController(
        UserConfig? current = null,
        StubUserConfigQueryPort? queryPort = null,
        RecordingUserConfigCommandService? commandService = null,
        RecordingHttpHandler? httpHandler = null,
        string? bearerToken = null,
        UserLlmSettingsOptions? llmSettingsOptions = null)
    {
        commandService ??= new RecordingUserConfigCommandService();
        queryPort ??= new StubUserConfigQueryPort(current ?? new UserConfig(string.Empty));
        var catalogPort = CreateCatalogPort(
            httpHandler ?? new RecordingHttpHandler("""{"services":[]}"""));
        var settingsService = new UserLlmPreferenceService(
            queryPort,
            catalogPort,
            Options.Create(llmSettingsOptions ?? new UserLlmSettingsOptions()));
        var configService = new UserConfigService(
            queryPort,
            commandService,
            new UserLlmPreferenceWriter(commandService, catalogPort),
            new StubScopeResolver("scope-alpha"));
        var controller = new UserConfigController(
            configService,
            settingsService,
            NullLogger<UserConfigController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        if (!string.IsNullOrWhiteSpace(bearerToken))
            controller.ControllerContext.HttpContext.Request.Headers.Authorization = $"Bearer {bearerToken}";

        return controller;
    }

    private static IConfiguration BuildNyxIdConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:Authority"] = "https://nyxid.example",
        })
        .Build();

    private static NyxIdLlmCatalogHttpClient CreateCatalogPort(
        RecordingHttpHandler httpHandler,
        IConfiguration? configuration = null) => new(
        new StubHttpClientFactory(httpHandler),
        configuration ?? BuildNyxIdConfiguration(),
        NullLogger<NyxIdLlmCatalogHttpClient>.Instance);

    private static string PersonalUserServicesJson(string id, string slug, string label, string? defaultModel = null)
    {
        if (string.IsNullOrWhiteSpace(defaultModel))
        {
            return $$"""
                {
                  "services": [
                    {
                      "id": "{{id}}",
                      "slug": "{{slug}}",
                      "label": "{{label}}",
                      "catalog_service_name": "{{label}}",
                      "is_active": true,
                      "credential_source": { "type": "personal" }
                    }
                  ]
                }
                """;
        }

        return $$"""
            {
              "services": [
                {
                  "id": "{{id}}",
                  "slug": "{{slug}}",
                  "label": "{{label}}",
                  "catalog_service_name": "{{label}}",
                  "is_active": true,
                  "credential_source": { "type": "personal" },
                  "default_model": "{{defaultModel}}"
                }
              ]
            }
            """;
    }

    private static string SingleReadyServiceJson() => """
        {
          "services": [
            {
              "user_service_id": "svc-openai",
              "service_slug": "openai-work",
              "display_name": "OpenAI Work",
              "route_value": "/api/v1/proxy/s/openai-work",
              "default_model": "gpt-5.4",
              "models": ["gpt-5.4"],
              "status": "ready",
              "source": "user",
              "allowed": true
            }
          ]
        }
        """;

    private static UserConfig UserServiceConfig(string defaultModel, string serviceSlug, string userServiceId)
    {
        var route = $"/api/v1/proxy/s/{serviceSlug}";
        return new UserConfig(
            DefaultModel: defaultModel,
            PreferredLlmRoute: route,
            LlmSelection: new LLMSelection
            {
                RouteKind = LLMRouteKind.NyxIdUserService,
                RouteValue = route,
                NyxIdUserServiceId = userServiceId,
                ServiceSlugSnapshot = serviceSlug,
                ModelSelection = new LLMModelSelection
                {
                    Kind = LLMModelSelectionKind.ExplicitModel,
                    ModelId = defaultModel,
                },
            });
    }

    private static UserConfig GatewayConfig(string defaultModel) => new(
        DefaultModel: defaultModel,
        PreferredLlmRoute: UserConfigLlmRouteDefaults.Gateway,
        LlmSelection: new LLMSelection
        {
            RouteKind = LLMRouteKind.Gateway,
            RouteValue = UserConfigLlmRouteDefaults.Gateway,
            ModelSelection = new LLMModelSelection
            {
                Kind = LLMModelSelectionKind.ExplicitModel,
                ModelId = defaultModel,
            },
        });

    private sealed class StubUserConfigQueryPort(UserConfig config) : IUserConfigQueryPort
    {
        public int ReadCount { get; private set; }

        public Task<UserConfig> GetAsync(CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(config);
        }

        public Task<UserConfig> GetAsync(UserConfigResourceKey resource, CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(config);
        }
    }

    private sealed class RecordingUserConfigCommandService : IUserConfigCommandService
    {
        public List<(UserConfigResourceKey Resource, UserConfigUpdate Update)> Updates { get; } = [];
        public UserConfigSaveReceipt? NextReceipt { get; init; }

        public Task<UserConfigSaveReceipt> UpdateAsync(
            UserConfigResourceKey resource,
            UserConfigUpdate update,
            CancellationToken ct = default)
        {
            Updates.Add((resource, update));
            return Task.FromResult(NextReceipt ?? new UserConfigSaveReceipt(
                Accepted: true,
                CommandId: "command-1",
                AckStage: UserConfigCommandAckStage.Accepted,
                ActorId: "user-config-default",
                CorrelationId: "command-1",
                AckedAtUtc: DateTimeOffset.UtcNow));
        }
    }

    private sealed class StubScopeResolver(string scopeId) : IAppScopeResolver
    {
        public AppScopeContext? Resolve(HttpContext? httpContext = null) => new(scopeId, "test");

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) => false;

        public bool HasHttpRequestContext(HttpContext? httpContext = null) => false;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode StatusCode, string Body)> _responses;
        private readonly (HttpStatusCode StatusCode, string Body) _fallback;
        private readonly Dictionary<string, (HttpStatusCode StatusCode, string Body)> _pathResponses = new(StringComparer.Ordinal);
        private (HttpStatusCode StatusCode, string Body) _userServicesResponse =
            (HttpStatusCode.OK, """{"services":[]}""");

        public RecordingHttpHandler(string body)
            : this((HttpStatusCode.OK, body))
        {
        }

        public RecordingHttpHandler(params (HttpStatusCode StatusCode, string Body)[] responses)
        {
            _responses = new Queue<(HttpStatusCode StatusCode, string Body)>(responses);
            _fallback = responses.LastOrDefault();
            if (_fallback == default)
                _fallback = (HttpStatusCode.OK, string.Empty);
        }

        public List<(string Path, string Method, string? Authorization, string Body)> Requests { get; } = [];
        public List<string> RequestUris { get; } = [];

        public RecordingHttpHandler RespondToUserServicesWith(
            string body,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _userServicesResponse = (statusCode, body);
            return this;
        }

        public RecordingHttpHandler RespondToPathWith(
            string path,
            string body,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _pathResponses[path] = (statusCode, body);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            RequestUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            Requests.Add((
                pathAndQuery,
                request.Method.Method,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            var response = _pathResponses.TryGetValue(pathAndQuery, out var pathResponse)
                ? pathResponse
                : request.RequestUri?.AbsolutePath == "/api/v1/user-services"
                    ? _userServicesResponse
                    : _responses.Count > 0
                        ? _responses.Dequeue()
                        : _fallback;
            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
