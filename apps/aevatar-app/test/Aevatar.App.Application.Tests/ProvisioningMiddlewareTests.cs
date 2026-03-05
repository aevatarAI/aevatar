using System.Reflection;
using Aevatar.App.Application.Projection.Orchestration;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Aevatar.App.Application.Auth;
using Aevatar.App.Application.Services;
using Aevatar.App.GAgents;
using static Aevatar.App.Application.Tests.AuthLookupTestHelper;

namespace Aevatar.App.Application.Tests;

public sealed class ProvisioningMiddlewareTests
{
    private const string TrialSecret = "test-secret-that-is-at-least-32-chars!!";

    private static (AppUserProvisioningMiddleware Mw, StubActorRuntime Runtime, AppAuthContextAccessor Accessor,
        IProjectionDocumentStore<AppAuthLookupReadModel, string> AuthLookupStore) CreateMiddleware(
        RequestDelegate? next = null,
        StubActorRuntime? runtime = null,
        string? trialSecret = null,
        IProjectionDocumentStore<AppAuthLookupReadModel, string>? authLookupStore = null)
    {
        var authOptions = Options.Create(new AppAuthOptions
        {
            FirebaseProjectId = "",
            TrialTokenSecret = trialSecret ?? TrialSecret,
            TrialAuthEnabled = true,
        });
        var authService = new AppAuthService(authOptions, NullLogger<AppAuthService>.Instance);
        runtime ??= new StubActorRuntime();
        var actors = new ActorAccessAppService(runtime);
        var accessor = new AppAuthContextAccessor();
        authLookupStore ??= new AppInMemoryDocumentStore<AppAuthLookupReadModel, string>(m => m.Id);
        var accountStore = new AppInMemoryDocumentStore<AppUserAccountReadModel, string>(m => m.Id);
        var projectionManager = new NoOpAppProjectionManager();
        var mw = new AppUserProvisioningMiddleware(
            next ?? (_ => Task.CompletedTask),
            authService,
            actors,
            authLookupStore,
            accountStore,
            projectionManager,
            NullLogger<AppUserProvisioningMiddleware>.Instance);
        return (mw, runtime, accessor, authLookupStore);
    }

    private static DefaultHttpContext CreateHttpContext(string? bearerToken = null)
    {
        var ctx = new DefaultHttpContext();
        if (bearerToken is not null)
            ctx.Request.Headers.Authorization = $"Bearer {bearerToken}";
        return ctx;
    }

