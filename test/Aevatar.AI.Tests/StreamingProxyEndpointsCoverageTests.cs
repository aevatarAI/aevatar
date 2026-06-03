using System.Reflection;
using System.Security.Claims;
using System.Text;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
                "Summary Standup"));

        var result = await InvokeHandleCreateRoomAsync(
            CreateScopedHttpContext(),
            "scope-a",
            new StreamingProxyEndpoints.CreateRoomRequest("  Summary Standup  "),
            service,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        service.Commands.Should().ContainSingle();
        service.Commands[0].Should().Be(new StreamingProxyRoomCreateCommand("scope-a", "  Summary Standup  "));
        body.Should().Contain("room-123");
        body.Should().Contain("Summary Standup");
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
    public async Task HandleListParticipantsAsync_ShouldReturnRoomProjectionParticipants()
    {
        var joinedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-14T10:00:00+08:00"));
        var participantsQueryPort = new RecordingRoomParticipantsQueryPort
        {
            Result = new StreamingProxyRoomParticipantsSnapshot
            {
                RootActorId = "room-1",
                StateVersion = 5,
                UpdatedAt = joinedAt,
                Participants =
                {
                    new StreamingProxyRoomParticipantSnapshotEntry
                    {
                        AgentId = "agent-1",
                        DisplayName = "Bot",
                        JoinedAt = joinedAt,
                    },
                },
            },
        };
        var loggerFactory = LoggerFactory.Create(_ => { });

        var result = await InvokeHandleListParticipantsAsync(
            CreateScopedHttpContext(),
            "scope-a",
            "room-1",
            participantsQueryPort,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("agent-1");
        body.Should().Contain("Bot");
        participantsQueryPort.Queries.Should().ContainSingle().Which.Should().Be("room-1");
    }

    [Fact]
    public async Task HandleListParticipantsAsync_ShouldReturnServerError_WhenParticipantsQueryThrows()
    {
        var participantsQueryPort = new RecordingRoomParticipantsQueryPort
        {
            ThrowOnGet = new InvalidOperationException("list failed"),
        };
        var loggerFactory = LoggerFactory.Create(_ => { });

        var result = await InvokeHandleListParticipantsAsync(
            CreateScopedHttpContext(),
            "scope-a",
            "room-1",
            participantsQueryPort,
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
        var participantsQueryPort = new RecordingRoomParticipantsQueryPort();
        var loggerFactory = LoggerFactory.Create(_ => { });

        var result = await InvokeHandleListParticipantsAsync(
            CreateScopedHttpContext("scope-b"),
            "scope-a",
            "room-1",
            participantsQueryPort,
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
        IStreamingProxyRoomParticipantsQueryPort participantsQueryPort,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        return await (Task<IResult>)HandleListParticipantsAsyncMethod.Invoke(
            null,
            [context, scopeId, roomId, new RecordingGAgentActorStore([]), participantsQueryPort, loggerFactory, ct])!;
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

        public Task<StreamingProxyRoomPostMessageResult> PostMessageAsync(
            StreamingProxyRoomPostMessageCommand command,
            CancellationToken cancellationToken = default)
        {
            _ = command;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new StreamingProxyRoomPostMessageResult(
                StreamingProxyRoomPostMessageStatus.Accepted));
        }

        public Task<StreamingProxyRoomJoinResult> JoinAsync(
            StreamingProxyRoomJoinCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new StreamingProxyRoomJoinResult(
                StreamingProxyRoomJoinStatus.Accepted,
                command.AgentId?.Trim(),
                command.DisplayName?.Trim()));
        }

        public Task<StreamingProxyRoomLeaveResult> LeaveAsync(
            StreamingProxyRoomLeaveCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new StreamingProxyRoomLeaveResult(
                StreamingProxyRoomLeaveStatus.Accepted,
                command.AgentId?.Trim()));
        }

        public Task PublishTerminalStateAsync(
            StreamingProxyRoomTerminalStateCommand command,
            CancellationToken cancellationToken = default)
        {
            _ = command;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SubmitParticipantsResolvedAsync(
            StreamingProxyRoomParticipantsResolvedCommand command,
            CancellationToken cancellationToken = default)
        {
            _ = command;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SubmitParticipantReplyObservedAsync(
            StreamingProxyRoomParticipantReplyObservedCommand command,
            CancellationToken cancellationToken = default)
        {
            _ = command;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SubmitParticipantReplyFailedAsync(
            StreamingProxyRoomParticipantReplyFailedCommand command,
            CancellationToken cancellationToken = default)
        {
            _ = command;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRoomParticipantsQueryPort : IStreamingProxyRoomParticipantsQueryPort
    {
        public List<string> Queries { get; } = [];
        public Exception? ThrowOnGet { get; init; }
        public StreamingProxyRoomParticipantsSnapshot? Result { get; init; }

        public Task<StreamingProxyRoomParticipantsSnapshot?> GetAsync(
            string rootActorId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnGet is not null)
                throw ThrowOnGet;

            Queries.Add(rootActorId);
            return Task.FromResult(Result);
        }
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
