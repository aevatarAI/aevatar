using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class UserConfigAmbientScopeTests
{
    [Fact]
    public async Task QueryPort_GetAsync_ShouldFailClosed_WhenAuthenticatedRequestHasNoScope()
    {
        var port = new ProjectionUserConfigQueryPort(
            new RecordingUserConfigReader(),
            new StubScopeResolver(null, authenticatedWithoutScope: true),
            new StubUserConfigDefaults());

        await FluentActions.Awaiting(() => port.GetAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*scope_id*");
    }

    [Fact]
    public async Task QueryPort_GetAsync_ShouldUseDefaultScope_WhenNoRequestScopeExists()
    {
        var reader = new RecordingUserConfigReader();
        var port = new ProjectionUserConfigQueryPort(
            reader,
            new StubScopeResolver(null, authenticatedWithoutScope: false),
            new StubUserConfigDefaults());

        await port.GetAsync();

        reader.Keys.Should().ContainSingle().Which.Should().Be("user-config-default");
    }

    [Fact]
    public async Task CommandService_SaveAsync_ShouldFailClosed_WhenAuthenticatedRequestHasNoScope()
    {
        var service = new ActorDispatchUserConfigCommandService(
            new RecordingBootstrap(),
            new RecordingDispatchPort(),
            new StubScopeResolver(null, authenticatedWithoutScope: true));

        await FluentActions.Awaiting(() => service.SaveAsync(new UserConfig(DefaultModel: "model-a")))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*scope_id*");
    }

    [Fact]
    public async Task CommandService_SaveAsync_ShouldUseResolvedAmbientScope()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchUserConfigCommandService(
            bootstrap,
            dispatch,
            new StubScopeResolver("scope-1", authenticatedWithoutScope: false));

        await service.SaveAsync(new UserConfig(DefaultModel: "model-a"));

        bootstrap.ActorIds.Should().ContainSingle().Which.Should().Be("user-config-scope-1");
        dispatch.TargetActorIds.Should().ContainSingle().Which.Should().Be("user-config-scope-1");
    }

    private sealed class StubScopeResolver(string? scopeId, bool authenticatedWithoutScope) : IAppScopeResolver
    {
        public AppScopeContext? Resolve(Microsoft.AspNetCore.Http.HttpContext? httpContext = null) =>
            string.IsNullOrWhiteSpace(scopeId) ? null : new AppScopeContext(scopeId, "test");

        public bool HasAuthenticatedRequestWithoutScope(Microsoft.AspNetCore.Http.HttpContext? httpContext = null) =>
            authenticatedWithoutScope;
    }

    private sealed class StubUserConfigDefaults : IUserConfigDefaults
    {
        public string LocalRuntimeBaseUrl => UserConfigRuntimeDefaults.LocalRuntimeBaseUrl;

        public string RemoteRuntimeBaseUrl => UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl;
    }

    private sealed class RecordingUserConfigReader
        : IProjectionDocumentReader<UserConfigCurrentStateDocument, string>
    {
        public List<string> Keys { get; } = [];

        public Task<UserConfigCurrentStateDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            Keys.Add(key);
            return Task.FromResult<UserConfigCurrentStateDocument?>(null);
        }

        public Task<ProjectionDocumentQueryResult<UserConfigCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new ProjectionDocumentQueryResult<UserConfigCurrentStateDocument>());
    }

    private sealed class RecordingBootstrap : IStudioActorBootstrap
    {
        public List<string> ActorIds { get; } = [];

        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor
        {
            ActorIds.Add(actorId);
            return Task.FromResult<IActor>(new StubActor(actorId));
        }
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<string> TargetActorIds { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            TargetActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
