using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Aevatar.App.Application.Auth;
using Aevatar.App.Application.Concurrency;
using Aevatar.App.Application.Completion;
using Aevatar.App.Application.Contracts;
using Aevatar.App.Application.Errors;
using Aevatar.App.Application.Projection.DependencyInjection;
using Aevatar.App.Application.Projection.Orchestration;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Services;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.App.Application.Validation;
using Aevatar.App.GAgents;
using Google.Protobuf;
using Aevatar.App.Host.Api.Endpoints;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;

namespace Aevatar.App.Host.Api.Tests;

public sealed class AppTestFixture : IAsyncLifetime
{
    public const string TrialSecret = "integration-test-secret-32-chars!";

    private WebApplication? _app;
    private readonly InMemoryEventStore _eventStore = new();
    private StubActorRuntime _runtime = null!;

    public HttpClient Client { get; private set; } = null!;
    public StubWorkflowService WorkflowStub { get; } = new();
    public StubConnector ConnectorStub { get; private set; } = null!;
    public StubS3StorageClient S3Stub { get; } = new();
    public InMemoryEventStore EventStore => _eventStore;
    public IActorRuntime Runtime => _runtime;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var services = builder.Services;
        _runtime = new StubActorRuntime(_eventStore);
        services.AddSingleton<IActorRuntime>(_runtime);
        services.AddSingleton<Aevatar.Foundation.Abstractions.IStreamProvider, NoOpStreamProvider>();
        services.AddScoped<IAppAuthContextAccessor, AppAuthContextAccessor>();
        services.AddSingleton<IAppAuthService, AppAuthService>();
        services.Configure<AppAuthOptions>(o =>
        {
            o.FirebaseProjectId = "";
            o.TrialTokenSecret = TrialSecret;
            o.TrialAuthEnabled = true;
        });
        var authBuilder = services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = AppAuthSchemeProvider.AppAuthScheme;
                options.DefaultChallengeScheme = AppAuthSchemeProvider.AppAuthScheme;
            })
            .AddPolicyScheme(AppAuthSchemeProvider.AppAuthScheme, "App auth scheme", options =>
            {
                options.ForwardDefaultSelector = context => AppAuthSchemeProvider.SelectScheme(context);
            })
            .AddScheme<AuthenticationSchemeOptions, FirebaseAuthHandler>(AppAuthSchemeProvider.FirebaseScheme, _ => { });
        if (builder.Environment.IsDevelopment())
            authBuilder.AddScheme<AuthenticationSchemeOptions, TrialAuthHandler>(AppAuthSchemeProvider.TrialScheme, _ => { });

        var connectorRegistry = new StubConnectorRegistry();
        services.AddSingleton<IConnectorRegistry>(connectorRegistry);
        ConnectorStub = connectorRegistry.Connector;
        services.AddSingleton<IWorkflowRunCommandService>(WorkflowStub);
        services.Configure<AppQuotaOptions>(options =>
        {
            options.MaxSavedEntities = 10;
            options.MaxEntitiesPerWeek = 3;
            options.MaxOperationsPerDay = 3;
        });
        services.Configure<FallbackOptions>(_ => { });
        services.AddSingleton<ActorAccessAppService>();
        services.AddSingleton<IActorAccessAppService>(sp =>
            new SyncingActorAccessAppService(
                sp.GetRequiredService<ActorAccessAppService>(),
                sp.GetRequiredService<IAppProjectionManager>()));
        services.AddSingleton<IAppProjectionManager, SyncingAppProjectionManager>();
        services.AddAppProjection();
        services.AddSingleton<ICompletionPort, TestCompletionPort>();
        services.AddSingleton<IFallbackContent, FallbackContent>();
        services.AddSingleton<IAIGenerationAppService, AIGenerationAppService>();
        services.AddSingleton<IAuthAppService, AuthAppService>();
        services.AddSingleton<IGenerationAppService, GenerationAppService>();
        services.AddSingleton<ISyncAppService, SyncAppService>();
        services.AddSingleton<IUserAppService, UserAppService>();
        services.AddSingleton<IValidator<EntityDto>, EntityValidator>();
        services.AddSingleton<IValidator<SyncRequestDto>, SyncRequestValidator>();
        services.AddSingleton<IImageConcurrencyCoordinator>(
            new ImageConcurrencyCoordinator(maxTotal: 20, maxQueueSize: 100, queueTimeoutMs: 30_000));

        services.AddSingleton<IS3StorageClient>(S3Stub);
        services.AddSingleton<IImageStorageAppService, ImageStorageAppService>();
        services.Configure<ImageStorageOptions>(o =>
        {
            o.Region = "us-east-1";
            o.BucketName = "test-bucket";
            o.CdnUrl = "https://cdn.test";
        });

        services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

        _app = builder.Build();
        _app.UseCors();
        _app.UseAuthentication();
        _app.UseMiddleware<AppErrorMiddleware>();

        _app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/sync")
                && context.Request.Method == "POST"
                && context.Request.ContentType?.Contains("text/plain", StringComparison.OrdinalIgnoreCase) == true)
            {
                context.Request.ContentType = "application/json";
            }
            await next();
        });

        _app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/api/users")
                || context.Request.Path.StartsWithSegments("/api/state")
                || context.Request.Path.StartsWithSegments("/api/sync")
                || context.Request.Path.StartsWithSegments("/api/generate")
                || context.Request.Path.StartsWithSegments("/api/upload"),
            branch => branch.UseMiddleware<AppUserProvisioningMiddleware>());

        _app.MapHealthEndpoints();
        _app.MapConfigEndpoints();
        _app.MapAuthEndpoints();
        _app.MapUserEndpoints();
        _app.MapStateEndpoints();
        _app.MapSyncEndpoints();
        _app.MapGenerateEndpoints();
        _app.MapUploadEndpoints();

        await _app.StartAsync();
        Client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }

    public string GenerateTrialToken(string sub, string email)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TrialSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            claims: [new Claim("sub", sub), new Claim("email", email)],
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public HttpClient CreateAuthenticatedClient(string sub = "user-1", string email = "test@test.com")
    {
        var token = GenerateTrialToken(sub, email);
        var client = new HttpClient { BaseAddress = Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

public sealed class StubWorkflowService : IWorkflowRunCommandService
{
    public string NextResponse { get; set; } = "";
    public bool ShouldFail { get; set; }

    public async Task<WorkflowChatRunExecutionResult> ExecuteAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null,
        CancellationToken ct = default)
    {
        if (ShouldFail)
            return new WorkflowChatRunExecutionResult(WorkflowChatRunStartError.WorkflowNotFound, null, null);

        var frame = new WorkflowOutputFrame { Type = "delta", Delta = NextResponse };
        await emitAsync(frame, ct);
        return new WorkflowChatRunExecutionResult(WorkflowChatRunStartError.None,
            new WorkflowChatRunStarted("test-actor", request.WorkflowName ?? "", "cmd-1"), null);
    }
}

public sealed class StubS3StorageClient : IS3StorageClient
{
    public int PutCallCount { get; private set; }
    public int DeleteCallCount { get; private set; }
    public bool ShouldFail { get; set; }

    public Task PutObjectAsync(string bucket, string key, Stream data, string contentType, CancellationToken ct = default)
    {
        if (ShouldFail) throw new InvalidOperationException("Simulated S3 failure");
        PutCallCount++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string bucket, string prefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task DeleteObjectsAsync(string bucket, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        DeleteCallCount++;
        return Task.CompletedTask;
    }
}

internal sealed class StubConnectorRegistry : IConnectorRegistry
{
    public StubConnector Connector { get; } = new();

    public void Register(IConnector connector) { }
    public bool TryGet(string name, out IConnector? connector) { connector = Connector; return true; }
    public IReadOnlyList<string> ListNames() => ["gemini_imagen", "gemini_tts"];
}

public sealed class StubConnector : IConnector
{
    public string Name => "stub";
    public string Type => "stub";
    public string NextResponse { get; set; } = "";
    public bool ShouldFail { get; set; }

    public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
    {
        if (ShouldFail)
            return Task.FromResult(new ConnectorResponse { Success = false, Error = "Simulated connector failure" });
        return Task.FromResult(new ConnectorResponse { Success = true, Output = NextResponse });
    }
}

internal sealed class SyncingActorAccessAppService : IActorAccessAppService
{
    private readonly ActorAccessAppService _inner;
    private readonly IAppProjectionManager _projection;

    public SyncingActorAccessAppService(ActorAccessAppService inner, IAppProjectionManager projection)
    {
        _inner = inner;
        _projection = projection;
    }

    public async Task SendCommandAsync<TAgent>(string id, IMessage command, CancellationToken ct = default)
        where TAgent : class, IAgent
    {
        await _inner.SendCommandAsync<TAgent>(id, command, ct);
        await _projection.EnsureSubscribedAsync(_inner.ResolveActorId<TAgent>(id), ct);
    }

    public string ResolveActorId<TAgent>(string id) where TAgent : class, IAgent
        => _inner.ResolveActorId<TAgent>(id);
}

internal sealed class SyncingAppProjectionManager : IAppProjectionManager
{
    private readonly IActorRuntime _runtime;
    private readonly IProjectionDocumentStore<AppSyncEntityReadModel, string> _syncStore;
    private readonly IProjectionDocumentStore<AppUserAccountReadModel, string> _accountStore;
    private readonly IProjectionDocumentStore<AppUserProfileReadModel, string> _profileStore;
    private readonly IProjectionDocumentStore<AppAuthLookupReadModel, string> _authLookupStore;

    public SyncingAppProjectionManager(
        IActorRuntime runtime,
        IProjectionDocumentStore<AppSyncEntityReadModel, string> syncStore,
        IProjectionDocumentStore<AppUserAccountReadModel, string> accountStore,
        IProjectionDocumentStore<AppUserProfileReadModel, string> profileStore,
        IProjectionDocumentStore<AppAuthLookupReadModel, string> authLookupStore)
    {
        _runtime = runtime;
        _syncStore = syncStore;
        _accountStore = accountStore;
        _profileStore = profileStore;
        _authLookupStore = authLookupStore;
    }

    public async Task EnsureSubscribedAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId)) return;

        var actor = await _runtime.GetAsync(actorId);
        if (actor?.Agent is null) return;

        switch (actor.Agent)
        {
            case GAgentBase<SyncEntityState> syncAgent:
                await _syncStore.MutateAsync(actorId, m =>
                {
                    m.UserId = actorId;
                    m.ServerRevision = syncAgent.State.Meta?.Revision ?? 0;
                    m.Entities = syncAgent.State.Entities.ToDictionary(
                        kv => kv.Key,
                        kv => ProtoToEntry(kv.Value),
                        StringComparer.Ordinal);

                    var last = syncAgent.State.LastSyncResult;
                    if (last is not null && !string.IsNullOrEmpty(last.SyncId))
                    {
                        m.SyncResults[last.SyncId] = new SyncResultEntry
                        {
                            SyncId = last.SyncId,
                            ClientRevision = last.ClientRevision,
                            ServerRevision = last.ServerRevision,
                            Accepted = [.. last.Accepted],
                            Rejected = last.Rejected
                                .Select(r => new RejectedEntityEntry
                                {
                                    ClientId = r.ClientId,
                                    ServerRevision = r.ServerRevision,
                                    Reason = r.Reason,
                                })
                                .ToList(),
                        };
                        if (!m.SyncResultOrder.Contains(last.SyncId))
                            m.SyncResultOrder.Add(last.SyncId);
                    }
                }, ct);
                break;

            case GAgentBase<UserAccountState> accountAgent:
                var user = accountAgent.State.User;
                if (user is not null)
                {
                    await _accountStore.MutateAsync(actorId, m =>
                    {
                        m.UserId = user.Id;
                        m.AuthProvider = user.AuthProvider;
                        m.AuthProviderId = user.AuthProviderId;
                        m.Email = user.Email;
                        m.EmailVerified = user.EmailVerified;
                        m.CreatedAt = user.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
                        m.LastLoginAt = user.LastLoginAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
                        m.Deleted = false;
                    }, ct);
                }
                else
                {
                    await _accountStore.MutateAsync(actorId, m => m.Deleted = true, ct);
                }
                break;

            case GAgentBase<UserProfileState> profileAgent:
                var profile = profileAgent.State.Profile;
                await _profileStore.MutateAsync(actorId, m =>
                {
                    m.UserId = actorId;
                    m.HasProfile = profile is not null;
                    if (profile is null) return;
                    m.FirstName = profile.FirstName;
                    m.LastName = profile.LastName;
                    m.Gender = profile.Gender;
                    m.Timezone = profile.Timezone;
                    m.Purpose = profile.Purpose;
                    m.NotificationsEnabled = profile.NotificationsEnabled;
                    m.ReminderTime = profile.ReminderTime;
                    m.DateOfBirth = profile.DateOfBirth?.ToDateTimeOffset();
                    m.Interests = profile.Interests.ToList();
                    m.ProfileUpdatedAt = profile.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
                }, ct);
                break;

            case GAgentBase<AuthLookupState> authAgent:
                await _authLookupStore.MutateAsync(actorId, m =>
                {
                    m.LookupKey = authAgent.State.LookupKey;
                    m.UserId = authAgent.State.UserId;
                }, ct);
                break;
        }
    }

    public Task UnsubscribeAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;

    private static SyncEntityEntry ProtoToEntry(SyncEntity e)
    {
        return new SyncEntityEntry
        {
            ClientId = e.ClientId,
            EntityType = e.EntityType,
            UserId = e.UserId,
            Revision = e.Revision,
            Position = e.Position,
            Source = e.Source switch
            {
                EntitySource.Bank => "bank",
                EntitySource.User => "user",
                EntitySource.Edited => "edited",
                _ => "ai"
            },
            BankEligible = e.BankEligible,
            BankHash = e.BankHash,
            Inputs = StructToJson(e.Inputs),
            Output = StructToJson(e.Output),
            State = StructToJson(e.State),
            DeletedAt = e.DeletedAt is not null ? e.DeletedAt.ToDateTimeOffset() : null,
            CreatedAt = e.CreatedAt is not null ? e.CreatedAt.ToDateTimeOffset() : null,
            UpdatedAt = e.UpdatedAt is not null ? e.UpdatedAt.ToDateTimeOffset() : null,
            Refs = new Dictionary<string, string>(e.Refs, StringComparer.Ordinal),
        };
    }

    private static System.Text.Json.JsonElement? StructToJson(Google.Protobuf.WellKnownTypes.Struct? s)
    {
        if (s is null || s.Fields.Count == 0) return null;
        var json = Google.Protobuf.JsonFormatter.Default.Format(s);
        return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
    }
}

