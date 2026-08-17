using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
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
    public async Task StartAsync_ShouldOnlyReturnFencedAcceptance()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);
        var request = NewScriptStartRequest();

        var accepted = await service.StartAsync("studio-member-binding-run:bind-1", request);

        accepted.BindingRunId.Should().Be("bind-1");
        accepted.PlatformBindingCommandId.Should().Be("platform-bind-1");
        accepted.ProtocolVersion.Should().Be(StudioMemberConventions.PlatformBindingProtocolVersion);
        accepted.ExecutionAttempt.Should().Be(0);
        scopeBindingPort.Requests.Should().BeEmpty();
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_WhenCommandIdMissing_ShouldUseSharedFallbackConvention()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var service = CreateService(scopeBindingPort, new RecordingDispatchPort());
        var request = NewScriptStartRequest();
        request.PlatformBindingCommandId = string.Empty;

        var accepted = await service.StartAsync("studio-member-binding-run:bind-1", request);

        accepted.PlatformBindingCommandId.Should().Be("platform-bind-1-1");
        scopeBindingPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_CommandStage_ShouldDispatchCheckpointWithoutObservingReadiness()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            ScriptEndpointIds = [" script.query ", "script.command", "script.command", " "],
        };
        var readinessPort = RecordingReadinessQueryPort.Ready();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);
        var request = NewCommandExecutionRequest(NewScriptStartRequest());

        var accepted = await service.ExecuteAsync("studio-member-binding-run:bind-1", request);

        accepted.Should().Be(new StudioMemberPlatformBindingExecutionAccepted(
            "bind-1",
            "platform-bind-1",
            StudioMemberConventions.PlatformBindingProtocolVersion,
            1));
        var completed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingCommandsCompleted>();
        completed.ProtocolVersion.Should().Be(StudioMemberConventions.PlatformBindingProtocolVersion);
        completed.ExecutionAttempt.Should().Be(1);
        completed.RecoverySnapshot.Result.PublishedServiceId.Should().Be("member-m-1");
        completed.RecoverySnapshot.Result.RevisionId.Should().Be("rev-platform-bind-1");
        completed.RecoverySnapshot.Result.ImplementationRef.Script.ScriptId.Should().Be("script-1");
        completed.RecoverySnapshot.Result.ImplementationRef.Script.EndpointIds.Should()
            .Equal("script.command", "script.query");
        completed.RecoverySnapshot.ExpectedEndpointIds.Should()
            .Equal("script.command", "script.query");
        completed.RecoverySnapshot.ActivationAttemptId.Should().Be("platform-bind-1:a1");
        scopeBindingPort.Requests.Should().ContainSingle().Which.ActivationAttemptId.Should()
            .Be("platform-bind-1:a1");
        readinessPort.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_WhenScriptCommandResultHasNoEndpoints_ShouldFailBeforeCheckpoint(
        bool nullEndpointIds)
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            ScriptEndpointIds = nullEndpointIds ? null : [],
        };
        var readinessPort = RecordingReadinessQueryPort.Ready();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewCommandExecutionRequest(NewScriptStartRequest()));

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionFailed>();
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_FAILED");
        failed.Failure.Message.Should().Contain("at least one command endpoint");
        scopeBindingPort.Requests.Should().ContainSingle();
        readinessPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WorkflowCommand_ShouldBuildCanonicalCheckpoint()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var readinessPort = RecordingReadinessQueryPort.Ready();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewCommandExecutionRequest(NewWorkflowStartRequest()));

        var completed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingCommandsCompleted>();
        var result = completed.RecoverySnapshot.Result;
        result.ImplementationKind.Should().Be(StudioMemberImplementationKind.Workflow);
        result.ImplementationRef.Workflow.WorkflowId.Should().Be("workflow-stable-id");
        result.ImplementationRef.Workflow.WorkflowRevision.Should().Be("rev-platform-bind-1");
        result.ImplementationRef.Workflow.DefinitionActorIdPrefix.Should()
            .Be("scope-workflow:scope-1:workflow-main");
        completed.RecoverySnapshot.ExpectedEndpointIds.Should().Equal("chat");

        var upsert = scopeBindingPort.Requests.Should().ContainSingle().Subject;
        upsert.Workflow!.WorkflowId.Should().Be("workflow-stable-id");
        upsert.CapabilityAdmission.Should().NotBeNull();
        upsert.CapabilityAdmission!.ExistingPlan.Should().NotBeNull();
        readinessPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_GAgentCommand_ShouldSealActorTypeAgentKindAndEndpoints()
    {
        var start = NewGAgentStartRequest();
        start.Request.Gagent.Endpoints[0].EndpointId = "CHAT";
        start.Request.Gagent.Endpoints.Add(new StudioMemberGAgentEndpointBindingRequest
        {
            EndpointId = "command.run",
            DisplayName = "Run",
            Kind = StudioMemberGAgentEndpointKind.Command,
            RequestTypeUrl = "type.googleapis.com/a.RunRequest",
            ResponseTypeUrl = "type.googleapis.com/a.RunResponse",
        });
        start.Request.Gagent.Endpoints.Add(start.Request.Gagent.Endpoints[1].Clone());
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var readinessPort = RecordingReadinessQueryPort.Ready();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewCommandExecutionRequest(start));

        var completed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingCommandsCompleted>();
        completed.RecoverySnapshot.Result.ImplementationRef.Gagent.ActorTypeName.Should().Be("Tests.JokerGAgent");
        completed.RecoverySnapshot.Result.ImplementationRef.Gagent.AgentKind.Should().Be("tests.joker");
        completed.RecoverySnapshot.ExpectedEndpointIds.Should().Equal("CHAT", "command.run");
        scopeBindingPort.Requests.Should().ContainSingle()
            .Which.GAgent!.Endpoints.Should().HaveCount(3);
        scopeBindingPort.Requests[0].GAgent!.Endpoints.Count(endpoint =>
            endpoint.EndpointId == "command.run" && endpoint.Kind == ServiceEndpointKind.Command).Should().Be(2);
        readinessPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WorkflowCommand_ShouldPreserveDurableOwnerWithoutCallerCredentials()
    {
        const string workflowYaml = "name: workflow-main\nsteps: []\n";
        var start = NewWorkflowStartRequest();
        start.Request.Workflow.CapabilityAdmissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            [],
            [],
            new ExternalCapabilityAuthorizationOwner
            {
                Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
                OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            });
        var submittedPlan = start.Request.Workflow.CapabilityAdmissionPlan.Clone();
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewCommandExecutionRequest(start));

        await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingCommandsCompleted>();
        var admission = scopeBindingPort.Requests.Should().ContainSingle().Subject.CapabilityAdmission;
        admission.Should().NotBeNull();
        admission!.CallerId.Should().BeEmpty();
        admission.NyxIdCallerCredential.Should().BeNull();
        admission.NyxIdOrganizationBearerToken.Should().BeNull();
        admission.ExistingPlan.Should().NotBeSameAs(submittedPlan);
        admission.ExistingPlan.Should().BeEquivalentTo(submittedPlan);
    }

    [Theory]
    [InlineData("script")]
    [InlineData("workflow")]
    [InlineData("gagent")]
    public async Task ExecuteAsync_CommandStage_ShouldHonorExplicitRevision(string implementation)
    {
        var start = implementation switch
        {
            "workflow" => NewWorkflowStartRequest(),
            "gagent" => NewGAgentStartRequest(),
            _ => NewScriptStartRequest(),
        };
        start.Request.RevisionId = "revision-explicit";
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewCommandExecutionRequest(start));

        var completed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingCommandsCompleted>();
        completed.RecoverySnapshot.Result.RevisionId.Should().Be("revision-explicit");
        var upsert = scopeBindingPort.Requests.Should().ContainSingle().Subject;
        upsert.RevisionId.Should().Be("revision-explicit");
        upsert.AllowExistingRevisionReplay.Should().BeTrue();
        upsert.ReplayRevisionId.Should().Be(implementation == "script" ? null : "revision-explicit");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCommandIdContainsSeparators_ShouldNormalizeRevisionAndReplayId()
    {
        var request = NewCommandExecutionRequest(NewScriptStartRequest());
        request.PlatformBindingCommandId = "Platform Bind: 2!!";
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync("studio-member-binding-run:bind-1", request);

        var completed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingCommandsCompleted>();
        completed.RecoverySnapshot.Result.RevisionId.Should().Be("rev-platform-bind-2");
        scopeBindingPort.Requests.Should().ContainSingle().Which.ReplayRevisionId.Should().Be("rev-platform-bind-2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenScriptRevisionAbsent_ShouldPassNullRevision()
    {
        var start = NewScriptStartRequest();
        start.Request.Script.ClearScriptRevision();
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewCommandExecutionRequest(start));

        await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingCommandsCompleted>();
        scopeBindingPort.Requests.Should().ContainSingle().Which.Script!.ScriptRevision.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenGAgentEndpointKindUnsupported_ShouldFailBeforeUpsert()
    {
        var start = NewGAgentStartRequest();
        start.Request.Gagent.Endpoints[0].Kind = StudioMemberGAgentEndpointKind.Unspecified;
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewCommandExecutionRequest(start));

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionFailed>();
        failed.Failure.Message.Should().Contain("Unsupported gagent endpoint kind");
        scopeBindingPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkflowResultHasNoWorkflowId_ShouldDispatchFailure()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            OmitWorkflowId = true,
        };
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewCommandExecutionRequest(NewWorkflowStartRequest()));

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionFailed>();
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_FAILED");
        failed.Failure.Message.Should().Contain("workflow id is required");
    }

    [Fact]
    public async Task ExecuteAsync_CommandStage_ShouldReturnBeforeUpsertCompletes()
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
            NewCommandExecutionRequest(NewScriptStartRequest()));

        await scopeBindingPort.UpsertStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        executeTask.IsCompletedSuccessfully.Should().BeTrue();
        dispatchPort.Dispatches.Should().BeEmpty();

        releaseUpsert.SetResult(null);
        await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingCommandsCompleted>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenPlatformCommandFails_ShouldDispatchFencedFailure()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            Failure = new InvalidOperationException("platform rejected"),
        };
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewCommandExecutionRequest(NewScriptStartRequest()));

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionFailed>();
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_FAILED");
        failed.ExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.CommandInFlight);
        failed.ExecutionAttempt.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPlatformResultDeploymentIdMissing_ShouldCheckpointThenFailReadinessValidation()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            OmitExpectedDeploymentId = true,
        };
        var commandDispatch = new RecordingDispatchPort();
        var commandService = CreateService(scopeBindingPort, commandDispatch);

        await commandService.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewCommandExecutionRequest(NewScriptStartRequest()));

        var completed = await commandDispatch.WaitForPayloadAsync<StudioMemberPlatformBindingCommandsCompleted>();
        completed.RecoverySnapshot.ExpectedDeploymentId.Should().BeEmpty();

        var readinessPort = RecordingReadinessQueryPort.Ready();
        var readinessDispatch = new RecordingDispatchPort();
        var readinessService = CreateService(scopeBindingPort, readinessDispatch, readinessPort);
        await readinessService.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewReadinessExecutionRequest(NewScriptStartRequest(), completed.RecoverySnapshot));

        var failed = await readinessDispatch.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionFailed>();
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_RECOVERY_SNAPSHOT_INVALID");
        scopeBindingPort.Requests.Should().ContainSingle();
        readinessPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ReadinessStage_ShouldOnlyObserveCommittedSnapshot()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var readinessPort = RecordingReadinessQueryPort.Ready();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewReadinessExecutionRequest(NewScriptStartRequest(), NewScriptRecoverySnapshot()));

        var succeeded = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionSucceeded>();
        succeeded.Result.PublishedServiceId.Should().Be("member-m-1");
        succeeded.ProtocolVersion.Should().Be(StudioMemberConventions.PlatformBindingProtocolVersion);
        succeeded.ExecutionAttempt.Should().Be(2);
        scopeBindingPort.Requests.Should().BeEmpty();
        readinessPort.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ScopeBindingReadinessRequest(
                ScopeId: "scope-1",
                ServiceId: "member-m-1",
                ExpectedRevisionId: "rev-platform-bind-1",
                ExpectedDeploymentId: "deployment-1",
                ExpectedEndpointIds: ["script.command"],
                ExpectedActivationAttemptId: "platform-bind-1:a1"));
    }

    [Fact]
    public async Task ExecuteAsync_ReadinessStage_ShouldPollUntilReady()
    {
        var readinessPort = new RecordingReadinessQueryPort([NotReadySnapshot(), ReadySnapshot()]);
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(
            new RecordingScopeBindingCommandPort(),
            dispatchPort,
            readinessPort,
            delayAsync: (_, _) => Task.CompletedTask,
            utcNow: () => DateTimeOffset.Parse("2026-08-14T00:00:00+00:00"));

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewReadinessExecutionRequest(NewScriptStartRequest(), NewScriptRecoverySnapshot()));

        await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionSucceeded>();
        readinessPort.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadinessTimesOut_ShouldKeepCheckpointOnActorSide()
    {
        var now = DateTimeOffset.Parse("2026-08-14T00:00:00+00:00");
        var readinessPort = new RecordingReadinessQueryPort([NotReadySnapshot()]);
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
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
            NewReadinessExecutionRequest(NewScriptStartRequest(), NewScriptRecoverySnapshot()));

        var timedOut = await dispatchPort
            .WaitForPayloadAsync<StudioMemberPlatformBindingReadinessObservationTimedOut>();
        timedOut.ReadinessStatus.Should().Be(StudioMemberPlatformBindingReadinessStatus.ServingSetMissing);
        timedOut.ExecutionAttempt.Should().Be(2);
        scopeBindingPort.Requests.Should().BeEmpty();
        readinessPort.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(
        ServiceDeploymentActivationFailureCode.PreparedArtifactMissing,
        "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_PREPARED_ARTIFACT_MISSING",
        "platform service activation failed because its prepared artifact was unavailable.")]
    [InlineData(
        ServiceDeploymentActivationFailureCode.RevisionPreparationFailed,
        "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_REVISION_PREPARATION_FAILED",
        "platform service activation failed because revision preparation failed.")]
    [InlineData(
        ServiceDeploymentActivationFailureCode.CapabilityViewNotReady,
        "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_CAPABILITY_VIEW_NOT_READY",
        "platform service activation failed because capability readiness was not established.")]
    [InlineData(
        ServiceDeploymentActivationFailureCode.AdmissionRejected,
        "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_ADMISSION_REJECTED",
        "platform service activation was rejected by admission policy.")]
    [InlineData(
        ServiceDeploymentActivationFailureCode.AdmissionEvaluationFailed,
        "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_ADMISSION_EVALUATION_FAILED",
        "platform service activation admission evaluation failed.")]
    [InlineData(
        ServiceDeploymentActivationFailureCode.RuntimeActivationFailed,
        "STUDIO_MEMBER_PLATFORM_BINDING_RUNTIME_ACTIVATION_FAILED",
        "platform service runtime activation failed.")]
    [InlineData(
        ServiceDeploymentActivationFailureCode.ServingTargetDeliveryFailed,
        "STUDIO_MEMBER_PLATFORM_BINDING_SERVING_TARGET_DELIVERY_FAILED",
        "platform service activation could not deliver serving targets.")]
    [InlineData(
        ServiceDeploymentActivationFailureCode.DefaultServingRevisionDeliveryFailed,
        "STUDIO_MEMBER_PLATFORM_BINDING_DEFAULT_SERVING_REVISION_DELIVERY_FAILED",
        "platform service activation could not commit the default serving revision.")]
    [InlineData(
        ServiceDeploymentActivationFailureCode.DefaultServingRevisionSuperseded,
        "STUDIO_MEMBER_PLATFORM_BINDING_DEFAULT_SERVING_REVISION_SUPERSEDED",
        "platform service activation was superseded by a newer serving generation.")]
    [InlineData(
        ServiceDeploymentActivationFailureCode.ActivationDependencyUnavailable,
        "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_DEPENDENCY_UNAVAILABLE",
        "platform service activation dependency was unavailable.")]
    [InlineData(
        (ServiceDeploymentActivationFailureCode)999,
        "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_FAILED",
        "platform service activation failed.")]
    public async Task ExecuteAsync_WhenActivationFailedTerminally_ShouldDispatchSanitizedFailureWithoutPolling(
        ServiceDeploymentActivationFailureCode activationFailureCode,
        string expectedFailureCode,
        string expectedFailureMessage)
    {
        var readinessPort = new RecordingReadinessQueryPort([
            new ScopeBindingReadinessSnapshot(
                ScopeId: "scope-1",
                ServiceId: "member-m-1",
                Status: ScopeBindingReadinessStatus.ServingSetMissing,
                ServiceCatalogVisible: true,
                ServingSetVisible: false,
                EligibleServingTargetVisible: false,
                InvokeReady: false,
                TerminalActivationFailureCode: activationFailureCode),
        ]);
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(
            scopeBindingPort,
            dispatchPort,
            readinessPort,
            delayAsync: (_, _) => throw new InvalidOperationException("terminal failure must not be polled"));

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewReadinessExecutionRequest(NewScriptStartRequest(), NewScriptRecoverySnapshot()));

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionFailed>();
        failed.Failure.Code.Should().Be(expectedFailureCode);
        failed.Failure.Message.Should().Be(expectedFailureMessage);
        failed.ExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.ReadinessInFlight);
        failed.ExecutionAttempt.Should().Be(2);
        scopeBindingPort.Requests.Should().BeEmpty();
        readinessPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadySnapshotAlsoCarriesLaggingFailure_ShouldSucceed()
    {
        var readinessPort = new RecordingReadinessQueryPort([
            ReadySnapshot() with
            {
                TerminalActivationFailureCode = ServiceDeploymentActivationFailureCode.PreparedArtifactMissing,
            },
        ]);
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(new RecordingScopeBindingCommandPort(), dispatchPort, readinessPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewReadinessExecutionRequest(NewScriptStartRequest(), NewScriptRecoverySnapshot()));

        await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionSucceeded>();
        dispatchPort.Dispatches.Should().NotContain(dispatch =>
            dispatch.Envelope.Payload.Is(StudioMemberPlatformBindingExecutionFailed.Descriptor));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLegacyRecoverySnapshotHasNoActivationFence_ShouldRemainPending()
    {
        var now = DateTimeOffset.Parse("2026-08-14T00:00:00+00:00");
        var snapshot = NewScriptRecoverySnapshot();
        snapshot.ActivationAttemptId = string.Empty;
        var readinessPort = new RecordingReadinessQueryPort([NotReadySnapshot()]);
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(
            new RecordingScopeBindingCommandPort(),
            dispatchPort,
            readinessPort,
            delayAsync: (_, _) =>
            {
                now = now.AddSeconds(2);
                return Task.CompletedTask;
            },
            utcNow: () => now,
            options: new StudioMemberPlatformBindingOptions
            {
                BindingReadinessTimeout = TimeSpan.FromSeconds(1),
            });

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewReadinessExecutionRequest(NewScriptStartRequest(), snapshot));

        await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingReadinessObservationTimedOut>();
        readinessPort.Requests.Should().HaveCount(2);
        readinessPort.Requests.Should().OnlyContain(request =>
            string.IsNullOrEmpty(request.ExpectedActivationAttemptId));
    }

    [Theory]
    [InlineData("service")]
    [InlineData("revision")]
    [InlineData("implementation")]
    [InlineData("deployment")]
    [InlineData("endpoints")]
    [InlineData("endpoints_empty")]
    [InlineData("endpoint_duplicate")]
    [InlineData("ref_endpoints")]
    [InlineData("ref_endpoints_empty")]
    [InlineData("ref_endpoint_duplicate")]
    [InlineData("actor")]
    [InlineData("ref_family")]
    [InlineData("ref_id")]
    [InlineData("ref_revision")]
    public async Task ExecuteAsync_WhenScriptRecoverySnapshotIsInvalid_ShouldFailWithoutCallingPorts(
        string invalidField)
    {
        var snapshot = NewScriptRecoverySnapshot();
        switch (invalidField)
        {
            case "service":
                snapshot.Result.PublishedServiceId = " member-m-1";
                break;
            case "revision":
                snapshot.Result.RevisionId = "rev-platform-bind-1 ";
                break;
            case "implementation":
                snapshot.Result.ImplementationKind = StudioMemberImplementationKind.Workflow;
                break;
            case "deployment":
                snapshot.ExpectedDeploymentId = " deployment-1";
                break;
            case "endpoints":
                snapshot.ExpectedEndpointIds.Add("script.other");
                break;
            case "endpoints_empty":
                snapshot.ExpectedEndpointIds.Clear();
                break;
            case "endpoint_duplicate":
                snapshot.ExpectedEndpointIds.Add("script.command");
                break;
            case "ref_endpoints":
                snapshot.Result.ImplementationRef.Script.EndpointIds.Add("script.other");
                break;
            case "ref_endpoints_empty":
                snapshot.Result.ImplementationRef.Script.EndpointIds.Clear();
                break;
            case "ref_endpoint_duplicate":
                snapshot.Result.ImplementationRef.Script.EndpointIds.Add("script.command");
                break;
            case "actor":
                snapshot.Result.ExpectedActorId = "gagent-service:script-runtime:deployment-1 ";
                break;
            case "ref_family":
                snapshot.Result.ImplementationRef = new StudioMemberImplementationRef
                {
                    Workflow = new StudioMemberWorkflowRef(),
                };
                break;
            case "ref_id":
                snapshot.Result.ImplementationRef.Script.ScriptId = " script-1";
                break;
            case "ref_revision":
                snapshot.Result.ImplementationRef.Script.ScriptRevision = "draft-other";
                break;
        }

        await AssertInvalidSnapshotAsync(
            NewReadinessExecutionRequest(NewScriptStartRequest(), snapshot));
    }

    [Theory]
    [InlineData("workflow_id")]
    [InlineData("workflow_revision")]
    [InlineData("definition_prefix")]
    [InlineData("actor")]
    [InlineData("endpoints")]
    public async Task ExecuteAsync_WhenWorkflowRecoverySnapshotIsInvalid_ShouldFailWithoutCallingPorts(
        string invalidField)
    {
        var snapshot = NewWorkflowRecoverySnapshot();
        switch (invalidField)
        {
            case "workflow_id":
                snapshot.Result.ImplementationRef.Workflow.WorkflowId = "workflow-other";
                break;
            case "workflow_revision":
                snapshot.Result.ImplementationRef.Workflow.WorkflowRevision = "rev-other";
                break;
            case "definition_prefix":
                snapshot.Result.ImplementationRef.Workflow.DefinitionActorIdPrefix += " ";
                break;
            case "actor":
                snapshot.Result.ExpectedActorId = "scope-workflow:scope-1:other:deployment-1";
                break;
            case "endpoints":
                snapshot.ExpectedEndpointIds.Clear();
                break;
        }

        await AssertInvalidSnapshotAsync(
            NewReadinessExecutionRequest(NewWorkflowStartRequest(), snapshot));
    }

    [Theory]
    [InlineData("agent_kind")]
    [InlineData("actor")]
    [InlineData("endpoint_duplicate")]
    public async Task ExecuteAsync_WhenGAgentRecoverySnapshotIsInvalid_ShouldFailWithoutCallingPorts(
        string invalidField)
    {
        var snapshot = NewGAgentRecoverySnapshot();
        switch (invalidField)
        {
            case "agent_kind":
                snapshot.Result.ImplementationRef.Gagent.AgentKind = "tests.other";
                break;
            case "actor":
                snapshot.Result.ExpectedActorId = "gagent-service:static-runtime:deployment-1 ";
                break;
            case "endpoint_duplicate":
                snapshot.ExpectedEndpointIds.Add("chat");
                break;
        }

        await AssertInvalidSnapshotAsync(
            NewReadinessExecutionRequest(NewGAgentStartRequest(), snapshot));
    }

    [Fact]
    public async Task ExecuteAsync_WhenGAgentHasNoEndpoints_ShouldSealCanonicalChatEndpoint()
    {
        var start = NewGAgentStartRequest();
        start.Request.Gagent.Endpoints.Clear();
        var snapshot = NewGAgentRecoverySnapshot();
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var readinessPort = RecordingReadinessQueryPort.Ready();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewReadinessExecutionRequest(start, snapshot));

        await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionSucceeded>();
        scopeBindingPort.Requests.Should().BeEmpty();
        readinessPort.Requests.Should().ContainSingle().Which.ExpectedEndpointIds.Should().Equal("chat");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Diagnostics.OtherGAgent")]
    public async Task ExecuteAsync_WhenGAgentDiagnosticActorTypeDrifts_ShouldRemainReadinessEligible(
        string diagnosticActorTypeName)
    {
        var snapshot = NewGAgentRecoverySnapshot();
        snapshot.Result.ImplementationRef.Gagent.ActorTypeName = diagnosticActorTypeName;
        var readinessPort = RecordingReadinessQueryPort.Ready();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(new RecordingScopeBindingCommandPort(), dispatchPort, readinessPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewReadinessExecutionRequest(NewGAgentStartRequest(), snapshot));

        await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionSucceeded>();
        readinessPort.Requests.Should().ContainSingle().Which.ExpectedEndpointIds.Should().Equal("chat");
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadinessQueryFails_ShouldDispatchFencedFailure()
    {
        var readinessPort = new RecordingReadinessQueryPort([ReadySnapshot()])
        {
            Failure = new InvalidOperationException("readiness unavailable"),
        };
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(new RecordingScopeBindingCommandPort(), dispatchPort, readinessPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewReadinessExecutionRequest(NewScriptStartRequest(), NewScriptRecoverySnapshot()));

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionFailed>();
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_READINESS_FAILED");
        failed.Failure.Message.Should().Be("platform binding readiness could not be verified.");
        failed.Failure.Message.Should().NotContain("readiness unavailable");
        failed.ExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.ReadinessInFlight);
        failed.ExecutionAttempt.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_WhenContinuationDispatchFails_ShouldNotProduceAnotherOutcome()
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort
        {
            Failure = new InvalidOperationException("dispatch unavailable"),
        };
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewCommandExecutionRequest(NewScriptStartRequest()));

        await dispatchPort.DispatchAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scopeBindingPort.Requests.Should().ContainSingle();
        dispatchPort.DispatchAttempts.Should().Be(1);
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("command_success")]
    [InlineData("command_failure")]
    [InlineData("readiness_success")]
    [InlineData("readiness_failure")]
    public async Task ExecuteAsync_WhenOutcomeDispatchFails_ShouldAttemptExactlyOneContinuation(string scenario)
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort
        {
            Failure = scenario == "command_failure"
                ? new InvalidOperationException("platform rejected")
                : null,
        };
        var readinessPort = new RecordingReadinessQueryPort([ReadySnapshot()])
        {
            Failure = scenario == "readiness_failure"
                ? new InvalidOperationException("readiness unavailable")
                : null,
        };
        var dispatchPort = new RecordingDispatchPort
        {
            Failure = new InvalidOperationException("dispatch unavailable"),
        };
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);
        var request = scenario.StartsWith("command", StringComparison.Ordinal)
            ? NewCommandExecutionRequest(NewScriptStartRequest())
            : NewReadinessExecutionRequest(NewScriptStartRequest(), NewScriptRecoverySnapshot());

        await service.ExecuteAsync("studio-member-binding-run:bind-1", request);

        await dispatchPort.DispatchAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatchPort.DispatchAttempts.Should().Be(1);
        dispatchPort.Dispatches.Should().BeEmpty();
        scopeBindingPort.Requests.Should().HaveCount(scenario.StartsWith("command", StringComparison.Ordinal) ? 1 : 0);
        readinessPort.Requests.Should().HaveCount(scenario.StartsWith("readiness", StringComparison.Ordinal) ? 1 : 0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCommandPayloadMissing_ShouldFailBeforeUpsert()
    {
        var start = NewScriptStartRequest();
        start.Request.Script = null;
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort);

        await service.ExecuteAsync(
            "studio-member-binding-run:bind-1",
            NewCommandExecutionRequest(start));

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionFailed>();
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_FAILED");
        scopeBindingPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_CommandStageWithCheckpoint_ShouldFailBeforeUpsert()
    {
        var request = NewCommandExecutionRequest(NewScriptStartRequest());
        request.RecoverySnapshot = NewScriptRecoverySnapshot();
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var readinessPort = RecordingReadinessQueryPort.Ready();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);

        await service.ExecuteAsync("studio-member-binding-run:bind-1", request);

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionFailed>();
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_RECOVERY_SNAPSHOT_INVALID");
        scopeBindingPort.Requests.Should().BeEmpty();
        readinessPort.Requests.Should().BeEmpty();
    }

    private static async Task AssertInvalidSnapshotAsync(StudioMemberPlatformBindingExecutionRequest request)
    {
        var scopeBindingPort = new RecordingScopeBindingCommandPort();
        var readinessPort = RecordingReadinessQueryPort.Ready();
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(scopeBindingPort, dispatchPort, readinessPort);

        await service.ExecuteAsync("studio-member-binding-run:bind-1", request);

        var failed = await dispatchPort.WaitForPayloadAsync<StudioMemberPlatformBindingExecutionFailed>();
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_RECOVERY_SNAPSHOT_INVALID");
        scopeBindingPort.Requests.Should().BeEmpty();
        readinessPort.Requests.Should().BeEmpty();
    }

    private static StudioMemberPlatformBindingExecutionRequest NewCommandExecutionRequest(
        StudioMemberPlatformBindingExecutionStartRequested start,
        int executionAttempt = 1) =>
        new()
        {
            BindingRunId = start.BindingRunId,
            PlatformBindingCommandId = start.PlatformBindingCommandId,
            Request = start.Request.Clone(),
            Admitted = start.Admitted.Clone(),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = executionAttempt,
            ExecutionStage = StudioMemberPlatformBindingExecutionStage.CommandInFlight,
        };

    private static StudioMemberPlatformBindingExecutionRequest NewReadinessExecutionRequest(
        StudioMemberPlatformBindingExecutionStartRequested start,
        StudioMemberPlatformBindingRecoverySnapshot snapshot,
        int executionAttempt = 2) =>
        new()
        {
            BindingRunId = start.BindingRunId,
            PlatformBindingCommandId = start.PlatformBindingCommandId,
            Request = start.Request.Clone(),
            Admitted = start.Admitted.Clone(),
            RecoverySnapshot = snapshot.Clone(),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = executionAttempt,
            ExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
        };

    private static StudioMemberPlatformBindingExecutionStartRequested NewScriptStartRequest(string? revisionId = null)
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

        return new StudioMemberPlatformBindingExecutionStartRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-bind-1",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 0,
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

    private static StudioMemberPlatformBindingExecutionStartRequested NewWorkflowStartRequest()
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

    private static StudioMemberPlatformBindingExecutionStartRequested NewGAgentStartRequest()
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
                },
            },
        };
        return request;
    }

    private static StudioMemberPlatformBindingRecoverySnapshot NewScriptRecoverySnapshot() =>
        new()
        {
            Result = new StudioMemberPlatformBindingResult
            {
                PublishedServiceId = "member-m-1",
                RevisionId = "rev-platform-bind-1",
                ImplementationKind = StudioMemberImplementationKind.Script,
                ExpectedActorId = "gagent-service:script-runtime:deployment-1",
                ImplementationRef = new StudioMemberImplementationRef
                {
                    Script = new StudioMemberScriptRef
                    {
                        ScriptId = "script-1",
                        ScriptRevision = "draft-1",
                        EndpointIds = { "script.command" },
                    },
                },
            },
            ExpectedDeploymentId = "deployment-1",
            ExpectedEndpointIds = { "script.command" },
            ActivationAttemptId = "platform-bind-1:a1",
        };

    private static StudioMemberPlatformBindingRecoverySnapshot NewWorkflowRecoverySnapshot()
    {
        var snapshot = new StudioMemberPlatformBindingRecoverySnapshot
        {
            Result = new StudioMemberPlatformBindingResult
            {
                PublishedServiceId = "member-m-1",
                RevisionId = "rev-platform-bind-1",
                ImplementationKind = StudioMemberImplementationKind.Workflow,
                ExpectedActorId = "scope-workflow:scope-1:workflow-main:deployment-1",
                ImplementationRef = new StudioMemberImplementationRef
                {
                    Workflow = new StudioMemberWorkflowRef
                    {
                        WorkflowId = "workflow-stable-id",
                        WorkflowRevision = "rev-platform-bind-1",
                        DefinitionActorIdPrefix = "scope-workflow:scope-1:workflow-main",
                    },
                },
            },
            ExpectedDeploymentId = "deployment-1",
            ActivationAttemptId = "platform-bind-1:a1",
        };
        snapshot.ExpectedEndpointIds.Add("chat");
        return snapshot;
    }

    private static StudioMemberPlatformBindingRecoverySnapshot NewGAgentRecoverySnapshot()
    {
        var snapshot = new StudioMemberPlatformBindingRecoverySnapshot
        {
            Result = new StudioMemberPlatformBindingResult
            {
                PublishedServiceId = "member-m-1",
                RevisionId = "rev-platform-bind-1",
                ImplementationKind = StudioMemberImplementationKind.Gagent,
                ExpectedActorId = "gagent-service:static-runtime:deployment-1",
                ImplementationRef = new StudioMemberImplementationRef
                {
                    Gagent = new StudioMemberGAgentRef
                    {
                        ActorTypeName = "Tests.JokerGAgent",
                        AgentKind = "tests.joker",
                    },
                },
            },
            ExpectedDeploymentId = "deployment-1",
            ActivationAttemptId = "platform-bind-1:a1",
        };
        snapshot.ExpectedEndpointIds.Add("chat");
        return snapshot;
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
        public IReadOnlyList<string>? ScriptEndpointIds { get; init; } = ["script.command"];
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
                _ => BuildScriptResult(request, ScriptEndpointIds),
            };
            return OmitExpectedDeploymentId ? result with { ExpectedDeploymentId = string.Empty } : result;
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
                ExpectedActorId: "scope-workflow:scope-1:workflow-main:deployment-1",
                WorkflowName: "workflow-main",
                DefinitionActorIdPrefix: "scope-workflow:scope-1:workflow-main",
                Workflow: new ScopeBindingWorkflowResult(
                    omitWorkflowId ? string.Empty : request.Workflow?.WorkflowId ?? string.Empty,
                    "workflow-main",
                    "scope-workflow:scope-1:workflow-main"),
                ExpectedDeploymentId: "deployment-1")
            {
                ActivationAttemptId = request.ActivationAttemptId,
            };
        }

        private static ScopeBindingUpsertResult BuildScriptResult(
            ScopeBindingUpsertRequest request,
            IReadOnlyList<string>? endpointIds)
        {
            var revisionId = request.RevisionId ?? "rev-1";
            return new ScopeBindingUpsertResult(
                ScopeId: request.ScopeId,
                ServiceId: request.ServiceId ?? string.Empty,
                DisplayName: request.DisplayName ?? string.Empty,
                RevisionId: revisionId,
                ImplementationKind: request.ImplementationKind,
                ExpectedActorId: "gagent-service:script-runtime:deployment-1",
                Script: new ScopeBindingScriptResult(
                    "script-1",
                    request.Script?.ScriptRevision ?? revisionId,
                    "scope-script:scope-1:script-1")
                {
                    EndpointIds = endpointIds!,
                },
                ExpectedDeploymentId: "deployment-1")
            {
                ActivationAttemptId = request.ActivationAttemptId,
            };
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
                ExpectedActorId: "gagent-service:static-runtime:deployment-1",
                GAgent: new ScopeBindingGAgentResult("Tests.JokerGAgent"),
                ExpectedDeploymentId: "deployment-1")
            {
                ActivationAttemptId = request.ActivationAttemptId,
            };
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
        public Exception? Failure { get; init; }

        public static RecordingReadinessQueryPort Ready() => new([ReadySnapshot()]);

        public Task<ScopeBindingReadinessSnapshot> GetReadinessAsync(
            ScopeBindingReadinessRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
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

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
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
