using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Projection.CommandServices;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class ScopeBindingStudioMemberPlatformBindingCommandServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldOnlyAcceptWithoutRunningPlatformBinding()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var service = CreateService(scopeBindingPort);

        var accepted = await service.StartAsync(
            "studio-member-binding-run:bind-1",
            NewScriptStartRequest());

        accepted.BindingRunId.Should().Be("bind-1");
        accepted.PlatformBindingCommandId.Should().Be("platform-bind-1");
        scopeBindingPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_WhenCommandIdMissing_ShouldUseSharedFallbackConvention()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var service = CreateService(scopeBindingPort);
        var request = NewScriptStartRequest();
        request.PlatformBindingCommandId = "";

        var accepted = await service.StartAsync(
            "studio-member-binding-run:bind-1",
            request);

        accepted.PlatformBindingCommandId.Should().Be("platform-bind-1-1");
        scopeBindingPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRunPlatformBindingAndDispatchSucceededContinuation()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        var accepted = await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest());

        accepted.Should().Be(new StudioMemberPlatformBindingExecutionAccepted("bind-1", "platform-bind-1"));
        var succeeded = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingSucceeded>();
        succeeded.BindingRunId.Should().Be("bind-1");
        succeeded.PlatformBindingCommandId.Should().Be("platform-bind-1");
        succeeded.Result.PublishedServiceId.Should().Be("member-m-1");
        succeeded.Result.RevisionId.Should().Be("rev-platform-bind-1");
        succeeded.Result.ImplementationKind.Should().Be(StudioMemberImplementationKind.Script);
        succeeded.Result.ImplementationRef.Script.ScriptId.Should().Be("script-1");

        scopeBindingPort.Requests.Should().ContainSingle();
        var request = scopeBindingPort.Requests[0];
        request.ScopeId.Should().Be("scope-1");
        request.ServiceId.Should().Be("member-m-1");
        request.DisplayName.Should().Be("Script member");
        request.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Scripting);
        request.Script!.ScriptId.Should().Be("script-1");
        request.Script!.ScriptRevision.Should().Be("draft-1");
        request.RevisionId.Should().Be("rev-platform-bind-1");
        request.AllowExistingRevisionReplay.Should().BeTrue();
        request.ReplayRevisionId.Should().Be("rev-platform-bind-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldBuildWorkflowBindingRequestAndDispatchWorkflowResult()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewWorkflowStartRequest());

        var succeeded = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingSucceeded>();
        succeeded.Result.ImplementationKind.Should().Be(StudioMemberImplementationKind.Workflow);
        succeeded.Result.ImplementationRef.Workflow.WorkflowId.Should().Be("workflow-main");
        succeeded.Result.ImplementationRef.Workflow.WorkflowRevision.Should().Be("rev-platform-bind-1");

        scopeBindingPort.Requests.Should().ContainSingle();
        var request = scopeBindingPort.Requests[0];
        request.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Workflow);
        request.Workflow!.WorkflowYamls.Should().ContainSingle().Which.Should().Contain("name: workflow-main");
        request.AllowExistingRevisionReplay.Should().BeTrue();
        request.ReplayRevisionId.Should().Be("rev-platform-bind-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldBuildGAgentBindingRequestAndDispatchGAgentResult()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewGAgentStartRequest());

        var succeeded = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingSucceeded>();
        succeeded.Result.ImplementationKind.Should().Be(StudioMemberImplementationKind.Gagent);
        succeeded.Result.ImplementationRef.Gagent.ActorTypeName.Should().Be("Tests.JokerGAgent");

        scopeBindingPort.Requests.Should().ContainSingle();
        var request = scopeBindingPort.Requests[0];
        request.ImplementationKind.Should().Be(ScopeBindingImplementationKind.GAgent);
        request.GAgent!.ActorTypeName.Should().Be("Tests.JokerGAgent");
        request.GAgent.Endpoints.Should().ContainSingle().Which.Kind.Should().Be(ServiceEndpointKind.Chat);
        request.GAgent.Endpoints[0].EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task ExecuteAsync_WhenGAgentEndpointIsCommand_ShouldMapEndpointKind()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);
        var request = NewGAgentStartRequest();
        request.Request.Gagent.Endpoints[0].Kind = StudioMemberGAgentEndpointKind.Command;

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            request);

        var succeeded = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingSucceeded>();
        succeeded.Result.ImplementationKind.Should().Be(StudioMemberImplementationKind.Gagent);
        scopeBindingPort.Requests.Should().ContainSingle();
        scopeBindingPort.Requests[0].GAgent!.Endpoints.Should().ContainSingle().Which.Kind.Should().Be(ServiceEndpointKind.Command);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCommandIdContainsSeparators_ShouldNormalizeRevisionAndReplayId()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "Platform Bind: 2!!",
            NewScriptStartRequest());

        var succeeded = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingSucceeded>();
        succeeded.Result.RevisionId.Should().Be("rev-platform-bind-2");
        scopeBindingPort.Requests.Should().ContainSingle();
        scopeBindingPort.Requests[0].RevisionId.Should().Be("rev-platform-bind-2");
        scopeBindingPort.Requests[0].ReplayRevisionId.Should().Be("rev-platform-bind-2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenScriptRevisionAbsent_ShouldPassNullScriptRevision()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);
        var request = NewScriptStartRequest();
        request.Request.Script.ClearScriptRevision();

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            request);

        await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingSucceeded>();
        scopeBindingPort.Requests.Should().ContainSingle();
        scopeBindingPort.Requests[0].Script!.ScriptRevision.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAckBeforePlatformOutcomeAndDispatchContinuation()
    {
        var releaseUpsert = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            ReleaseUpsert = releaseUpsert,
        };
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        var accepted = await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest());

        var request = await scopeBindingPort.UpsertStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.RevisionId.Should().Be("rev-platform-bind-1");
        accepted.PlatformBindingCommandId.Should().Be("platform-bind-1");
        dispatchPort.Dispatched.Should().BeEmpty();

        releaseUpsert.SetResult(null);
        var succeeded = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingSucceeded>();
        succeeded.PlatformBindingCommandId.Should().Be("platform-bind-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHonorExplicitRevisionId()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var service = CreateService(scopeBindingPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest("rev-explicit"));

        scopeBindingPort.Requests.Should().ContainSingle();
        var request = scopeBindingPort.Requests[0];
        request.RevisionId.Should().Be("rev-explicit");
        request.ReplayRevisionId.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenScopeBindingFails_ShouldDispatchFailedContinuation()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            Failure = new InvalidOperationException("platform rejected"),
        };
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest());

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingFailed>();
        failed.BindingRunId.Should().Be("bind-1");
        failed.PlatformBindingCommandId.Should().Be("platform-bind-1");
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_FAILED");
        failed.Failure.Message.Should().Be("platform rejected");
    }

    [Fact]
    public async Task ExecuteAsync_WhenImplementationPayloadMissing_ShouldReturnFailedOutcome()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var request = NewScriptStartRequest();
        request.Request.Script = null;

        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);
        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            request);

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingFailed>();
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_FAILED");
        failed.Failure.Message.Should().Contain("binding request must carry exactly one implementation payload");
        scopeBindingPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenGAgentEndpointKindUnsupported_ShouldReturnFailedOutcome()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var request = NewGAgentStartRequest();
        request.Request.Gagent.Endpoints[0].Kind = StudioMemberGAgentEndpointKind.Unspecified;

        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);
        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            request);

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingFailed>();
        failed.Failure.Message.Should().Contain("Unsupported gagent endpoint kind");
        scopeBindingPort.Requests.Should().BeEmpty();
    }

    private static ScopeBindingStudioMemberPlatformBindingCommandService CreateService(
        RecordingScopeBindingCommandPort scopeBindingPort,
        RecordingActorDispatchPort? dispatchPort = null) =>
        new(
            scopeBindingPort,
            dispatchPort ?? new RecordingActorDispatchPort(),
            NullLogger<ScopeBindingStudioMemberPlatformBindingCommandService>.Instance);

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        private readonly List<(string ActorId, EventEnvelope Envelope)> _dispatched = [];
        private readonly TaskCompletionSource<(string ActorId, EventEnvelope Envelope)> _firstDispatch = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(string ActorId, EventEnvelope Envelope)> Dispatched => _dispatched;

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            _dispatched.Add((actorId, envelope));
            _firstDispatch.TrySetResult((actorId, envelope));
            return Task.CompletedTask;
        }

        public async Task<TPayload> WaitForPayloadAsync<TPayload>()
            where TPayload : class, IMessage<TPayload>, new()
        {
            var (_, envelope) = _dispatched.Count == 0
                ? await _firstDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5))
                : _dispatched[^1];

            var payload = new TPayload();
            envelope.Payload.Is(payload.Descriptor).Should().BeTrue();
            return envelope.Payload.Unpack<TPayload>();
        }
    }

    private static StudioMemberPlatformBindingStartRequested NewScriptStartRequest(string? revisionId = null)
    {
        var bindingRequest = new StudioMemberBindingRequest
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
        };
        if (revisionId != null)
            bindingRequest.RevisionId = revisionId;

        return new StudioMemberPlatformBindingStartRequested
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
            Request = bindingRequest,
        };
    }

    private static StudioMemberPlatformBindingStartRequested NewWorkflowStartRequest()
    {
        var request = NewScriptStartRequest();
        request.Admitted.ImplementationKind = StudioMemberImplementationKind.Workflow;
        request.Admitted.DisplayName = "Workflow member";
        request.Request.Script = null;
        request.Request.Workflow = new StudioMemberWorkflowBindingRequest
        {
            WorkflowYamls = { "name: workflow-main\nsteps: []\n" },
        };
        return request;
    }

    private static StudioMemberPlatformBindingStartRequested NewGAgentStartRequest()
    {
        var request = NewScriptStartRequest();
        request.Admitted.ImplementationKind = StudioMemberImplementationKind.Gagent;
        request.Admitted.DisplayName = "GAgent member";
        request.Request.Script = null;
        request.Request.Gagent = new StudioMemberGAgentBindingRequest
        {
            ActorTypeName = "Tests.JokerGAgent",
            Endpoints =
            {
                new StudioMemberGAgentEndpointBindingRequest
                {
                    EndpointId = "chat",
                    DisplayName = "Chat",
                    Kind = StudioMemberGAgentEndpointKind.Chat,
                    RequestTypeUrl = "type.googleapis.com/a.Request",
                    ResponseTypeUrl = "type.googleapis.com/a.Response",
                    Description = "Chat endpoint",
                },
            },
        };
        return request;
    }

    private sealed class RecordingScopeBindingCommandPort : IScopeBindingCommandPort
    {
        public List<ScopeBindingUpsertRequest> Requests { get; } = [];
        public Exception? Failure { get; init; }
        public TaskCompletionSource<ScopeBindingUpsertRequest> UpsertStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object?>? ReleaseUpsert { get; init; }

        public async Task<ScopeBindingUpsertResult> UpsertAsync(
            ScopeBindingUpsertRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            UpsertStarted.TrySetResult(request);
            if (ReleaseUpsert != null)
                await ReleaseUpsert.Task.ConfigureAwait(false);
            if (Failure != null)
                throw Failure;

            return request.ImplementationKind switch
            {
                ScopeBindingImplementationKind.Workflow => BuildWorkflowResult(request),
                ScopeBindingImplementationKind.GAgent => BuildGAgentResult(request),
                _ => BuildScriptResult(request),
            };
        }

        private static ScopeBindingUpsertResult BuildWorkflowResult(ScopeBindingUpsertRequest request)
        {
            var revisionId = request.RevisionId ?? "rev-1";
            return new ScopeBindingUpsertResult(
                ScopeId: request.ScopeId,
                ServiceId: request.ServiceId ?? string.Empty,
                DisplayName: request.DisplayName ?? string.Empty,
                RevisionId: revisionId,
                ImplementationKind: request.ImplementationKind,
                ExpectedActorId: "scope-workflow:scope-1:workflow-main",
                WorkflowName: "workflow-main",
                DefinitionActorIdPrefix: "scope-workflow:scope-1:workflow-main",
                Workflow: new ScopeBindingWorkflowResult(
                    "workflow-main",
                    "scope-workflow:scope-1:workflow-main"));
        }

        private static ScopeBindingUpsertResult BuildScriptResult(ScopeBindingUpsertRequest request)
        {
            var revisionId = request.RevisionId ?? "rev-1";
            return new ScopeBindingUpsertResult(
                ScopeId: request.ScopeId,
                ServiceId: request.ServiceId ?? string.Empty,
                DisplayName: request.DisplayName ?? string.Empty,
                RevisionId: revisionId,
                ImplementationKind: request.ImplementationKind,
                ExpectedActorId: "scope-script:scope-1:script-1",
                Script: new ScopeBindingScriptResult("script-1", revisionId, "scope-script:scope-1:script-1"));
        }

        private static ScopeBindingUpsertResult BuildGAgentResult(ScopeBindingUpsertRequest request)
        {
            var revisionId = request.RevisionId ?? "rev-1";
            return new ScopeBindingUpsertResult(
                ScopeId: request.ScopeId,
                ServiceId: request.ServiceId ?? string.Empty,
                DisplayName: request.DisplayName ?? string.Empty,
                RevisionId: revisionId,
                ImplementationKind: request.ImplementationKind,
                ExpectedActorId: "scope-gagent:scope-1:joker",
                GAgent: new ScopeBindingGAgentResult(request.GAgent?.ActorTypeName ?? string.Empty));
        }
    }
}
