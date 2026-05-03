using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Projection.CommandServices;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class ScopeBindingStudioMemberPlatformBindingCommandServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldOnlyAcceptWithoutRunningPlatformBinding()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = new ScopeBindingStudioMemberPlatformBindingCommandService(
            scopeBindingPort,
            dispatchPort,
            NullLogger<ScopeBindingStudioMemberPlatformBindingCommandService>.Instance);

        var accepted = await service.StartAsync(
            "studio-member-binding-run:bind-1",
            NewScriptStartRequest(),
            CancellationToken.None);

        accepted.BindingRunId.Should().Be("bind-1");
        accepted.PlatformBindingCommandId.Should().Be("platform-bind-1");

        scopeBindingPort.Requests.Should().BeEmpty();
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRunPlatformBindingAndDispatchSucceededContinuation()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = new ScopeBindingStudioMemberPlatformBindingCommandService(
            scopeBindingPort,
            dispatchPort,
            NullLogger<ScopeBindingStudioMemberPlatformBindingCommandService>.Instance);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest(),
            CancellationToken.None);

        var dispatch = dispatchPort.Dispatches.Should().ContainSingle().Which;
        dispatch.ActorId.Should().Be("studio-member-binding-run:bind-1");
        var succeeded = dispatch.Envelope.Payload.Unpack<StudioMemberPlatformBindingSucceeded>();
        succeeded.BindingRunId.Should().Be("bind-1");
        succeeded.PlatformBindingCommandId.Should().Be("platform-bind-1");
        succeeded.Result.PublishedServiceId.Should().Be("member-m-1");
        succeeded.Result.RevisionId.Should().Be("rev-1");
        succeeded.Result.ImplementationKind.Should().Be(StudioMemberImplementationKind.Script);
        succeeded.Result.ImplementationRef.Script.ScriptId.Should().Be("script-1");

        scopeBindingPort.Requests.Should().ContainSingle();
        scopeBindingPort.Requests[0].ScopeId.Should().Be("scope-1");
        scopeBindingPort.Requests[0].ServiceId.Should().Be("member-m-1");
        scopeBindingPort.Requests[0].DisplayName.Should().Be("Script member");
        scopeBindingPort.Requests[0].ImplementationKind.Should().Be(ScopeBindingImplementationKind.Scripting);
        scopeBindingPort.Requests[0].Script!.ScriptId.Should().Be("script-1");
        scopeBindingPort.Requests[0].Script!.ScriptRevision.Should().Be("draft-1");
    }

    [Fact]
    public async Task ExecuteAsync_WhenScopeBindingFails_ShouldDispatchFailedContinuation()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            Failure = new InvalidOperationException("platform rejected"),
        };
        var dispatchPort = new RecordingDispatchPort();
        var service = new ScopeBindingStudioMemberPlatformBindingCommandService(
            scopeBindingPort,
            dispatchPort,
            NullLogger<ScopeBindingStudioMemberPlatformBindingCommandService>.Instance);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest(),
            CancellationToken.None);

        var dispatch = dispatchPort.Dispatches.Should().ContainSingle().Which;
        var failed = dispatch.Envelope.Payload.Unpack<StudioMemberPlatformBindingFailed>();
        failed.BindingRunId.Should().Be("bind-1");
        failed.PlatformBindingCommandId.Should().Be("platform-bind-1");
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_FAILED");
        failed.Failure.Message.Should().Be("platform rejected");
    }

    private static StudioMemberPlatformBindingStartRequested NewScriptStartRequest() =>
        new()
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-bind-1",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Admitted = new StudioMemberBindingAdmittedSnapshot
            {
                ScopeId = "scope-1",
                MemberId = "m-1",
                PublishedServiceId = "member-m-1",
                ImplementationKind = StudioMemberImplementationKind.Script,
                DisplayName = "Script member",
            },
            Request = new StudioMemberBindingRequest
            {
                BindingRunId = "bind-1",
                ScopeId = "scope-1",
                MemberId = "m-1",
                RequestHash = "hash-1",
                Script = new StudioMemberScriptBindingRequest
                {
                    ScriptId = "script-1",
                    ScriptRevision = "draft-1",
                },
            },
        };

    private sealed class RecordingScopeBindingCommandPort : IScopeBindingCommandPort
    {
        public List<ScopeBindingUpsertRequest> Requests { get; } = [];
        public Exception? Failure { get; init; }

        public Task<ScopeBindingUpsertResult> UpsertAsync(
            ScopeBindingUpsertRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Failure != null)
                throw Failure;

            return Task.FromResult(new ScopeBindingUpsertResult(
                ScopeId: request.ScopeId,
                ServiceId: request.ServiceId ?? string.Empty,
                DisplayName: request.DisplayName ?? string.Empty,
                RevisionId: "rev-1",
                ImplementationKind: request.ImplementationKind,
                ExpectedActorId: "scope-script:scope-1:script-1",
                Script: new ScopeBindingScriptResult("script-1", "rev-1", "scope-script:scope-1:script-1")));
        }
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<DispatchedCommand> Dispatches { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add(new DispatchedCommand(actorId, envelope));
            return Task.CompletedTask;
        }

        public sealed record DispatchedCommand(string ActorId, EventEnvelope Envelope);
    }
}