internal sealed class TestCompletionPort : ICompletionPort
{
    public Task WaitAsync(string completionKey, CancellationToken ct = default) => Task.CompletedTask;
    public void Complete(string completionKey) { }
}

internal sealed class NoOpStreamProvider : Aevatar.Foundation.Abstractions.IStreamProvider
{
    public IStream GetStream(string actorId) => new NoOpStream(actorId);
}

internal sealed class NoOpStream : IStream
{
    public string StreamId { get; }
    public NoOpStream(string streamId) => StreamId = streamId;
    public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage => Task.CompletedTask;
    public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default) where T : IMessage, new()
        => Task.FromResult<IAsyncDisposable>(new NoOpSubscription());
    public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
    private sealed class NoOpSubscription : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}

internal sealed class StubActorRuntime : IActorRuntime
{
    private readonly Dictionary<string, IActor> _actors = new();
    private readonly IServiceProvider _services;

    public StubActorRuntime(InMemoryEventStore store)
    {
        _services = new ServiceCollection()
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
    }

    private static string TypeKey<TAgent>(string id) where TAgent : IAgent
        => $"{typeof(TAgent).Name}:{id}";

    public async Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
        where TAgent : IAgent
    {
        id ??= Guid.NewGuid().ToString("N");
        var key = TypeKey<TAgent>(id);
        if (_actors.TryGetValue(key, out var existing)) return existing;

        var agent = Activator.CreateInstance<TAgent>();
        if (agent is GAgentBase gab)
        {
            var eventStoreId = $"{typeof(TAgent).Name}_{id}";
            typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(gab, [eventStoreId]);
            gab.Services = _services;
            InjectEventSourcing(gab);
        }

        var actor = new StubActor(id, agent);
        _actors[key] = actor;
        await actor.ActivateAsync(ct);
        return actor;
    }

