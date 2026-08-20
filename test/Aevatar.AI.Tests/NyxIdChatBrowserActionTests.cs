using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
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
    public void ServiceAccessRequired_ShouldCreateExactServiceAccessReviewAction()
    {
        var state = AuthorizationWaitingState();
        var signal = AuthorizationRequiredSignal(state);
        signal.Tool.Receipt.AuthorizationRequired.ReasonCode =
            "USER_SERVICE_ACCESS_REQUIRED";
        signal.Tool.Receipt.AuthorizationRequired.UserServiceId =
            "us-github-alpha";
        signal.Tool.Receipt.AuthorizationRequired.ResourceUri =
            "https://nyx-api.chrono-ai.fun/api/v1/proxy/s/api-github";

        var decision = NyxIdChatBrowserActions.RequestAuthorization(
            state,
            signal,
            Registry(),
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.Request.RegistryRevision.Should().Be(
            NyxIdAssistantActionRegistry.ServiceAccessReviewRegistryRevision);
        decision.Request.Action.Should().Be(
            NyxIdAssistantActionKind.ServiceAccessReview);
        decision.Request.Params.ParamsCase.Should().Be(
            NyxIdAssistantActionParams.ParamsOneofCase.ServiceAccessReview);
        decision.Request.Params.ServiceAccessReview.UserServiceId.Should()
            .Be("us-github-alpha");
        decision.Request.Params.ServiceAccessReview.ServiceSlug.Should()
            .Be("api-github");
        decision.Request.Params.ServiceAccessReview.ResourceUri.Should()
            .Be("https://nyx-api.chrono-ai.fun/api/v1/proxy/s/api-github");
        decision.Request.AdvisoryRisk.Should().Be(NyxIdAssistantActionRisk.Grant);
        decision.Request.RememberEligible.Should().BeFalse();
        decision.State.PendingActions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(decision.Request);
        decision.State.ActiveTask.Steps.Should().ContainSingle(step =>
            step.Kind == NyxIdChatStepKind.BrowserAction &&
            step.Source.BrowserAction.Action ==
                NyxIdAssistantActionKind.ServiceAccessReview &&
            step.ActionRequestId == decision.Request.ActionRequestId);
    }

    [Fact]
    public void KeyCreateAuthorizationRequired_ShouldCommitExactLeastScopeActionRequest()
    {
        var state = AuthorizationWaitingState();
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
        decision.Request.Params.ParamsCase.Should().Be(
            NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate);
        decision.Request.Params.KeyCreate.Name.Should().Be("agent-alpha");
        decision.Request.Params.KeyCreate.Platform.Should().Be("codex");
        decision.Request.Params.KeyCreate.AllowedServiceIds.Should()
            .Equal("us-github-alpha");
        decision.Request.RememberEligible.Should().BeFalse();
        decision.State.PendingActions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(decision.Request);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Blocked);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Blocked);
        decision.State.ActiveTask.Steps.Should().ContainSingle(step =>
            step.Kind == NyxIdChatStepKind.BrowserAction &&
            step.Source.BrowserAction.Action == NyxIdAssistantActionKind.KeyCreate &&
            step.ActionRequestId == decision.Request.ActionRequestId);
    }

    [Fact]
    public void KeyRotateAuthorizationRequired_ShouldCommitExactKeyActionRequest()
    {
        var state = AuthorizationWaitingState();
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
            NyxIdAssistantActionRegistry.KeyRotationRegistryRevision);
        decision.Request.Action.Should().Be(NyxIdAssistantActionKind.KeyRotate);
        decision.Request.Params.ParamsCase.Should().Be(
            NyxIdAssistantActionParams.ParamsOneofCase.KeyRotate);
        decision.Request.Params.KeyRotate.KeyId.Should().Be("key-alpha");
        decision.Request.RememberEligible.Should().BeFalse();
        decision.State.PendingActions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(decision.Request);
        decision.State.ActiveTask.Steps.Should().ContainSingle(step =>
            step.Kind == NyxIdChatStepKind.BrowserAction &&
            step.Source.BrowserAction.Action == NyxIdAssistantActionKind.KeyRotate &&
            step.ActionRequestId == decision.Request.ActionRequestId);
    }

    [Fact]
    public void ServiceReauthorizeAuthorizationRequired_ShouldRemainUnsupportedOnPinnedV8()
    {
        var state = AuthorizationWaitingState();
        var signal = ServiceReauthorizeSignal(state);

        Action resolve = () => NyxIdChatBrowserActions.RequestAuthorization(
            state,
            signal,
            ReauthorizeRegistry(),
            Now);

        resolve.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
    }

    [Fact]
    public void ServiceReauthorizeAuthorizationRequired_ShouldRejectWhenRegistryDoesNotExecuteIt()
    {
        var state = AuthorizationWaitingState();

        Action resolve = () => NyxIdChatBrowserActions.RequestAuthorization(
            state,
            ServiceReauthorizeSignal(state),
            RotationRegistry(),
            Now);

        resolve.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
    }

    [Fact]
    public void ServiceReauthorizeAuthorizationRequired_ShouldRejectMixedBlockerVariants()
    {
        var state = AuthorizationWaitingState();
        var signal = ServiceReauthorizeSignal(state);
        signal.Tool.Receipt.AuthorizationRequired.KeyRotate =
            new NyxIdKeyRotateActionRequirement { KeyId = "key-alpha" };

        var decision = NyxIdChatBrowserActions.RequestAuthorization(
            state,
            signal,
            ReauthorizeRegistry(),
            Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionRequestInvalid);
    }

    [Theory]
    [InlineData(NyxIdChatActionDisposition.Completed)]
    [InlineData(NyxIdChatActionDisposition.Declined)]
    public void PersistedDormantServiceReauthorize_ShouldRejectContinuationWithoutDispatch(
        NyxIdChatActionDisposition disposition)
    {
        var blocked = DormantServiceReauthorizeState();
        var actionRequestId = blocked.PendingActions.Single().ActionRequestId;
        var command = ContinueCommand(actionRequestId, disposition);

        var decision = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionContinuationInvalid);
    }

    [Fact]
    public void PersistedDormantServiceReauthorize_ShouldRejectEmptyActionWake()
    {
        var blocked = DormantServiceReauthorizeState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.OriginTurnId = string.Empty;
        command.Actions.Clear();

        var decision = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionContinuationInvalid);
    }

    [Fact]
    public void PersistedDormantServiceReauthorize_ShouldRejectIdempotentReplay()
    {
        var blocked = BlockedActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        var admitted = NyxIdChatBrowserActions.Continue(blocked, command, Now);
        var dormant = AsDormantServiceReauthorize(admitted.State);

        var decision = NyxIdChatBrowserActions.Continue(
            dormant,
            command.Clone(),
            Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionContinuationInvalid);
    }

    [Fact]
    public void PersistedDormantServiceReauthorize_ShouldNotBuildRecoveryDispatch()
    {
        var blocked = BlockedActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        var admitted = NyxIdChatBrowserActions.Continue(blocked, command, Now);
        var dormant = AsDormantServiceReauthorize(admitted.State);

        var dispatch = NyxIdChatBrowserActions.TryBuildRecoveryDispatch(
            dormant,
            admitted.NextCommand!.Key);

        dispatch.Should().BeNull();
    }

    [Fact]
    public void PersistedDormantServiceReauthorize_ShouldRejectPostconditionSignal()
    {
        var blocked = BlockedActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        var admitted = NyxIdChatBrowserActions.Continue(blocked, command, Now);
        var dormant = AsDormantServiceReauthorize(admitted.State);

        var decision = NyxIdChatBrowserActions.ReconcilePostcondition(
            dormant,
            VerifiedPostcondition(
                admitted.NextCommand!,
                dormant.PendingActions.Single().ActionRequestId),
            Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionContinuationInvalid);
    }

    [Fact]
    public void PersistedDormantServiceReauthorize_ShouldNotBecomeNextPostconditionDispatch()
    {
        var blocked = BlockedActionStateWithTwoRequests();
        var actionIds = blocked.PendingActions
            .Select(static action => action.ActionRequestId)
            .ToArray();
        var command = ContinueCommand(actionIds[0], NyxIdChatActionDisposition.Completed);
        command.Actions.Add(ActionReport(actionIds[1], NyxIdChatActionDisposition.Completed));
        var admitted = NyxIdChatBrowserActions.Continue(blocked, command, Now);
        var dispatchedId = admitted.NextCommand!.ActionPostcondition.ActionRequestId;
        var dormantId = actionIds.Single(id => !string.Equals(
            id,
            dispatchedId,
            StringComparison.Ordinal));
        var persisted = AsDormantServiceReauthorize(admitted.State, dormantId);

        var reconciled = NyxIdChatBrowserActions.ReconcilePostcondition(
            persisted,
            VerifiedPostcondition(admitted.NextCommand, dispatchedId),
            Now);

        reconciled.ShouldCommit.Should().BeTrue();
        reconciled.ShouldDispatch.Should().BeFalse();
        reconciled.State.PendingActions.Should().ContainSingle(action =>
            action.ActionRequestId == dormantId);
        reconciled.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Blocked);
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
    public void CommitRequest_ShouldAcceptLeastScopeKeyCreateOnlyOnV6()
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

        var accepted = NyxIdChatBrowserActions.CommitRequest(state, request, Now);

        accepted.ShouldCommit.Should().BeTrue();
        accepted.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        accepted.Request.Params.KeyCreate.AllowedServiceIds.Should().Equal("us-github-alpha");

        request.RegistryRevision = "nyxid-assistant-actions.v5";
        var rejectedLegacy = NyxIdChatBrowserActions.CommitRequest(state, request, Now);
        rejectedLegacy.ShouldCommit.Should().BeFalse();
        rejectedLegacy.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionRequestInvalid);
    }

    [Fact]
    public void CommitRequest_ShouldAcceptKeyRotateOnlyOnV7()
    {
        var state = AuthorizationWaitingState();
        var request = NyxIdChatBrowserActions.RequestAuthorization(
            state,
            AuthorizationRequiredSignal(state),
            Registry(),
            Now).Request;
        request.RegistryRevision = NyxIdAssistantActionRegistry.KeyRotationRegistryRevision;
        request.Action = NyxIdAssistantActionKind.KeyRotate;
        request.Params = new NyxIdAssistantActionParams
        {
            KeyRotate = new NyxIdKeyRotateParams { KeyId = "key-alpha" },
        };

        var accepted = NyxIdChatBrowserActions.CommitRequest(state, request, Now);

        accepted.ShouldCommit.Should().BeTrue();
        accepted.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        accepted.Request.Params.KeyRotate.KeyId.Should().Be("key-alpha");

        request.RegistryRevision = NyxIdAssistantActionRegistry.SupportedRegistryRevision;
        var acceptedV8 = NyxIdChatBrowserActions.CommitRequest(state, request, Now);
        acceptedV8.ShouldCommit.Should().BeTrue();
        acceptedV8.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);

        request.RegistryRevision = NyxIdAssistantActionRegistry.LeastScopeRegistryRevision;
        var rejectedV6 = NyxIdChatBrowserActions.CommitRequest(state, request, Now);
        rejectedV6.ShouldCommit.Should().BeFalse();
        rejectedV6.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionRequestInvalid);
    }

    [Fact]
    public void CommitRequest_ShouldRejectServiceReauthorizeOnAllPinnedRevisions()
    {
        var state = AuthorizationWaitingState();
        var request = NyxIdChatBrowserActions.RequestAuthorization(
            state,
            AuthorizationRequiredSignal(state),
            Registry(),
            Now).Request;
        request.RegistryRevision = NyxIdAssistantActionRegistry.SupportedRegistryRevision;
        request.Action = NyxIdAssistantActionKind.ServiceReauthorize;
        request.Params = new NyxIdAssistantActionParams
        {
            ServiceReauthorize = new NyxIdServiceReauthorizeParams
            {
                UserServiceId = "service-alpha",
                RequestedScopes = { "repo" },
            },
        };

        foreach (var revision in new[]
                 {
                     NyxIdAssistantActionRegistry.LegacyRegistryRevision,
                     NyxIdAssistantActionRegistry.WaveOneDraftRegistryRevision,
                     NyxIdAssistantActionRegistry.LeastScopeRegistryRevision,
                     NyxIdAssistantActionRegistry.KeyRotationRegistryRevision,
                     NyxIdAssistantActionRegistry.SupportedRegistryRevision,
                 })
        {
            request.RegistryRevision = revision;
            var rejected = NyxIdChatBrowserActions.CommitRequest(state, request, Now);
            rejected.ShouldCommit.Should().BeFalse(revision);
            rejected.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionRequestInvalid);
        }

        request.RegistryRevision = NyxIdAssistantActionRegistry.SupportedRegistryRevision;
        request.Params.ServiceReauthorize.RequestedScopes.Clear();
        var rejectedEmptyScopes = NyxIdChatBrowserActions.CommitRequest(state, request, Now);
        rejectedEmptyScopes.ShouldCommit.Should().BeFalse();
        rejectedEmptyScopes.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionRequestInvalid);
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
        decision.State.PendingActions.Single().Reports.Should().ContainSingle()
            .Which.SafeMessage.Should().BeEmpty(
            "browser supplied prose is not durable action evidence");
    }

    [Fact]
    public void VerifiedAuthorizationPostcondition_ShouldResumeOriginTurnAndPreserveItsExecutionActor()
    {
        var origin = AuthorizationWaitingStateWithPlannedContinuation();
        var requested = NyxIdChatBrowserActions.RequestAuthorization(
            origin,
            AuthorizationRequiredSignal(origin),
            Registry(),
            Now);
        var actionRequestId = requested.Request.ActionRequestId;
        var superseded = requested.State.ActiveTask.Steps.Single(step =>
            step.StepId == "step-llm-after-readiness");
        var actionContinuation = requested.State.ActiveTask.Steps.Single(step =>
            step.Source?.Llm?.ActionRequestId == actionRequestId);

        requested.Request.SourceToolStepId.Should().Be("step-tool-alpha");
        superseded.Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        superseded.Required.Should().BeFalse();
        actionContinuation.Status.Should().Be(NyxIdChatStepStatus.Planned);
        requested.State.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.BrowserAction)
            .DependsOn.Should().Equal("step-tool-alpha");
        actionContinuation.DependsOn.Should().Equal(
            requested.State.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.Postcondition).StepId);
        var actionRevision = requested.State.ActiveTask.PlanRevisions[^1];
        actionRevision.AddedStepIds.Should().Contain(actionContinuation.StepId);
        actionRevision.CancelledStepIds.Should().Equal(superseded.StepId);
        superseded.CancelledInPlanRevision.Should().Be(actionRevision.PlanRevision);

        var continuation = ContinueCommand(
            actionRequestId,
            NyxIdChatActionDisposition.Completed);
        continuation.ToolContext = new AgentToolExecutionContextPayload
        {
            Credentials = new AgentToolCredentialsPayload
            {
                NyxIdAccessToken = "fresh-token",
                NyxIdCredentialKind =
                    AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
            },
        };
        var admitted = NyxIdChatBrowserActions.Continue(
            requested.State,
            continuation,
            Now);
        admitted.NextCommand!.ActionPostcondition.ToolContext.Credentials.NyxIdAccessToken
            .Should().Be("fresh-token");
        var reconciled = NyxIdChatBrowserActions.ReconcilePostcondition(
            admitted.State,
            VerifiedPostcondition(admitted.NextCommand!, actionRequestId),
            Now);

        reconciled.ShouldDispatch.Should().BeTrue();
        reconciled.NextCommand.Should().NotBeNull();
        reconciled.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm);
        reconciled.NextCommand.Key.TurnId.Should().Be("turn-alpha");
        reconciled.NextCommand.Llm.ContinueSession.Should().BeTrue();
        reconciled.NextCommand.Llm.RematerializeTurnCatalog.Should().BeTrue();
        reconciled.NextCommand.Llm.AgentProfile.ProfileId.Should().Be("profile-alpha");
        reconciled.NextCommand.Llm.AgentProfileTurnAuthority.AuthorityKind.Should().Be(
            AgentProfileTurnAuthorityKind.Selected);
        reconciled.State.ActiveTurn.TurnId.Should().Be("turn-action-alpha");
        reconciled.State.ActiveTurn.CommandId.Should().Be("command-action-alpha");
        reconciled.State.ActiveTurn.Prompt.Should().Be(
            "retrieve one issue that is assigned to me via my github account.");
        reconciled.State.ActiveTurn.AgentProfileTurnAuthority.AuthorityKind.Should().Be(
            AgentProfileTurnAuthorityKind.Selected);
        reconciled.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        reconciled.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        reconciled.State.ActiveTask.Steps.Single(step =>
                step.StepId == "step-tool-alpha")
            .Should().Match<NyxIdChatTaskStepState>(step =>
                step.Status == NyxIdChatStepStatus.Done &&
                step.ExternalEffect == NyxIdChatEffectEvidence.NotApplied);
        reconciled.State.ActiveTask.Steps.Single(step =>
                step.Source?.Llm?.ActionRequestId == actionRequestId)
            .Status.Should().Be(NyxIdChatStepStatus.Running);

        var plannedTool = NyxIdChatTaskLifecycle.ApplyOperationResult(
            reconciled.State,
            new NyxIdChatOperationResultSignal
            {
                Key = reconciled.NextCommand.Key.Clone(),
                Llm = new NyxIdChatLLMOperationResult
                {
                    ToolCalls =
                    {
                        new NyxIdChatToolCall
                        {
                            CallId = "call-github-issue",
                            ToolName = "nyxop_github_issue_read",
                            ArgumentsJson = "{\"limit\":1}",
                            Safety = new NyxIdChatToolCallSafety
                            {
                                IsReadOnly = true,
                                MayChangeExternalState = false,
                            },
                        },
                    },
                },
            },
            Now);

        plannedTool.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        plannedTool.NextCommand.Should().NotBeNull();
        plannedTool.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        plannedTool.NextCommand.Key.TurnId.Should().Be("turn-alpha",
            "the original turn actor owns the transient LLM session and authorized tool capability");
    }

    [Fact]
    public void VerifiedAuthorizationPostcondition_ShouldDispatchTypedGenericContinuation()
    {
        var origin = AuthorizationWaitingStateWithPlannedContinuation();
        origin.ActiveTurn.Prompt = "retrieve one assigned item through my connected service";
        origin.LatestTurn = origin.ActiveTurn.Clone();
        var sourceTool = origin.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        sourceTool.Source.Tool.ToolName = "nyxid_require_service";
        sourceTool.Source.Tool.AuthorizationReadiness =
            new NyxIdChatAuthorizationReadinessInput
            {
                ToolName = "nyxid_require_service",
                Params = new NyxIdChatRequireServiceParams
                {
                    ServiceSlug = "service-alpha",
                    ServiceLabel = "Service Alpha",
                    ResourceUri = "https://service.example",
                    RequestedScopes = { "items:read" },
                },
            };
        var authorizationRequired = AuthorizationRequiredSignal(origin);
        authorizationRequired.Tool.Receipt.ToolName = "nyxid_require_service";
        authorizationRequired.Tool.Receipt.AuthorizationRequired.ServiceSlug = "service-alpha";
        authorizationRequired.Tool.Receipt.AuthorizationRequired.SafeMessage =
            "Connect or reauthorize the requested service.";
        var requested = NyxIdChatBrowserActions.RequestAuthorization(
            origin,
            authorizationRequired,
            Registry(),
            Now);
        var actionRequestId = requested.Request.ActionRequestId;
        var continuation = ContinueCommand(
            actionRequestId,
            NyxIdChatActionDisposition.Completed);
        continuation.ToolContext = new AgentToolExecutionContextPayload
        {
            Credentials = new AgentToolCredentialsPayload
            {
                NyxIdAccessToken = "fresh-token-that-must-remain-transient",
                NyxIdCredentialKind =
                    AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
            },
        };
        var admitted = NyxIdChatBrowserActions.Continue(
            requested.State,
            continuation,
            Now);
        var postconditionStep = admitted.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Postcondition &&
            step.ActionRequestId == actionRequestId);

        var reconciled = NyxIdChatBrowserActions.ReconcilePostcondition(
            admitted.State,
            new NyxIdChatOperationResultSignal
            {
                Key = admitted.NextCommand!.Key.Clone(),
                ActionPostcondition = new NyxIdChatActionPostconditionResult
                {
                    ActionRequestId = actionRequestId,
                    Disposition = NyxIdChatActionDisposition.Completed,
                    Verified = true,
                    Resource = new NyxIdChatSafeResourceRef
                    {
                        UserService = new NyxIdChatUserServiceRef
                        {
                            UserServiceId = "us-alpha",
                        },
                    },
                    VerificationInputSha256 =
                        NyxIdChatActionPostconditionEvidence.ComputeVerificationInputSha256(
                            admitted.NextCommand.ActionPostcondition),
                },
            },
            Now);

        reconciled.ShouldDispatch.Should().BeTrue();
        reconciled.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm);
        var typed = reconciled.NextCommand.Llm.VerifiedAuthorizationContinuation;
        typed.Should().NotBeNull();
        typed.ActionRequestId.Should().Be(actionRequestId);
        typed.OriginTurnId.Should().Be("turn-alpha");
        typed.SourceToolStepId.Should().Be("step-tool-alpha");
        typed.PostconditionStepId.Should().Be(postconditionStep.StepId);
        typed.VerifiedResource.UserService.UserServiceId.Should().Be("us-alpha");
        typed.ServiceSlug.Should().Be("service-alpha");
        typed.VerifiedAt.Should().Be(Now);
        typed.ResumeRequirement.Should().Be(
            NyxIdChatAuthorizationResumeRequirement.CompleteOriginalServiceRequest);
        var typedJson = JsonFormatter.Default.Format(typed);
        typedJson.Should().Contain("\"authorizationReadiness\"");
        typedJson.Should().Contain("\"toolName\": \"nyxid_require_service\"");
        typedJson.Should().Contain("\"serviceSlug\": \"service-alpha\"");
        typedJson.Should().Contain("\"requestedScopes\": [ \"items:read\" ]");
        typedJson.ToLowerInvariant().Should().NotContain("token");
        typedJson.ToLowerInvariant().Should().NotContain("credential");
        reconciled.State.ActiveTask.Steps.Single(step =>
                step.Source?.Llm?.ActionRequestId == actionRequestId)
            .Source.Llm.ResumeRequirement.Should().Be(
                NyxIdChatAuthorizationResumeRequirement.CompleteOriginalServiceRequest);
        reconciled.NextCommand.ToString().Should()
            .NotContain("fresh-token-that-must-remain-transient");
    }

    [Fact]
    public void DedicatedServiceConnectAuthorization_ShouldFreezeCommunicateCompletionRequirement()
    {
        var origin = AuthorizationWaitingStateWithPlannedContinuation();
        origin.ActiveTurn.Prompt = "connect my requested service";
        origin.ActiveTurn.Intent = NyxIdChatTurnIntent.ServiceConnect;
        origin.LatestTurn = origin.ActiveTurn.Clone();
        var authorizationRequired = AuthorizationRequiredSignal(origin);
        authorizationRequired.Tool.Receipt.AuthorizationRequired.ServiceSlug = "service-alpha";
        authorizationRequired.Tool.Receipt.AuthorizationRequired.SafeMessage =
            "Connect or reauthorize the requested service.";
        var requested = NyxIdChatBrowserActions.RequestAuthorization(
            origin,
            authorizationRequired,
            Registry(),
            Now);
        var actionRequestId = requested.Request.ActionRequestId;
        requested.State.ActiveTask.Steps.Single(step =>
                step.Source?.Llm?.ActionRequestId == actionRequestId)
            .Source.Llm.ResumeRequirement.Should().Be(
                NyxIdChatAuthorizationResumeRequirement.CommunicateAuthorizationCompletion);
        var admitted = NyxIdChatBrowserActions.Continue(
            requested.State,
            ContinueCommand(actionRequestId, NyxIdChatActionDisposition.Completed),
            Now);

        var reconciled = NyxIdChatBrowserActions.ReconcilePostcondition(
            admitted.State,
            new NyxIdChatOperationResultSignal
            {
                Key = admitted.NextCommand!.Key.Clone(),
                ActionPostcondition = new NyxIdChatActionPostconditionResult
                {
                    ActionRequestId = actionRequestId,
                    Disposition = NyxIdChatActionDisposition.Completed,
                    Verified = true,
                    Resource = new NyxIdChatSafeResourceRef
                    {
                        UserService = new NyxIdChatUserServiceRef
                        {
                            UserServiceId = "service-alpha",
                        },
                    },
                    VerificationInputSha256 =
                        NyxIdChatActionPostconditionEvidence.ComputeVerificationInputSha256(
                            admitted.NextCommand.ActionPostcondition),
                },
            },
            Now);

        reconciled.ShouldDispatch.Should().BeTrue();
        reconciled.NextCommand!.Llm.VerifiedAuthorizationContinuation.ResumeRequirement.Should()
            .Be(NyxIdChatAuthorizationResumeRequirement.CommunicateAuthorizationCompletion);
        reconciled.State.ActiveTask.Steps.Single(step =>
                step.Source?.Llm?.ActionRequestId == actionRequestId)
            .Source.Llm.ResumeRequirement.Should().Be(
                NyxIdChatAuthorizationResumeRequirement.CommunicateAuthorizationCompletion);
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
            VerifiedPostcondition(first.NextCommand!, actionIds[0]),
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
            VerifiedPostcondition(second.NextCommand, actionIds[1]),
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
                VerificationInputSha256 =
                    NyxIdChatActionPostconditionEvidence.ComputeVerificationInputSha256(
                        admitted.NextCommand.ActionPostcondition),
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
                VerificationInputSha256 =
                    NyxIdChatActionPostconditionEvidence.ComputeVerificationInputSha256(
                        admitted.NextCommand.ActionPostcondition),
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
    public void ForgedPostconditionInputDigest_ShouldRejectEveryFrozenInputDrift()
    {
        var blocked = BlockedActionState();
        var actionRequestId = blocked.PendingActions.Single().ActionRequestId;
        var admitted = NyxIdChatBrowserActions.Continue(
            blocked,
            ContinueCommand(actionRequestId, NyxIdChatActionDisposition.Completed),
            Now);
        var command = admitted.NextCommand!;
        var mutations = new (string Name, Action<NyxIdChatActionPostconditionInput> Apply)[]
        {
            ("service reauthorize action", input =>
            {
                input.Action = NyxIdAssistantActionKind.ServiceReauthorize;
                input.Params = new NyxIdAssistantActionParams
                {
                    ServiceReauthorize = new NyxIdServiceReauthorizeParams
                    {
                        UserServiceId = "service-alpha",
                        RequestedScopes = { "repo" },
                    },
                };
            }),
            ("service access review action", input =>
            {
                input.Action = NyxIdAssistantActionKind.ServiceAccessReview;
                input.Params = new NyxIdAssistantActionParams
                {
                    ServiceAccessReview = new NyxIdServiceAccessReviewParams
                    {
                        UserServiceId = "service-alpha",
                        ServiceSlug = "api-github",
                        ResourceUri = "https://nyx.example/resource",
                    },
                };
            }),
            ("same action params", input =>
                input.Params.CatalogServiceConnect.ServiceSlug = "api-slack"),
            ("resource hint", input =>
                input.ResourceHint.UserService.UserServiceId = "service-other"),
            ("scope", input => input.ScopeId = "scope-other"),
            ("owner", input => input.OwnerSubject = "owner-other"),
            ("origin turn", input => input.OriginTurnId = "turn-other"),
            ("disposition", input =>
                input.ReportedDisposition = NyxIdChatActionDisposition.Unspecified),
            ("request time", input =>
                input.RequestedAt = Timestamp.FromDateTimeOffset(
                    input.RequestedAt.ToDateTimeOffset().AddSeconds(1))),
        };

        foreach (var mutation in mutations)
        {
            var forgedInput = command.ActionPostcondition.Clone();
            mutation.Apply(forgedInput);
            var signal = VerifiedPostcondition(command, actionRequestId);
            signal.ActionPostcondition.VerificationInputSha256 =
                NyxIdChatActionPostconditionEvidence.ComputeVerificationInputSha256(forgedInput);

            var decision = NyxIdChatBrowserActions.ReconcilePostcondition(
                admitted.State,
                signal,
                Now);

            decision.ShouldCommit.Should().BeFalse(mutation.Name);
            decision.ShouldDispatch.Should().BeFalse(mutation.Name);
            decision.Outcome.Should().Be(
                NyxIdChatTransitionOutcome.Rejected,
                mutation.Name);
            decision.ReasonCode.Should().Be(
                NyxIdChatBrowserActions.ActionContinuationInvalid,
                mutation.Name);
        }
    }

    [Fact]
    public void ForgedKeyRotateInputDigest_ShouldNotCompleteKeyCreateRequest()
    {
        var state = AuthorizationWaitingState();
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
        var blocked = NyxIdChatBrowserActions.RequestAuthorization(
            state,
            signal,
            LeastScopeRegistry(),
            Now).State;
        var actionRequestId = blocked.PendingActions.Single().ActionRequestId;
        var continuation = ContinueCommand(
            actionRequestId,
            NyxIdChatActionDisposition.Completed);
        continuation.Actions[0].Resource = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
        };
        var admitted = NyxIdChatBrowserActions.Continue(blocked, continuation, Now);
        var forgedInput = admitted.NextCommand!.ActionPostcondition.Clone();
        forgedInput.Action = NyxIdAssistantActionKind.KeyRotate;
        forgedInput.Params = new NyxIdAssistantActionParams
        {
            KeyRotate = new NyxIdKeyRotateParams { KeyId = "key-alpha" },
        };
        var forgedResult = new NyxIdChatOperationResultSignal
        {
            Key = admitted.NextCommand.Key.Clone(),
            ActionPostcondition = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = actionRequestId,
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = true,
                Resource = continuation.Actions[0].Resource.Clone(),
                VerificationInputSha256 =
                    NyxIdChatActionPostconditionEvidence.ComputeVerificationInputSha256(
                        forgedInput),
            },
        };

        var decision = NyxIdChatBrowserActions.ReconcilePostcondition(
            admitted.State,
            forgedResult,
            Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationInvalid);
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
                VerificationInputSha256 =
                    NyxIdChatActionPostconditionEvidence.ComputeVerificationInputSha256(
                        admitted.NextCommand.ActionPostcondition),
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
                    VerificationInputSha256 =
                        NyxIdChatActionPostconditionEvidence.ComputeVerificationInputSha256(
                            admitted.NextCommand.ActionPostcondition),
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

    [Theory]
    [InlineData(NyxIdAssistantActionKind.ServiceConnect)]
    [InlineData(NyxIdAssistantActionKind.KeyCreate)]
    [InlineData(NyxIdAssistantActionKind.KeyRotate)]
    public void LegacyRequestedPostcondition_ShouldUpgradeBindingBeforeRedispatch(
        NyxIdAssistantActionKind action)
    {
        var admitted = AdmittedAction(action);
        var legacy = WithLegacyPostconditionBinding(admitted.State);

        var decision = NyxIdChatBrowserActionPostconditionRecovery.BuildRequestedRecovery(
            legacy,
            admitted.NextCommand!.Key,
            Later);

        decision.Status.Should().Be(
            NyxIdChatBrowserActionPostconditionRecoveryStatus.UpgradeRequired);
        decision.Action.Should().Be(action);
        decision.Command.Should().NotBeNull();
        decision.Command!.Key.Should().BeEquivalentTo(admitted.NextCommand.Key);
        var source = decision.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Postcondition).Source.Postcondition;
        source.Action.Should().Be(action);
        source.VerificationInputBinding.Should().Be(
            NyxIdChatVerificationInputBinding.Sha256V1);
    }

    [Theory]
    [InlineData(NyxIdAssistantActionKind.ServiceConnect)]
    [InlineData(NyxIdAssistantActionKind.KeyCreate)]
    [InlineData(NyxIdAssistantActionKind.KeyRotate)]
    public void LegacyDigestlessResult_ShouldRedispatchFreshBoundGeneration(
        NyxIdAssistantActionKind action)
    {
        var admitted = AdmittedAction(action);
        var legacy = WithLegacyPostconditionBinding(admitted.State);
        legacy.PendingOperationDeliveryProbe = admitted.NextCommand!.Key.Clone();
        var digestless = VerifiedPostconditionForAction(
            admitted.NextCommand!,
            action,
            includeDigest: false);

        var redispatch = NyxIdChatBrowserActionPostconditionRecovery.BuildFreshRedispatch(
            legacy,
            digestless,
            Later);

        redispatch.Status.Should().Be(
            NyxIdChatBrowserActionPostconditionRecoveryStatus.UpgradeRequired);
        redispatch.Action.Should().Be(action);
        redispatch.Command.Should().NotBeNull();
        redispatch.Command!.Key.OperationGeneration.Should().Be(2);
        redispatch.Command.Key.OperationId.Should().NotBe(
            admitted.NextCommand!.Key.OperationId);
        redispatch.State.ActiveTask.ActiveOperationId.Should().Be(
            redispatch.Command.Key.OperationId);
        redispatch.State.PendingOperationDeliveryProbe.Should().BeNull();

        var mismatchedProbe = legacy.Clone();
        mismatchedProbe.PendingOperationDeliveryProbe!.OperationId = "operation-other";
        var preserved = NyxIdChatBrowserActionPostconditionRecovery.BuildFreshRedispatch(
            mismatchedProbe,
            digestless,
            Later);
        preserved.State.PendingOperationDeliveryProbe.Should().BeEquivalentTo(
            mismatchedProbe.PendingOperationDeliveryProbe);

        var reconciled = NyxIdChatBrowserActions.ReconcilePostcondition(
            redispatch.State,
            VerifiedPostconditionForAction(redispatch.Command, action, includeDigest: true),
            Later);

        reconciled.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        reconciled.ShouldDispatch.Should().BeTrue();
        reconciled.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm);
        NyxIdChatBrowserActionPostconditionRecovery.HasVerifiedCompletedBoundState(
                reconciled.State,
                reconciled.NextCommand.Key)
            .Should().BeTrue("a verified generation-2 redispatch remains valid completion evidence");
    }

    [Fact]
    public void BoundGenerationOneDigestlessResult_ShouldFenceAndRedispatchFreshGeneration()
    {
        var admitted = AdmittedAction(NyxIdAssistantActionKind.ServiceConnect);
        var digestless = VerifiedPostconditionForAction(
            admitted.NextCommand!,
            NyxIdAssistantActionKind.ServiceConnect,
            includeDigest: false);
        var fenced = admitted.State.Clone();
        fenced.ResultAcknowledgementFences.Add(
            new NyxIdChatOperationResultAcknowledgementFence
            {
                Key = digestless.Key.Clone(),
                ResultSha256 = ByteString.CopyFrom(
                    new byte[NyxIdChatActionPostconditionEvidence.Sha256Length]),
            });

        var decision = NyxIdChatBrowserActionPostconditionRecovery.BuildFreshRedispatch(
            fenced,
            digestless,
            Later);

        decision.Status.Should().Be(
            NyxIdChatBrowserActionPostconditionRecoveryStatus.UpgradeRequired);
        decision.Command.Should().NotBeNull();
        decision.Command!.Key.OperationGeneration.Should().Be(2);
        decision.State.ResultAcknowledgementFences.Should().ContainSingle();
    }

    [Fact]
    public void GenerationTwoDigestlessResult_ShouldFailClosedInsteadOfRedispatching()
    {
        var admitted = AdmittedAction(NyxIdAssistantActionKind.ServiceConnect);
        var first = NyxIdChatBrowserActionPostconditionRecovery.BuildFreshRedispatch(
            admitted.State,
            VerifiedPostconditionForAction(
                admitted.NextCommand!,
                NyxIdAssistantActionKind.ServiceConnect,
                includeDigest: false),
            Later);
        var digestlessGenerationTwo = VerifiedPostconditionForAction(
            first.Command!,
            NyxIdAssistantActionKind.ServiceConnect,
            includeDigest: false);

        var decision = NyxIdChatBrowserActionPostconditionRecovery.BuildFreshRedispatch(
            first.State,
            digestlessGenerationTwo,
            Later);

        decision.Status.Should().Be(
            NyxIdChatBrowserActionPostconditionRecoveryStatus.Invalid);
        decision.Command.Should().BeNull();
        decision.State.Should().BeEquivalentTo(first.State);
    }

    [Fact]
    public void CompletedGenerationTwoLegacyPostcondition_ShouldFailClosed()
    {
        var admitted = AdmittedAction(NyxIdAssistantActionKind.ServiceConnect);
        var redispatch = NyxIdChatBrowserActionPostconditionRecovery.BuildFreshRedispatch(
            admitted.State,
            VerifiedPostconditionForAction(
                admitted.NextCommand!,
                NyxIdAssistantActionKind.ServiceConnect,
                includeDigest: false),
            Later);
        var completed = NyxIdChatBrowserActions.ReconcilePostcondition(
            redispatch.State,
            VerifiedPostconditionForAction(
                redispatch.Command!,
                NyxIdAssistantActionKind.ServiceConnect,
                includeDigest: true),
            Later);
        var legacy = WithLegacyPostconditionBinding(completed.State);

        var decision = NyxIdChatBrowserActionPostconditionRecovery.BuildCompletedUpgrade(
            legacy,
            completed.NextCommand!.Key,
            Later);

        decision.Status.Should().Be(
            NyxIdChatBrowserActionPostconditionRecoveryStatus.Invalid);
        decision.Command.Should().BeNull();
        decision.State.Should().BeEquivalentTo(legacy);
    }

    [Fact]
    public void LegacyCompletedPostconditionWithMatchingDigest_ShouldOnlyUpgradeMarker()
    {
        var completed = CompletedAction(NyxIdAssistantActionKind.ServiceConnect);
        var legacy = WithLegacyPostconditionBinding(completed.State);
        var durableDigest = legacy.RecentActions.Single().PostconditionResult
            .VerificationInputSha256;

        var decision = NyxIdChatBrowserActionPostconditionRecovery.BuildCompletedUpgrade(
            legacy,
            completed.NextCommand!.Key,
            Later);

        decision.Status.Should().Be(
            NyxIdChatBrowserActionPostconditionRecoveryStatus.CompletedUpgradeRequired);
        decision.Command.Should().BeNull();
        decision.State.RecentActions.Single().PostconditionResult.VerificationInputSha256
            .Should().Equal(durableDigest);
        NyxIdChatBrowserActionPostconditionRecovery.HasVerifiedCompletedBoundState(
                decision.State,
                completed.NextCommand.Key)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedPostconditionWithoutMatchingDigest_ShouldFailClosed(bool missing)
    {
        var completed = CompletedAction(NyxIdAssistantActionKind.ServiceConnect);
        var invalid = WithLegacyPostconditionBinding(completed.State);
        invalid.RecentActions.Single().PostconditionResult.VerificationInputSha256 = missing
            ? ByteString.Empty
            : ByteString.CopyFrom(new byte[NyxIdChatActionPostconditionEvidence.Sha256Length]);

        var decision = NyxIdChatBrowserActionPostconditionRecovery.BuildCompletedUpgrade(
            invalid,
            completed.NextCommand!.Key,
            Later);

        decision.Status.Should().Be(
            NyxIdChatBrowserActionPostconditionRecoveryStatus.Invalid);
        decision.Command.Should().BeNull();
    }

    [Fact]
    public void CompletedPostconditionCorrelation_ShouldRejectTamperedSourceToolLinkage()
    {
        var completed = CompletedAction(NyxIdAssistantActionKind.ServiceConnect);
        NyxIdChatActionContinuationCorrelation.TryMatch(
                completed.State,
                completed.State.ActiveTask,
                completed.State.ActiveTurn,
                completed.NextCommand!.Key,
                out _)
            .Should().BeTrue();

        var mutations = new (string Name, Action<NyxIdChatConversationGAgentState> Apply)[]
        {
            ("duplicate source step", state =>
                state.ActiveTask.Steps.Add(SourceTool(state).Clone())),
            ("request source id", state =>
                state.RecentActions.Single().SourceToolStepId = "step-tool-other"),
            ("source key step id", state =>
                SourceTool(state).Operation.Key.StepId = "step-tool-other"),
            ("source status", state =>
                SourceTool(state).Status = NyxIdChatStepStatus.Waiting),
            ("source effect", state =>
                SourceTool(state).ExternalEffect = NyxIdChatEffectEvidence.NotStarted),
            ("source phase", state =>
                SourceTool(state).Operation.Phase = NyxIdChatOperationPhase.Running),
            ("action dependency", state =>
                BrowserActionStep(state).DependsOn[0] = "step-tool-other"),
        };

        foreach (var (name, apply) in mutations)
        {
            var tampered = completed.State.Clone();
            apply(tampered);

            NyxIdChatActionContinuationCorrelation.TryMatch(
                    tampered,
                    tampered.ActiveTask,
                    tampered.ActiveTurn,
                    completed.NextCommand.Key,
                    out _)
                .Should().BeFalse(name);
        }
    }

    [Theory]
    [InlineData(NyxIdAssistantActionKind.ServiceConnect)]
    [InlineData(NyxIdAssistantActionKind.KeyCreate)]
    [InlineData(NyxIdAssistantActionKind.KeyRotate)]
    public void CompletedBoundPostcondition_ShouldResolveOriginalActionForLateResultFence(
        NyxIdAssistantActionKind action)
    {
        var completed = CompletedAction(action);
        var postconditionKey = completed.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Postcondition).Operation.Key;

        var resolved =
            NyxIdChatBrowserActionPostconditionRecovery.TryResolveVerifiedCompletedBoundAction(
                completed.State,
                postconditionKey,
                out var resolvedAction);

        resolved.Should().BeTrue();
        resolvedAction.Should().Be(action);
    }

    private static NyxIdChatConversationGAgentState BlockedActionState() =>
        NyxIdChatBrowserActions.RequestAuthorization(
            AuthorizationWaitingState(),
            AuthorizationRequiredSignal(AuthorizationWaitingState()),
            Registry(),
            Now).State;

    private static readonly Timestamp Later = Timestamp.FromDateTimeOffset(
        new DateTimeOffset(2026, 7, 25, 8, 1, 0, TimeSpan.Zero));

    internal static NyxIdChatBrowserActionDecision AdmittedAction(
        NyxIdAssistantActionKind action,
        string conversationActorId = "conversation-alpha")
    {
        var origin = AuthorizationWaitingStateWithPlannedContinuation(conversationActorId);
        var signal = AuthorizationRequiredSignal(origin);
        var registry = Registry();
        switch (action)
        {
            case NyxIdAssistantActionKind.ServiceConnect:
                break;
            case NyxIdAssistantActionKind.ServiceAccessReview:
                var sourceTool = origin.ActiveTask.Steps.Single(step =>
                    step.Kind == NyxIdChatStepKind.Tool);
                sourceTool.Source.Tool.ToolName = "nyxid_require_service";
                sourceTool.Source.Tool.AuthorizationReadiness =
                    new NyxIdChatAuthorizationReadinessInput
                    {
                        ToolName = "nyxid_require_service",
                        Params = new NyxIdChatRequireServiceParams
                        {
                            ServiceSlug = "service-alpha",
                            RequestedScopes = { "items:read" },
                        },
                    };
                signal.Tool.Receipt.ToolName = "nyxid_require_service";
                signal.Tool.Receipt.AuthorizationRequired.ReasonCode =
                    "USER_SERVICE_ACCESS_REQUIRED";
                signal.Tool.Receipt.AuthorizationRequired.UserServiceId = "us-alpha";
                signal.Tool.Receipt.AuthorizationRequired.ServiceSlug = "service-alpha";
                signal.Tool.Receipt.AuthorizationRequired.ResourceUri =
                    "https://service.invalid/api/v1/proxy/s/service-alpha";
                break;
            case NyxIdAssistantActionKind.KeyCreate:
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
                registry = LeastScopeRegistry();
                break;
            case NyxIdAssistantActionKind.KeyRotate:
                signal.Tool.Receipt.ToolName = "nyxid_request_key_rotate";
                signal.Tool.Receipt.AuthorizationRequired.ServiceSlug = string.Empty;
                signal.Tool.Receipt.AuthorizationRequired.RequestedScopes.Clear();
                signal.Tool.Receipt.AuthorizationRequired.KeyRotate =
                    new NyxIdKeyRotateActionRequirement { KeyId = "key-alpha" };
                registry = RotationRegistry();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }

        var requested = NyxIdChatBrowserActions.RequestAuthorization(
            origin,
            signal,
            registry,
            Now);
        var continuation = ContinueCommand(
            requested.Request.ActionRequestId,
            NyxIdChatActionDisposition.Completed,
            conversationActorId);
        continuation.Actions[0].Resource = VerifiedResource(action);
        return NyxIdChatBrowserActions.Continue(requested.State, continuation, Now);
    }

    internal static NyxIdChatBrowserActionDecision CompletedAction(
        NyxIdAssistantActionKind action,
        string conversationActorId = "conversation-alpha")
    {
        var admitted = AdmittedAction(action, conversationActorId);
        return NyxIdChatBrowserActions.ReconcilePostcondition(
            admitted.State,
            VerifiedPostconditionForAction(admitted.NextCommand!, action, includeDigest: true),
            Now);
    }

    private static NyxIdChatConversationGAgentState WithLegacyPostconditionBinding(
        NyxIdChatConversationGAgentState source)
    {
        var legacy = source.Clone();
        var postcondition = legacy.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Postcondition);
        postcondition.Source.Postcondition.Action = NyxIdAssistantActionKind.Unspecified;
        postcondition.Source.Postcondition.VerificationInputBinding =
            NyxIdChatVerificationInputBinding.Unspecified;
        return legacy;
    }

    private static NyxIdChatOperationResultSignal VerifiedPostconditionForAction(
        NyxIdChatOperationDispatchCommand command,
        NyxIdAssistantActionKind action,
        bool includeDigest) =>
        new()
        {
            Key = command.Key.Clone(),
            ActionPostcondition = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = command.ActionPostcondition.ActionRequestId,
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = true,
                Resource = VerifiedResource(action),
                VerificationInputSha256 = includeDigest
                    ? NyxIdChatActionPostconditionEvidence.ComputeVerificationInputSha256(
                        command.ActionPostcondition)
                    : ByteString.Empty,
            },
        };

    private static NyxIdChatSafeResourceRef VerifiedResource(
        NyxIdAssistantActionKind action) =>
        action switch
        {
            NyxIdAssistantActionKind.ServiceConnect => new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef
                {
                    UserServiceId = "service-alpha",
                },
            },
            NyxIdAssistantActionKind.ServiceAccessReview => new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef
                {
                    UserServiceId = "us-alpha",
                },
            },
            NyxIdAssistantActionKind.KeyCreate or NyxIdAssistantActionKind.KeyRotate =>
                new NyxIdChatSafeResourceRef
                {
                    Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
                },
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };

    private static NyxIdChatTaskStepState SourceTool(
        NyxIdChatConversationGAgentState state) =>
        state.ActiveTask.Steps.Single(step => string.Equals(
            step.StepId,
            state.RecentActions.Single().SourceToolStepId,
            StringComparison.Ordinal));

    private static NyxIdChatTaskStepState BrowserActionStep(
        NyxIdChatConversationGAgentState state) =>
        state.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.BrowserAction &&
            string.Equals(
                step.ActionRequestId,
                state.RecentActions.Single().ActionRequestId,
                StringComparison.Ordinal));

    private static NyxIdChatOperationResultSignal VerifiedPostcondition(
        NyxIdChatOperationDispatchCommand command,
        string actionRequestId) =>
        new()
        {
            Key = command.Key.Clone(),
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
                VerificationInputSha256 =
                    NyxIdChatActionPostconditionEvidence.ComputeVerificationInputSha256(
                        command.ActionPostcondition),
            },
        };

    private static NyxIdChatConversationGAgentState BlockedActionStateWithTwoRequests()
    {
        var state = BlockedActionState();
        var second = state.PendingActions.Single().Clone();
        second.ActionRequestId = "action-beta";
        second.StepId = "step-action-beta";
        second.Params.CatalogServiceConnect.ServiceSlug = "api-slack";
        return NyxIdChatBrowserActions.CommitRequest(state, second, Now).State;
    }

    private static NyxIdChatConversationGAgentState AuthorizationWaitingState(
        string conversationActorId = "conversation-alpha")
    {
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = conversationActorId,
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
            ConversationActorId = conversationActorId,
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

    private static NyxIdChatConversationGAgentState AuthorizationWaitingStateWithPlannedContinuation(
        string conversationActorId = "conversation-alpha")
    {
        var state = AuthorizationWaitingState(conversationActorId);
        state.AgentProfile = new AgentProfileSnapshot
        {
            ProfileId = "profile-alpha",
            ProfileVersion = "profile-v1",
            PolicyRevision = "policy-v1",
        };
        state.ActiveTurn.Prompt =
            "retrieve one issue that is assigned to me via my github account.";
        state.ActiveTurn.Intent = NyxIdChatTurnIntent.Unspecified;
        state.ActiveTurn.AgentProfileTurnAuthority = new AgentProfileTurnAuthorityState
        {
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
            AuthorityCeilingToolNames = { "nyxid_catalog", "nyxid_require_service" },
        };
        state.LatestTurn = state.ActiveTurn.Clone();
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = "step-llm-after-readiness",
            Order = 2,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Planned,
            Required = true,
            Description = "Communicate the typed read result.",
            Source = new NyxIdChatStepSource
            {
                Llm = new NyxIdChatLLMStepSource(),
            },
            DependsOn = { "step-tool-alpha" },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            AddedBy = NyxIdChatStepAddedBy.Replan,
            Operation = new NyxIdChatOperationState
            {
                Key = new NyxIdChatOperationKey
                {
                    ConversationActorId = conversationActorId,
                    TurnId = "turn-alpha",
                    TaskId = "task-alpha",
                    StepId = "step-llm-after-readiness",
                    OperationId = "operation-llm-after-readiness",
                    OperationGeneration = 1,
                },
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Requested,
                RequestedAt = Now.Clone(),
            },
            UpdatedAt = Now.Clone(),
        });
        return state;
    }

    private static NyxIdChatOperationResultSignal AuthorizationRequiredSignal(
        NyxIdChatConversationGAgentState state) => new()
    {
        Key = state.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool).Operation.Key.Clone(),
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
        NyxIdChatActionDisposition disposition,
        string conversationActorId = "conversation-alpha")
    {
        var command = new NyxIdChatActionContinueCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = conversationActorId,
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
        manifest["revision"] = NyxIdAssistantActionRegistry.KeyRotationRegistryRevision;
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

    private static NyxIdAssistantActionRegistry ReauthorizeRegistry()
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
        manifest["actions"]!.AsArray().Add(JsonNode.Parse("""
            {
              "action": "service.reauthorize",
              "description": "Reauthorize a connected service.",
              "params_schema": {
                "type": "object",
                "additionalProperties": false,
                "required": ["userServiceId", "requestedScopes"],
                "properties": {
                  "userServiceId": {"type": "string"},
                  "requestedScopes": {"type": "array", "items": {"type": "string"}}
                }
              },
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            }
            """));
        return NyxIdAssistantActionRegistry.Load(manifest.ToJsonString());
    }

    private static NyxIdChatOperationResultSignal ServiceReauthorizeSignal(
        NyxIdChatConversationGAgentState state)
    {
        var signal = AuthorizationRequiredSignal(state);
        signal.Tool.Receipt.ToolName = "nyxid_request_service_reauthorize";
        signal.Tool.Receipt.ErrorCode = "NYXID_SERVICE_REAUTHORIZATION_REQUIRED";
        signal.Tool.Receipt.AuthorizationRequired.ServiceSlug = string.Empty;
        signal.Tool.Receipt.AuthorizationRequired.ReasonCode =
            "NYXID_SERVICE_REAUTHORIZATION_REQUIRED";
        signal.Tool.Receipt.AuthorizationRequired.RequestedScopes.Clear();
        signal.Tool.Receipt.AuthorizationRequired.ServiceReauthorize =
            new NyxIdServiceReauthorizeActionRequirement
            {
                UserServiceId = "service-alpha",
                RequestedScopes = { "repo", "read:org" },
            };
        return signal;
    }

    private static NyxIdChatConversationGAgentState DormantServiceReauthorizeState() =>
        AsDormantServiceReauthorize(BlockedActionState());

    private static NyxIdChatConversationGAgentState AsDormantServiceReauthorize(
        NyxIdChatConversationGAgentState source,
        string? actionRequestId = null)
    {
        var state = source.Clone();
        var request = actionRequestId is null
            ? state.PendingActions.Single()
            : state.PendingActions.Single(action => string.Equals(
                action.ActionRequestId,
                actionRequestId,
                StringComparison.Ordinal));
        request.RegistryRevision = NyxIdAssistantActionRegistry.SupportedRegistryRevision;
        request.Action = NyxIdAssistantActionKind.ServiceReauthorize;
        request.Params = new NyxIdAssistantActionParams
        {
            ServiceReauthorize = new NyxIdServiceReauthorizeParams
            {
                UserServiceId = "service-alpha",
                RequestedScopes = { "repo", "read:org" },
            },
        };
        var actionStep = state.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.BrowserAction &&
            step.ActionRequestId == request.ActionRequestId);
        actionStep.Source.BrowserAction.Action = NyxIdAssistantActionKind.ServiceReauthorize;
        return state;
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
