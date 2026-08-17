using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
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
    public void KeyCompletedReport_ShouldDispatchFreshToolContextWithoutPersistingItAndRecoverCredentialFree()
    {
        const string freshToken = "fresh-key-continuation-token";
        var blocked = BlockedKeyActionState(NyxIdAssistantActionKind.KeyCreate);
        var actionRequestId = blocked.PendingActions.Single().ActionRequestId;
        var command = ContinueCommand(
            actionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.Actions[0].Resource = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
        };
        command.ToolContext = BrowserToolContext(freshToken, command.CommandId);

        var decision = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeTrue();
        decision.NextCommand.Should().NotBeNull();
        decision.NextCommand!.ActionPostcondition.ActionRequestId.Should().Be(actionRequestId);
        decision.NextCommand.ActionPostcondition.Action.Should().Be(
            NyxIdAssistantActionKind.KeyCreate);
        decision.NextCommand.ActionPostcondition.ResourceHint.Key.KeyId.Should().Be("key-alpha");
        decision.NextCommand.ActionPostcondition.ToolContext.Should()
            .BeEquivalentTo(command.ToolContext);
        AssertToolContextNotPersisted(decision.State, decision.Admission, freshToken);

        var restarted = NyxIdChatConversationGAgentState.Parser.ParseFrom(
            decision.State.ToByteArray());
        var recovery = NyxIdChatBrowserActions.TryBuildRecoveryDispatch(
            restarted,
            decision.NextCommand.Key);

        recovery.Should().NotBeNull();
        recovery!.Key.Should().BeEquivalentTo(decision.NextCommand.Key);
        recovery.ActionPostcondition.ActionRequestId.Should().Be(actionRequestId);
        recovery.ActionPostcondition.Action.Should().Be(NyxIdAssistantActionKind.KeyCreate);
        recovery.ActionPostcondition.ReportedDisposition.Should().Be(
            NyxIdChatActionDisposition.Completed);
        recovery.ActionPostcondition.ResourceHint.Key.KeyId.Should().Be("key-alpha");
        recovery.ActionPostcondition.ToolContext.Should().BeNull();
    }

    [Fact]
    public void KeyRecoveryDispatch_ShouldFallBackToCompletedRequestReport()
    {
        var blocked = BlockedKeyActionState(NyxIdAssistantActionKind.KeyRotate);
        var actionRequestId = blocked.PendingActions.Single().ActionRequestId;
        var command = ContinueCommand(
            actionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.Actions[0].Resource = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = "key-beta" },
        };
        command.ToolContext = BrowserToolContext("initial-rotate-token", command.CommandId);
        var admitted = NyxIdChatBrowserActions.Continue(blocked, command, Now);
        var restarted = NyxIdChatConversationGAgentState.Parser.ParseFrom(
            admitted.State.ToByteArray());
        restarted.ContinuationAdmission.ActionReports.Clear();

        var recovery = NyxIdChatBrowserActions.TryBuildRecoveryDispatch(
            restarted,
            admitted.NextCommand!.Key);

        restarted.PendingActions.Single().Reports.Should().ContainSingle(report =>
            report.ActionRequestId == actionRequestId &&
            report.Disposition == NyxIdChatActionDisposition.Completed &&
            report.Resource.Key.KeyId == "key-beta");
        recovery.Should().NotBeNull();
        recovery!.Key.Should().BeEquivalentTo(admitted.NextCommand.Key);
        recovery.ActionPostcondition.ActionRequestId.Should().Be(actionRequestId);
        recovery.ActionPostcondition.Action.Should().Be(NyxIdAssistantActionKind.KeyRotate);
        recovery.ActionPostcondition.ResourceHint.Key.KeyId.Should().Be("key-beta");
        recovery.ActionPostcondition.ToolContext.Should().BeNull();
    }

    [Fact]
    public void KeyStateChangeWakeWithoutCompletedReport_ShouldFailClosed()
    {
        var blocked = BlockedKeyActionState(NyxIdAssistantActionKind.KeyCreate);
        var wake = KeyStateChangeWake("missing-report");

        var decision = NyxIdChatBrowserActions.Continue(blocked, wake, Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatBrowserActions.ActionContinuationInvalid);
        decision.State.Should().BeEquivalentTo(blocked);
    }

    [Fact]
    public void AuthenticatedKeyStateChangeWake_AfterFailedPostcondition_ShouldRenewGeneration()
    {
        var failed = FailedKeyPostconditionState();
        var retryAt = Timestamp.FromDateTimeOffset(
            Now.ToDateTimeOffset().AddMinutes(1));
        var wake = KeyStateChangeWake("retry");
        var freshToken = wake.ToolContext.Credentials.NyxIdAccessToken;

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
        request.Reports.Should().ContainSingle(report =>
            report.Disposition == NyxIdChatActionDisposition.Completed &&
            report.Resource.Key.KeyId == failed.ExpectedResourceKeyId);

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

        decision.NextCommand.Should().NotBeNull();
        decision.NextCommand!.Key.Should().BeEquivalentTo(operation.Key);
        decision.NextCommand.ActionPostcondition.ActionRequestId.Should()
            .Be(failed.ActionRequestId);
        decision.NextCommand.ActionPostcondition.ResourceHint.Key.KeyId.Should()
            .Be(failed.ExpectedResourceKeyId);
        decision.NextCommand.ActionPostcondition.ToolContext.Should()
            .BeEquivalentTo(wake.ToolContext);
        AssertToolContextNotPersisted(decision.State, decision.Admission, freshToken);
    }

    [Theory]
    [InlineData(NyxIdAssistantActionKind.KeyCreate, "key-alpha")]
    [InlineData(NyxIdAssistantActionKind.KeyRotate, "key-beta")]
    public async Task AuthenticatedKeyStateChangeWake_ShouldPreserveExactEvidencePredicate(
        NyxIdAssistantActionKind action,
        string expectedKeyId)
    {
        var failed = FailedKeyPostconditionState(action);
        var wake = KeyStateChangeWake($"retry-{action}");

        var decision = NyxIdChatBrowserActions.Continue(failed.State, wake, Now);

        decision.NextCommand.Should().NotBeNull();
        var dispatch = decision.NextCommand!;
        dispatch.Key.OperationGeneration.Should().Be(2);
        dispatch.Key.StepId.Should().Be(failed.PreviousKey.StepId);
        dispatch.ActionPostcondition.ActionRequestId.Should().Be(failed.ActionRequestId);
        dispatch.ActionPostcondition.Action.Should().Be(action);
        dispatch.ActionPostcondition.ReportedDisposition.Should().Be(
            NyxIdChatActionDisposition.Completed);
        dispatch.ActionPostcondition.ResourceHint.Key.KeyId.Should().Be(expectedKeyId);
        dispatch.ActionPostcondition.ToolContext.Should().BeEquivalentTo(wake.ToolContext);

        var recovery = NyxIdChatBrowserActions.TryBuildRecoveryDispatch(
            decision.State,
            dispatch.Key);
        recovery.Should().NotBeNull();
        recovery!.Key.Should().BeEquivalentTo(dispatch.Key);
        recovery.ActionPostcondition.Action.Should().Be(action);
        recovery.ActionPostcondition.ActionRequestId.Should().Be(failed.ActionRequestId);
        recovery.ActionPostcondition.ResourceHint.Key.KeyId.Should().Be(expectedKeyId);
        recovery.ActionPostcondition.ToolContext.Should().BeNull();

        var evidence = VerifiableKeyEvidence(action, expectedKeyId);
        var reads = new RecordingKeyEvidenceReadPort(evidence);
        var verifier = new NyxIdActionPostconditionPort(
            catalogQueryPort: null,
            reads,
            new FixedTestTimeProvider(Now.ToDateTimeOffset().AddMinutes(2)));

        var result = await verifier.VerifyAsync(
            dispatch.ActionPostcondition,
            dispatch.ActionPostcondition.ToolContext);

        reads.KeyIds.Should().ContainSingle().Which.Should().Be(expectedKeyId);
        result.Verified.Should().BeTrue();
        result.Resource.Key.KeyId.Should().Be(expectedKeyId);
    }

    [Fact]
    public void ExactKeyStateChangeWakeReplay_ShouldNotCreateAnotherGeneration()
    {
        var failed = FailedKeyPostconditionState(NyxIdAssistantActionKind.KeyRotate);
        var wake = KeyStateChangeWake("retry-replay");
        var first = NyxIdChatBrowserActions.Continue(failed.State, wake, Now);
        var replayWake = wake.Clone();
        replayWake.ToolContext = BrowserToolContext(
            "fresh-replay-token",
            replayWake.CommandId);

        var replay = NyxIdChatBrowserActions.Continue(
            first.State,
            replayWake,
            Now);

        first.NextCommand!.Key.OperationGeneration.Should().Be(2);
        replay.ShouldCommit.Should().BeFalse();
        replay.ShouldDispatch.Should().BeTrue();
        replay.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);
        replay.NextCommand.Should().NotBeNull();
        replay.NextCommand!.Key.Should().BeEquivalentTo(first.NextCommand.Key);
        replay.NextCommand.Key.OperationGeneration.Should().Be(2);
        replay.NextCommand.Key.OperationId.Should().Be(first.NextCommand.Key.OperationId);
        replay.NextCommand.ActionPostcondition.ActionRequestId.Should()
            .Be(failed.ActionRequestId);
        replay.NextCommand.ActionPostcondition.ResourceHint.Key.KeyId.Should()
            .Be(failed.ExpectedResourceKeyId);
        replay.NextCommand.ActionPostcondition.ToolContext.Should()
            .BeEquivalentTo(replayWake.ToolContext);
        AssertToolContextNotPersisted(
            replay.State,
            replay.Admission,
            replayWake.ToolContext.Credentials.NyxIdAccessToken);
    }

    [Fact]
    public void VerifiedKeyActionExactReplay_ShouldRemainIdempotentWithoutDispatch()
    {
        var blocked = BlockedKeyActionState(NyxIdAssistantActionKind.KeyCreate);
        var actionRequestId = blocked.PendingActions.Single().ActionRequestId;
        var command = ContinueCommand(
            actionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.Actions[0].Resource = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
        };
        command.ToolContext = BrowserToolContext("verified-key-token", command.CommandId);
        var admitted = NyxIdChatBrowserActions.Continue(blocked, command, Now);
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
                        Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
                    },
                },
            },
            Now);

        var replay = NyxIdChatBrowserActions.Continue(
            reconciled.State,
            command.Clone(),
            Timestamp.FromDateTimeOffset(Now.ToDateTimeOffset().AddMinutes(30)));

        reconciled.State.PendingActions.Should().BeEmpty();
        reconciled.State.RecentActions.Should().ContainSingle(action =>
            action.ActionRequestId == actionRequestId &&
            action.PostconditionResult.Verified);
        replay.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);
        replay.ShouldCommit.Should().BeFalse();
        replay.ShouldDispatch.Should().BeFalse();
        replay.NextCommand.Should().BeNull();
        replay.State.Should().BeEquivalentTo(reconciled.State);
    }

    [Fact]
    public void SupersededKeyGenerationResult_ShouldNotCompletePendingAction()
    {
        var failed = FailedKeyPostconditionState(NyxIdAssistantActionKind.KeyRotate);
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
                    Key = new NyxIdChatKeyRef { KeyId = failed.ExpectedResourceKeyId },
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
            VerifiedPostcondition(admitted.NextCommand!.Key, actionRequestId),
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
        second.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        second.ShouldCommit.Should().BeTrue();
        second.ShouldDispatch.Should().BeTrue();
        second.Admission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Accepted);
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
    public void ExactEmptyWakeReplayWithoutPendingActions_ShouldRemainIdempotent()
    {
        var terminal = AuthorizationWaitingState();
        terminal.ActiveTurn.Status = NyxIdChatTurnStatus.Succeeded;
        terminal.LatestTurn = terminal.ActiveTurn.Clone();
        terminal.ActiveTask.Status = NyxIdChatTaskStatus.Succeeded;
        terminal.ActiveTask.ActiveStepId = string.Empty;
        terminal.ActiveTask.ActiveOperationId = string.Empty;
        terminal.ActiveTask.Steps.Clear();
        var wake = KeyStateChangeWake("terminal-replay");
        var first = NyxIdChatBrowserActions.Continue(terminal, wake, Now);
        var replayAt = Timestamp.FromDateTimeOffset(
            Now.ToDateTimeOffset().AddMinutes(30));

        var replay = NyxIdChatBrowserActions.Continue(
            first.State,
            wake.Clone(),
            replayAt);

        replay.ShouldCommit.Should().BeFalse();
        replay.ShouldDispatch.Should().BeFalse();
        replay.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);
        replay.State.Should().BeEquivalentTo(first.State);
    }

    [Fact]
    public void DifferentClientWithSamePersistedReport_ShouldBeIdempotentBeforeActiveTurnCheck()
    {
        var blocked = BlockedActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        var first = NyxIdChatBrowserActions.Continue(blocked, command, Now);
        var retry = command.Clone();
        retry.ClientRequestId = "client-action-beta";
        retry.CommandId = "command-action-beta";
        retry.CorrelationId = "correlation-action-beta";
        retry.ContinuationTurnId = "turn-action-beta";
        retry.OriginTurnId = " turn-alpha ";
        retry.Actions[0].ActionRequestId = $" {retry.Actions[0].ActionRequestId} ";
        retry.Actions[0].OriginTurnId = " turn-alpha ";

        var decision = NyxIdChatBrowserActions.Continue(first.State, retry, Now);

        first.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        first.State.PendingActions.Single().Reports.Should().ContainSingle();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);
        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationAccepted);
        decision.State.Should().BeEquivalentTo(first.State);
    }

    [Fact]
    public void DifferentClientWithSamePersistedReportFromForeignOwner_ShouldFailClosed()
    {
        var blocked = BlockedActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        var first = NyxIdChatBrowserActions.Continue(blocked, command, Now);
        var retry = command.Clone();
        retry.ClientRequestId = "client-action-beta";
        retry.CommandId = "command-action-beta";
        retry.CorrelationId = "correlation-action-beta";
        retry.ContinuationTurnId = "turn-action-beta";
        retry.OwnerSubject = "owner-other";

        var decision = NyxIdChatBrowserActions.Continue(first.State, retry, Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationInvalid);
        decision.State.Should().BeEquivalentTo(first.State);
    }

    [Fact]
    public void DifferentClientWithConflictingRecentReport_ShouldFailClosed()
    {
        var blocked = BlockedActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        var first = NyxIdChatBrowserActions.Continue(blocked, command, Now);
        var reconciled = NyxIdChatBrowserActions.ReconcilePostcondition(
            first.State,
            VerifiedPostcondition(first.NextCommand!.Key, command.Actions[0].ActionRequestId),
            Now);
        var retry = command.Clone();
        retry.ClientRequestId = "client-action-beta";
        retry.CommandId = "command-action-beta";
        retry.CorrelationId = "correlation-action-beta";
        retry.ContinuationTurnId = "turn-action-beta";
        retry.Actions[0].Disposition = NyxIdChatActionDisposition.Declined;

        var decision = NyxIdChatBrowserActions.Continue(reconciled.State, retry, Now);

        reconciled.State.PendingActions.Should().BeEmpty();
        reconciled.State.RecentActions.Should().ContainSingle(action =>
            action.ActionRequestId == command.Actions[0].ActionRequestId &&
            action.Reports.Count == 1);
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationConflict);
        decision.State.Should().BeEquivalentTo(reconciled.State);
    }

    [Fact]
    public void DifferentClientWithPersistedReportUsingWrongResourceVariant_ShouldBeInvalid()
    {
        var blocked = BlockedActionState();
        var command = ContinueCommand(
            blocked.PendingActions.Single().ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        var first = NyxIdChatBrowserActions.Continue(blocked, command, Now);
        var retry = command.Clone();
        retry.ClientRequestId = "client-action-beta";
        retry.CommandId = "command-action-beta";
        retry.CorrelationId = "correlation-action-beta";
        retry.ContinuationTurnId = "turn-action-beta";
        retry.Actions[0].Resource = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
        };

        var decision = NyxIdChatBrowserActions.Continue(first.State, retry, Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationInvalid);
        decision.State.Should().BeEquivalentTo(first.State);
    }

    [Fact]
    public void DifferentClientWithPersistedAndFreshReports_ShouldFailClosed()
    {
        var blocked = BlockedActionStateWithTwoRequests();
        var reportedId = blocked.PendingActions[0].ActionRequestId;
        var freshId = blocked.PendingActions[1].ActionRequestId;
        var firstCommand = ContinueCommand(
            reportedId,
            NyxIdChatActionDisposition.Completed);
        var first = NyxIdChatBrowserActions.Continue(blocked, firstCommand, Now);
        var mixed = firstCommand.Clone();
        mixed.ClientRequestId = "client-action-beta";
        mixed.CommandId = "command-action-beta";
        mixed.CorrelationId = "correlation-action-beta";
        mixed.ContinuationTurnId = "turn-action-beta";
        mixed.Actions.Add(ActionReport(
            freshId,
            NyxIdChatActionDisposition.Completed));

        var decision = NyxIdChatBrowserActions.Continue(first.State, mixed, Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationConflict);
        decision.State.Should().BeEquivalentTo(first.State);
        decision.State.PendingActions.Single(action =>
                action.ActionRequestId == freshId)
            .Reports.Should().BeEmpty();
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

    [Fact]
    public void SameClientChangedReportFromServiceAdmissionToKeyAction_ShouldConflict()
    {
        var blocked = BlockedActionStateWithTwoRequests();
        var serviceRequest = blocked.PendingActions[0];
        var keyRequest = blocked.PendingActions[1];
        keyRequest.RegistryRevision = NyxIdAssistantActionRegistry.LeastScopeRegistryRevision;
        keyRequest.Action = NyxIdAssistantActionKind.KeyCreate;
        keyRequest.Params = new NyxIdAssistantActionParams
        {
            KeyCreate = new NyxIdKeyCreateParams
            {
                Name = "agent-alpha",
                Platform = "codex",
                AllowedServiceIds = { "service-alpha" },
            },
        };
        var firstCommand = ContinueCommand(
            serviceRequest.ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        var first = NyxIdChatBrowserActions.Continue(blocked, firstCommand, Now);
        var changed = firstCommand.Clone();
        changed.Actions.Clear();
        changed.Actions.Add(new NyxIdChatActionReport
        {
            ActionRequestId = keyRequest.ActionRequestId,
            OriginTurnId = keyRequest.OriginTurnId,
            Disposition = NyxIdChatActionDisposition.Completed,
            Resource = new NyxIdChatSafeResourceRef
            {
                Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
            },
        });

        var decision = NyxIdChatBrowserActions.Continue(first.State, changed, Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationConflict);
        decision.State.Should().BeEquivalentTo(first.State);
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
    public void OutOfOrderReports_ShouldNormalizeByActionIdentity()
    {
        var blocked = BlockedActionStateWithTwoRequests();
        var firstId = blocked.PendingActions[0].ActionRequestId;
        var secondId = blocked.PendingActions[1].ActionRequestId;
        var command = ContinueCommand(secondId, NyxIdChatActionDisposition.Completed);
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
        reorderedReplay.Actions.Add(ActionReport(firstId, NyxIdChatActionDisposition.Completed));
        reorderedReplay.Actions.Add(ActionReport(secondId, NyxIdChatActionDisposition.Completed));
        var replay = NyxIdChatBrowserActions.Continue(
            admitted.State,
            reorderedReplay,
            Now);

        replay.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);
        replay.ShouldCommit.Should().BeFalse();
        replay.NextCommand!.Key.Should().BeEquivalentTo(admitted.NextCommand!.Key);
    }

    [Fact]
    public void IdenticalDuplicateReport_ShouldFailClosedWithoutChangingState()
    {
        var blocked = BlockedActionState();
        var actionRequestId = blocked.PendingActions.Single().ActionRequestId;
        var command = ContinueCommand(
            actionRequestId,
            NyxIdChatActionDisposition.Completed);
        var duplicate = command.Actions.Single().Clone();
        duplicate.ActionRequestId = $" {duplicate.ActionRequestId} ";
        duplicate.OriginTurnId = $" {duplicate.OriginTurnId} ";
        command.Actions.Add(duplicate);

        var decision = NyxIdChatBrowserActions.Continue(blocked, command, Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationConflict);
        decision.State.Should().BeEquivalentTo(blocked);
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

    private static NyxIdChatConversationGAgentState BlockedKeyActionState(
        NyxIdAssistantActionKind action)
    {
        var state = BlockedActionState();
        var request = state.PendingActions.Single();
        request.RegistryRevision = action == NyxIdAssistantActionKind.KeyCreate
            ? NyxIdAssistantActionRegistry.LeastScopeRegistryRevision
            : NyxIdAssistantActionRegistry.SupportedRegistryRevision;
        request.Action = action;
        request.Params = action == NyxIdAssistantActionKind.KeyCreate
            ? new NyxIdAssistantActionParams
            {
                KeyCreate = new NyxIdKeyCreateParams
                {
                    Name = "agent-alpha",
                    Platform = "codex",
                    AllowedServiceIds = { "service-alpha" },
                },
            }
            : new NyxIdAssistantActionParams
            {
                KeyRotate = new NyxIdKeyRotateParams { KeyId = "key-alpha" },
            };
        state.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.BrowserAction)
            .Source.BrowserAction.Action = action;
        state.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.Postcondition)
            .Source.Postcondition.Check = action.ToString();
        return state;
    }

    private static NyxIdAgentApiKeyEvidence VerifiableKeyEvidence(
        NyxIdAssistantActionKind action,
        string keyId) =>
        new(
            keyId,
            "agent-alpha",
            ["proxy"],
            "codex",
            true,
            ["service-alpha"],
            false,
            [],
            false,
            Now.ToDateTimeOffset().AddMinutes(1),
            action == NyxIdAssistantActionKind.KeyRotate
                ? new NyxIdApiKeyVersionEvidence(
                    "key-alpha",
                    2,
                    Now.ToDateTimeOffset().AddMinutes(1))
                : null);

    private static (
        NyxIdChatConversationGAgentState State,
        NyxIdChatOperationKey PreviousKey,
        string ActionRequestId,
        string ActionStepId,
        string ExpectedResourceKeyId) FailedKeyPostconditionState(
        NyxIdAssistantActionKind action = NyxIdAssistantActionKind.KeyCreate)
    {
        var blocked = BlockedKeyActionState(action);
        var request = blocked.PendingActions.Single();
        var expectedResourceKeyId = action == NyxIdAssistantActionKind.KeyRotate
            ? "key-beta"
            : "key-alpha";
        var command = ContinueCommand(
            request.ActionRequestId,
            NyxIdChatActionDisposition.Completed);
        command.Actions[0].Resource = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = expectedResourceKeyId },
        };
        command.ToolContext = BrowserToolContext(
            "initial-key-postcondition-token",
            command.CommandId);
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
            Title = "Read back the key",
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
                    SafeMessage = "The key is not visible yet.",
                },
            },
            Now);
        return (
            failed.State,
            previousKey,
            request.ActionRequestId,
            request.StepId,
            expectedResourceKeyId);
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
        command.ToolContext = BrowserToolContext($"wake-token-{suffix}", command.CommandId);
        return command;
    }

    private static AgentToolExecutionContextPayload BrowserToolContext(
        string accessToken,
        string requestId) => new()
    {
        Request = new AgentToolRequestIdentityPayload
        {
            RequestId = requestId,
            IssuedAtUnixMs = Now.ToDateTimeOffset().ToUnixTimeMilliseconds(),
        },
        Caller = new AgentToolCallerContextPayload
        {
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
        },
        Credentials = new AgentToolCredentialsPayload
        {
            NyxIdAccessToken = accessToken,
            NyxIdCredentialKind =
                AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
            NyxIdCredentialAuthority =
                AgentToolNyxIdCredentialAuthorityPayload.ToolExecutionContext,
        },
    };

    private static void AssertToolContextNotPersisted(
        NyxIdChatConversationGAgentState state,
        NyxIdChatContinuationAdmissionState admission,
        string accessToken)
    {
        JsonFormatter.Default.Format(state).Should().NotContain(accessToken);
        JsonFormatter.Default.Format(admission).Should().NotContain(accessToken);
    }

    private sealed class RecordingKeyEvidenceReadPort(NyxIdAgentApiKeyEvidence evidence)
        : INyxIdActionEvidenceReadPort
    {
        public List<string> KeyIds { get; } = [];

        public Task<NyxIdApiAccessResult<NyxIdUserServiceAuthorizationEvidence>>
            GetUserServiceAuthorizationAsync(
                string bearerToken,
                string userServiceId,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<NyxIdApiAccessResult<NyxIdServiceAccessEvidence>> GetServiceAccessAsync(
            string bearerToken,
            string userServiceId,
            string serviceSlug,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence>> GetAgentApiKeyAsync(
            string bearerToken,
            string keyId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            KeyIds.Add(keyId);
            return Task.FromResult(
                new NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence>(evidence, null));
        }
    }

    private sealed class FixedTestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static NyxIdChatConversationGAgentState BlockedActionState() =>
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
        return NyxIdChatBrowserActions.CommitRequest(state, second, Now).State;
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
            OwnerSubject = "owner-alpha",
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

    private static NyxIdChatConversationGAgentState AuthorizationWaitingStateWithPlannedContinuation()
    {
        var state = AuthorizationWaitingState();
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
                    ConversationActorId = "conversation-alpha",
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