    public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task DestroyAsync(string id, CancellationToken ct = default)
    {
        var keysToRemove = _actors.Keys.Where(k => k.EndsWith($":{id}")).ToList();
        foreach (var k in keysToRemove) _actors.Remove(k);
        return Task.CompletedTask;
    }

    public Task<IActor?> GetAsync(string id)
    {
        foreach (var kv in _actors)
        {
            if (kv.Value.Id == id)
                return Task.FromResult<IActor?>(kv.Value);
        }
        return Task.FromResult<IActor?>(null);
    }

    public IActor? GetByType<TAgent>(string id) where TAgent : IAgent
        => _actors.GetValueOrDefault(TypeKey<TAgent>(id));

    public Task<bool> ExistsAsync(string id)
        => Task.FromResult(_actors.Values.Any(a => a.Id == id));

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
    public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RestoreAllAsync(CancellationToken ct = default) => Task.CompletedTask;

    private void InjectEventSourcing(GAgentBase gab)
    {
        var baseType = gab.GetType().BaseType;
        while (baseType is not null && (!baseType.IsGenericType || baseType.GetGenericTypeDefinition() != typeof(GAgentBase<>)))
            baseType = baseType.BaseType;
        if (baseType is null) return;

        var tState = baseType.GetGenericArguments()[0];
        var factoryType = typeof(IEventSourcingBehaviorFactory<>).MakeGenericType(tState);
        var factory = _services.GetService(factoryType);
        if (factory is null) return;

        baseType.GetProperty("EventSourcingBehaviorFactory")?.SetValue(gab, factory);
    }
}

internal sealed class StubActor : IActor
{
    public string Id { get; }
    public IAgent Agent { get; }

    public StubActor(string id, IAgent agent) { Id = id; Agent = agent; }

    public async Task ActivateAsync(CancellationToken ct = default)
    {
        if (Agent is GAgentBase gab) await gab.ActivateAsync();
    }

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        if (Agent is GAgentBase gab)
            await gab.HandleEventAsync(envelope);
    }

    public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
}
