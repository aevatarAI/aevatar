using System.Reflection;
using System.Security.Claims;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AppStreamingProxyParticipant = Aevatar.Studio.Application.Studio.Abstractions.StreamingProxyParticipant;

namespace Aevatar.AI.Tests;

public sealed class StreamingProxyEndpointsCoverageTests
{
    private static readonly MethodInfo HandleCreateRoomAsyncMethod = typeof(StreamingProxyEndpoints)
        .GetMethod("HandleCreateRoomAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("HandleCreateRoomAsync not found.");

    private static readonly MethodInfo HandleListParticipantsAsyncMethod = typeof(StreamingProxyEndpoints)
        .GetMethod("HandleListParticipantsAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("HandleListParticipantsAsync not found.");

    [Fact]
    public async Task HandleCreateRoomAsync_ShouldDelegateRoomCreationToCommandService()
    {
        var service = new RecordingRoomCommandService(
            new StreamingProxyRoomCreateResult(
                StreamingProxyRoomCreateStatus.Created,
                "room-123",
                "Daily Standup"));

        var result = await InvokeHandleCreateRoomAsync(
            CreateScopedHttpContext(),
            "scope-a",
            new StreamingProxyEndpoints.CreateRoomRequest("  Daily Standup  "),
            service,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        service.Commands.Should().ContainSingle();
        service.Commands[0].Should().Be(new StreamingProxyRoomCreateCommand("scope-a", "  Daily Standup  "));
        body.Should().Contain("room-123");
        body.Should().Contain("Daily Standup");
    }

    [Fact]
    public async Task HandleCreateRoomAsync_ShouldMapAdmissionUnavailableToServiceUnavailable()
    {
        var service = new RecordingRoomCommandService(
            new StreamingProxyRoomCreateResult(
                StreamingProxyRoomCreateStatus.AdmissionUnavailable,
                null,
                "Incident Room"));

        var result = await InvokeHandleCreateRoomAsync(
            CreateScopedHttpContext(),
            "scope-a",
            new StreamingProxyEndpoints.CreateRoomRequest("Incident Room"),
            service,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("Failed to create room");
    }

    [Fact]
    public async Task HandleCreateRoomAsync_ShouldMapCommandFailureToServerError()
    {
        var service = new RecordingRoomCommandService(
            new StreamingProxyRoomCreateResult(
                StreamingProxyRoomCreateStatus.Failed,
                null,
                "Incident Room"));

        var result = await InvokeHandleCreateRoomAsync(
            CreateScopedHttpContext(),
            "scope-a",
            new StreamingProxyEndpoints.CreateRoomRequest("Incident Room"),
            service,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Should().Contain("Failed to create room");
    }

    [Fact]
    public async Task HandleListParticipantsAsync_ShouldReturnStoredParticipants()
    {
        var participantStore = new RecordingParticipantStore
        {
            Participants =
            [
                new AppStreamingProxyParticipant("agent-1", "Bot", DateTimeOffset.Parse("2026-04-14T10:00:00+08:00")),
            ],
        };
        var loggerFactory = LoggerFactory.Create(_ => { });

        var result = await InvokeHandleListParticipantsAsync(
            CreateScopedHttpContext(),
            "scope-a",
            "room-1",
            participantStore,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("agent-1");
        body.Should().Contain("Bot");
    }

    [Fact]
    public async Task HandleListParticipantsAsync_ShouldReturnServerError_WhenStoreThrows()
    {
        var participantStore = new RecordingParticipantStore
        {
            ThrowOnList = new InvalidOperationException("list failed"),
        };
        var loggerFactory = LoggerFactory.Create(_ => { });

        var result = await InvokeHandleListParticipantsAsync(
            CreateScopedHttpContext(),
            "scope-a",
            "room-1",
            participantStore,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Should().Contain("Failed to list participants");
    }

    [Fact]
    public async Task HandleCreateRoomAsync_ShouldRejectMismatchedAuthenticatedScope()
    {
        var service = new RecordingRoomCommandService(
            new StreamingProxyRoomCreateResult(
                StreamingProxyRoomCreateStatus.Created,
                "room-denied",
                "Denied Room"));

        var result = await InvokeHandleCreateRoomAsync(
            CreateScopedHttpContext("scope-b"),
            "scope-a",
            new StreamingProxyEndpoints.CreateRoomRequest("Denied Room"),
            service,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status403Forbidden);
        body.Should().Contain("SCOPE_ACCESS_DENIED");
        body.Should().Contain("Authenticated scope does not match requested scope.");
        service.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleListParticipantsAsync_ShouldRejectMismatchedAuthenticatedScope()
    {
        var participantStore = new RecordingParticipantStore();
        var loggerFactory = LoggerFactory.Create(_ => { });

        var result = await InvokeHandleListParticipantsAsync(
            CreateScopedHttpContext("scope-b"),
            "scope-a",
            "room-1",
            participantStore,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status403Forbidden);
        body.Should().Contain("SCOPE_ACCESS_DENIED");
        body.Should().Contain("Authenticated scope does not match requested scope.");
    }

    private static async Task<IResult> InvokeHandleCreateRoomAsync(
        HttpContext context,
        string scopeId,
        StreamingProxyEndpoints.CreateRoomRequest? request,
        IStreamingProxyRoomCommandService roomCommandService,
        CancellationToken ct)
    {
        return await (Task<IResult>)HandleCreateRoomAsyncMethod.Invoke(
            null,
            [context, scopeId, request, roomCommandService, ct])!;
    }

    private static async Task<IResult> InvokeHandleListParticipantsAsync(
        HttpContext context,
        string scopeId,
        string roomId,
        IStreamingProxyParticipantStore participantStore,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        return await (Task<IResult>)HandleListParticipantsAsyncMethod.Invoke(
            null,
            [context, scopeId, roomId, new RecordingGAgentActorStore([]), participantStore, loggerFactory, ct])!;
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();

        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static DefaultHttpContext CreateScopedHttpContext(string claimedScopeId = "scope-a")
    {
        return new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
                .BuildServiceProvider(),
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                [
                    new Claim("scope_id", claimedScopeId),
                ],
                authenticationType: "TestAuth")),
        };
    }

    private sealed class RecordingGAgentActorStore(List<string> operations) :
        IGAgentActorRegistryCommandPort,
        IGAgentActorRegistryQueryPort,
        IScopeResourceAdmissionPort
    {
        public List<(string ScopeId, string GAgentType, string ActorId)> AddedActors { get; } = [];
        public List<(string ScopeId, string GAgentType, string ActorId)> RemovedActors { get; } = [];
        public Exception? ThrowOnRegister { get; init; }
        public Exception? ThrowOnUnregister { get; init; }
        public GAgentActorRegistryCommandStage RegisterStage { get; init; } =
            GAgentActorRegistryCommandStage.AdmissionVisible;

        public Task<GAgentActorRegistrySnapshot> ListActorsAsync(
            string scopeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GAgentActorRegistrySnapshot(
                scopeId,
                [],
                0,
                DateTimeOffset.MinValue,
                DateTimeOffset.UtcNow));

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            operations.Add($"store:add:{registration.ActorId}");
            AddedActors.Add((registration.ScopeId, registration.GAgentType, registration.ActorId));
            if (ThrowOnRegister is not null)
                throw ThrowOnRegister;

            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                RegisterStage));
        }

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            operations.Add($"store:remove:{registration.ActorId}");
            RemovedActors.Add((registration.ScopeId, registration.GAgentType, registration.ActorId));
            if (ThrowOnUnregister is not null)
                throw ThrowOnUnregister;

            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ScopeResourceAdmissionResult.Allowed());
    }

    private sealed class RecordingRoomCommandService(StreamingProxyRoomCreateResult result)
        : IStreamingProxyRoomCommandService
    {
        public List<StreamingProxyRoomCreateCommand> Commands { get; } = [];

        public Task<StreamingProxyRoomCreateResult> CreateRoomAsync(
            StreamingProxyRoomCreateCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingParticipantStore : IStreamingProxyParticipantStore
    {
        public Exception? ThrowOnList { get; init; }
        public IReadOnlyList<AppStreamingProxyParticipant> Participants { get; init; } = [];

        public Task<IReadOnlyList<AppStreamingProxyParticipant>> ListAsync(
            string roomId,
            CancellationToken cancellationToken = default)
        {
            _ = roomId;
            if (ThrowOnList is not null)
                throw ThrowOnList;

            return Task.FromResult(Participants);
        }

        public Task AddAsync(
            string roomId,
            string agentId,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            _ = roomId;
            _ = agentId;
            _ = displayName;
            return Task.CompletedTask;
        }

        public Task RemoveParticipantAsync(
            string roomId,
            string agentId,
            CancellationToken cancellationToken = default)
        {
            _ = roomId;
            _ = agentId;
            return Task.CompletedTask;
        }

        public Task RemoveRoomAsync(string roomId, CancellationToken cancellationToken = default)
        {
            _ = roomId;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActorRuntime(List<string> operations, IActor actor) : IActorRuntime
    {
        public Exception? ThrowOnCreate { get; init; }
        public List<string> DestroyedActorIds { get; } = [];
        public RecordingActor? LastCreatedActor { get; private set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            _ = agentType;
            ct.ThrowIfCancellationRequested();

            var actorId = id ?? throw new InvalidOperationException("Actor id is required for this test.");
            operations.Add($"runtime:create:{actorId}");
            if (ThrowOnCreate is not null)
                throw ThrowOnCreate;

            LastCreatedActor = actor is RecordingActor recordingActor && recordingActor.Id == actorId
                ? recordingActor
                : new RecordingActor(actorId, operations);
            return Task.FromResult<IActor>(LastCreatedActor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            operations.Add($"runtime:destroy:{id}");
            DestroyedActorIds.Add(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            _ = id;
            return Task.FromResult<IActor?>(null);
        }

        public Task<bool> ExistsAsync(string id)
        {
            _ = id;
            return Task.FromResult(false);
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            _ = parentId;
            _ = childId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            _ = childId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActor(string id, List<string>? operations = null) : IActor
    {
        public List<EventEnvelope> ReceivedEnvelopes { get; } = [];

        public string Id { get; } = id;

        public IAgent Agent { get; } = new StubAgent(id);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            operations?.Add($"actor:init:{Id}");
            ReceivedEnvelopes.Add(envelope);
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "StreamingProxyEndpointsCoverageTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
