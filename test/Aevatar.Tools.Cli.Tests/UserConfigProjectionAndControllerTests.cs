using System.Net.Http;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.Controllers;
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
            MaxToolRounds: 9));

        dispatchPort.ActorId.Should().Be("user-config-scope-1");
        dispatchPort.Envelope.Should().NotBeNull();
        var evt = dispatchPort.Envelope!.Payload.Unpack<UserConfigUpdatedEvent>();
        evt.DefaultModel.Should().Be("claude-opus");
        evt.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        evt.RuntimeMode.Should().Be(UserConfigRuntimeDefaults.RemoteMode);
        evt.LocalRuntimeBaseUrl.Should().Be("http://127.0.0.1:5080");
        evt.RemoteRuntimeBaseUrl.Should().Be("https://runtime.example.com");
        evt.MaxToolRounds.Should().Be(9);
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
        result.MaxToolRounds.Should().Be(0);
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
            ConfigToReturn = new UserConfig("gpt-4.1", "/api/v1/proxy/s/custom", MaxToolRounds: 3),
        };
        var controller = CreateController(queryPort, new RecordingUserConfigCommandService());

        var response = await controller.Get(CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<UserConfig>().Subject;
        payload.DefaultModel.Should().Be("gpt-4.1");
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

    private static UserConfigController CreateController(
        IUserConfigQueryPort queryPort,
        IUserConfigCommandService commandService)
    {
        var controller = new UserConfigController(
            queryPort,
            commandService,
            new StubHttpClientFactory(),
            new ConfigurationBuilder().Build(),
            NullLogger<UserConfigController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        return controller;
    }

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

        public Task<UserConfig> GetAsync(CancellationToken ct = default)
        {
            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            return Task.FromResult(ConfigToReturn);
        }
    }

    private sealed class RecordingUserConfigCommandService : IUserConfigCommandService
    {
        public UserConfig? SavedConfig { get; private set; }

        public Task SaveAsync(UserConfig config, CancellationToken ct = default)
        {
            SavedConfig = config;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StaticHandler());
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage());
    }
}