    [Fact]
    public async Task MissingAuthHeader_Returns401()
    {
        var (mw, _, accessor, _) = CreateMiddleware();
        var ctx = CreateHttpContext();

        await mw.InvokeAsync(ctx, accessor);

        ctx.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task InvalidToken_Returns401()
    {
        var (mw, _, accessor, _) = CreateMiddleware();
        var ctx = CreateHttpContext("not.a.valid.token");

        await mw.InvokeAsync(ctx, accessor);

        ctx.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task ValidToken_NewUser_CreatesAccountAndSetsContext()
    {
        var (mw, _, accessor, _) = CreateMiddleware();
        var token = JwtHelper.GenerateHS256Token(TrialSecret, "sub-1", "new@test.com");
        var ctx = CreateHttpContext(token);

        await mw.InvokeAsync(ctx, accessor);

        ctx.Response.StatusCode.Should().NotBe(401);
        accessor.AuthContext.Should().NotBeNull();
        accessor.AuthContext!.AuthUser.Email.Should().Be("new@test.com");
        accessor.AuthContext.UserId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ValidToken_ExistingProvider_ReturnsExistingUserId()
    {
        var (mw, _, accessor, authLookupStore) = CreateMiddleware();
        var token = JwtHelper.GenerateHS256Token(TrialSecret, "sub-2", "existing@test.com");

        var ctx1 = CreateHttpContext(token);
        await mw.InvokeAsync(ctx1, accessor);
        var firstUserId = accessor.AuthContext!.UserId;

        await SimulateAuthLookupProjection(authLookupStore, $"trial:sub-2", firstUserId);
        await SimulateAuthLookupProjection(authLookupStore, $"email:existing@test.com", firstUserId);

        accessor.AuthContext = null;
        var ctx2 = CreateHttpContext(token);
        await mw.InvokeAsync(ctx2, accessor);
        var secondUserId = accessor.AuthContext!.UserId;

        secondUserId.Should().Be(firstUserId, "same provider should return same user");
    }

    [Fact]
    public async Task ValidToken_EmailCrossProvider_LinksToExistingUser()
    {
        var (mw, runtime, accessor, authLookupStore) = CreateMiddleware();

        var token1 = JwtHelper.GenerateHS256Token(TrialSecret, "sub-a", "shared@test.com");
        var ctx1 = CreateHttpContext(token1);
        await mw.InvokeAsync(ctx1, accessor);
        var userId1 = accessor.AuthContext!.UserId;

        await SimulateAuthLookupProjection(authLookupStore, $"email:shared@test.com", userId1);

        var otherSecret = "other-secret-that-is-at-least-32-chars!";
        var (mw2, _, _, _) = CreateMiddleware(
            runtime: runtime,
            trialSecret: otherSecret,
            authLookupStore: authLookupStore);

        var accessor2 = new AppAuthContextAccessor();
        var token2 = JwtHelper.GenerateHS256Token(otherSecret, "sub-b", "shared@test.com");
        var ctx2 = CreateHttpContext(token2);
        await mw2.InvokeAsync(ctx2, accessor2);
        var userId2 = accessor2.AuthContext!.UserId;

        userId2.Should().Be(userId1, "same email should link to same user");
    }

    [Fact]
    public async Task CallsNext_WhenAuthenticated()
    {
        var nextCalled = false;
        var (mw, _, accessor, _) = CreateMiddleware(next: _ => { nextCalled = true; return Task.CompletedTask; });
        var token = JwtHelper.GenerateHS256Token(TrialSecret, "sub-3", "next@test.com");
        var ctx = CreateHttpContext(token);

        await mw.InvokeAsync(ctx, accessor);

        nextCalled.Should().BeTrue();
    }
}

internal sealed class StubActorRuntime : IActorRuntime
{
    private readonly Dictionary<string, IActor> _actors = new();
    private readonly IServiceProvider _services;

    public StubActorRuntime()
    {
        var store = new InMemoryEventStore();
        _services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
    }

    public async Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
        where TAgent : IAgent
    {
        id ??= Guid.NewGuid().ToString("N");
        if (_actors.TryGetValue(id, out var existing))
            return existing;

        var agent = Activator.CreateInstance<TAgent>();
        if (agent is GAgentBase gAgentBase)
        {
            var setId = typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic);
            setId?.Invoke(gAgentBase, [id]);
            gAgentBase.Services = _services;

            var stateType = gAgentBase.GetType().BaseType;
            while (stateType is not null && (!stateType.IsGenericType || stateType.GetGenericTypeDefinition() != typeof(GAgentBase<>)))
                stateType = stateType.BaseType;

            if (stateType is not null)
            {
                var tState = stateType.GetGenericArguments()[0];
                var factoryType = typeof(IEventSourcingBehaviorFactory<>).MakeGenericType(tState);
                var factory = _services.GetService(factoryType);
                if (factory is not null)
                {
                    var prop = stateType.GetProperty("EventSourcingBehaviorFactory");
                    prop?.SetValue(gAgentBase, factory);
                }
            }
        }

        var actor = new StubActor(id, agent);
        _actors[id] = actor;
        await actor.ActivateAsync(ct);
        return actor;
    }

    public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task DestroyAsync(string id, CancellationToken ct = default)
    {
        _actors.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IActor?> GetAsync(string id)
        => Task.FromResult(_actors.TryGetValue(id, out var a) ? a : null);

    public Task<bool> ExistsAsync(string id)
        => Task.FromResult(_actors.ContainsKey(id));

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
    public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RestoreAllAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal static class AuthLookupTestHelper
{
    public static Task SimulateAuthLookupProjection(
        IProjectionDocumentStore<AppAuthLookupReadModel, string> store, string lookupKey, string userId)
    {
        var actorKey = $"authlookup:{lookupKey}";
        return store.UpsertAsync(new AppAuthLookupReadModel { Id = actorKey, LookupKey = actorKey, UserId = userId });
    }
}

internal sealed class NoOpAppProjectionManager : IAppProjectionManager
{
    public Task EnsureSubscribedAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    public Task UnsubscribeAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class StubActor : IActor
{
    public string Id { get; }
    public IAgent Agent { get; }

    public StubActor(string id, IAgent agent)
    {
        Id = id;
        Agent = agent;
    }

    public async Task ActivateAsync(CancellationToken ct = default)
    {
        if (Agent is GAgentBase gAgent)
            await gAgent.ActivateAsync();
    }

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
}
