using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Tests;

public sealed class ScopeBindingStudioMemberPlatformBindingCommandServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldOnlyAcceptWithoutRunningPlatformBinding()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        var accepted = await service.StartAsync(
            "studio-member-binding-run:bind-1",
            NewScriptStartRequest());

        accepted.BindingRunId.Should().Be("bind-1");
        accepted.PlatformBindingCommandId.Should().Be("platform-bind-1");
        scopeBindingPort.Requests.Should().BeEmpty();
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_WhenCommandIdMissing_ShouldUseSharedFallbackConvention()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);
        var request = NewScriptStartRequest();
        request.PlatformBindingCommandId = "";

        var accepted = await service.StartAsync(
            "studio-member-binding-run:bind-1",
            request);

        accepted.PlatformBindingCommandId.Should().Be("platform-bind-1-1");
        scopeBindingPort.Requests.Should().BeEmpty();
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRunPlatformBindingAndDispatchSucceededContinuation()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var readinessPort = RecordingReadinessQueryPort.Ready();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);

        var accepted = await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest());

        accepted.Should().Be(new StudioMemberPlatformBindingExecutionAccepted("bind-1", "platform-bind-1"));
        var dispatch = await dispatchPort.NextDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatch.ActorId.Should().Be("studio-member-binding-run:bind-1");
        var succeeded = dispatch.Envelope.Payload.Unpack<StudioMemberPlatformBindingSucceeded>();
        succeeded.BindingRunId.Should().Be("bind-1");
        succeeded.PlatformBindingCommandId.Should().Be("platform-bind-1");
        succeeded.Result.PublishedServiceId.Should().Be("member-m-1");
        succeeded.Result.RevisionId.Should().Be("rev-platform-bind-1");
        succeeded.Result.ImplementationKind.Should().Be(StudioMemberImplementationKind.Script);
        succeeded.Result.ImplementationRef.Script.ScriptId.Should().Be("script-1");

        var request = scopeBindingPort.Requests.Should().ContainSingle().Subject;
        request.ScopeId.Should().Be("scope-1");
        request.ServiceId.Should().Be("member-m-1");
        request.DisplayName.Should().Be("Script member");
        request.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Scripting);
        request.Script!.ScriptId.Should().Be("script-1");
        request.Script!.ScriptRevision.Should().Be("draft-1");
        request.RevisionId.Should().Be("rev-platform-bind-1");
        request.AllowExistingRevisionReplay.Should().BeTrue();
        request.ReplayRevisionId.Should().Be("rev-platform-bind-1");
        var readinessRequest = readinessPort.Requests.Should().ContainSingle().Subject;
        readinessRequest.Should().BeEquivalentTo(new ScopeBindingReadinessRequest(
            ScopeId: "scope-1",
            ServiceId: "member-m-1",
            ExpectedRevisionId: "rev-platform-bind-1",
            ExpectedDeploymentId: "deployment-1",
            ExpectedEndpointIds: ["script.command"]));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldBuildWorkflowBindingRequestAndDispatchWorkflowResult()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewWorkflowStartRequest());

        var dispatch = await dispatchPort.NextDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var succeeded = dispatch.Envelope.Payload.Unpack<StudioMemberPlatformBindingSucceeded>();
        succeeded.Result.ImplementationKind.Should().Be(StudioMemberImplementationKind.Workflow);
        succeeded.Result.ImplementationRef.Workflow.WorkflowId.Should().Be("workflow-stable-id");
        succeeded.Result.ImplementationRef.Workflow.WorkflowRevision.Should().Be("rev-platform-bind-1");

        var request = scopeBindingPort.Requests.Should().ContainSingle().Subject;
        request.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Workflow);
        request.Workflow!.WorkflowId.Should().Be("workflow-stable-id");
        request.Workflow!.WorkflowYamls.Should().ContainSingle().Which.Should().Contain("name: workflow-main");
        request.CapabilityAdmission.Should().NotBeNull();
        request.CapabilityAdmission!.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        request.CapabilityAdmission.ExistingPlan.Should().NotBeNull();
        request.CapabilityAdmission.ExistingPlan!.AdmissionDigest.Should().Be(
            NewWorkflowStartRequest().Request.Workflow.CapabilityAdmissionPlan.AdmissionDigest);
        request.AllowExistingRevisionReplay.Should().BeTrue();
        request.ReplayRevisionId.Should().Be("rev-platform-bind-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPreserveDurableOwnerWithoutReconstructingCallerCredentials()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);
        var startRequest = NewDurableWorkflowStartRequest();
        var submittedPlan = startRequest.Request.Workflow.CapabilityAdmissionPlan.Clone();

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            startRequest);

        await dispatchPort.NextDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var upsert = scopeBindingPort.Requests.Should().ContainSingle().Subject;
        upsert.ServiceId.Should().Be("svc-gamma");
        upsert.CapabilityAdmission.Should().NotBeNull();
        var admission = upsert.CapabilityAdmission!;
        admission.CallerId.Should().BeEmpty();
        admission.NyxIdCallerCredential.Should().BeNull();
        admission.NyxIdOrganizationBearerToken.Should().BeNull();
        admission.ExistingPlan.Should().NotBeNull();
        admission.ExistingPlan.Should().NotBeSameAs(submittedPlan);
        admission.ExistingPlan.Should().BeEquivalentTo(submittedPlan);
        admission.ExistingPlan!.DurableAuthorizationOwner.Should().BeEquivalentTo(
            new ExternalCapabilityAuthorizationOwner
            {
                Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
                OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            });
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkflowResultHasNoWorkflowId_ShouldDispatchFailedContinuation()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            OmitWorkflowId = true,
        };
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewWorkflowStartRequest());

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingFailed>();
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_FAILED");
        failed.Failure.Message.Should().Be("scope binding workflow result workflow id is required for workflow member binding.");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldBuildGAgentBindingRequestAndDispatchGAgentResult()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var readinessPort = RecordingReadinessQueryPort.Ready();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewGAgentStartRequest());

        var dispatch = await dispatchPort.NextDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var succeeded = dispatch.Envelope.Payload.Unpack<StudioMemberPlatformBindingSucceeded>();
        succeeded.Result.ImplementationKind.Should().Be(StudioMemberImplementationKind.Gagent);
        succeeded.Result.ImplementationRef.Gagent.ActorTypeName.Should().Be("Tests.JokerGAgent");

        var request = scopeBindingPort.Requests.Should().ContainSingle().Subject;
        request.ImplementationKind.Should().Be(ScopeBindingImplementationKind.GAgent);
        request.GAgent!.AgentKind.Should().Be("tests.joker");
        request.GAgent.Endpoints.Should().ContainSingle().Which.Kind.Should().Be(ServiceEndpointKind.Chat);
        request.GAgent.Endpoints[0].EndpointId.Should().Be("chat");
        readinessPort.Requests.Should().ContainSingle().Which.ExpectedEndpointIds.Should().BeEquivalentTo(["chat"]);
    }

    [Fact]
    public async Task ExecuteAsync_WhenGAgentEndpointIsCommand_ShouldMapEndpointKind()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var readinessPort = RecordingReadinessQueryPort.Ready();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);
        var request = NewGAgentStartRequest();
        request.Request.Gagent.Endpoints[0].Kind = StudioMemberGAgentEndpointKind.Command;

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            request);

        var dispatch = await dispatchPort.NextDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatch.Envelope.Payload.Unpack<StudioMemberPlatformBindingSucceeded>()
            .Result.ImplementationKind.Should().Be(StudioMemberImplementationKind.Gagent);
        scopeBindingPort.Requests.Should().ContainSingle();
        scopeBindingPort.Requests[0].GAgent!.Endpoints.Should().ContainSingle().Which.Kind.Should().Be(ServiceEndpointKind.Command);
        readinessPort.Requests.Should().ContainSingle().Which.ExpectedEndpointIds.Should().BeEquivalentTo(["chat"]);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCommandIdContainsSeparators_ShouldNormalizeRevisionAndReplayId()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "Platform Bind: 2!!",
            NewScriptStartRequest());

        var dispatch = await dispatchPort.NextDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var succeeded = dispatch.Envelope.Payload.Unpack<StudioMemberPlatformBindingSucceeded>();
        succeeded.Result.RevisionId.Should().Be("rev-platform-bind-2");
        scopeBindingPort.Requests.Should().ContainSingle();
        scopeBindingPort.Requests[0].RevisionId.Should().Be("rev-platform-bind-2");
        scopeBindingPort.Requests[0].ReplayRevisionId.Should().Be("rev-platform-bind-2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenScriptRevisionAbsent_ShouldPassNullScriptRevision()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);
        var request = NewScriptStartRequest();
        request.Request.Script.ClearScriptRevision();

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            request);

        await dispatchPort.NextDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scopeBindingPort.Requests.Should().ContainSingle();
        scopeBindingPort.Requests[0].Script!.ScriptRevision.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStartPlatformBindingAndReturnBeforeCompletion()
    {
        var releaseUpsert = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            ReleaseUpsert = releaseUpsert,
        };
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        var executeTask = service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest());

        var request = await scopeBindingPort.UpsertStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.RevisionId.Should().Be("rev-platform-bind-1");
        executeTask.IsCompletedSuccessfully.Should().BeTrue();
        var accepted = await executeTask;
        accepted.PlatformBindingCommandId.Should().Be("platform-bind-1");
        dispatchPort.Dispatches.Should().BeEmpty();

        releaseUpsert.SetResult(null);
        var dispatch = await dispatchPort.NextDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatch.Envelope.Payload.Unpack<StudioMemberPlatformBindingSucceeded>()
            .Result.RevisionId.Should().Be("rev-platform-bind-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHonorExplicitRevisionId()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest("rev-explicit"));

        await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingSucceeded>();
        var request = scopeBindingPort.Requests.Should().ContainSingle().Subject;
        request.RevisionId.Should().Be("rev-explicit");
        request.ReplayRevisionId.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenExplicitWorkflowRevisionId_ShouldAllowValidatedReplay()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);
        var request = NewWorkflowStartRequest();
        request.Request.RevisionId = "rev-explicit";

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            request);

        await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingSucceeded>();
        var upsert = scopeBindingPort.Requests.Should().ContainSingle().Subject;
        upsert.RevisionId.Should().Be("rev-explicit");
        upsert.AllowExistingRevisionReplay.Should().BeTrue();
        upsert.ReplayRevisionId.Should().Be("rev-explicit");
    }

    [Fact]
    public async Task ExecuteAsync_WhenExplicitGAgentRevisionId_ShouldAllowValidatedReplay()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);
        var request = NewGAgentStartRequest();
        request.Request.RevisionId = "rev-explicit";

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            request);

        await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingSucceeded>();
        var upsert = scopeBindingPort.Requests.Should().ContainSingle().Subject;
        upsert.RevisionId.Should().Be("rev-explicit");
        upsert.AllowExistingRevisionReplay.Should().BeTrue();
        upsert.ReplayRevisionId.Should().Be("rev-explicit");
    }

    [Fact]
    public async Task ExecuteAsync_WhenScopeBindingFails_ShouldDispatchFailedContinuation()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            Failure = new InvalidOperationException("platform rejected"),
        };
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest());

        var dispatch = await dispatchPort.NextDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failed = dispatch.Envelope.Payload.Unpack<StudioMemberPlatformBindingFailed>();
        failed.BindingRunId.Should().Be("bind-1");
        failed.PlatformBindingCommandId.Should().Be("platform-bind-1");
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_FAILED");
        failed.Failure.Message.Should().Be("platform rejected");
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadinessBecomesReady_ShouldWaitBeforeDispatchingSucceededContinuation()
    {
        var readinessPort = new RecordingReadinessQueryPort([
            NotReadySnapshot(),
            ReadySnapshot(),
        ]);
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var now = DateTimeOffset.Parse("2026-05-19T00:00:00+00:00");
        var service = CreateService(
            scopeBindingPort,
            dispatchPort,
            readinessPort,
            delayAsync: (_, _) => Task.CompletedTask,
            utcNow: () => now);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest());

        var succeeded = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingSucceeded>();
        succeeded.Result.PublishedServiceId.Should().Be("member-m-1");
        readinessPort.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadinessTimesOut_ShouldLeaveBindingRunPendingForWatchdogRecovery()
    {
        var readinessPort = new RecordingReadinessQueryPort([NotReadySnapshot()]);
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var now = DateTimeOffset.Parse("2026-05-19T00:00:00+00:00");
        var service = CreateService(
            scopeBindingPort,
            dispatchPort,
            readinessPort,
            delayAsync: (_, _) =>
            {
                now = now.AddSeconds(6);
                return Task.CompletedTask;
            },
            utcNow: () => now);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest());

        await readinessPort.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        readinessPort.Requests.Should().HaveCount(2);
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadinessQueryFails_ShouldDispatchReadinessFailedContinuation()
    {
        var readinessPort = new RecordingReadinessQueryPort([ReadySnapshot()])
        {
            Failure = new InvalidOperationException("readiness unavailable"),
        };
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest());

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingFailed>();
        failed.BindingRunId.Should().Be("bind-1");
        failed.PlatformBindingCommandId.Should().Be("platform-bind-1");
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_READINESS_FAILED");
        failed.Failure.Message.Should().Be("readiness unavailable");
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadinessQueryFailsAndFailureContinuationDispatchFails_ShouldNotThrow()
    {
        var readinessPort = new RecordingReadinessQueryPort([ReadySnapshot()])
        {
            Failure = new InvalidOperationException("readiness unavailable"),
        };
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort
        {
            Failure = new InvalidOperationException("dispatch unavailable"),
        };
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest());

        await dispatchPort.DispatchAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scopeBindingPort.Requests.Should().ContainSingle();
        readinessPort.Requests.Should().ContainSingle();
        dispatchPort.DispatchAttempts.Should().Be(1);
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenBindingResultDeploymentIdMissing_ShouldDispatchReadinessFailedContinuation()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            OmitExpectedDeploymentId = true,
        };
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest());

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingFailed>();
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_READINESS_FAILED");
        failed.Failure.Message.Should().Be("scope binding result deployment id is required for readiness observation.");
    }

    [Fact]
    public async Task ExecuteAsync_WhenImplementationPayloadMissing_ShouldDispatchFailedContinuation()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);
        var request = NewScriptStartRequest();
        request.Request.Script = null;

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            request);

        var dispatch = await dispatchPort.NextDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failed = dispatch.Envelope.Payload.Unpack<StudioMemberPlatformBindingFailed>();
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_FAILED");
        failed.Failure.Message.Should().Contain("binding request must carry exactly one implementation payload");
        scopeBindingPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenGAgentEndpointKindUnsupported_ShouldDispatchFailedContinuation()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);
        var request = NewGAgentStartRequest();
        request.Request.Gagent.Endpoints[0].Kind = StudioMemberGAgentEndpointKind.Unspecified;

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            request);

        var dispatch = await dispatchPort.NextDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failed = dispatch.Envelope.Payload.Unpack<StudioMemberPlatformBindingFailed>();
        failed.Failure.Message.Should().Contain("Unsupported gagent endpoint kind");
        scopeBindingPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenSuccessContinuationDispatchFails_ShouldNotDispatchFailedContinuation()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort
        {
            Failure = new InvalidOperationException("dispatch unavailable"),
        };
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest());

        await dispatchPort.DispatchAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scopeBindingPort.Requests.Should().ContainSingle();
        dispatchPort.DispatchAttempts.Should().Be(1);
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenPlatformFailsAndFailureContinuationDispatchFails_ShouldNotRetryAsDifferentOutcome()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            Failure = new InvalidOperationException("platform rejected"),
        };
        var dispatchPort = new RecordingDispatchPort
        {
            Failure = new InvalidOperationException("dispatch unavailable"),
        };
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            "platform-bind-1",
            NewScriptStartRequest());

        await dispatchPort.DispatchAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scopeBindingPort.Requests.Should().ContainSingle();
        dispatchPort.DispatchAttempts.Should().Be(1);
        dispatchPort.Dispatches.Should().BeEmpty();
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
            WorkflowId = "workflow-stable-id",
            WorkflowYamls = { "name: workflow-main\nsteps: []\n" },
            CapabilityAdmissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
                "name: workflow-main\nsteps: []",
                new Dictionary<string, string>(),
                ExternalCapabilityExecutionMode.Interactive,
                [],
                []),
        };
        return request;
    }

    private static StudioMemberPlatformBindingStartRequested NewDurableWorkflowStartRequest()
    {
        const string workflowYaml = "name: workflow-main\nsteps: []\n";
        var request = NewWorkflowStartRequest();
        request.Request.MemberId = "m-beta";
        request.Admitted.MemberId = "m-beta";
        request.Admitted.PublishedServiceId = "svc-gamma";
        var capability = new ExternalWorkflowCapabilityRef
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "us-gamma",
                ServiceSlugSnapshot = "service-gamma",
                EndpointId = "invoke-gamma",
                HttpMethod = "GET",
                PathTemplate = "/invoke",
                ContractDigest = "operation-gamma-digest",
                ExecutionPolicy = new NyxIdOperationExecutionPolicy
                {
                    Risk = NyxIdOperationRisk.ReadOnly,
                    Approval = NyxIdOperationApproval.None,
                    EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                    AllowedExecutionModes =
                    {
                        ExternalCapabilityExecutionMode.Interactive,
                        ExternalCapabilityExecutionMode.Durable,
                    },
                },
            },
        };
        var owner = new ExternalCapabilityAuthorizationOwner
        {
            Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
            OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            OwnerSubject = "caller-alpha",
        };
        var observedAt = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        request.Request.Workflow.CapabilityAdmissionPlan =
            WorkflowCapabilityAdmissionPlanIntegrity.Create(
                workflowYaml,
                new Dictionary<string, string>(),
                ExternalCapabilityExecutionMode.Durable,
                [new WorkflowCapabilityInvocationAdmission
                {
                    CallSiteId = "workflow-main/invoke-gamma",
                    Capability = capability,
                }],
                [
                    Source(
                        ExternalCapabilitySourceKind.NyxIdMcpConfig,
                        "nyxid-mcp-config:caller:nyx-user-gamma",
                        observedAt,
                        "mcp-config-gamma-digest"),
                    Source(
                        ExternalCapabilitySourceKind.DurableAuthorizationCatalog,
                        NyxIdAuthorizationCatalogActorIds.Build(new AuthorizationOwnerIdentity
                        {
                            Authority = NyxIdAuthorizationAuthorities.NyxId,
                            OwnerKind = AuthorizationOwnerKind.Personal,
                            OwnerSubject = "caller-alpha",
                        }),
                        observedAt,
                        "catalog-gamma-digest",
                        sourceVersion: 17),
                ],
                owner);
        return request;
    }

    private static ExternalCapabilitySourceStamp Source(
        ExternalCapabilitySourceKind sourceKind,
        string sourceId,
        DateTimeOffset observedAt,
        string contentDigest,
        long sourceVersion = 0) =>
        new()
        {
            SourceKind = sourceKind,
            SourceId = sourceId,
            SourceVersion = sourceVersion,
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
            FreshUntil = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(5)),
            ContentDigest = contentDigest,
        };

    private static StudioMemberPlatformBindingStartRequested NewGAgentStartRequest()
    {
        var request = NewScriptStartRequest();
        request.Admitted.ImplementationKind = StudioMemberImplementationKind.Gagent;
        request.Admitted.DisplayName = "GAgent member";
        request.Request.Script = null;
        request.Request.Gagent = new StudioMemberGAgentBindingRequest
        {
            AgentKind = "tests.joker",
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

    private static ScopeBindingStudioMemberPlatformBindingCommandService CreateService(
        RecordingScopeBindingCommandPort scopeBindingPort,
        RecordingDispatchPort dispatchPort,
        RecordingReadinessQueryPort? readinessPort = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<DateTimeOffset>? utcNow = null,
        StudioMemberPlatformBindingOptions? options = null) =>
        new(
            scopeBindingPort,
            readinessPort ?? RecordingReadinessQueryPort.Ready(),
            dispatchPort,
            NullLogger<ScopeBindingStudioMemberPlatformBindingCommandService>.Instance,
            Options.Create(options ?? new StudioMemberPlatformBindingOptions()),
            delayAsync ?? ((_, _) => Task.CompletedTask),
            utcNow ?? (() => DateTimeOffset.UtcNow));

    private static ScopeBindingReadinessSnapshot ReadySnapshot() =>
        new(
            ScopeId: "scope-1",
            ServiceId: "member-m-1",
            Status: ScopeBindingReadinessStatus.Ready,
            ServiceCatalogVisible: true,
            ServingSetVisible: true,
            EligibleServingTargetVisible: true,
            InvokeReady: true,
            RevisionId: "rev-platform-bind-1",
            DeploymentId: "deployment-1");

    private static ScopeBindingReadinessSnapshot NotReadySnapshot() =>
        new(
            ScopeId: "scope-1",
            ServiceId: "member-m-1",
            Status: ScopeBindingReadinessStatus.ServingSetMissing,
            ServiceCatalogVisible: true,
            ServingSetVisible: false,
            EligibleServingTargetVisible: false,
            InvokeReady: false);

    private sealed class RecordingScopeBindingCommandPort : IScopeBindingCommandPort
    {
        public List<ScopeBindingUpsertRequest> Requests { get; } = [];
        public Exception? Failure { get; init; }
        public bool OmitExpectedDeploymentId { get; init; }
        public bool OmitWorkflowId { get; init; }
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

            var result = request.ImplementationKind switch
            {
                ScopeBindingImplementationKind.Workflow => BuildWorkflowResult(request, OmitWorkflowId),
                ScopeBindingImplementationKind.GAgent => BuildGAgentResult(request),
                _ => BuildScriptResult(request),
            };

            return OmitExpectedDeploymentId ? result with { ExpectedDeploymentId = "" } : result;
        }

        private static ScopeBindingUpsertResult BuildWorkflowResult(
            ScopeBindingUpsertRequest request,
            bool omitWorkflowId)
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
                    omitWorkflowId ? string.Empty : request.Workflow?.WorkflowId ?? string.Empty,
                    "workflow-main",
                    "scope-workflow:scope-1:workflow-main"),
                ExpectedDeploymentId: "deployment-1");
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
                Script: new ScopeBindingScriptResult("script-1", revisionId, "scope-script:scope-1:script-1")
                {
                    EndpointIds = ["script.command"],
                },
                ExpectedDeploymentId: "deployment-1");
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
                GAgent: new ScopeBindingGAgentResult("Tests.JokerGAgent"),
                ExpectedDeploymentId: "deployment-1");
        }
    }

    private sealed class RecordingReadinessQueryPort : IScopeBindingReadinessQueryPort
    {
        private readonly Queue<ScopeBindingReadinessSnapshot> _snapshots;

        public RecordingReadinessQueryPort(IEnumerable<ScopeBindingReadinessSnapshot> snapshots)
        {
            _snapshots = new Queue<ScopeBindingReadinessSnapshot>(snapshots);
            if (_snapshots.Count == 0)
                throw new ArgumentException("At least one readiness snapshot is required.", nameof(snapshots));
        }

        public List<ScopeBindingReadinessRequest> Requests { get; } = [];
        public TaskCompletionSource<object?> Observed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? Failure { get; init; }

        public static RecordingReadinessQueryPort Ready() => new([ReadySnapshot()]);

        public Task<ScopeBindingReadinessSnapshot> GetReadinessAsync(
            ScopeBindingReadinessRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            Observed.TrySetResult(null);
            if (Failure != null)
                throw Failure;
            if (_snapshots.Count <= 1)
                return Task.FromResult(_snapshots.Peek());

            return Task.FromResult(_snapshots.Dequeue());
        }
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<DispatchedCommand> Dispatches { get; } = [];
        public Exception? Failure { get; init; }
        public int DispatchAttempts { get; private set; }
        public TaskCompletionSource<DispatchedCommand> NextDispatch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object?> DispatchAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            DispatchAttempts++;
            DispatchAttempted.TrySetResult(null);
            if (Failure != null)
                throw Failure;

            var dispatch = new DispatchedCommand(actorId, envelope);
            Dispatches.Add(dispatch);
            NextDispatch.TrySetResult(dispatch);
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public async Task<TPayload> WaitForPayloadAsync<TPayload>()
            where TPayload : class, IMessage<TPayload>, new()
        {
            var dispatch = Dispatches.Count == 0
                ? await NextDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5))
                : Dispatches[^1];

            var payload = new TPayload();
            dispatch.Envelope.Payload.Is(payload.Descriptor).Should().BeTrue();
            return dispatch.Envelope.Payload.Unpack<TPayload>();
        }

        public sealed record DispatchedCommand(string ActorId, EventEnvelope Envelope);
    }
}
