using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatBrowserActionTests
{
    private static readonly Timestamp Now = Timestamp.FromDateTimeOffset(
        new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void AuthorizationRequired_ShouldCreateCommittedBlockedActionRequest()
    {
        var state = AuthorizationWaitingState();
        var decision = NyxIdChatBrowserActions.RequestAuthorization(
            state,
            AuthorizationRequiredSignal(state),
            Registry(),
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.Request.ActionRequestId.Should().StartWith("action-");
        decision.Request.Action.Should().Be(NyxIdAssistantActionKind.ServiceConnect);
        decision.Request.Params.ParamsCase.Should().Be(
            NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect);
        decision.Request.Params.CatalogServiceConnect.ServiceSlug.Should().Be("api-github");
        decision.Request.AdvisoryRisk.Should().Be(NyxIdAssistantActionRisk.Grant);
        decision.State.PendingActions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(decision.Request);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Blocked);
        decision.State.ActiveTask.Gate.Mode.Should().Be(NyxIdChatPlanGateMode.Confirm);
        decision.State.ActiveTask.Gate.Status.Should().Be(NyxIdChatPlanGateStatus.Pending);
        decision.State.ActiveTask.Gate.PlanId.Should().Be("plan-alpha");
        decision.State.ActiveTask.Gate.Admissions.Should().ContainSingle().Which
            .Should().Match<NyxIdChatPlanOperationAdmission>(admission =>
                admission.ActionRequestId == decision.Request.ActionRequestId &&
                admission.Action == decision.Request.Action &&
                admission.ActionParamsSha256.Equals(
                    NyxIdChatPlanGateDecisions.HashActionParams(decision.Request.Params)));
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Blocked);
        decision.State.ActiveTurn.TerminalAt.Should().NotBeNull();
        decision.State.RecentTerminalTurns.Should().ContainSingle(summary =>
            summary.TurnId == "turn-alpha" &&
            summary.Status == NyxIdChatTurnStatus.Blocked);
        var actionStep = decision.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.BrowserAction);
        actionStep.StepId.Should().Be(decision.Request.StepId);
        actionStep.Kind.Should().Be(NyxIdChatStepKind.BrowserAction);
        actionStep.Status.Should().Be(NyxIdChatStepStatus.Waiting);
        actionStep.ActionRequestId.Should().Be(decision.Request.ActionRequestId);
        actionStep.Source.BrowserAction.Action.Should().Be(
            NyxIdAssistantActionKind.ServiceConnect);
        var postcondition = decision.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Postcondition);
        postcondition.Status.Should().Be(NyxIdChatStepStatus.Planned);
        postcondition.DependsOn.Should().Equal(actionStep.StepId);
        postcondition.AddedInPlanRevision.Should().Be(actionStep.AddedInPlanRevision);
        decision.State.ActiveTask.PlanRevisions.Should().HaveCount(2);
        decision.State.ActiveTask.PlanRevisionHistoryStart.Should().Be(1);
        decision.State.ActiveTask.PlanRevisions[0].AddedStepIds.Should()
            .Equal("step-tool-alpha");
        decision.State.ActiveTask.PlanRevisions[1].RevisionCause.Should()
            .Be(NyxIdChatPlanRevisionCause.ScopeResolution);
        decision.State.ActiveTask.PlanRevisions[1].AddedStepIds.Should()
            .Equal(actionStep.StepId, postcondition.StepId);
    }

    [Fact]
    public void KeyCreateAuthorizationRequired_ShouldCreateLeastScopeActionRequest()
    {
        var state = AuthorizationWaitingState();
        var original = state.Clone();
        var signal = AuthorizationRequiredSignal(state);
        signal.Tool.Receipt.ToolName = "nyxid_request_key_create";
        signal.Tool.Receipt.AuthorizationRequired.ServiceSlug = string.Empty;
        signal.Tool.Receipt.AuthorizationRequired.RequestedScopes.Clear();
        signal.Tool.Receipt.AuthorizationRequired.KeyCreate =
            new NyxIdKeyCreateActionRequirement
            {
                Name = "agent-alpha",
                Platform = "codex",
                AllowedServiceIds = { "us-github-alpha" },
            };

        var decision = NyxIdChatBrowserActions.RequestAuthorization(
            state,
            signal,
            LeastScopeRegistry(),
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.Request.RegistryRevision.Should().Be(
            NyxIdAssistantActionRegistry.LeastScopeRegistryRevision);
        decision.Request.Action.Should().Be(NyxIdAssistantActionKind.KeyCreate);
        decision.Request.Params.KeyCreate.Name.Should().Be("agent-alpha");
        decision.Request.Params.KeyCreate.Platform.Should().Be("codex");
        decision.Request.Params.KeyCreate.AllowedServiceIds.Should()
            .Equal("us-github-alpha");
        state.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void KeyRotateAuthorizationRequired_ShouldCreateV7ActionRequest()
    {
        var state = AuthorizationWaitingState();
        var original = state.Clone();
        var signal = AuthorizationRequiredSignal(state);
        signal.Tool.Receipt.ToolName = "nyxid_request_key_rotate";
        signal.Tool.Receipt.AuthorizationRequired.ServiceSlug = string.Empty;
        signal.Tool.Receipt.AuthorizationRequired.RequestedScopes.Clear();
        signal.Tool.Receipt.AuthorizationRequired.KeyRotate =
            new NyxIdKeyRotateActionRequirement { KeyId = "key-alpha" };

        var decision = NyxIdChatBrowserActions.RequestAuthorization(
            state,
            signal,
            RotationRegistry(),
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.Request.RegistryRevision.Should().Be(
            NyxIdAssistantActionRegistry.SupportedRegistryRevision);
        decision.Request.Action.Should().Be(NyxIdAssistantActionKind.KeyRotate);
        decision.Request.Params.KeyRotate.KeyId.Should().Be("key-alpha");
        state.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void ExactActionPlanConfirm_ShouldSatisfyOnlyLocalGateWithoutDispatchOrRevision()
    {
        var state = BlockedActionStateWithPendingGate();
        var gate = state.ActiveTask.Gate.Clone();
        var revision = state.ActiveTask.PlanRevision;
        var history = RevisionHistory(state.ActiveTask);

        var decision = NyxIdChatPlanGateDecisions.Resolve(
            state,
            ResolvePlanCommand(gate, confirmed: true),
            currentStateVersion: 17,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.NextCommand.Should().BeNull(
            "local plan admission does not impersonate NyxID authorization");
        decision.State.ActiveTask.Gate.Status.Should().Be(NyxIdChatPlanGateStatus.Satisfied);
        decision.State.PendingActions.Should().ContainSingle();
        decision.State.ActiveTask.Steps.Where(step =>
                step.ActionRequestId == gate.Admissions.Single().ActionRequestId)
            .Select(static step => step.Status)
            .Should().Equal(NyxIdChatStepStatus.Waiting, NyxIdChatStepStatus.Planned);
        decision.State.ActiveTask.PlanRevision.Should().Be(revision);
        RevisionHistory(decision.State.ActiveTask).Should().Equal(history);
    }

    [Fact]
    public void ExactActionPlanReject_ShouldCancelActionAndPostconditionWithoutDispatch()
    {
        var state = BlockedActionStateWithPendingGate();
        var gate = state.ActiveTask.Gate.Clone();
        var revision = state.ActiveTask.PlanRevision;
        var history = RevisionHistory(state.ActiveTask);

        var decision = NyxIdChatPlanGateDecisions.Resolve(
            state,
            ResolvePlanCommand(gate, confirmed: false),
            currentStateVersion: 17,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.NextCommand.Should().BeNull();
        decision.State.ActiveTask.Gate.Status.Should().Be(NyxIdChatPlanGateStatus.Rejected);
        decision.State.PendingActions.Should().BeEmpty();
        decision.State.RecentActions.Should().ContainSingle(action =>
            action.ActionRequestId == gate.Admissions.Single().ActionRequestId);
        decision.State.ActiveTask.Steps.Where(step =>
                step.ActionRequestId == gate.Admissions.Single().ActionRequestId)
            .Should().OnlyContain(step =>
                step.Status == NyxIdChatStepStatus.Cancelled &&
                step.ExternalEffect == NyxIdChatEffectEvidence.NotApplied);
        decision.State.ActiveTask.PlanRevision.Should().Be(revision);
        RevisionHistory(decision.State.ActiveTask).Should().Equal(history);
    }

    [Fact]
    public void ActionContinueBeforePlanConfirm_ShouldRejectWithoutDispatchOrRevisionChange()
    {
        var state = BlockedActionStateWithPendingGate();
        var gate = state.ActiveTask.Gate.Clone();
        var revision = state.ActiveTask.PlanRevision;
        var history = RevisionHistory(state.ActiveTask);

        var decision = NyxIdChatBrowserActions.Continue(
            state,
            ContinueCommand(
                state.PendingActions.Single().ActionRequestId,
                NyxIdChatActionDisposition.Completed),
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationPlanConfirmationRequired);
        decision.State.ActiveTask.Gate.Should().BeEquivalentTo(gate);
        decision.State.PendingActions.Should().ContainSingle();
        decision.State.ActiveTask.PlanRevision.Should().Be(revision);
        RevisionHistory(decision.State.ActiveTask).Should().Equal(history);
    }

    [Fact]
    public void ActionRequest_ShouldBeContentIdempotentAndRejectIdentityReuseConflict()
    {
        var first = NyxIdChatBrowserActions.RequestAuthorization(
            AuthorizationWaitingState(),
            AuthorizationRequiredSignal(AuthorizationWaitingState()),
            Registry(),
            Now);

        var replay = NyxIdChatBrowserActions.CommitRequest(
            first.State,
            first.Request.Clone(),
            Now);
        replay.ShouldCommit.Should().BeFalse();
        replay.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);

        var conflicting = first.Request.Clone();
        conflicting.Params.CatalogServiceConnect.ServiceSlug = "api-slack";
        var conflict = NyxIdChatBrowserActions.CommitRequest(
            first.State,
            conflicting,
            Now);
        conflict.ShouldCommit.Should().BeFalse();
        conflict.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        conflict.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionRequestConflict);
        conflict.State.Should().BeEquivalentTo(first.State);
    }

    [Fact]
    public void ActionRequestReplay_ShouldIgnoreObservationTimestampAfterRestart()
    {
        var first = NyxIdChatBrowserActions.RequestAuthorization(
            AuthorizationWaitingState(),
            AuthorizationRequiredSignal(AuthorizationWaitingState()),
            Registry(),
            Now);
        var replayedRequest = first.Request.Clone();
        replayedRequest.RequestedAt = Timestamp.FromDateTimeOffset(
            Now.ToDateTimeOffset().AddMinutes(5));

        var replay = NyxIdChatBrowserActions.CommitRequest(
            NyxIdChatConversationGAgentState.Parser.ParseFrom(
                first.State.ToByteArray()),
            replayedRequest,
            replayedRequest.RequestedAt);

        replay.ShouldCommit.Should().BeFalse();
        replay.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);
        replay.Request.RequestedAt.Should().Be(first.Request.RequestedAt);
    }

    [Fact]
    public void LegacyTask_ShouldStartVerifiableRevisionSuffixWithoutFabricatingHistory()
    {
        var legacy = AuthorizationWaitingState();
        legacy.ActiveTask.PlanRevision = 3;
        legacy.ActiveTask.PlanRevisionHistoryStart = 0;
        legacy.ActiveTask.PlanRevisions.Clear();

        var decision = NyxIdChatBrowserActions.RequestAuthorization(
            legacy,
            AuthorizationRequiredSignal(legacy),
            Registry(),
            Now);

        decision.State.ActiveTask.PlanRevision.Should().Be(4);
        decision.State.ActiveTask.PlanRevisionHistoryStart.Should().Be(4);
        decision.State.ActiveTask.PlanRevisions.Should().ContainSingle().Which
            .Should().Match<NyxIdChatPlanRevisionRecord>(revision =>
                revision.PlanRevision == 4 &&
                revision.RevisionCause == NyxIdChatPlanRevisionCause.ScopeResolution);
        decision.State.ActiveTask.PlanRevisions.Single().AddedStepIds.Should().HaveCount(2);
        decision.State.ActiveTask.Steps.Single(step => step.StepId == "step-tool-alpha")
            .AddedInPlanRevision.Should().Be(0,
                "deployment must not fabricate provenance for a legacy step");
    }

    [Fact]
    public void CommitRequest_ShouldRejectUnsupportedRevisionActionOrParams()
    {
        var sourceState = AuthorizationWaitingState();
        var valid = NyxIdChatBrowserActions.RequestAuthorization(
            sourceState,
            AuthorizationRequiredSignal(sourceState),
            Registry(),
            Now).Request;
        var invalidRequests = new[]
        {
            Mutate(valid, request => request.RegistryRevision = "nyxid-assistant-actions.future"),
            Mutate(valid, request =>
            {
                request.Action = NyxIdAssistantActionKind.KeyCreate;
                request.Params = new NyxIdAssistantActionParams
                {
                    KeyCreate = new NyxIdKeyCreateParams
                    {
                        Name = "Key Alpha",
                        Platform = "api",
                    },
                };
            }),
            Mutate(valid, request =>
                request.Params = new NyxIdAssistantActionParams
                {
                    ServiceReauthorize = new NyxIdServiceReauthorizeParams
                    {
                        UserServiceId = "service-alpha",
                    },
                }),
        };

        foreach (var request in invalidRequests)
        {
            var decision = NyxIdChatBrowserActions.CommitRequest(
                sourceState,
                request,
                Now);

            decision.ShouldCommit.Should().BeFalse();
            decision.ShouldDispatch.Should().BeFalse();
            decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
            decision.ReasonCode.Should().Be(
                NyxIdChatBrowserActions.ActionRequestInvalid);
            decision.State.Should().BeEquivalentTo(sourceState);
        }
    }

    [Fact]
    public void CommitRequest_ShouldAcceptLegacyRevisionDuringRegistryTransition()
    {
        var state = AuthorizationWaitingState();
        var request = NyxIdChatBrowserActions.RequestAuthorization(
            state,
            AuthorizationRequiredSignal(state),
            Registry(),
            Now).Request;
        request.RegistryRevision = NyxIdAssistantActionRegistry.LegacyRegistryRevision;

        var decision = NyxIdChatBrowserActions.CommitRequest(state, request, Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.Request.RegistryRevision.Should().Be(
            NyxIdAssistantActionRegistry.LegacyRegistryRevision);
        decision.Request.Action.Should().Be(NyxIdAssistantActionKind.ServiceConnect);
    }

    [Fact]
    public void CommitRequest_ShouldAcceptV6KeyCreateAndRejectV5Draft()
    {
        var state = AuthorizationWaitingState();
        var request = NyxIdChatBrowserActions.RequestAuthorization(
            state,
            AuthorizationRequiredSignal(state),
            Registry(),
            Now).Request;
        request.RegistryRevision = NyxIdAssistantActionRegistry.LeastScopeRegistryRevision;
        request.Action = NyxIdAssistantActionKind.KeyCreate;
        request.Params = new NyxIdAssistantActionParams
        {
            KeyCreate = new NyxIdKeyCreateParams
            {
                Name = "agent-alpha",
                Platform = "codex",
                AllowedServiceIds = { "us-github-alpha" },
            },
        };

        var acceptedV6 = NyxIdChatBrowserActions.CommitRequest(state, request, Now);

        acceptedV6.ShouldCommit.Should().BeTrue();
        acceptedV6.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        acceptedV6.Request.RegistryRevision.Should().Be(
            NyxIdAssistantActionRegistry.LeastScopeRegistryRevision);
        acceptedV6.Request.Action.Should().Be(NyxIdAssistantActionKind.KeyCreate);

        request.RegistryRevision = "nyxid-assistant-actions.v5";
        var rejectedLegacy = NyxIdChatBrowserActions.CommitRequest(state, request, Now);
        rejectedLegacy.ShouldCommit.Should().BeFalse();
        rejectedLegacy.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionRequestInvalid);
    }

    [Fact]
    public void CommitRequest_ShouldAcceptV7KeyRotateAndRejectV6()
    {
        var state = AuthorizationWaitingState();
        var request = NyxIdChatBrowserActions.RequestAuthorization(
            state,
            AuthorizationRequiredSignal(state),
            Registry(),
            Now).Request;
        request.RegistryRevision = NyxIdAssistantActionRegistry.SupportedRegistryRevision;
        request.Action = NyxIdAssistantActionKind.KeyRotate;
        request.Params = new NyxIdAssistantActionParams
        {
            KeyRotate = new NyxIdKeyRotateParams { KeyId = "key-alpha" },
        };

        var acceptedV7 = NyxIdChatBrowserActions.CommitRequest(state, request, Now);

        acceptedV7.ShouldCommit.Should().BeTrue();
        acceptedV7.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        acceptedV7.Request.RegistryRevision.Should().Be(
            NyxIdAssistantActionRegistry.SupportedRegistryRevision);
        acceptedV7.Request.Action.Should().Be(NyxIdAssistantActionKind.KeyRotate);

        request.RegistryRevision = NyxIdAssistantActionRegistry.LeastScopeRegistryRevision;
        var rejectedV6 = NyxIdChatBrowserActions.CommitRequest(state, request, Now);
        rejectedV6.ShouldCommit.Should().BeFalse();
        rejectedV6.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionRequestInvalid);
    }

    [Fact]
    public void CompletedReport_ShouldRejectResourceVariantThatDoesNotMatchAction()
    {
        var blocked = BlockedActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.Actions[0].Resource = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
        };

        var decision = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionContinuationInvalid);
    }

    [Fact]
    public void WaveOneActions_ShouldBindToExactlyOneSafeResourceVariant()
    {
        var userService = new NyxIdChatSafeResourceRef
        {
            UserService = new NyxIdChatUserServiceRef { UserServiceId = "us-alpha" },
        };
        var key = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
        };

        foreach (var action in new[]
                 {
                     NyxIdAssistantActionKind.ServiceConnect,
                     NyxIdAssistantActionKind.ServiceReauthorize,
                 })
        {
            NyxIdChatBrowserActions.ResourceMatchesAction(
                    action,
                    NyxIdChatActionDisposition.Completed,
                    userService)
                .Should().BeTrue();
            NyxIdChatBrowserActions.ResourceMatchesAction(
                    action,
                    NyxIdChatActionDisposition.Completed,
                    key)
                .Should().BeFalse();
        }

        foreach (var action in new[]
                 {
                     NyxIdAssistantActionKind.KeyCreate,
                     NyxIdAssistantActionKind.KeyRotate,
                 })
        {
            NyxIdChatBrowserActions.ResourceMatchesAction(
                    action,
                    NyxIdChatActionDisposition.Completed,
                    key)
                .Should().BeTrue();
            NyxIdChatBrowserActions.ResourceMatchesAction(
                    action,
                    NyxIdChatActionDisposition.Completed,
                    userService)
                .Should().BeFalse();
        }

        NyxIdChatBrowserActions.ResourceMatchesAction(
                NyxIdAssistantActionKind.KeyCreate,
                NyxIdChatActionDisposition.Completed,
                resource: null)
            .Should().BeFalse();
    }

    [Fact]
    public void CompletedReport_ShouldStartNewContinuationTurnWithTypedPostcondition()
    {
        var blocked = BlockedActionState();
        var frozenPlanId = blocked.ActiveTask.PlanId;
        var frozenRevision = blocked.ActiveTask.PlanRevision;
        var frozenHistory = blocked.ActiveTask.PlanRevisions.Select(static revision => revision.Clone())
            .ToArray();
        var predeclaredPostconditionId = blocked.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Postcondition).StepId;
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.ReadAuthority = ReadAuthority(command.CommandId);

        var decision = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeTrue();
        decision.Admission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Accepted);
        decision.State.ActiveTurn.TurnId.Should().Be("turn-action-alpha");
        decision.State.ActiveTurn.TurnId.Should().NotBe("turn-alpha");
        decision.State.ActiveTask.TaskId.Should().Be("task-alpha",
            "one goal keeps the same task identity across continuation turns");
        decision.State.ActiveTask.TurnId.Should().Be("turn-action-alpha");
        decision.State.ActiveTask.PlanId.Should().Be(frozenPlanId);
        decision.State.ActiveTask.PlanRevision.Should().Be(frozenRevision);
        decision.State.ActiveTask.PlanRevisions.Should().BeEquivalentTo(frozenHistory);
        decision.State.RecentTerminalTurns.Should().Contain(summary =>
            summary.TurnId == "turn-alpha" &&
            summary.Status == NyxIdChatTurnStatus.Blocked);
        decision.State.ActiveTask.Steps.Should().Contain(step =>
            step.StepId == blocked.PendingActions.Single().StepId &&
            step.Status == NyxIdChatStepStatus.Waiting);
        var postconditionStep = decision.State.ActiveTask.Steps.Should()
            .ContainSingle(step => step.Kind == NyxIdChatStepKind.Postcondition).Which;
        postconditionStep.Kind.Should().Be(NyxIdChatStepKind.Postcondition);
        postconditionStep.Status.Should().Be(NyxIdChatStepStatus.Running);
        postconditionStep.StepId.Should().Be(predeclaredPostconditionId);
        postconditionStep.Source.Postcondition.ActionRequestId.Should().Be(
            command.Actions.Single().ActionRequestId);
        decision.NextCommand.Should().NotBeNull();
        decision.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.ActionPostcondition);
        decision.NextCommand.ActionPostcondition.ScopeId.Should().Be("scope-alpha");
        decision.NextCommand.ActionPostcondition.OwnerSubject.Should().Be("owner-alpha");
        decision.NextCommand.ActionPostcondition.OriginTurnId.Should().Be("turn-alpha");
        decision.NextCommand.ActionPostcondition.RequestedAt.Should().Be(
            blocked.PendingActions.Single().RequestedAt);
        decision.NextCommand.ActionPostcondition.ReportedDisposition.Should().Be(
            NyxIdChatActionDisposition.Completed);
        decision.NextCommand.ActionPostcondition.Params.ParamsCase.Should().Be(
            NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect);
        decision.Admission.ReadAuthority.Should().BeNull();
        decision.State.ContinuationAdmission.ReadAuthority.Should().BeNull();
        decision.NextCommand.ActionPostcondition.ReadAuthority.Should().BeNull();
        decision.State.PendingActions.Single().Reports.Should().ContainSingle()
            .Which.SafeMessage.Should().BeEmpty(
            "browser supplied prose is not durable action evidence");
    }

    [Fact]
    public void KeyCompletedReport_ShouldPersistCanonicalReadAuthorityThroughRecovery()
    {
        var blocked = BlockedKeyCreateActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.Actions[0].Resource = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
        };
        command.ReadAuthority = ReadAuthority(command.CommandId);

        var decision = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeTrue();
        decision.Admission.ReadAuthority.Should().BeEquivalentTo(command.ReadAuthority);
        decision.State.ContinuationAdmission.ReadAuthority.Should()
            .BeEquivalentTo(command.ReadAuthority);
        decision.NextCommand!.ActionPostcondition.ReadAuthority.Should()
            .BeEquivalentTo(command.ReadAuthority);

        var restarted = NyxIdChatConversationGAgentState.Parser.ParseFrom(
            decision.State.ToByteArray());
        var recovery = NyxIdChatBrowserActions.TryBuildRecoveryDispatch(
            restarted,
            decision.NextCommand.Key);

        recovery.Should().NotBeNull();
        recovery!.ActionPostcondition.ReadAuthority.Should()
            .BeEquivalentTo(command.ReadAuthority);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("scope")]
    [InlineData("owner")]
    [InlineData("purpose")]
    [InlineData("request")]
    [InlineData("expired")]
    public void KeyCompletedReport_ShouldRejectInvalidReadAuthority(string mutation)
    {
        var blocked = BlockedKeyCreateActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.Actions[0].Resource = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
        };
        command.ReadAuthority = mutation == "missing"
            ? null
            : ReadAuthority(command.CommandId);
        switch (mutation)
        {
            case "scope":
                command.ReadAuthority!.ScopeId = "scope-other";
                break;
            case "owner":
                command.ReadAuthority!.OwnerSubject = "owner-other";
                break;
            case "purpose":
                command.ReadAuthority!.Purpose = "wrong-purpose";
                break;
            case "request":
                command.ReadAuthority!.SecretRef = ReadAuthority("command-action-other").SecretRef;
                break;
            case "expired":
                command.ReadAuthority!.ExpiresAtUnixMs = Now.ToDateTimeOffset()
                    .AddMilliseconds(-1)
                    .ToUnixTimeMilliseconds();
                break;
        }

        var decision = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionContinuationInvalid);
    }

    [Fact]
    public void KeyStateChangeWakeWithoutReadAuthority_ShouldRejectWithoutDispatch()
    {
        var blocked = BlockedKeyCreateActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.OriginTurnId = string.Empty;
        command.Actions.Clear();

        var decision = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionContinuationInvalid);
    }

    [Fact]
    public void KeyStateChangeWakeWithReadAuthority_ShouldDispatchWithAuthority()
    {
        var blocked = BlockedKeyCreateActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.OriginTurnId = string.Empty;
        command.Actions.Clear();
        command.ReadAuthority = ReadAuthority(command.CommandId);

        var decision = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeTrue();
        decision.Admission.ReadAuthority.Should().BeEquivalentTo(command.ReadAuthority);
        decision.NextCommand!.ActionPostcondition.ReadAuthority.Should()
            .BeEquivalentTo(command.ReadAuthority);
    }

    [Fact]
    public void AuthenticatedStateChangeWake_AfterUnverifiedPostcondition_ShouldStartCleanNextGeneration()
    {
        var failed = FailedKeyPostconditionState();
        var retryAt = Timestamp.FromDateTimeOffset(
            Now.ToDateTimeOffset().AddMinutes(1));
        var wake = KeyStateChangeWake("retry");

        var decision = NyxIdChatBrowserActions.Continue(
            failed.State,
            wake,
            retryAt);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeTrue();
        decision.State.PendingActions.Should().ContainSingle();
        decision.State.RecentActions.Should().BeEmpty();
        var request = decision.State.PendingActions.Single();
        request.ActionRequestId.Should().Be(failed.ActionRequestId);
        request.StepId.Should().Be(failed.ActionStepId);
        request.PostconditionResult.Should().BeNull();

        var step = decision.State.ActiveTask.Steps.Single(candidate =>
            candidate.Kind == NyxIdChatStepKind.Postcondition);
        step.StepId.Should().Be(failed.PreviousKey.StepId);
        step.ActionRequestId.Should().Be(failed.ActionRequestId);
        step.Status.Should().Be(NyxIdChatStepStatus.Running);
        step.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        step.FailureCode.Should().BeEmpty();
        step.SafeMessage.Should().BeEmpty();
        step.Substeps.Should().BeEmpty();

        var operation = step.Operation;
        operation.Key.OperationGeneration.Should().Be(
            failed.PreviousKey.OperationGeneration + 1);
        operation.Key.OperationId.Should().NotBe(failed.PreviousKey.OperationId);
        operation.Key.OperationId.Should().Be(NyxIdChatBrowserActions.BuildStableIdentity(
            "operation",
            operation.Key.ConversationActorId,
            operation.Key.TurnId,
            operation.Key.TaskId,
            operation.Key.StepId,
            operation.Key.OperationGeneration.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
        operation.Key.ConversationActorId.Should().Be(failed.PreviousKey.ConversationActorId);
        operation.Key.TurnId.Should().Be(failed.PreviousKey.TurnId);
        operation.Key.TaskId.Should().Be(failed.PreviousKey.TaskId);
        operation.Key.StepId.Should().Be(failed.PreviousKey.StepId);
        operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        operation.RequestedAt.Should().Be(retryAt);
        operation.DispatchedAt.Should().BeNull();
        operation.CompletedAt.Should().BeNull();
        operation.TerminalCode.Should().BeEmpty();
        operation.SafeMessage.Should().BeEmpty();
        operation.LatestProgressSequence.Should().Be(0);
        operation.LastProgressAt.Should().BeNull();
        operation.StalledAt.Should().BeNull();
        operation.LastStepChangedAt.Should().BeNull();
        operation.PendingStepChangedProgressSequence.Should().Be(0);
        operation.StepChangedDueAt.Should().BeNull();

        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        decision.State.ActiveTask.ActiveStepId.Should().Be(step.StepId);
        decision.State.ActiveTask.ActiveOperationId.Should().Be(operation.Key.OperationId);
        decision.State.ActiveTask.FailureCode.Should().BeEmpty();
        decision.State.ActiveTask.SafeMessage.Should().BeEmpty();
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        decision.State.ActiveTurn.FailureCode.Should().BeEmpty();
        decision.State.ActiveTurn.SafeMessage.Should().BeEmpty();
        decision.State.ActiveTurn.TerminalAt.Should().BeNull();

        decision.Admission.ReadAuthority.Should().BeEquivalentTo(wake.ReadAuthority);
        decision.State.ContinuationAdmission.ReadAuthority.Should()
            .BeEquivalentTo(wake.ReadAuthority);
        decision.NextCommand!.Key.Should().BeEquivalentTo(operation.Key);
        decision.NextCommand.ActionPostcondition.ActionRequestId.Should()
            .Be(failed.ActionRequestId);
        decision.NextCommand.ActionPostcondition.ReadAuthority.Should()
            .BeEquivalentTo(wake.ReadAuthority);
        decision.NextCommand.ActionPostcondition.ReadAuthority.Should()
            .NotBeEquivalentTo(failed.PreviousAuthority);
    }

    [Fact]
    public void ExactStateChangeWakeReplay_WithinRetryGeneration_ShouldRemainIdempotent()
    {
        var failed = FailedKeyPostconditionState();
        var wake = KeyStateChangeWake("retry-replay");
        var first = NyxIdChatBrowserActions.Continue(failed.State, wake, Now);

        var replay = NyxIdChatBrowserActions.Continue(
            first.State,
            wake.Clone(),
            Now);

        first.NextCommand!.Key.OperationGeneration.Should().Be(2);
        replay.ShouldCommit.Should().BeFalse();
        replay.ShouldDispatch.Should().BeTrue();
        replay.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);
        replay.NextCommand!.Key.Should().BeEquivalentTo(first.NextCommand.Key);
        replay.State.PendingActions.Should().ContainSingle()
            .Which.ActionRequestId.Should().Be(failed.ActionRequestId);
    }

    [Fact]
    public void SupersededGenerationResult_ShouldNotCompletePendingAction()
    {
        var failed = FailedKeyPostconditionState();
        var retry = NyxIdChatBrowserActions.Continue(
            failed.State,
            KeyStateChangeWake("retry-fence"),
            Now);
        var staleSuccess = new NyxIdChatOperationResultSignal
        {
            Key = failed.PreviousKey.Clone(),
            ActionPostcondition = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = failed.ActionRequestId,
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = true,
                Resource = new NyxIdChatSafeResourceRef
                {
                    Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
                },
            },
        };

        var decision = NyxIdChatBrowserActions.ReconcilePostcondition(
            retry.State,
            staleSuccess,
            Now);

        retry.NextCommand!.Key.OperationGeneration.Should().Be(2);
        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.State.PendingActions.Should().ContainSingle()
            .Which.ActionRequestId.Should().Be(failed.ActionRequestId);
        decision.State.PendingActions.Single().PostconditionResult.Should().BeNull();
        decision.State.RecentActions.Should().BeEmpty();
        decision.State.ActiveTask.Steps.Single(candidate =>
                candidate.Kind == NyxIdChatStepKind.Postcondition)
            .Operation.Key.Should().BeEquivalentTo(retry.NextCommand.Key);
    }

    [Fact]
    public void DistinctSequentialActionContinuations_ShouldPreserveFrozenPlanIdentity()
    {
        var blocked = BlockedActionStateWithTwoRequests();
        var taskId = blocked.ActiveTask.TaskId;
        var planId = blocked.ActiveTask.PlanId;
        var planRevision = blocked.ActiveTask.PlanRevision;
        var revisionHistory = blocked.ActiveTask.PlanRevisions
            .Select(static revision => revision.ToByteString().ToBase64())
            .ToArray();
        var actionIds = blocked.PendingActions
            .Select(static request => request.ActionRequestId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var postconditionIds = blocked.ActiveTask.Steps
            .Where(static step => step.Kind == NyxIdChatStepKind.Postcondition)
            .ToDictionary(
                static step => step.ActionRequestId,
                static step => step.StepId,
                StringComparer.Ordinal);

        var firstCommand = ContinueCommand(
            actionIds[0],
            NyxIdChatActionDisposition.Completed);
        var first = NyxIdChatBrowserActions.Continue(blocked, firstCommand, Now);
        var firstTurnId = first.State.ActiveTurn.TurnId;
        var firstReconciled = NyxIdChatBrowserActions.ReconcilePostcondition(
            first.State,
            VerifiedPostcondition(first.NextCommand!.Key, actionIds[0]),
            Now);

        firstReconciled.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Blocked);
        firstReconciled.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Blocked);
        firstReconciled.State.PendingActions.Should().ContainSingle(action =>
            action.ActionRequestId == actionIds[1]);
        firstReconciled.State.ActiveTask.Steps.Single(step =>
                step.ActionRequestId == actionIds[1] &&
                step.Kind == NyxIdChatStepKind.Postcondition)
            .Status.Should().Be(NyxIdChatStepStatus.Planned,
                "an unreported browser action cannot be postcondition-checked early");

        var secondCommand = ContinueCommand(
            actionIds[1],
            NyxIdChatActionDisposition.Completed);
        secondCommand.ClientRequestId = "client-action-beta";
        secondCommand.CommandId = "command-action-beta";
        secondCommand.CorrelationId = "correlation-action-beta";
        secondCommand.ContinuationTurnId = "turn-action-beta";
        var second = NyxIdChatBrowserActions.Continue(
            firstReconciled.State,
            secondCommand,
            Now);

        firstTurnId.Should().NotBe("turn-alpha");
        second.State.ActiveTurn.TurnId.Should().Be("turn-action-beta");
        second.State.ActiveTurn.TurnId.Should().NotBe(firstTurnId);
        second.State.ActiveTask.TaskId.Should().Be(taskId);
        second.State.ActiveTask.PlanId.Should().Be(planId);
        second.State.ActiveTask.PlanRevision.Should().Be(planRevision);
        second.State.ActiveTask.PlanRevisions.Select(static revision =>
                revision.ToByteString().ToBase64())
            .Should().Equal(revisionHistory);
        second.State.ActiveTask.Steps
            .Where(static step => step.Kind == NyxIdChatStepKind.Postcondition)
            .ToDictionary(
                static step => step.ActionRequestId,
                static step => step.StepId,
                StringComparer.Ordinal)
            .Should().BeEquivalentTo(postconditionIds);
        second.NextCommand!.Key.StepId.Should().Be(postconditionIds[actionIds[1]]);

        var completed = NyxIdChatBrowserActions.ReconcilePostcondition(
            second.State,
            VerifiedPostcondition(second.NextCommand.Key, actionIds[1]),
            Now);
        completed.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        completed.State.PendingActions.Should().BeEmpty();
        completed.State.ActiveTask.TaskId.Should().Be(taskId);
        completed.State.ActiveTask.PlanRevision.Should().Be(planRevision);
        completed.State.ActiveTask.PlanRevisions.Select(static revision =>
                revision.ToByteString().ToBase64())
            .Should().Equal(revisionHistory,
                "pure action continuations cannot fabricate legacy or new revision history");
    }

    [Fact]
    public void EmptyActionWake_ShouldReverifyPendingActionWithoutPersistingACompletionClaim()
    {
        var blocked = BlockedActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.OriginTurnId = string.Empty;
        command.Actions.Clear();

        var decision = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeTrue();
        decision.Admission.ActionReports.Should().BeEmpty();
        decision.State.PendingActions.Should().ContainSingle()
            .Which.Reports.Should().BeEmpty();
        decision.State.ActiveTask.Steps.Should().ContainSingle(step =>
            step.Kind == NyxIdChatStepKind.Postcondition &&
            step.ActionRequestId == blocked.PendingActions.Single().ActionRequestId);
        decision.NextCommand!.ActionPostcondition.ReportedDisposition.Should().Be(
            NyxIdChatActionDisposition.Unspecified);
        decision.NextCommand.ActionPostcondition.ResourceHint.Should().BeNull();
        decision.NextCommand.ActionPostcondition.Params.CatalogServiceConnect.ServiceSlug
            .Should().Be("api-github");
        decision.Admission.ReadAuthority.Should().BeNull();
        decision.State.ContinuationAdmission.ReadAuthority.Should().BeNull();
        decision.NextCommand.ActionPostcondition.ReadAuthority.Should().BeNull();
    }

    [Fact]
    public void EmptyActionWakeWithoutPendingActions_ShouldCommitFastNoOpTerminal()
    {
        var terminal = AuthorizationWaitingState();
        terminal.ActiveTurn.Status = NyxIdChatTurnStatus.Succeeded;
        terminal.LatestTurn = terminal.ActiveTurn.Clone();
        terminal.ActiveTask.Status = NyxIdChatTaskStatus.Succeeded;
        terminal.ActiveTask.ActiveStepId = string.Empty;
        terminal.ActiveTask.ActiveOperationId = string.Empty;
        terminal.ActiveTask.Steps.Clear();
        var command = ContinueCommand("unused-action", NyxIdChatActionDisposition.Completed);
        command.OriginTurnId = string.Empty;
        command.Actions.Clear();

        var decision = NyxIdChatBrowserActions.Continue(terminal, command, Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.Admission.OriginTurnId.Should().BeEmpty();
        decision.Admission.ActionReports.Should().BeEmpty();
        decision.State.ActiveTurn.TurnId.Should().Be("turn-action-alpha");
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        decision.State.ActiveTurn.TerminalAt.Should().NotBeNull();
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        decision.State.ActiveTask.Steps.Should().BeEmpty();
        decision.NextCommand.Should().BeNull();
    }

    [Fact]
    public void ExactContinuationReplayAtRequestedWaterline_ShouldRedispatchWithoutCommit()
    {
        var blocked = BlockedActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.ReadAuthority = ReadAuthority(command.CommandId);
        var first = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        var replay = NyxIdChatBrowserActions.Continue(first.State, command.Clone(), Now);

        replay.ShouldCommit.Should().BeFalse();
        replay.ShouldDispatch.Should().BeTrue();
        replay.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);
        replay.NextCommand!.Key.Should().BeEquivalentTo(first.NextCommand!.Key);
        replay.State.ActiveTask.PlanRevision.Should().Be(blocked.ActiveTask.PlanRevision);
        replay.State.ActiveTask.PlanRevisions.Should().BeEquivalentTo(
            blocked.ActiveTask.PlanRevisions);
        replay.State.ActiveTask.Steps.Select(static step => step.StepId).Should()
            .OnlyHaveUniqueItems();
        first.Admission.ReadAuthority.Should().BeNull();
        replay.Admission.ReadAuthority.Should().BeNull();
    }

    [Fact]
    public void SameContinuationIdentityWithDifferentReport_ShouldFailClosed()
    {
        var blocked = BlockedActionState();
        var completed = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        var first = NyxIdChatBrowserActions.Continue(blocked, completed, Now);
        var conflicting = completed.Clone();
        conflicting.Actions[0].Disposition = NyxIdChatActionDisposition.Declined;

        var decision = NyxIdChatBrowserActions.Continue(
            first.State,
            conflicting,
            Now);

        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationConflict);
        decision.State.ActiveTurn.TurnId.Should().Be("turn-action-alpha");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ContinuationReplay_ShouldRevalidateScopeAndConversation(bool changeScope)
    {
        var blocked = BlockedActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        var first = NyxIdChatBrowserActions.Continue(blocked, command, Now);
        var replay = command.Clone();
        if (changeScope)
            replay.ScopeId = "scope-other";
        else
            replay.ConversationActorId = "conversation-other";

        var decision = NyxIdChatBrowserActions.Continue(first.State, replay, Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationInvalid);
    }

    [Theory]
    [InlineData(NyxIdChatActionDisposition.Declined, "NYXID_ACTION_DECLINED")]
    [InlineData(NyxIdChatActionDisposition.Failed, "NYXID_ACTION_FAILED")]
    [InlineData(NyxIdChatActionDisposition.Cancelled, "NYXID_ACTION_CANCELLED")]
    [InlineData(NyxIdChatActionDisposition.Expired, "NYXID_ACTION_EXPIRED")]
    public void NonCompletedDisposition_ShouldEndNewTurnWithoutPostconditionDispatch(
        NyxIdChatActionDisposition disposition,
        string expectedCode)
    {
        var blocked = BlockedActionState();

        var decision = NyxIdChatBrowserActions.Continue(
            blocked,
            ContinueCommand(blocked.PendingActions.Single().ActionRequestId, disposition),
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeFalse();
        decision.State.ActiveTurn.TurnId.Should().Be("turn-action-alpha");
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        decision.State.ActiveTurn.FailureCode.Should().Be(expectedCode);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        decision.State.PendingActions.Should().BeEmpty();
        decision.State.RecentActions.Should().ContainSingle(action =>
            action.Reports.Single().Disposition == disposition);
    }

    [Fact]
    public void CompletedReportWithoutVerifiedPostcondition_ShouldRemainBlocked()
    {
        var blocked = BlockedActionState();
        var admitted = NyxIdChatBrowserActions.Continue(
            blocked,
            ContinueCommand(
                blocked.PendingActions.Single().ActionRequestId,
                NyxIdChatActionDisposition.Completed),
            Now);
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = admitted.NextCommand!.Key.Clone(),
            ActionPostcondition = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = blocked.PendingActions.Single().ActionRequestId,
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = false,
                FailureCode = "NYXID_ACTION_POSTCONDITION_MISMATCH",
                SafeMessage = "The connected service did not match the requested action.",
            },
        };

        var decision = NyxIdChatBrowserActions.ReconcilePostcondition(
            admitted.State,
            signal,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeFalse();
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Blocked);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Blocked);
        var postcondition = decision.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Postcondition);
        postcondition.Status.Should().Be(NyxIdChatStepStatus.Waiting);
        postcondition.Status.Should().NotBe(NyxIdChatStepStatus.Done);
        decision.State.PendingActions.Single().PostconditionResult.Verified.Should().BeFalse();
    }

    [Fact]
    public void MatchingPostcondition_ShouldCompleteOnlyNewContinuationTurn()
    {
        var blocked = BlockedActionState();
        var admitted = NyxIdChatBrowserActions.Continue(
            blocked,
            ContinueCommand(
                blocked.PendingActions.Single().ActionRequestId,
                NyxIdChatActionDisposition.Completed),
            Now);
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = admitted.NextCommand!.Key.Clone(),
            ActionPostcondition = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = blocked.PendingActions.Single().ActionRequestId,
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = true,
                Resource = new NyxIdChatSafeResourceRef
                {
                    UserService = new NyxIdChatUserServiceRef
                    {
                        UserServiceId = "service-alpha",
                    },
                },
            },
        };

        var decision = NyxIdChatBrowserActions.ReconcilePostcondition(
            admitted.State,
            signal,
            Now);

        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        decision.State.ActiveTurn.TurnId.Should().Be("turn-action-alpha");
        decision.State.RecentTerminalTurns.Should().Contain(summary =>
            summary.TurnId == "turn-alpha" &&
            summary.Status == NyxIdChatTurnStatus.Blocked);
        decision.State.PendingActions.Should().BeEmpty();
        decision.State.ActiveTask.Steps.Single(step =>
                step.StepId == blocked.PendingActions.Single().StepId)
            .Should().Match<NyxIdChatTaskStepState>(step =>
                step.Status == NyxIdChatStepStatus.Done &&
                step.ExternalEffect == NyxIdChatEffectEvidence.Confirmed);
        decision.State.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.Postcondition)
            .Status.Should().Be(NyxIdChatStepStatus.Done);
        decision.State.RecentActions.Should().ContainSingle(action =>
            action.PostconditionResult.Verified);
    }

    [Fact]
    public void VerifiedPostcondition_ShouldRejectResourceVariantThatDoesNotMatchAction()
    {
        var blocked = BlockedActionState();
        var admitted = NyxIdChatBrowserActions.Continue(
            blocked,
            ContinueCommand(
                blocked.PendingActions.Single().ActionRequestId,
                NyxIdChatActionDisposition.Completed),
            Now);
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = admitted.NextCommand!.Key.Clone(),
            ActionPostcondition = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = blocked.PendingActions.Single().ActionRequestId,
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = true,
                Resource = new NyxIdChatSafeResourceRef
                {
                    Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
                },
            },
        };

        var decision = NyxIdChatBrowserActions.ReconcilePostcondition(
            admitted.State,
            signal,
            Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionContinuationInvalid);
    }

    [Fact]
    public void CrossScopeOriginOrConversation_ShouldRejectWithoutDispatch()
    {
        var blocked = BlockedActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);

        foreach (var mutate in new Action<NyxIdChatActionContinueCommand>[]
                 {
                     current => current.ScopeId = "scope-other",
                     current => current.ConversationActorId = "conversation-other",
                     current => current.OriginTurnId = "turn-other",
                 })
        {
            var invalid = command.Clone();
            mutate(invalid);
            var decision = NyxIdChatBrowserActions.Continue(blocked, invalid, Now);
            decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
            decision.ShouldDispatch.Should().BeFalse();
        }
    }

    [Fact]
    public void ContinuationDuringAnotherActiveTurn_ShouldNeverStartHiddenWork()
    {
        var blocked = BlockedActionState();
        blocked.ActiveTurn = new NyxIdChatTurnState
        {
            TurnId = "turn-other-active",
            TaskId = "task-other-active",
            Status = NyxIdChatTurnStatus.Active,
        };
        blocked.ActiveTask = new NyxIdChatTaskState
        {
            TurnId = "turn-other-active",
            TaskId = "task-other-active",
            Status = NyxIdChatTaskStatus.Active,
        };

        var decision = NyxIdChatBrowserActions.Continue(
            blocked,
            ContinueCommand(
                blocked.PendingActions.Single().ActionRequestId,
                NyxIdChatActionDisposition.Completed),
            Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeFalse();
        decision.State.ActiveTurn.TurnId.Should().Be("turn-other-active");
        decision.Admission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Rejected);
        decision.Admission.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationActiveTurn);
        decision.State.ContinuationAdmission.Should().BeEquivalentTo(
            decision.Admission);

        var replay = NyxIdChatBrowserActions.Continue(
            decision.State,
            ContinueCommand(
                blocked.PendingActions.Single().ActionRequestId,
                NyxIdChatActionDisposition.Completed),
            Now);
        replay.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);
        replay.ShouldCommit.Should().BeFalse();
        replay.ShouldDispatch.Should().BeFalse();
        replay.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationActiveTurn);
    }

    [Fact]
    public void DuplicateAndOutOfOrderReports_ShouldNormalizeByActionIdentity()
    {
        var blocked = BlockedActionStateWithTwoRequests();
        var firstId = blocked.PendingActions[0].ActionRequestId;
        var secondId = blocked.PendingActions[1].ActionRequestId;
        var command = ContinueCommand(firstId, NyxIdChatActionDisposition.Completed);
        command.Actions.Add(ActionReport(secondId, NyxIdChatActionDisposition.Completed));
        command.Actions.Add(ActionReport(firstId, NyxIdChatActionDisposition.Completed));

        var admitted = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        admitted.ShouldCommit.Should().BeTrue();
        admitted.Admission.ActionReports.Select(static report => report.ActionRequestId)
            .Should().Equal(firstId, secondId);
        admitted.State.ActiveTask.Steps.Should().Contain(step =>
            step.StepId == "step-tool-alpha");
        admitted.State.ActiveTask.Steps.Count(step =>
            step.Kind == NyxIdChatStepKind.Postcondition).Should().Be(2);
        admitted.State.PendingActions.Single(action => action.ActionRequestId == firstId)
            .Reports.Should().ContainSingle();

        var reorderedReplay = command.Clone();
        reorderedReplay.Actions.Clear();
        reorderedReplay.Actions.Add(ActionReport(secondId, NyxIdChatActionDisposition.Completed));
        reorderedReplay.Actions.Add(ActionReport(firstId, NyxIdChatActionDisposition.Completed));
        var replay = NyxIdChatBrowserActions.Continue(
            admitted.State,
            reorderedReplay,
            Now);

        replay.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);
        replay.ShouldCommit.Should().BeFalse();
        replay.NextCommand!.Key.Should().BeEquivalentTo(admitted.NextCommand!.Key);
    }

    [Fact]
    public void ConflictingDuplicateReport_ShouldFailClosed()
    {
        var blocked = BlockedActionState();
        var actionRequestId = blocked.PendingActions.Single().ActionRequestId;
        var command = ContinueCommand(
            actionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.Actions.Add(ActionReport(
            actionRequestId,
            NyxIdChatActionDisposition.Declined));

        var decision = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationConflict);
    }

    [Fact]
    public void PartialReports_ShouldLeaveUnreportedActionPending()
    {
        var blocked = BlockedActionStateWithTwoRequests();
        var reportedId = blocked.PendingActions[0].ActionRequestId;
        var unreportedId = blocked.PendingActions[1].ActionRequestId;

        var decision = NyxIdChatBrowserActions.Continue(
            blocked,
            ContinueCommand(reportedId, NyxIdChatActionDisposition.Declined),
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.State.PendingActions.Should().ContainSingle(action =>
            action.ActionRequestId == unreportedId);
        decision.State.RecentActions.Should().ContainSingle(action =>
            action.ActionRequestId == reportedId &&
            action.Reports.Single().Disposition == NyxIdChatActionDisposition.Declined);
    }

    [Fact]
    public void MixedCompletedAndDeclinedReports_ShouldVerifyCompletedButNeverSucceedBatch()
    {
        var blocked = BlockedActionStateWithTwoRequests();
        var completedId = blocked.PendingActions[0].ActionRequestId;
        var declinedId = blocked.PendingActions[1].ActionRequestId;
        var command = ContinueCommand(
            completedId,
            NyxIdChatActionDisposition.Completed);
        command.Actions.Add(ActionReport(
            declinedId,
            NyxIdChatActionDisposition.Declined));
        var admitted = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        admitted.ShouldDispatch.Should().BeTrue();
        admitted.State.ActiveTask.Steps.Should().Contain(step =>
            step.ActionRequestId == declinedId &&
            step.Status == NyxIdChatStepStatus.Cancelled);
        var reconciled = NyxIdChatBrowserActions.ReconcilePostcondition(
            admitted.State,
            new NyxIdChatOperationResultSignal
            {
                Key = admitted.NextCommand!.Key.Clone(),
                ActionPostcondition = new NyxIdChatActionPostconditionResult
                {
                    ActionRequestId = completedId,
                    Disposition = NyxIdChatActionDisposition.Completed,
                    Verified = true,
                    Resource = new NyxIdChatSafeResourceRef
                    {
                        UserService = new NyxIdChatUserServiceRef
                        {
                            UserServiceId = "service-alpha",
                        },
                    },
                },
            },
            Now);

        reconciled.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        reconciled.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        reconciled.State.ActiveTask.Status.Should().NotBe(NyxIdChatTaskStatus.Succeeded);
        reconciled.State.RecentActions.Should().Contain(action =>
            action.ActionRequestId == completedId &&
            action.PostconditionResult.Verified);
    }

    private static NyxIdChatConversationGAgentState BlockedActionState()
    {
        var state = BlockedActionStateWithPendingGate();
        state.ActiveTask.Gate.Status = NyxIdChatPlanGateStatus.Satisfied;
        state.ActiveTask.Gate.DecidedAt = Now.Clone();
        return state;
    }

    private static NyxIdChatConversationGAgentState BlockedKeyCreateActionState()
    {
        var state = BlockedActionState();
        var request = state.PendingActions.Single();
        request.RegistryRevision = NyxIdAssistantActionRegistry.LeastScopeRegistryRevision;
        request.Action = NyxIdAssistantActionKind.KeyCreate;
        request.Params = new NyxIdAssistantActionParams
        {
            KeyCreate = new NyxIdKeyCreateParams
            {
                Name = "agent-alpha",
                Platform = "codex",
                AllowedServiceIds = { "service-alpha" },
            },
        };
        state.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.BrowserAction)
            .Source.BrowserAction.Action = NyxIdAssistantActionKind.KeyCreate;
        return state;
    }

    private static (
        NyxIdChatConversationGAgentState State,
        NyxIdChatOperationKey PreviousKey,
        NyxIdReadAuthorityRef PreviousAuthority,
        string ActionRequestId,
        string ActionStepId) FailedKeyPostconditionState()
    {
        var blocked = BlockedKeyCreateActionState();
        var request = blocked.PendingActions.Single();
        var command = ContinueCommand(
            request.ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.Actions[0].Resource = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
        };
        command.ReadAuthority = ReadAuthority(command.CommandId);
        var admitted = NyxIdChatBrowserActions.Continue(blocked, command, Now);
        var step = admitted.State.ActiveTask.Steps.Single(candidate =>
            candidate.Kind == NyxIdChatStepKind.Postcondition);
        step.Operation.DispatchedAt = Now.Clone();
        step.Operation.LatestProgressSequence = 7;
        step.Operation.LastProgressAt = Now.Clone();
        step.Operation.StalledAt = Now.Clone();
        step.Operation.LastStepChangedAt = Now.Clone();
        step.Operation.PendingStepChangedProgressSequence = 7;
        step.Operation.StepChangedDueAt = Now.Clone();
        step.Substeps.Add(new NyxIdChatSubstepState
        {
            SubstepId = "read-back",
            Title = "Read back the created key",
            Status = NyxIdChatSubstepStatus.Failed,
        });
        var previousKey = step.Operation.Key.Clone();
        var failed = NyxIdChatBrowserActions.ReconcilePostcondition(
            admitted.State,
            new NyxIdChatOperationResultSignal
            {
                Key = previousKey.Clone(),
                ActionPostcondition = new NyxIdChatActionPostconditionResult
                {
                    ActionRequestId = request.ActionRequestId,
                    Disposition = NyxIdChatActionDisposition.Completed,
                    Verified = false,
                    FailureCode = "NYXID_ACTION_POSTCONDITION_UNAVAILABLE",
                    SafeMessage = "The created key is not visible yet.",
                },
            },
            Now);
        return (
            failed.State,
            previousKey,
            command.ReadAuthority.Clone(),
            request.ActionRequestId,
            request.StepId);
    }

    private static NyxIdChatActionContinueCommand KeyStateChangeWake(string suffix)
    {
        var command = ContinueCommand(
            "unused-action",
            NyxIdChatActionDisposition.Completed);
        command.OriginTurnId = string.Empty;
        command.Actions.Clear();
        command.ContinuationTurnId = $"turn-action-{suffix}";
        command.ClientRequestId = $"client-action-{suffix}";
        command.CommandId = $"command-action-{suffix}";
        command.CorrelationId = $"correlation-action-{suffix}";
        command.ReadAuthority = ReadAuthority(command.CommandId);
        return command;
    }

    private static NyxIdChatConversationGAgentState BlockedActionStateWithPendingGate() =>
        NyxIdChatBrowserActions.RequestAuthorization(
            AuthorizationWaitingState(),
            AuthorizationRequiredSignal(AuthorizationWaitingState()),
            Registry(),
            Now).State;

    private static NyxIdChatOperationResultSignal VerifiedPostcondition(
        NyxIdChatOperationKey key,
        string actionRequestId) =>
        new()
        {
            Key = key.Clone(),
            ActionPostcondition = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = actionRequestId,
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = true,
                Resource = new NyxIdChatSafeResourceRef
                {
                    UserService = new NyxIdChatUserServiceRef
                    {
                        UserServiceId = $"service-{actionRequestId}",
                    },
                },
            },
        };

    private static NyxIdChatConversationGAgentState BlockedActionStateWithTwoRequests()
    {
        var state = BlockedActionState();
        var second = state.PendingActions.Single().Clone();
        second.ActionRequestId = "action-beta";
        second.StepId = "step-action-beta";
        second.Params.CatalogServiceConnect.ServiceSlug = "api-slack";
        var committed = NyxIdChatBrowserActions.CommitRequest(state, second, Now).State;
        committed.ActiveTask.Gate.Status = NyxIdChatPlanGateStatus.Satisfied;
        committed.ActiveTask.Gate.DecidedAt = Now.Clone();
        return committed;
    }

    private static NyxIdChatConversationGAgentState AuthorizationWaitingState()
    {
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = "conversation-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-tool-alpha",
            OperationId = "operation-tool-alpha",
            OperationGeneration = 1,
        };
        var step = new NyxIdChatTaskStepState
        {
            StepId = key.StepId,
            Order = 1,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Waiting,
            Required = true,
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource { ToolName = "nyxid_proxy" },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = key,
                Kind = NyxIdChatStepKind.Tool,
                Phase = NyxIdChatOperationPhase.Succeeded,
            },
        };
        var task = new NyxIdChatTaskState
        {
            TaskId = "task-alpha",
            TurnId = "turn-alpha",
            Status = NyxIdChatTaskStatus.Active,
            ActiveStepId = key.StepId,
            PlanId = "plan-alpha",
            CreatedAt = Now.Clone(),
            UpdatedAt = Now.Clone(),
        };
        task.Steps.Add(step);
        return new NyxIdChatConversationGAgentState
        {
            ConversationActorId = "conversation-alpha",
            ScopeId = "scope-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
                CreatedAt = Now.Clone(),
            },
            LatestTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
                CreatedAt = Now.Clone(),
            },
            ActiveTask = task,
            ProgressSequence = 4,
            UpdatedAt = Now.Clone(),
        };
    }

    private static NyxIdChatOperationResultSignal AuthorizationRequiredSignal(
        NyxIdChatConversationGAgentState state) => new()
    {
        Key = state.ActiveTask.Steps.Single().Operation.Key.Clone(),
        Tool = new NyxIdChatToolOperationResult
        {
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Receipt = new AgentToolReceipt
            {
                CallId = "call-alpha",
                ToolName = "nyxid_proxy",
                Status = AgentToolReceiptStatus.AuthorizationRequired,
                ErrorCode = "NYXID_UNAUTHORIZED",
                ErrorMessage = "Connect or reauthorize api-github to continue.",
                AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
                {
                    ServiceSlug = "api-github",
                    ReasonCode = "NYXID_UNAUTHORIZED",
                    SafeMessage = "Connect or reauthorize api-github to continue.",
                },
            },
        },
    };

    private static NyxIdChatActionContinueCommand ContinueCommand(
        string actionRequestId,
        NyxIdChatActionDisposition disposition)
    {
        var command = new NyxIdChatActionContinueCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            OriginTurnId = "turn-alpha",
            ContinuationTurnId = "turn-action-alpha",
            OwnerSubject = "owner-alpha",
            ClientRequestId = "client-action-alpha",
            CommandId = "command-action-alpha",
            CorrelationId = "correlation-action-alpha",
        };
        command.Actions.Add(ActionReport(actionRequestId, disposition));
        return command;
    }

    private static NyxIdReadAuthorityRef ReadAuthority(string commandId) => new()
    {
        SecretRef = NyxIdActionReadAuthorityPort.BuildRequestedRef(
            CredentialSecretPurposes.NyxIdChatActionReadAuthority,
            "scope-alpha",
            "owner-alpha",
            commandId),
        Purpose = CredentialSecretPurposes.NyxIdChatActionReadAuthority,
        ScopeId = "scope-alpha",
        OwnerSubject = "owner-alpha",
        Version = 1,
        ExpiresAtUnixMs = Now.ToDateTimeOffset().AddMinutes(10).ToUnixTimeMilliseconds(),
    };

    private static NyxIdChatPlanResolveCommand ResolvePlanCommand(
        NyxIdChatPlanGate gate,
        bool confirmed) => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TaskId = gate.TaskId,
        PlanId = gate.PlanId,
        PlanRevision = gate.PlanRevision,
        RequestId = gate.RequestId,
        ClientRequestId = confirmed ? "confirm-action-alpha" : "reject-action-alpha",
        Confirmed = confirmed,
        ExpectedStateVersion = 17,
    };

    private static string[] RevisionHistory(NyxIdChatTaskState task) =>
        task.PlanRevisions
            .Select(static revision => revision.ToByteString().ToBase64())
            .ToArray();

    private static NyxIdChatActionReport ActionReport(
        string actionRequestId,
        NyxIdChatActionDisposition disposition) => new()
    {
        ActionRequestId = actionRequestId,
        OriginTurnId = "turn-alpha",
        Disposition = disposition,
        SafeMessage = "browser-message-that-must-not-be-persisted",
        Resource = new NyxIdChatSafeResourceRef
        {
            UserService = new NyxIdChatUserServiceRef
            {
                UserServiceId = actionRequestId == "action-beta"
                    ? "service-beta"
                    : "service-alpha",
            },
        },
        ReportedAt = Now.Clone(),
    };

    private static NyxIdChatActionRequestState Mutate(
        NyxIdChatActionRequestState source,
        Action<NyxIdChatActionRequestState> mutate)
    {
        var clone = source.Clone();
        mutate(clone);
        return clone;
    }

    private static NyxIdAssistantActionRegistry Registry() =>
        NyxIdAssistantActionRegistry.Load("""
        {
          "schema_version": 4,
          "revision": "nyxid-assistant-actions.v4",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a catalog service in the NyxID browser.",
              "params_schema": {
                "oneOf": [
                  {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["catalogService"],
                    "properties": {
                      "catalogService": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["serviceSlug"],
                        "properties": {
                          "serviceSlug": {"type": "string"},
                          "requestedScopes": {"type": "array", "items": {"type": "string"}},
                          "viaNodeId": {"type": "string"},
                          "targetOrgId": {"type": "string"}
                        }
                      }
                    }
                  },
                  {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["customService"],
                    "properties": {
                      "customService": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["name", "endpointUrl", "authMethod"],
                        "properties": {
                          "name": {"type": "string"},
                          "endpointUrl": {"type": "string"},
                          "authMethod": {"type": "string"},
                          "authKeyName": {"type": "string"},
                          "viaNodeId": {"type": "string"},
                          "targetOrgId": {"type": "string"}
                        }
                      }
                    }
                  }
                ]
              },
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            }
          ]
        }
        """);

    private static NyxIdAssistantActionRegistry LeastScopeRegistry() =>
        NyxIdAssistantActionRegistry.Load(LeastScopeRegistryJson);

    private static NyxIdAssistantActionRegistry RotationRegistry()
    {
        var manifest = JsonNode.Parse(LeastScopeRegistryJson)!.AsObject();
        manifest["revision"] = NyxIdAssistantActionRegistry.SupportedRegistryRevision;
        manifest["actions"]!.AsArray().Add(JsonNode.Parse("""
            {
              "action": "key.rotate",
              "description": "Rotate an API key.",
              "params_schema": {
                "type": "object",
                "additionalProperties": false,
                "required": ["keyId"],
                "properties": {
                  "keyId": {"type": "string"}
                }
              },
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            }
            """));
        return NyxIdAssistantActionRegistry.Load(manifest.ToJsonString());
    }

    private const string LeastScopeRegistryJson = """
        {
          "schema_version": 4,
          "revision": "nyxid-assistant-actions.v6",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a catalog service in the NyxID browser.",
              "params_schema": {
                "oneOf": [
                  {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["catalogService"],
                    "properties": {
                      "catalogService": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["serviceSlug"],
                        "properties": {
                          "serviceSlug": {"type": "string"},
                          "requestedScopes": {"type": "array", "items": {"type": "string"}},
                          "viaNodeId": {"type": "string"},
                          "targetOrgId": {"type": "string"}
                        }
                      }
                    }
                  },
                  {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["customService"],
                    "properties": {
                      "customService": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["name", "endpointUrl", "authMethod"],
                        "properties": {
                          "name": {"type": "string"},
                          "endpointUrl": {"type": "string"},
                          "authMethod": {"type": "string"},
                          "authKeyName": {"type": "string"},
                          "viaNodeId": {"type": "string"},
                          "targetOrgId": {"type": "string"}
                        }
                      }
                    }
                  }
                ]
              },
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            },
            {
              "action": "key.create",
              "description": "Create a least-scope API key.",
              "params_schema": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "platform", "allowedServiceIds"],
                "properties": {
                  "name": {"type": "string"},
                  "platform": {"type": "string"},
                  "allowedServiceIds": {
                    "type": "array",
                    "minItems": 1,
                    "maxItems": 64,
                    "uniqueItems": true,
                    "items": {"type": "string"}
                  }
                }
              },
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            }
          ]
        }
        """;
}
