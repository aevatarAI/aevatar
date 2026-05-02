using System.Net.Http;
using System.Net;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Hosting.Controllers;
using Aevatar.Studio.Hosting.NyxId;
using Aevatar.Studio.Projection.DependencyInjection;
using Aevatar.Studio.Projection.Metadata;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class UserConfigProjectionAndControllerTests
{
    [Fact]
    public async Task AddStudioProjectionComponents_RegistersPortsAndDispatchesNormalizedEvent()
    {
        var services = new ServiceCollection();
        var dispatchPort = new RecordingActorDispatchPort();
        var actorRuntime = new StubActorRuntime();
        var scopeResolver = new StubScopeResolver { ScopeIdToReturn = "scope-1" };
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        services.AddSingleton<IActorRuntime>(actorRuntime);
        services.AddSingleton<IAppScopeResolver>(scopeResolver);
        services.AddSingleton<IUserConfigDefaults>(new StubUserConfigDefaults());
        services.AddSingleton<IProjectionDocumentReader<UserConfigCurrentStateDocument, string>>(
            new StubUserConfigDocumentReader());
        services.AddStudioProjectionComponents();

        await using var provider = services.BuildServiceProvider();
        var commandService = provider.GetRequiredService<IUserConfigCommandService>();
        var queryPort = provider.GetRequiredService<IUserConfigQueryPort>();
        var metadataProvider = provider.GetRequiredService<IProjectionDocumentMetadataProvider<UserConfigCurrentStateDocument>>();

        commandService.Should().NotBeNull();
        queryPort.Should().NotBeNull();
        metadataProvider.Should().BeOfType<UserConfigCurrentStateDocumentMetadataProvider>();

        await commandService.SaveAsync(new UserConfig(
            DefaultModel: "claude-opus",
            PreferredLlmRoute: "gateway",
            RuntimeMode: "REMOTE",
            LocalRuntimeBaseUrl: "http://127.0.0.1:5080/",
            RemoteRuntimeBaseUrl: "https://runtime.example.com/",
            GithubUsername: "octocat",
            MaxToolRounds: 9));

        dispatchPort.ActorId.Should().Be("user-config-scope-1");
        dispatchPort.Envelope.Should().NotBeNull();
        var evt = dispatchPort.Envelope!.Payload.Unpack<UserConfigUpdatedEvent>();
        evt.DefaultModel.Should().Be("claude-opus");
        evt.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        evt.RuntimeMode.Should().Be(UserConfigRuntimeDefaults.RemoteMode);
        evt.LocalRuntimeBaseUrl.Should().Be("http://127.0.0.1:5080");
        evt.RemoteRuntimeBaseUrl.Should().Be("https://runtime.example.com");
        evt.GithubUsername.Should().Be("octocat");
        evt.MaxToolRounds.Should().Be(9);
    }

    [Fact]
    public async Task SaveAsync_WhenExplicitScopeProvided_ShouldDispatchToThatScope()
    {
        var provider = BuildCommandServiceProvider(
            out _,
            out var dispatchPort,
            scopeId: null);
        await using var serviceProvider = provider;
        var commandService = provider.GetRequiredService<IUserConfigCommandService>();

        await commandService.SaveAsync(
            "scope-explicit",
            new UserConfig(DefaultModel: "gpt-5.4", GithubUsername: "saved-user"),
            CancellationToken.None);

        dispatchPort.ActorId.Should().Be("user-config-scope-explicit");
        dispatchPort.Envelope.Should().NotBeNull();
        dispatchPort.Envelope!.Payload.Unpack<UserConfigUpdatedEvent>().GithubUsername.Should().Be("saved-user");
    }

    [Fact]
    public async Task SaveGithubUsernameAsync_WhenExplicitScopeProvided_ShouldDispatchPartialEvent()
    {
        var provider = BuildCommandServiceProvider(
            out _,
            out var dispatchPort,
            scopeId: null);
        await using var serviceProvider = provider;
        var commandService = provider.GetRequiredService<IUserConfigCommandService>();

        await commandService.SaveGithubUsernameAsync("scope-explicit", "  saved-user  ", CancellationToken.None);

        dispatchPort.ActorId.Should().Be("user-config-scope-explicit");
        dispatchPort.Envelope.Should().NotBeNull();
        dispatchPort.Envelope!.Payload.Unpack<UserConfigGithubUsernameUpdatedEvent>().GithubUsername.Should().Be("saved-user");
    }

    [Fact]
    public async Task SaveAsync_WhenActorMissing_ShouldCreateActorBeforeDispatch()
    {
        // Regression: first save in a scope previously failed with
        // "Actor user-config-{scopeId} is not initialized" because the command
        // service dispatched without ensuring the actor existed.
        var provider = BuildCommandServiceProvider(
            out var actorRuntime,
            out var dispatchPort,
            scopeId: "scope-new");
        await using var _ = provider;
        var commandService = provider.GetRequiredService<IUserConfigCommandService>();

        await commandService.SaveAsync(new UserConfig(
            DefaultModel: "gpt-5.4",
            PreferredLlmRoute: UserConfigLlmRouteDefaults.Gateway,
            RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
            LocalRuntimeBaseUrl: UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl: UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl));

        actorRuntime.GetCalls.Should().ContainSingle().Which.Should().Be("user-config-scope-new");
        actorRuntime.CreateCalls.Should().Contain("user-config-scope-new", "user-config actor must be created before dispatch");
        actorRuntime.CreateCalls.Should().Contain(
            x => x.Contains("user-config-scope-new", StringComparison.Ordinal) && x.Contains("projection", StringComparison.Ordinal),
            "projection scope actor must also be created so the materializer subscribes to the stream");
        dispatchPort.ActorId.Should().Be("user-config-scope-new", "last dispatch is the UserConfigUpdatedEvent to the user-config actor");
        dispatchPort.Envelope.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveAsync_WhenActorAlreadyExists_ShouldSkipCreateAndDispatch()
    {
        var provider = BuildCommandServiceProvider(
            out var actorRuntime,
            out var dispatchPort,
            scopeId: "scope-existing");
        await using var _ = provider;
        actorRuntime.ExistingActors["user-config-scope-existing"] = new StubActor("user-config-scope-existing");
        var commandService = provider.GetRequiredService<IUserConfigCommandService>();

        await commandService.SaveAsync(new UserConfig(
            DefaultModel: "claude-opus",
            PreferredLlmRoute: UserConfigLlmRouteDefaults.Gateway,
            RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
            LocalRuntimeBaseUrl: UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl: UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl));

        actorRuntime.GetCalls.Should().ContainSingle().Which.Should().Be("user-config-scope-existing");
        actorRuntime.CreateCalls.Should().NotContain("user-config-scope-existing",
            "user-config actor already existed, CreateAsync should be skipped for it");
        dispatchPort.ActorId.Should().Be("user-config-scope-existing");
    }

    [Fact]
    public async Task SaveAsync_WhenScopeUnresolved_ShouldFallBackToDefaultScopeActor()
    {
        var provider = BuildCommandServiceProvider(
            out var actorRuntime,
            out var dispatchPort,
            scopeId: null);
        await using var _ = provider;
        var commandService = provider.GetRequiredService<IUserConfigCommandService>();

        await commandService.SaveAsync(new UserConfig(
            DefaultModel: string.Empty,
            PreferredLlmRoute: UserConfigLlmRouteDefaults.Gateway,
            RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
            LocalRuntimeBaseUrl: UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl: UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl));

        actorRuntime.CreateCalls.Should().Contain("user-config-default");
        dispatchPort.ActorId.Should().Be("user-config-default");
    }

    private static ServiceProvider BuildCommandServiceProvider(
        out StubActorRuntime actorRuntime,
        out RecordingActorDispatchPort dispatchPort,
        string? scopeId)
    {
        actorRuntime = new StubActorRuntime();
        dispatchPort = new RecordingActorDispatchPort();
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(actorRuntime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        services.AddSingleton<IAppScopeResolver>(new StubScopeResolver { ScopeIdToReturn = scopeId });
        services.AddSingleton<IProjectionDocumentReader<UserConfigCurrentStateDocument, string>>(
            new StubUserConfigDocumentReader());
        services.AddStudioProjectionComponents();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ProjectionUserConfigQueryPort_GetAsync_ReturnsDefaultsWhenDocumentMissing()
    {
        var reader = new StubUserConfigDocumentReader();
        var scopeResolver = new StubScopeResolver();
        var port = new ProjectionUserConfigQueryPort(
            reader,
            scopeResolver,
            new StubUserConfigDefaults
            {
                LocalRuntimeBaseUrl = "http://127.0.0.1:6100/",
                RemoteRuntimeBaseUrl = "https://runtime.example.cn/",
            });

        var result = await port.GetAsync();

        reader.LastKey.Should().Be("user-config-default");
        result.DefaultModel.Should().BeEmpty();
        result.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        result.RuntimeMode.Should().Be(UserConfigRuntimeDefaults.LocalMode);
        result.LocalRuntimeBaseUrl.Should().Be("http://127.0.0.1:6100");
        result.RemoteRuntimeBaseUrl.Should().Be("https://runtime.example.cn");
        result.GithubUsername.Should().BeNull();
        result.MaxToolRounds.Should().Be(0);
    }

    [Fact]
    public async Task ProjectionUserConfigQueryPort_GetAsync_ExplicitScope_UsesProvidedScope()
    {
        var reader = new StubUserConfigDocumentReader
        {
            Document = new UserConfigCurrentStateDocument
            {
                Id = "user-config-scope-explicit",
                ActorId = "user-config-scope-explicit",
                DefaultModel = "gpt-5.4",
                GithubUsername = "explicit-user",
            },
        };
        var port = new ProjectionUserConfigQueryPort(
            reader,
            new StubScopeResolver { ScopeIdToReturn = "ignored-scope" },
            new StubUserConfigDefaults());

        var result = await port.GetAsync("scope-explicit");

        reader.LastKey.Should().Be("user-config-scope-explicit");
        result.GithubUsername.Should().Be("explicit-user");
    }

    [Fact]
    public async Task ProjectionUserConfigQueryPort_GetAsync_MapsDocumentAndNormalizesEmptyStrings()
    {
        var reader = new StubUserConfigDocumentReader
        {
            Document = new UserConfigCurrentStateDocument
            {
                Id = "user-config-scope-2",
                ActorId = "user-config-scope-2",
                DefaultModel = "gpt-4.1",
                PreferredLlmRoute = string.Empty,
                RuntimeMode = string.Empty,
                LocalRuntimeBaseUrl = string.Empty,
                RemoteRuntimeBaseUrl = string.Empty,
                GithubUsername = "saved-user",
                MaxToolRounds = 7,
            },
        };
        var scopeResolver = new StubScopeResolver { ScopeIdToReturn = "scope-2" };
        var port = new ProjectionUserConfigQueryPort(
            reader,
            scopeResolver,
            new StubUserConfigDefaults
            {
                LocalRuntimeBaseUrl = "http://127.0.0.1:6200/",
                RemoteRuntimeBaseUrl = "https://runtime.example.net/",
            });

        var result = await port.GetAsync();

        reader.LastKey.Should().Be("user-config-scope-2");
        result.DefaultModel.Should().Be("gpt-4.1");
        result.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        result.RuntimeMode.Should().Be(UserConfigRuntimeDefaults.LocalMode);
        result.LocalRuntimeBaseUrl.Should().Be("http://127.0.0.1:6200");
        result.RemoteRuntimeBaseUrl.Should().Be("https://runtime.example.net");
        result.GithubUsername.Should().Be("saved-user");
        result.MaxToolRounds.Should().Be(7);
    }

    [Fact]
    public async Task UserConfigCurrentStateProjector_ProjectAsync_UpsertsCommittedState()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var observedAt = DateTimeOffset.Parse("2026-04-15T10:00:00+00:00");
        var projector = new UserConfigCurrentStateProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.MinValue));
        var context = new StudioMaterializationContext
        {
            RootActorId = "user-config-scope-1",
            ProjectionKind = "user-config",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new UserConfigUpdatedEvent { DefaultModel = "gpt-4.1" },
                "evt-1",
                4,
                observedAt,
                new UserConfigGAgentState
                {
                    DefaultModel = "gpt-4.1",
                    PreferredLlmRoute = "/api/v1/proxy/s/custom",
                    RuntimeMode = UserConfigRuntimeDefaults.RemoteMode,
                    LocalRuntimeBaseUrl = "http://127.0.0.1:5080",
                    RemoteRuntimeBaseUrl = "https://runtime.example.com",
                    GithubUsername = "projected-user",
                    MaxToolRounds = 6,
                }));

        dispatcher.LastUpsert.Should().NotBeNull();
        dispatcher.LastUpsert!.ActorId.Should().Be("user-config-scope-1");
        dispatcher.LastUpsert.StateVersion.Should().Be(4);
        dispatcher.LastUpsert.LastEventId.Should().Be("evt-1");
        dispatcher.LastUpsert.UpdatedAt!.ToDateTimeOffset().Should().Be(observedAt);
        dispatcher.LastUpsert.DefaultModel.Should().Be("gpt-4.1");
        dispatcher.LastUpsert.PreferredLlmRoute.Should().Be("/api/v1/proxy/s/custom");
        dispatcher.LastUpsert.RuntimeMode.Should().Be(UserConfigRuntimeDefaults.RemoteMode);
        dispatcher.LastUpsert.GithubUsername.Should().Be("projected-user");
        dispatcher.LastUpsert.MaxToolRounds.Should().Be(6);
    }

    [Fact]
    public async Task UserConfigCurrentStateProjector_ProjectAsync_IgnoresEnvelopeWithoutState()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new UserConfigCurrentStateProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new StudioMaterializationContext
        {
            RootActorId = "user-config-scope-1",
            ProjectionKind = "user-config",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "outer",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-missing-state",
                        Version = 1,
                    },
                }),
            });

        dispatcher.LastUpsert.Should().BeNull();
    }

    [Fact]
    public void UserConfigCurrentStateDocumentMetadataProvider_UsesStudioIndex()
    {
        var provider = new UserConfigCurrentStateDocumentMetadataProvider();

        provider.Metadata.IndexName.Should().Be("studio-user-config");
        provider.Metadata.Mappings["dynamic"].Should().Be(true);
        provider.Metadata.Settings.Should().BeEmpty();
        provider.Metadata.Aliases.Should().BeEmpty();
    }

    [Fact]
    public async Task UserConfigController_Get_ReturnsQueriedConfig()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ConfigToReturn = new UserConfig("gpt-4.1", "/api/v1/proxy/s/custom", GithubUsername: "saved-user", MaxToolRounds: 3),
        };
        var controller = CreateController(queryPort, new RecordingUserConfigCommandService());

        var response = await controller.Get(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserConfig>().Subject;
        payload.DefaultModel.Should().Be("gpt-4.1");
        payload.GithubUsername.Should().Be("saved-user");
        payload.MaxToolRounds.Should().Be(3);
    }

    [Fact]
    public async Task UserConfigController_Get_ReturnsBadRequestForInvalidOperation()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ExceptionToThrow = new InvalidOperationException("invalid"),
        };
        var controller = CreateController(queryPort, new RecordingUserConfigCommandService());

        var response = await controller.Get(CancellationToken.None);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UserConfigController_Get_Returns502ForUnexpectedFailure()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ExceptionToThrow = new Exception("boom"),
        };
        var controller = CreateController(queryPort, new RecordingUserConfigCommandService());

        var response = await controller.Get(CancellationToken.None);

        var result = response.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(502);
    }

    [Fact]
    public async Task UserConfigController_Save_PreservesCurrentMaxToolRounds_WhenRequestOmitsIt()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ConfigToReturn = new UserConfig(
                DefaultModel: "old-model",
                PreferredLlmRoute: "/api/v1/proxy/s/old",
                RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
                LocalRuntimeBaseUrl: "http://127.0.0.1:5080",
                RemoteRuntimeBaseUrl: "https://remote.example.com",
                GithubUsername: "saved-user",
                MaxToolRounds: 7),
        };
        var commandService = new RecordingUserConfigCommandService();
        var controller = CreateController(queryPort, commandService);

        var response = await controller.Save(
            new UserConfigController.SaveUserConfigRequest(
                DefaultModel: " gpt-4.1 ",
                PreferredLlmRoute: "gateway",
                RuntimeMode: "remote",
                LocalRuntimeBaseUrl: "http://localhost:5080/"),
            CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserConfig>().Subject;
        payload.DefaultModel.Should().Be("gpt-4.1");
        payload.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        payload.RuntimeMode.Should().Be("remote");
        payload.LocalRuntimeBaseUrl.Should().Be("http://localhost:5080/");
        payload.RemoteRuntimeBaseUrl.Should().Be("https://remote.example.com");
        payload.GithubUsername.Should().Be("saved-user");
        payload.MaxToolRounds.Should().Be(7);
        commandService.SavedConfig.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task UserConfigController_Save_UsesRequestMaxToolRounds_WhenProvided()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ConfigToReturn = new UserConfig(DefaultModel: "old-model", MaxToolRounds: 1),
        };
        var commandService = new RecordingUserConfigCommandService();
        var controller = CreateController(queryPort, commandService);

        await controller.Save(
            new UserConfigController.SaveUserConfigRequest(
                MaxToolRounds: 12),
            CancellationToken.None);

        commandService.SavedConfig.Should().NotBeNull();
        commandService.SavedConfig!.MaxToolRounds.Should().Be(12);
    }

    [Fact]
    public async Task UserConfigController_Save_UsesRequestGithubUsername_WhenProvided()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ConfigToReturn = new UserConfig(DefaultModel: "old-model", GithubUsername: "old-user"),
        };
        var commandService = new RecordingUserConfigCommandService();
        var controller = CreateController(queryPort, commandService);

        await controller.Save(
            new UserConfigController.SaveUserConfigRequest(
                GithubUsername: "  new-user  "),
            CancellationToken.None);

        commandService.SavedConfig.Should().NotBeNull();
        commandService.SavedConfig!.GithubUsername.Should().Be("new-user");
    }

    [Fact]
    public async Task UserConfigController_Save_ReturnsBadRequestForInvalidOperation()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ExceptionToThrow = new InvalidOperationException("invalid"),
        };
        var controller = CreateController(queryPort, new RecordingUserConfigCommandService());

        var response = await controller.Save(new UserConfigController.SaveUserConfigRequest(), CancellationToken.None);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UserConfigController_GetLlmOptions_UsesNyxIdLlmServicesEndpoint()
    {
        var httpHandler = new RecordingHttpHandler("""
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
          ],
          "setup_hint": {
            "setup_url": "https://nyxid.example/services",
            "presets": []
          }
        }
        """);
        var queryPort = new StubUserConfigQueryPort
        {
            ConfigToReturn = new UserConfig("gpt-5.4", "/api/v1/proxy/s/openai-work"),
        };
        var controller = CreateController(
            queryPort,
            new RecordingUserConfigCommandService(),
            new StubHttpClientFactory(httpHandler),
            BuildNyxIdConfiguration(),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmOptions(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmOptionsView>().Subject;
        payload.Available.Should().ContainSingle().Which.ServiceId.Should().Be("svc-openai");
        payload.Current.Should().NotBeNull();
        payload.Current!.DisplayName.Should().Be("OpenAI Work");
        httpHandler.Requests.Should().ContainSingle();
        httpHandler.Requests[0].Path.Should().Be("/api/v1/llm/services");
        httpHandler.Requests[0].Authorization.Should().Be("Bearer user-token-1");
    }

    [Fact]
    public async Task UserConfigController_GetLlmOptions_FallsBackToNyxIdLlmStatusEndpoint()
    {
        var httpHandler = new RecordingHttpHandler(
            (HttpStatusCode.NotFound, """{"error":"not_found"}"""),
            (HttpStatusCode.OK, """
            {
              "providers": [
                {
                  "provider_slug": "openai",
                  "provider_name": "OpenAI Gateway",
                  "status": "ready",
                  "proxy_url": "https://nyxid.example/api/v1/llm/openai/v1"
                }
              ],
              "supported_models": ["gpt-5.4"]
            }
            """));
        var controller = CreateController(
            new StubUserConfigQueryPort(),
            new RecordingUserConfigCommandService(),
            new StubHttpClientFactory(httpHandler),
            BuildNyxIdConfiguration(),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmOptions(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserLlmOptionsView>().Subject;
        var option = payload.Available.Should().ContainSingle().Subject;
        option.ServiceId.Should().Be("openai");
        option.RouteValue.Should().Be("/api/v1/llm/openai/v1");
        option.Source.Should().Be(NyxIdLlmProviderSource.GatewayProvider);
        option.Allowed.Should().BeTrue();
        httpHandler.Requests.Select(request => request.Path)
            .Should()
            .Equal("/api/v1/llm/services", "/api/v1/llm/status");
    }

    [Fact]
    public async Task UserConfigController_SaveLlmPreference_WithServiceId_WritesConfirmedRoute()
    {
        var httpHandler = new RecordingHttpHandler("""
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
        """);
        var queryPort = new StubUserConfigQueryPort
        {
            ConfigToReturn = new UserConfig("old-model", "/api/v1/proxy/s/old"),
        };
        var commandService = new RecordingUserConfigCommandService();
        var controller = CreateController(
            queryPort,
            commandService,
            new StubHttpClientFactory(httpHandler),
            BuildNyxIdConfiguration(),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmPreference(
            new UserConfigController.SaveUserLlmPreferenceRequest(ServiceId: "svc-openai"),
            CancellationToken.None);

        response.Result.Should().BeOfType<OkObjectResult>();
        commandService.SavedConfig.Should().NotBeNull();
        commandService.SavedConfig!.PreferredLlmRoute.Should().Be("/api/v1/proxy/s/openai-work");
        commandService.SavedConfig.DefaultModel.Should().Be("gpt-5.4");
        httpHandler.Requests.Should().ContainSingle(request => request.Path == "/api/v1/llm/services");
    }

    [Fact]
    public async Task UserConfigController_GetModels_MapsLlmServicesToLegacyShape()
    {
        var httpHandler = new RecordingHttpHandler("""
        {
          "items": [
            {
              "userServiceId": "svc-openai",
              "serviceSlug": "openai-work",
              "displayName": "OpenAI Work",
              "routeValue": "/api/v1/proxy/s/openai-work",
              "models": ["gpt-5.4", "gpt-5.4", "gpt-4.1"],
              "status": "ready",
              "source": "user",
              "allowed": true
            },
            {
              "userServiceId": "svc-openai-backup",
              "serviceSlug": "openai-work",
              "displayName": "OpenAI Work Backup",
              "routeValue": "/api/v1/proxy/s/openai-work-backup",
              "models": ["gpt-5.5"],
              "status": "ready",
              "source": "user",
              "allowed": true
            },
            {
              "userServiceId": "svc-anthropic",
              "serviceSlug": "anthropic-work",
              "serviceName": "Anthropic Work",
              "proxyUrl": "/api/v1/proxy/s/anthropic-work",
              "availableModels": ["claude-sonnet-4-5"],
              "status": "ready",
              "allowed": true
            }
          ]
        }
        """);
        var controller = CreateController(
            new StubUserConfigQueryPort(),
            new RecordingUserConfigCommandService(),
            new StubHttpClientFactory(httpHandler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Cli:App:NyxId:Authority"] = "https://nyxid.example/api/v1/llm/gateway/v1",
                })
                .Build(),
            bearerToken: "user-token-1");

        var response = await controller.GetModels(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<NyxIdLlmStatusResponse>().Subject;
        payload.GatewayUrl.Should().Be("https://nyxid.example/api/v1/llm/gateway/v1");
        payload.Providers.Should().HaveCount(2);
        payload.Providers![1].ProviderName.Should().Be("Anthropic Work");
        payload.ModelsByProvider!["openai-work"].Should().Equal("gpt-4.1", "gpt-5.4", "gpt-5.5");
        payload.SupportedModels.Should().Equal("gpt-4.1", "gpt-5.4", "gpt-5.5", "claude-sonnet-4-5");
    }

    [Fact]
    public async Task UserConfigController_GetModels_WhenNyxIdFails_ReturnsEmptyLegacyShape()
    {
        var httpHandler = new RecordingHttpHandler((HttpStatusCode.BadGateway, """{"error":"offline"}"""));
        var controller = CreateController(
            new StubUserConfigQueryPort(),
            new RecordingUserConfigCommandService(),
            new StubHttpClientFactory(httpHandler),
            BuildNyxIdConfiguration(),
            bearerToken: "user-token-1");

        var response = await controller.GetModels(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(NyxIdLlmStatusResponse.Empty);
    }

    [Fact]
    public async Task UserConfigController_GetLlmOptions_WithoutBearer_ReturnsEmptyWithoutNyxRequest()
    {
        var httpHandler = new RecordingHttpHandler("""{"services":[]}""");
        var controller = CreateController(
            new StubUserConfigQueryPort(),
            new RecordingUserConfigCommandService(),
            new StubHttpClientFactory(httpHandler),
            BuildNyxIdConfiguration());

        var response = await controller.GetLlmOptions(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(UserLlmOptionsView.Empty);
        httpHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UserConfigController_GetLlmOptions_WhenNyxIdAuthorityMissing_ReturnsBadRequest()
    {
        var controller = CreateController(
            new StubUserConfigQueryPort(),
            new RecordingUserConfigCommandService(),
            new StubHttpClientFactory(new RecordingHttpHandler("""{"services":[]}""")),
            new ConfigurationBuilder().Build(),
            bearerToken: "user-token-1");

        var response = await controller.GetLlmOptions(CancellationToken.None);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UserConfigController_SaveLlmPreference_Reset_ClearsSelection()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ConfigToReturn = new UserConfig("gpt-5.4", "/api/v1/proxy/s/openai-work"),
        };
        var commandService = new RecordingUserConfigCommandService();
        var controller = CreateController(queryPort, commandService);

        var response = await controller.SaveLlmPreference(
            new UserConfigController.SaveUserLlmPreferenceRequest(Reset: true),
            CancellationToken.None);

        response.Result.Should().BeOfType<OkObjectResult>();
        commandService.SavedConfig.Should().NotBeNull();
        commandService.SavedConfig!.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        commandService.SavedConfig.DefaultModel.Should().BeEmpty();
    }

    [Fact]
    public async Task UserConfigController_SaveLlmPreference_WithGatewayRoute_PreservesExplicitModel()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ConfigToReturn = new UserConfig("old-model", "/api/v1/proxy/s/old"),
        };
        var commandService = new RecordingUserConfigCommandService();
        var controller = CreateController(
            queryPort,
            commandService,
            new StubHttpClientFactory(new RecordingHttpHandler("""{"services":[]}""")),
            BuildNyxIdConfiguration(),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmPreference(
            new UserConfigController.SaveUserLlmPreferenceRequest(
                RouteValue: " gateway ",
                Model: " gpt-5.4 "),
            CancellationToken.None);

        response.Result.Should().BeOfType<OkObjectResult>();
        commandService.SavedConfig.Should().NotBeNull();
        commandService.SavedConfig!.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        commandService.SavedConfig.DefaultModel.Should().Be("gpt-5.4");
    }

    [Fact]
    public async Task UserConfigController_SaveLlmPreference_WithRouteValue_WritesConfirmedServiceRoute()
    {
        var httpHandler = new RecordingHttpHandler(SingleReadyServiceJson());
        var commandService = new RecordingUserConfigCommandService();
        var controller = CreateController(
            new StubUserConfigQueryPort { ConfigToReturn = new UserConfig("old-model", "gateway") },
            commandService,
            new StubHttpClientFactory(httpHandler),
            BuildNyxIdConfiguration(),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmPreference(
            new UserConfigController.SaveUserLlmPreferenceRequest(
                RouteValue: "/api/v1/proxy/s/openai-work"),
            CancellationToken.None);

        response.Result.Should().BeOfType<OkObjectResult>();
        commandService.SavedConfig.Should().NotBeNull();
        commandService.SavedConfig!.PreferredLlmRoute.Should().Be("/api/v1/proxy/s/openai-work");
        commandService.SavedConfig.DefaultModel.Should().Be("gpt-5.4");
    }

    [Fact]
    public async Task UserConfigController_SaveLlmPreference_WithModelOnly_PreservesRoute()
    {
        var commandService = new RecordingUserConfigCommandService();
        var controller = CreateController(
            new StubUserConfigQueryPort
            {
                ConfigToReturn = new UserConfig("old-model", "/api/v1/proxy/s/openai-work"),
            },
            commandService);

        var response = await controller.SaveLlmPreference(
            new UserConfigController.SaveUserLlmPreferenceRequest(Model: " claude-sonnet "),
            CancellationToken.None);

        response.Result.Should().BeOfType<OkObjectResult>();
        commandService.SavedConfig.Should().NotBeNull();
        commandService.SavedConfig!.PreferredLlmRoute.Should().Be("/api/v1/proxy/s/openai-work");
        commandService.SavedConfig.DefaultModel.Should().Be("claude-sonnet");
    }

    [Theory]
    [InlineData("missing", null)]
    [InlineData(null, "/api/v1/proxy/s/missing")]
    public async Task UserConfigController_SaveLlmPreference_WithUnknownServiceOrRoute_ReturnsBadRequest(
        string? serviceId,
        string? routeValue)
    {
        var controller = CreateController(
            new StubUserConfigQueryPort(),
            new RecordingUserConfigCommandService(),
            new StubHttpClientFactory(new RecordingHttpHandler(SingleReadyServiceJson())),
            BuildNyxIdConfiguration(),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmPreference(
            new UserConfigController.SaveUserLlmPreferenceRequest(
                ServiceId: serviceId,
                RouteValue: routeValue),
            CancellationToken.None);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(false, "ready")]
    [InlineData(true, "pending")]
    public async Task UserConfigController_SaveLlmPreference_WithUnselectableService_ReturnsBadRequest(
        bool allowed,
        string status)
    {
        var controller = CreateController(
            new StubUserConfigQueryPort(),
            new RecordingUserConfigCommandService(),
            new StubHttpClientFactory(new RecordingHttpHandler(SingleServiceJson(allowed: allowed, status: status))),
            BuildNyxIdConfiguration(),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmPreference(
            new UserConfigController.SaveUserLlmPreferenceRequest(ServiceId: "svc-openai"),
            CancellationToken.None);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UserConfigController_SaveLlmPreference_WithExistingPreset_WritesPresetRouteAndModel()
    {
        var httpHandler = new RecordingHttpHandler("""
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
          ],
          "setup_hint": {
            "setupUrl": "https://nyxid.example/setup",
            "presets": [
              {
                "id": "shared-openai",
                "title": "Shared OpenAI",
                "description": "Use existing shared service",
                "activation": {
                  "type": "use-existing-service",
                  "serviceId": "svc-openai",
                  "routeValue": "/api/v1/proxy/s/openai-work",
                  "defaultModel": "gpt-4.1"
                }
              }
            ]
          }
        }
        """);
        var commandService = new RecordingUserConfigCommandService();
        var controller = CreateController(
            new StubUserConfigQueryPort(),
            commandService,
            new StubHttpClientFactory(httpHandler),
            BuildNyxIdConfiguration(),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmPreference(
            new UserConfigController.SaveUserLlmPreferenceRequest(PresetId: "shared-openai"),
            CancellationToken.None);

        response.Result.Should().BeOfType<OkObjectResult>();
        commandService.SavedConfig.Should().NotBeNull();
        commandService.SavedConfig!.PreferredLlmRoute.Should().Be("/api/v1/proxy/s/openai-work");
        commandService.SavedConfig.DefaultModel.Should().Be("gpt-4.1");
    }

    [Fact]
    public async Task UserConfigController_SaveLlmPreference_WithProvisionPreset_PostsProvisionEndpoint()
    {
        var httpHandler = new RecordingHttpHandler(
            (HttpStatusCode.OK, """
            {
              "services": [],
              "setup_hint": {
                "setup_url": "https://nyxid.example/setup",
                "presets": [
                  {
                    "id": "chrono-shared",
                    "activation_type": "provision",
                    "provision_endpoint_id": "chrono-llm/shared"
                  }
                ]
              }
            }
            """),
            (HttpStatusCode.OK, """
            {
              "service": {
                "userServiceId": "svc-provisioned",
                "serviceSlug": "chrono-llm",
                "displayName": "Chrono LLM",
                "proxyUrl": "/api/v1/proxy/s/chrono-llm",
                "defaultModel": "chrono-default",
                "availableModels": ["chrono-default"],
                "status": "ready",
                "allowed": true,
                "description": "Provisioned shared LLM"
              }
            }
            """));
        var commandService = new RecordingUserConfigCommandService();
        var controller = CreateController(
            new StubUserConfigQueryPort(),
            commandService,
            new StubHttpClientFactory(httpHandler),
            BuildNyxIdConfiguration(),
            bearerToken: "user-token-1");

        var response = await controller.SaveLlmPreference(
            new UserConfigController.SaveUserLlmPreferenceRequest(
                PresetId: "chrono-shared",
                Model: " chrono-default "),
            CancellationToken.None);

        response.Result.Should().BeOfType<OkObjectResult>();
        commandService.SavedConfig.Should().NotBeNull();
        commandService.SavedConfig!.PreferredLlmRoute.Should().Be("/api/v1/proxy/s/chrono-llm");
        commandService.SavedConfig.DefaultModel.Should().Be("chrono-default");
        httpHandler.Requests.Select(request => request.Path)
            .Should()
            .Equal("/api/v1/llm/services", "/api/v1/llm/services/chrono-llm%2Fshared");
        httpHandler.Requests[1].Method.Should().Be("POST");
        httpHandler.Requests[1].Body.Should().Be("{}");
    }

    [Fact]
    public async Task UserConfigController_SaveLlmPreference_WithEmptyRequest_ReturnsBadRequest()
    {
        var controller = CreateController(new StubUserConfigQueryPort(), new RecordingUserConfigCommandService());

        var response = await controller.SaveLlmPreference(
            new UserConfigController.SaveUserLlmPreferenceRequest(),
            CancellationToken.None);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    private static string SingleReadyServiceJson() => SingleServiceJson(allowed: true, status: "ready");

    private static string SingleServiceJson(bool allowed, string status) => $$"""
        {
          "services": [
            {
              "user_service_id": "svc-openai",
              "service_slug": "openai-work",
              "display_name": "OpenAI Work",
              "route_value": "/api/v1/proxy/s/openai-work",
              "default_model": "gpt-5.4",
              "models": ["gpt-5.4"],
              "status": "{{status}}",
              "source": "user",
              "allowed": {{allowed.ToString().ToLowerInvariant()}}
            }
          ]
        }
        """;

    private static UserConfigController CreateController(
        IUserConfigQueryPort queryPort,
        IUserConfigCommandService commandService,
        IHttpClientFactory? httpClientFactory = null,
        IConfiguration? configuration = null,
        string? bearerToken = null)
    {
        var effectiveHttpClientFactory = httpClientFactory ?? new StubHttpClientFactory();
        var effectiveConfiguration = configuration ?? new ConfigurationBuilder().Build();
        var llmPreferenceService = new UserLlmPreferenceService(
            queryPort,
            commandService,
            new NyxIdLlmCatalogHttpClient(
                effectiveHttpClientFactory,
                effectiveConfiguration,
                NullLogger<NyxIdLlmCatalogHttpClient>.Instance));
        var controller = new UserConfigController(
            queryPort,
            commandService,
            llmPreferenceService,
            effectiveConfiguration,
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

    private static EventEnvelope BuildCommittedEnvelope(
        IMessage payload,
        string eventId,
        long version,
        DateTimeOffset observedAt,
        UserConfigGAgentState state) =>
        new()
        {
            Id = $"outer-{eventId}",
            Timestamp = Timestamp.FromDateTimeOffset(observedAt),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("projection-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(observedAt),
                    EventData = Any.Pack(payload),
                },
                StateRoot = Any.Pack(state),
            }),
        };

    private sealed class StubScopeResolver : IAppScopeResolver
    {
        public string? ScopeIdToReturn { get; set; }

        public AppScopeContext? Resolve(HttpContext? httpContext = null) =>
            ScopeIdToReturn is null ? null : new AppScopeContext(ScopeIdToReturn, "test");

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) => false;
    }

    private sealed class StubUserConfigDefaults : IUserConfigDefaults
    {
        public string LocalRuntimeBaseUrl { get; init; } = UserConfigRuntimeDefaults.LocalRuntimeBaseUrl;

        public string RemoteRuntimeBaseUrl { get; init; } = UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public string? ActorId { get; private set; }
        public EventEnvelope? Envelope { get; private set; }

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ActorId = actorId;
            Envelope = envelope.Clone();
            return Task.CompletedTask;
        }
    }

    private sealed class StubActorRuntime : IActorRuntime
    {
        public List<string> GetCalls { get; } = new();
        public List<string> CreateCalls { get; } = new();
        public Dictionary<string, IActor> ExistingActors { get; } = new(StringComparer.Ordinal);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            CreateCalls.Add(actorId);
            var actor = new StubActor(actorId);
            ExistingActors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            CreateCalls.Add(actorId);
            var actor = new StubActor(actorId);
            ExistingActors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ExistingActors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            GetCalls.Add(id);
            return Task.FromResult<IActor?>(ExistingActors.TryGetValue(id, out var actor) ? actor : null);
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(ExistingActors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubActor : IActor
    {
        public StubActor(string id) { Id = id; }
        public string Id { get; }
        public IAgent Agent => throw new NotSupportedException("test stub");
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class StubUserConfigDocumentReader
        : IProjectionDocumentReader<UserConfigCurrentStateDocument, string>
    {
        public string? LastKey { get; private set; }
        public UserConfigCurrentStateDocument? Document { get; set; }

        public Task<UserConfigCurrentStateDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            LastKey = key;
            return Task.FromResult(Document);
        }

        public Task<ProjectionDocumentQueryResult<UserConfigCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionDocumentQueryResult<UserConfigCurrentStateDocument>.Empty);
    }

    private sealed class RecordingWriteDispatcher : IProjectionWriteDispatcher<UserConfigCurrentStateDocument>
    {
        public UserConfigCurrentStateDocument? LastUpsert { get; private set; }

        public string? LastDeletedId { get; private set; }

        public Task<ProjectionWriteResult> UpsertAsync(
            UserConfigCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            LastUpsert = readModel.Clone();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            LastDeletedId = id;
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class StubUserConfigQueryPort : IUserConfigQueryPort
    {
        public UserConfig ConfigToReturn { get; set; } = new(string.Empty);
        public Exception? ExceptionToThrow { get; set; }
        public string? LastScopeId { get; private set; }

        public Task<UserConfig> GetAsync(CancellationToken ct = default)
        {
            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            return Task.FromResult(ConfigToReturn);
        }

        public Task<UserConfig> GetAsync(string scopeId, CancellationToken ct = default)
        {
            LastScopeId = scopeId;
            return GetAsync(ct);
        }
    }

    private sealed class RecordingUserConfigCommandService : IUserConfigCommandService
    {
        public UserConfig? SavedConfig { get; private set; }
        public string? SavedScopeId { get; private set; }
        public string? SavedGithubUsername { get; private set; }

        public Task SaveAsync(UserConfig config, CancellationToken ct = default)
        {
            SavedConfig = config;
            return Task.CompletedTask;
        }

        public Task SaveAsync(string scopeId, UserConfig config, CancellationToken ct = default)
        {
            SavedScopeId = scopeId;
            return SaveAsync(config, ct);
        }

        public Task SaveGithubUsernameAsync(string scopeId, string githubUsername, CancellationToken ct = default)
        {
            SavedScopeId = scopeId;
            SavedGithubUsername = githubUsername;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler ?? new StaticHandler());
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage());
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
