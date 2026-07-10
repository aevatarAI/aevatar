using System.Net;
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
                  "models": ["gpt-5.4"],
                  "status": "ready",
                  "source": "user",
                  "allowed": true
                }
              ]
            }
            """),
            (HttpStatusCode.OK, """{"keys":[]}"""),
            (HttpStatusCode.OK, """{"services":[]}"""));
        var controller = CreateController(
            current: new UserConfig("gpt-5.4", "/api/v1/proxy/s/openai-work"),
            httpHandler: httpHandler,
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.CatalogStatus.Should().Be(UserLlmCatalogStatus.Ready);
        payload.SavedRoute.Should().Be("/api/v1/proxy/s/openai-work");
        payload.EffectiveRoute.Should().Be("/api/v1/proxy/s/openai-work");
        payload.DefaultModel.Should().Be("gpt-5.4");
        payload.RouteOptions.Should().Contain(option => option.RouteValue == UserConfigLlmRouteDefaults.Gateway);
        payload.RouteOptions.Should().Contain(option => option.ServiceId == "svc-openai" && option.Ready);
        payload.ModelGroupsByRoute.Should()
            .Contain(group => group.RouteValue == "/api/v1/proxy/s/openai-work" && group.Models.Contains("gpt-5.4"));
        httpHandler.Requests.Select(request => request.Path)
            .Should()
            .Equal("/api/v1/llm/services", "/api/v1/keys", "/api/v1/proxy/services?per_page=100");
    }

    [Fact]
    public async Task GetLlmSettings_ShouldUseConfiguredGatewayRouteLabel()
    {
        var controller = CreateController(
            current: new UserConfig(string.Empty),
            httpHandler: new RecordingHttpHandler("""{"services":[]}"""),
            bearerToken: "user-token-1",
            llmSettingsOptions: new UserLlmSettingsOptions
            {
                GatewayRouteLabel = "Aevatar Gateway",
            });

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.SavedRouteLabel.Should().Be("Aevatar Gateway");
        payload.EffectiveRouteLabel.Should().Be("Aevatar Gateway");
        payload.RouteOptions.Should()
            .ContainSingle(option => option.RouteValue == UserConfigLlmRouteDefaults.Gateway)
            .Which.Label.Should().Be("Aevatar Gateway");
    }

    [Fact]
    public async Task GetLlmSettings_WhenCatalogFails_ShouldReturnDegradedView()
    {
        var controller = CreateController(
            current: new UserConfig("gpt-5.4", "/api/v1/proxy/s/openai-work"),
            httpHandler: new RecordingHttpHandler((HttpStatusCode.BadGateway, """{"error":"offline"}""")),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.CatalogStatus.Should().Be(UserLlmCatalogStatus.Unavailable);
        payload.SavedRoute.Should().Be("/api/v1/proxy/s/openai-work");
        payload.FallbackReason.Should().Be("catalog_unavailable");
        payload.RouteOptions.Should().ContainSingle().Which.Ready.Should().BeFalse();
        payload.Capabilities.CanSave.Should().BeFalse();
        payload.Capabilities.CanRetryCatalog.Should().BeTrue();
    }

    [Fact]
    public async Task GetLlmSettings_WhenSavedRouteUnavailable_ShouldFallbackToGateway()
    {
        var controller = CreateController(
            current: new UserConfig("gpt-5.4", "/api/v1/proxy/s/missing"),
            httpHandler: new RecordingHttpHandler(SingleReadyServiceJson()),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.SavedRoute.Should().Be("/api/v1/proxy/s/missing");
        payload.EffectiveRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        payload.RouteFallbackActive.Should().BeTrue();
        payload.FallbackReason.Should().Be(UserLlmFallbackReason.SavedRouteUnavailable);
    }

    [Fact]
    public async Task GetLlmSettings_WhenCatalogIsEmpty_ShouldExposeClosedEmptyCapabilities()
    {
        var controller = CreateController(
            current: new UserConfig("gpt-5.4", UserConfigLlmRouteDefaults.Gateway),
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
        payload.Capabilities.CanSave.Should().BeTrue();
        payload.Capabilities.CanRetryCatalog.Should().BeFalse();
    }

    [Fact]
    public async Task GetLlmSettings_WhenRouteIsReadyButNotAllowed_ShouldNotTreatItAsSaveableOrEffective()
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
                  "source": "user",
                  "allowed": false
                }
              ]
            }
            """),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.EffectiveRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        payload.RouteFallbackActive.Should().BeTrue();
        payload.RouteOptions.Should()
            .ContainSingle(option => option.RouteValue == "/api/v1/proxy/s/openai-work")
            .Which.Should().Match<UserLlmRouteOptionResponse>(option =>
                option.Status == UserLlmRouteStatus.Ready &&
                !option.Allowed &&
                !option.Ready);
        payload.Capabilities.CanSave.Should().BeTrue();
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
            """),
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
        payload.ModelGroupsByRoute.Should()
            .Contain(group =>
                group.RouteValue == UserConfigLlmRouteDefaults.Gateway &&
                group.GroupId == "openai" &&
                group.Models.Contains("gpt-5.4"));
        payload.EffectiveRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        httpHandler.Requests.Select(request => request.Path)
            .Should()
            .Equal("/api/v1/llm/services", "/api/v1/llm/status", "/api/v1/keys", "/api/v1/proxy/services?per_page=100");
    }

    [Fact]
    public async Task GetLlmSettings_ShouldPreferActiveUserKeyOverMisleadingProxyServices()
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
            """));
        var controller = CreateController(
            current: new UserConfig("gpt-5.5", "/api/v1/proxy/s/chrono-llm"),
            httpHandler: httpHandler,
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        var chrono = payload.RouteOptions.Should()
            .ContainSingle(option => option.ServiceSlug == "chrono-llm")
            .Subject;
        chrono.ServiceId.Should().Be("key-chrono");
        chrono.Source.Should().Be(UserLlmRouteSource.UserService);
        chrono.Ready.Should().BeTrue();
        payload.EffectiveRoute.Should().Be("/api/v1/proxy/s/chrono-llm");
        httpHandler.Requests.Select(request => request.Path)
            .Should()
            .Equal("/api/v1/llm/services", "/api/v1/keys", "/api/v1/proxy/services?per_page=100");
    }

    [Fact]
    public async Task GetLlmSettings_ShouldNotTreatOrgViewerKeyAsSelectable()
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
            (HttpStatusCode.OK, """{"services":[]}"""));
        var controller = CreateController(
            current: new UserConfig("gpt-5.5", "/api/v1/proxy/s/chrono-llm"),
            httpHandler: httpHandler,
            bearerToken: "user-token-1");

        var response = await controller.GetLlmSettings(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmSettingsResponse>().Subject;
        payload.RouteOptions.Should()
            .ContainSingle(option => option.ServiceSlug == "chrono-llm")
            .Which.Should().Match<UserLlmRouteOptionResponse>(option =>
                option.Source == UserLlmRouteSource.UserService &&
                option.Status == UserLlmRouteStatus.Ready &&
                !option.Allowed &&
                !option.Ready);
        payload.EffectiveRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
    }

    [Fact]
    public async Task GetLlmSettings_ServicesArrayWithoutSource_ShouldDefaultToUserServiceRouteWhenAllowedAndReady()
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
        payload.RouteOptions.Should()
            .ContainSingle(option => option.RouteValue == "/api/v1/proxy/s/openai-work")
            .Which.Should().Match<UserLlmRouteOptionResponse>(option =>
                option.Source == UserLlmRouteSource.UserService &&
                option.Status == UserLlmRouteStatus.Ready &&
                option.Allowed &&
                option.Ready);
        payload.EffectiveRoute.Should().Be("/api/v1/proxy/s/openai-work");
        payload.Capabilities.CanSave.Should().BeTrue();
    }

    [Fact]
    public async Task GetLlmSettings_ServicesArrayWithoutAllowed_ShouldDefaultClosed()
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
        payload.RouteOptions.Should()
            .ContainSingle(option => option.RouteValue == "/api/v1/proxy/s/openai-work")
            .Which.Should().Match<UserLlmRouteOptionResponse>(option =>
                option.Source == UserLlmRouteSource.UserService &&
                option.Status == UserLlmRouteStatus.Ready &&
                !option.Allowed &&
                !option.Ready);
        payload.EffectiveRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        payload.RouteFallbackActive.Should().BeTrue();
    }

    [Fact]
    public async Task SaveLlmSettings_WithGatewayRoute_ShouldPersistEmptyRoute()
    {
        var commandService = new RecordingUserConfigCommandService();
        var queryPort = new StubUserConfigQueryPort(new UserConfig("old-model", "/api/v1/proxy/s/old"));
        var controller = CreateController(
            queryPort: queryPort,
            commandService: commandService,
            httpHandler: new RecordingHttpHandler("""{"services":[]}"""),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmSettings(
            new SaveUserLlmSettingsRequest(RouteValue: string.Empty, Model: " gpt-5.4 "),
            CancellationToken.None);

        var accepted = response.Result.Should().BeOfType<AcceptedResult>().Subject;
        var payload = accepted.Value.Should().BeOfType<UserConfigSaveReceiptResponse>().Subject;
        payload.Accepted.Should().BeTrue();
        payload.AckStage.Should().Be(UserConfigCommandAckStage.Accepted);
        commandService.Saved.Should().ContainSingle()
            .Which.Should().Match<UserConfig>(config =>
                config.PreferredLlmRoute == UserConfigLlmRouteDefaults.Gateway &&
                config.DefaultModel == "gpt-5.4");
        queryPort.ReadCount.Should().Be(1);
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
            new SaveUserLlmSettingsRequest(RouteValue: string.Empty, Model: "gpt-5.4"),
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
    public async Task SaveLlmSettings_WhenRouteValueIsMissing_ShouldReturnBadRequest()
    {
        var controller = CreateController(
            current: new UserConfig(string.Empty),
            httpHandler: new RecordingHttpHandler("""{"services":[]}"""),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmSettings(
            new SaveUserLlmSettingsRequest(RouteValue: null, Model: "gpt-5.4"),
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
    public async Task SaveLlmSettings_WithUnknownRoute_ShouldReturnBadRequest()
    {
        var controller = CreateController(
            current: new UserConfig(string.Empty),
            httpHandler: new RecordingHttpHandler(SingleReadyServiceJson()),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmSettings(
            new SaveUserLlmSettingsRequest(RouteValue: "/api/v1/proxy/s/missing"),
            CancellationToken.None);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
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
        var catalogPort = new NyxIdLlmCatalogHttpClient(
            new StubHttpClientFactory(httpHandler ?? new RecordingHttpHandler("""{"services":[]}""")),
            BuildNyxIdConfiguration(),
            NullLogger<NyxIdLlmCatalogHttpClient>.Instance);
        var settingsService = new UserLlmPreferenceService(
            queryPort,
            catalogPort,
            Options.Create(llmSettingsOptions ?? new UserLlmSettingsOptions()));
        var configService = new UserConfigService(
            queryPort,
            commandService,
            new UserLlmPreferenceWriter(queryPort, commandService, catalogPort));
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

    private sealed class StubUserConfigQueryPort(UserConfig config) : IUserConfigQueryPort
    {
        public int ReadCount { get; private set; }

        public Task<UserConfig> GetAsync(CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(config);
        }

        public Task<UserConfig> GetAsync(string scopeId, CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(config);
        }
    }

    private sealed class RecordingUserConfigCommandService : IUserConfigCommandService
    {
        public List<UserConfig> Saved { get; } = [];
        public UserConfigSaveReceipt? NextReceipt { get; init; }

        public Task<UserConfigSaveReceipt> SaveAsync(UserConfig config, CancellationToken ct = default)
        {
            Saved.Add(config);
            return Task.FromResult(NextReceipt ?? new UserConfigSaveReceipt(
                Accepted: true,
                CommandId: "command-1",
                AckStage: UserConfigCommandAckStage.Accepted,
                ActorId: "user-config-default",
                CorrelationId: "command-1",
                AckedAtUtc: DateTimeOffset.UtcNow));
        }

        public Task<UserConfigSaveReceipt> SaveAsync(string scopeId, UserConfig config, CancellationToken ct = default) =>
            SaveAsync(config, ct);

        public Task<UserConfigSaveReceipt> SaveGithubUsernameAsync(string scopeId, string githubUsername, CancellationToken ct = default) =>
            Task.FromResult(new UserConfigSaveReceipt(
                Accepted: true,
                CommandId: "command-github",
                AckStage: UserConfigCommandAckStage.Accepted,
                ActorId: "user-config-default",
                CorrelationId: "command-github",
                AckedAtUtc: DateTimeOffset.UtcNow));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode StatusCode, string Body)> _responses;
        private readonly (HttpStatusCode StatusCode, string Body) _fallback;

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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Method.Method,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            var response = _responses.Count > 0 ? _responses.Dequeue() : _fallback;
            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
