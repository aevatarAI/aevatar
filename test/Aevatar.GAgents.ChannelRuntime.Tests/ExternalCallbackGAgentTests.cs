using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.ChannelRuntime.Tests.Identity;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ExternalCallbackGAgentTests
{
    [Fact]
    public async Task CallerDeliveredOAuth_DoesNotRepublishLink_AndStillCompletesAfterBindingDecision()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(ExternalCallbackKind.Oauth, linkDeliveryMode: ExternalCallbackLinkDeliveryMode.Caller);
        await h.AttachAsync();
        await h.ReactivateAsync();
        await h.FireAsync();
        h.Publisher.Sent.OfType<ExternalCallbackActionContextRecorded>().Count().Should().BeGreaterThan(1);
        await h.Agent.HandleContextAcceptedAsync(new() { CallbackId = "cb-a", ContextRevision = 1 });
        await h.FireAsync();
        await h.ReactivateAsync();
        await h.FireAsync();
        h.Publisher.Sent.OfType<ExternalCallbackLinkReady>().Should().BeEmpty();
        h.Agent.State.LinkPresented.Should().BeFalse("returning a URL is not a Channel delivery receipt");

        await h.Agent.HandleOAuthAsync(h.Submission());
        h.OAuth.Exchanges.Should().Be(1);
        h.Agent.State.Result.Should().Be(CallbackResult.Unspecified);
        await h.Agent.HandleOAuthOutcomeAsync(h.Outcome(true));
        await h.Agent.HandleContextAcceptedAsync(new() { CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision });

        h.Agent.State.Result.Should().Be(CallbackResult.Succeeded);
        h.Publisher.Sent.OfType<CallbackCompleted>().Should().ContainSingle().Which.Result.Should().Be(CallbackResult.Succeeded);
        h.Publisher.Sent.OfType<ExternalCallbackLinkReady>().Should().BeEmpty();
    }

    [Theory]
    [InlineData(ExternalCallbackLinkDeliveryMode.Caller)]
    [InlineData((ExternalCallbackLinkDeliveryMode)99)]
    public async Task ConnectLink_RejectsUnsupportedLinkDeliveryMode(ExternalCallbackLinkDeliveryMode mode)
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(linkDeliveryMode: mode);
        h.Agent.State.Registration.Should().BeNull();
        h.Creator.Calls.Should().Be(0);
    }

    [Fact]
    public async Task LinkPublication_WaitsForCommittedExactRequestAndContextAck_ThenStopsOnPresentationAck()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        h.Publisher.Sent.OfType<ExternalCallbackLinkReady>().Should().BeEmpty();
        h.Creator.BeforeCreate = () =>
        {
            h.Agent.State.Registration.CallbackId.Should().Be("cb-a");
            h.Agent.State.LinkCreationStarted.Should().BeTrue();
            h.Agent.EventSourcing!.CurrentVersion.Should().BeGreaterThan(1);
        };
        await h.AttachAsync();
        h.Agent.State.Registration.ExternalRequestId.Should().Be("link-exact");
        h.Publisher.Sent.OfType<ExternalCallbackLinkReady>().Should().BeEmpty();
        await h.Agent.HandleContextAcceptedAsync(new() { CallbackId = "cb-a", ContextRevision = 1 });
        var link = h.Publisher.Sent.OfType<ExternalCallbackLinkReady>().Should().ContainSingle().Subject;
        link.ConnectUrl.Should().Be(h.Agent.State.LinkUrl);
        await h.Agent.HandleLinkPresentedAsync(new() { CallbackId = "cb-a", ContextRevision = 1 });
        await h.FireAsync();
        h.Publisher.Sent.OfType<ExternalCallbackLinkReady>().Should().ContainSingle();
        h.Creator.Calls.Should().Be(1);
    }

    [Fact]
    public async Task LostInitialContext_IsRedeliveredUntilAcceptedBeforeLinkVisibility()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        await h.AttachAsync();
        await h.ReactivateAsync();
        await h.FireAsync();
        h.Publisher.Sent.OfType<ExternalCallbackActionContextRecorded>().Count().Should().BeGreaterThan(1);
        h.Publisher.Sent.OfType<ExternalCallbackLinkReady>().Should().BeEmpty();
        await h.Agent.HandleContextAcceptedAsync(new() { CallbackId = "cb-a", ContextRevision = 1 });
        h.Publisher.Sent.OfType<ExternalCallbackLinkReady>().Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPublicationFailure_RetainsSuccessorThatVerifiesExactLinkWithoutBrowserReturn(bool contextAccepted)
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        await h.AttachAsync();
        var sameActivation = h.Agent;
        if (contextAccepted)
            await h.DeliverAsync(new ExternalCallbackActionContextAccepted { CallbackId = "cb-a", ContextRevision = 1 });
        h.Verifier.Result = new() { Disposition = CallbackVerificationDisposition.Pending };
        h.Publisher.FailNextSendType = contextAccepted
            ? typeof(ExternalCallbackLinkReady) : typeof(ExternalCallbackActionContextRecorded);
        var generation = h.Agent.State.TimerGeneration;
        var deliveryDeadline = h.Agent.State.DeliveryDeadlineAtUnixMs;

        var failedRetry = () => h.FireScheduledAsync();
        await failedRetry.Should().ThrowAsync<IOException>();

        h.Publisher.FailedSends.Should().Be(1);
        h.Scheduler.Pending.Should().NotBeNull("the consumed one-shot must have a successor before publication");
        h.Scheduler.Pending!.TriggerEnvelope.Payload.Unpack<ExternalCallbackRetryFired>().Generation
            .Should().BeGreaterThan(generation).And.Be(h.Agent.State.TimerGeneration);
        h.Agent.State.NextAttemptAtUnixMs.Should().Be(h.Clock.GetUtcNow().Add(ExternalCallbackGAgent.RetryInterval).ToUnixTimeMilliseconds());
        h.Agent.State.DeliveryDeadlineAtUnixMs.Should().Be(deliveryDeadline);
        h.Agent.State.Result.Should().Be(CallbackResult.Unspecified);
        var verificationsBeforeRecovery = h.Verifier.Requests.Count;

        // Late receipts are ordinary inbox messages; neither they nor this test invent a retry.
        if (!contextAccepted)
            await h.DeliverAsync(new ExternalCallbackActionContextAccepted { CallbackId = "cb-a", ContextRevision = 1 });
        await h.DeliverAsync(new ExternalCallbackLinkPresented { CallbackId = "cb-a", ContextRevision = 1 });
        h.Verifier.Result = Success();
        await h.FireScheduledAsync();

        h.Agent.Should().BeSameAs(sameActivation);
        h.Verifier.Requests.Should().HaveCount(verificationsBeforeRecovery + 1);
        var verified = h.Verifier.Requests.Last();
        verified.ExternalRequestId.Should().Be("link-exact");
        verified.Authorization.BindingId.Should().Be("sender-binding");
        verified.Authorization.OwnerScopeId.Should().Be("owner-a");
        verified.Authorization.ExternalSubject.ExternalUserId.Should().Be("sender-a");
        verified.RequestedCatalogServiceSlug.Should().Be("google");
        h.Agent.State.Result.Should().Be(CallbackResult.Succeeded);
        h.Agent.State.VerifiedReferences.Should().Be(Success().References);
        h.Creator.Calls.Should().Be(1);
        await h.DeliverAsync(new ExternalCallbackActionContextAccepted { CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision });
        h.Publisher.Sent.OfType<CallbackCompleted>().Should().ContainSingle().Which.Result.Should().Be(CallbackResult.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuthStartPublicationFailure_RetainsSuccessorThatExpiresWithoutBrowserReturn(bool contextAccepted)
    {
        using var h = await Harness.CreateAsync();
        // Registration's immediately due timeout is scheduled one millisecond ahead.
        var expiresAt = h.Clock.GetUtcNow().AddMinutes(2).AddMilliseconds(1).ToUnixTimeMilliseconds();
        await h.RegisterAsync(ExternalCallbackKind.Oauth, expiresAt);
        var sameActivation = h.Agent;
        if (contextAccepted)
            await h.DeliverAsync(new ExternalCallbackActionContextAccepted { CallbackId = "cb-a", ContextRevision = 1 });
        h.Agent.State.LinkUrl.Should().BeEmpty("the due retry must recover the missing initial start");
        h.Publisher.FailNextSendType = contextAccepted
            ? typeof(ExternalCallbackLinkReady) : typeof(ExternalCallbackActionContextRecorded);
        var generation = h.Agent.State.TimerGeneration;
        var deliveryDeadline = h.Agent.State.DeliveryDeadlineAtUnixMs;

        var failedRetry = () => h.FireScheduledAsync();
        await failedRetry.Should().ThrowAsync<IOException>();

        h.Publisher.FailedSends.Should().Be(1);
        h.Agent.State.LinkUrl.Should().Be(h.Agent.State.Registration.OauthAuthorizeUrl);
        h.Scheduler.Pending.Should().NotBeNull("OAuth start publication must not consume the only wakeup");
        h.Scheduler.Pending!.TriggerEnvelope.Payload.Unpack<ExternalCallbackRetryFired>().Generation
            .Should().BeGreaterThan(generation).And.Be(h.Agent.State.TimerGeneration);
        h.Agent.State.DeliveryDeadlineAtUnixMs.Should().Be(deliveryDeadline);
        await h.DeliverAsync(new ExternalCallbackActionContextAccepted { CallbackId = "cb-a", ContextRevision = 1 });
        await h.DeliverAsync(new ExternalCallbackLinkPresented { CallbackId = "cb-a", ContextRevision = 1 });

        // Consume only leases actually scheduled by this same activation, up to expiry.
        for (var i = 0; i < 2; i++)
            await h.FireScheduledAsync();

        h.Agent.Should().BeSameAs(sameActivation);
        h.Clock.GetUtcNow().ToUnixTimeMilliseconds().Should().Be(expiresAt);
        h.Agent.State.Result.Should().Be(CallbackResult.Expired);
        h.Agent.State.FailureCode.Should().Be("expired");
        h.Agent.State.VerificationAttempts.Should().Be(0);
        h.OAuth.Exchanges.Should().Be(0);
        h.OAuth.Validations.Should().Be(0);
        h.OAuth.Dispatches.Should().Be(0);
        h.Verifier.Requests.Should().BeEmpty();
        await h.DeliverAsync(new ExternalCallbackActionContextAccepted { CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision });
        h.Publisher.Sent.OfType<CallbackCompleted>().Should().ContainSingle().Which.Result.Should().Be(CallbackResult.Expired);
    }

    [Fact]
    public async Task FailedPreparation_RetirementRemainsPendingAfterCompletionConsumptionUntilAuthorityAcknowledges()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(ExternalCallbackKind.Oauth);
        h.OAuth.TransientValidation = true;
        await h.Agent.HandleOAuthAsync(h.Submission());
        for (var i = 0; i < ExternalCallbackGAgent.MaxVerificationAttempts; i++) await h.FireAsync();
        h.Agent.State.Result.Should().Be(CallbackResult.Failed);
        h.Agent.State.AbandonPending.Should().BeTrue();
        await h.Agent.HandleContextAcceptedAsync(new() { CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision });
        await h.Agent.HandleConsumedAsync(new() { CallbackId = "cb-a", Result = CallbackResult.Failed });
        var cleanupCalls = h.OAuth.Abandons;
        await h.ReactivateAsync();
        await h.FireAsync();
        h.OAuth.Abandons.Should().BeGreaterThan(cleanupCalls);
        var outcome = h.Outcome(false); outcome.Abandoned = true;
        await h.Agent.HandleOAuthOutcomeAsync(outcome);
        h.Agent.State.AbandonPending.Should().BeFalse();
    }

    [Theory]
    [InlineData(CallbackVerificationDisposition.Failed, CallbackResult.Failed)]
    [InlineData(CallbackVerificationDisposition.Cancelled, CallbackResult.Cancelled)]
    [InlineData(CallbackVerificationDisposition.Expired, CallbackResult.Expired)]
    public async Task VerifiedNonSuccess_UsesSameAcknowledgedCompletionProtocol(CallbackVerificationDisposition disposition, CallbackResult expected)
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(); await h.AttachAsync();
        h.Verifier.Result = new() { Disposition = disposition };
        await h.FireAsync();
        h.Agent.State.Result.Should().Be(expected);
        h.Agent.State.VerifiedReferences.Should().BeNull();
        await h.Agent.HandleContextAcceptedAsync(new() { CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision });
        h.Publisher.Sent.OfType<CallbackCompleted>().Single().Result.Should().Be(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndeliveredBindingCommand_RetiresPreparationAfterRetryExhaustionOrExpiry(bool expires)
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(ExternalCallbackKind.Oauth);
        h.OAuth.ThrowDispatch = true;
        await h.Agent.HandleOAuthAsync(h.Submission());
        h.Agent.State.OauthPhase.Should().Be(ExternalCallbackAuthorizationPhase.BindingPending);
        h.Agent.State.Result.Should().Be(CallbackResult.Unspecified);
        if (expires)
            h.Clock.Advance(TimeSpan.FromDays(2));
        var retries = expires ? 1 : ExternalCallbackGAgent.MaxVerificationAttempts - h.Agent.State.VerificationAttempts;
        for (var i = 0; i < retries; i++)
            await h.FireAsync();
        h.Agent.State.Result.Should().Be(expires ? CallbackResult.Expired : CallbackResult.Failed);
        h.Agent.State.AbandonPending.Should().BeTrue();
        h.OAuth.Abandons.Should().Be(1);
        await h.ReactivateAsync();
        await h.FireAsync();
        h.OAuth.Abandons.Should().Be(2);
        var outcome = h.Outcome(false); outcome.Abandoned = true;
        await h.Agent.HandleOAuthOutcomeAsync(outcome);
        h.Agent.State.AbandonPending.Should().BeFalse();
    }

    [Fact]
    public async Task Registration_PersistsOriginalIdentity_AndArmsMissingReturnRetry()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        h.Agent.State.Registration.CallbackId.Should().Be("cb-a");
        h.Agent.State.Registration.OperationActorId.Should().Be("opaque-operation-a");
        h.Agent.State.Registration.Origin.OriginalActivity.Conversation.CanonicalKey.Should().Be("thread-original");
        h.Agent.State.Registration.Origin.OriginalActivity.TransportExtras.NyxUserAccessToken.Should().BeEmpty();
        h.Scheduler.TimeoutRequests.Should().NotBeEmpty();
        h.Agent.State.Result.Should().Be(CallbackResult.Unspecified);
    }

    [Fact]
    public async Task MissingBrowserReturn_VerifiesExactSavedRequest_OnDurableTimer()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        await h.AttachAsync();
        h.Verifier.Result = Success();
        await h.FireAsync();
        h.Verifier.Requests.Single().ExternalRequestId.Should().Be("link-exact");
        h.Agent.State.Result.Should().Be(CallbackResult.Succeeded);
        h.Agent.State.VerifiedReferences.ConnectedServiceId.Should().Be("instance-exact");
        h.Publisher.Sent.OfType<CallbackCompleted>().Should().BeEmpty("context must be committed by the conversation first");
        await h.Agent.HandleContextAcceptedAsync(new ExternalCallbackActionContextAccepted
        {
            CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision,
        });
        h.Publisher.Sent.OfType<CallbackCompleted>().Single().Result.Should().Be(CallbackResult.Succeeded);
    }

    [Fact]
    public async Task Hint_DoesNotTrustBrowserOutcome_AndCoalescesRepeatedWakeups()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        await h.AttachAsync();
        h.Verifier.Result = new() { Disposition = CallbackVerificationDisposition.Pending };
        var hint = new ExternalCallbackHint { CallbackId = "cb-a", ExternalRequestId = "link-exact" };
        await h.Agent.HandleHintAsync(hint);
        await h.Agent.HandleHintAsync(hint);
        h.Verifier.Requests.Should().HaveCount(1);
        h.Agent.State.Result.Should().Be(CallbackResult.Unspecified);
        h.Agent.State.VerificationAttempts.Should().Be(0, "a healthy pending read resets consecutive verification failures");
    }

    [Fact]
    public async Task WrongLinkAndStaleGeneration_DoNotCallProvider()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        await h.AttachAsync();
        await h.Agent.HandleHintAsync(new() { CallbackId = "cb-a", ExternalRequestId = "another-link" });
        await h.Agent.HandleRetryAsync(new() { CallbackId = "cb-a", Generation = h.Agent.State.TimerGeneration - 1 });
        h.Verifier.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProviderUnavailable_RemainsPending_ThenFailsAtBoundedRetryLimit()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        await h.AttachAsync();
        h.Verifier.Throw = true;
        await h.FireAsync();
        h.Agent.State.Result.Should().Be(CallbackResult.Unspecified);
        for (var i = 1; i < ExternalCallbackGAgent.MaxVerificationAttempts; i++)
            await h.FireAsync();
        h.Agent.State.Result.Should().Be(CallbackResult.Failed);
        h.Agent.State.VerifiedReferences.Should().BeNull();
    }

    [Theory]
    [InlineData(15, 13)]
    [InlineData(60, 58)]
    public async Task HealthyPendingChecks_DoNotConsumeBudget_AndCanSucceedAfterTwelveChecks(int expiryMinutes, int pendingChecks)
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(expiresAtUnixMs: h.Clock.GetUtcNow().AddMinutes(expiryMinutes).ToUnixTimeMilliseconds());
        await h.AttachAsync();
        h.Verifier.Result = new() { Disposition = CallbackVerificationDisposition.Pending, FailureCode = "connect_link_pending" };

        for (var i = 0; i < pendingChecks; i++)
        {
            await h.FireAsync();
            h.Agent.State.Result.Should().Be(CallbackResult.Unspecified);
            h.Agent.State.VerificationAttempts.Should().Be(0);
        }

        h.Verifier.Result = Success();
        await h.FireAsync();
        h.Agent.State.Result.Should().Be(CallbackResult.Succeeded);
    }

    [Fact]
    public async Task HealthyPendingRead_ResetsConsecutiveFailures_ThenUnavailableRemainsBounded()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(); await h.AttachAsync();
        h.Verifier.Result = new() { Disposition = CallbackVerificationDisposition.Unavailable };
        for (var i = 0; i < ExternalCallbackGAgent.MaxVerificationAttempts - 1; i++) await h.FireAsync();
        h.Agent.State.Result.Should().Be(CallbackResult.Unspecified);
        h.Verifier.Result = new() { Disposition = CallbackVerificationDisposition.Pending };
        await h.FireAsync();
        h.Agent.State.VerificationAttempts.Should().Be(0);
        await h.ReactivateAsync();
        h.Agent.State.VerificationAttempts.Should().Be(0);
        h.Verifier.Result = new() { Disposition = CallbackVerificationDisposition.Unavailable };
        for (var i = 0; i < ExternalCallbackGAgent.MaxVerificationAttempts - 1; i++) await h.FireAsync();
        h.Agent.State.Result.Should().Be(CallbackResult.Unspecified);
        await h.FireAsync();
        h.Agent.State.Result.Should().Be(CallbackResult.Failed);
        h.Verifier.Requests.Should().HaveCount(ExternalCallbackGAgent.MaxVerificationAttempts * 2);
    }

    [Fact]
    public async Task OAuthHealthyValidation_ResetsUnavailableStreakBeforeBindingDispatch()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(ExternalCallbackKind.Oauth);
        h.OAuth.TransientValidation = true;
        await h.Agent.HandleOAuthAsync(h.Submission());
        h.Agent.State.VerificationAttempts.Should().Be(1);

        h.OAuth.TransientValidation = false;
        await h.FireAsync();

        h.Agent.State.OauthPhase.Should().Be(ExternalCallbackAuthorizationPhase.BindingPending);
        h.Agent.State.VerificationAttempts.Should().Be(1, "the first binding dispatch is durably reserved");
        h.Agent.State.Result.Should().Be(CallbackResult.Unspecified);
    }

    [Theory]
    [InlineData(CallbackVerificationDisposition.Unavailable)]
    [InlineData(CallbackVerificationDisposition.Unspecified)]
    public async Task BrowserHints_ReserveFailureBudget_AndCannotResetUnknownOutcomes(CallbackVerificationDisposition disposition)
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(); await h.AttachAsync();
        h.Verifier.Result = new() { Disposition = disposition };
        for (var i = 0; i < ExternalCallbackGAgent.MaxVerificationAttempts; i++)
        {
            await h.Agent.HandleHintAsync(new() { CallbackId = "cb-a", ExternalRequestId = "link-exact" });
            h.Clock.Advance(ExternalCallbackGAgent.RetryInterval);
        }
        h.Agent.State.Result.Should().Be(CallbackResult.Failed);
        h.Verifier.Requests.Should().HaveCount(ExternalCallbackGAgent.MaxVerificationAttempts);
    }

    [Fact]
    public async Task ReactivationAtPersistedVerificationLimit_TerminalizesWithoutThirteenthProviderCall()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        await h.AttachAsync();
        h.Verifier.Throw = true;
        await h.SetVerificationAttemptsAtLimitAsync();
        var callsBefore = h.Verifier.Requests.Count;

        await h.ReactivateAsync();
        await h.FireAsync();

        h.Agent.State.Result.Should().Be(CallbackResult.Failed);
        h.Agent.State.FailureCode.Should().Be("verification_retry_exhausted");
        h.Verifier.Requests.Count.Should().Be(callsBefore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReactivationAtPersistedOAuthLimit_DoesNotValidateOrDispatchAgain(bool bindingPending)
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(ExternalCallbackKind.Oauth);
        h.OAuth.TransientValidation = !bindingPending;
        h.OAuth.ThrowDispatch = bindingPending;
        await h.Agent.HandleOAuthAsync(h.Submission());
        await h.SetVerificationAttemptsAtLimitAsync();
        var dispatchesBefore = h.OAuth.Dispatches;
        var validationsBefore = h.OAuth.Validations;
        await h.ReactivateAsync();
        await h.FireAsync();
        h.Agent.State.Result.Should().Be(CallbackResult.Failed);
        h.Agent.State.FailureCode.Should().Be("verification_retry_exhausted");
        h.OAuth.Dispatches.Should().Be(dispatchesBefore);
        h.OAuth.Validations.Should().Be(validationsBefore);
        h.Agent.State.AbandonPending.Should().BeTrue();
    }

    [Fact]
    public async Task ContinuationRejected_TerminalizesAndConsumesWithoutContextAck()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        await h.AttachAsync();

        await h.Agent.HandleContinuationRejectedAsync(new()
        {
            CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision,
            FailureCode = "callback_origin_unavailable",
        });

        h.Agent.State.Result.Should().Be(CallbackResult.Failed);
        h.Agent.State.ConsumedAtUnixMs.Should().BeGreaterThan(0);
        h.Agent.State.DeliveryFailureCode.Should().Be("callback_origin_unavailable");
    }

    [Fact]
    public async Task ContinuationRejected_PreservesExistingTerminalResult()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        await h.AttachAsync();
        h.Verifier.Result = Success();
        await h.FireAsync();
        h.Agent.State.Result.Should().Be(CallbackResult.Succeeded);

        await h.Agent.HandleContinuationRejectedAsync(new()
        {
            CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision,
            FailureCode = "callback_origin_unavailable",
        });

        h.Agent.State.Result.Should().Be(CallbackResult.Succeeded);
        h.Agent.State.ConsumedAtUnixMs.Should().BeGreaterThan(0);
        h.Agent.State.DeliveryFailureCode.Should().Be("callback_origin_unavailable");
    }

    [Fact]
    public async Task ContinuationRejected_AfterConsumptionAttachesDeliveryReceiptWithoutReopeningOperation()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(); await h.AttachAsync();
        h.Verifier.Result = Success();
        await h.FireAsync();
        var result = h.Agent.State.Result;
        var revision = h.Agent.State.ContextRevision;
        await h.Agent.HandleContextAcceptedAsync(new() { CallbackId = "cb-a", ContextRevision = revision });
        await h.Agent.HandleConsumedAsync(new() { CallbackId = "cb-a", Result = result });

        await h.Agent.HandleContinuationRejectedAsync(new()
        {
            CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision - 1,
            FailureCode = "stale_delivery_failure",
        });
        h.Agent.State.DeliveryFailureCode.Should().BeEmpty();

        await h.Agent.HandleContinuationRejectedAsync(new()
        {
            CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision,
            FailureCode = "callback_delivery_credential_missing",
        });

        h.Agent.State.Result.Should().Be(result);
        h.Agent.State.ConsumedAtUnixMs.Should().BeGreaterThan(0);
        h.Agent.State.DeliveryFailureCode.Should().Be("callback_delivery_credential_missing");
        var timerGeneration = h.Agent.State.TimerGeneration;
        await h.Agent.HandleRetryAsync(new() { CallbackId = "cb-a", Generation = timerGeneration - 1 });
        h.Agent.State.TimerGeneration.Should().Be(timerGeneration);
    }

    [Fact]
    public async Task ContinuationRejected_WithStaleRevisionDoesNotConsumeOrChangeResult()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        await h.AttachAsync();
        var revision = h.Agent.State.ContextRevision;

        await h.Agent.HandleContinuationRejectedAsync(new()
        {
            CallbackId = "cb-a", ContextRevision = revision - 1,
            FailureCode = "callback_origin_unavailable",
        });

        h.Agent.State.Result.Should().Be(CallbackResult.Unspecified);
        h.Agent.State.ConsumedAtUnixMs.Should().Be(0);
        h.Agent.State.DeliveryFailureCode.Should().BeEmpty();
    }

    [Fact]
    public async Task DeliveryDeadline_TerminalizesUnreachableContinuationAndStopsRedelivery()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(expiresAtUnixMs: h.Clock.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds());
        await h.AttachAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(17));

        await h.FireAsync();

        h.Agent.State.Result.Should().Be(CallbackResult.Expired);
        h.Agent.State.ConsumedAtUnixMs.Should().BeGreaterThan(0);
        h.Agent.State.DeliveryFailureCode.Should().Be("delivery_deadline_exceeded");
        var sent = h.Publisher.Sent.Count;
        await h.FireAsync();
        h.Publisher.Sent.Count.Should().Be(sent);
    }

    [Fact]
    public async Task Expiry_UsesSameCompletionProtocol_WithoutProviderCall()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        h.Clock.Advance(TimeSpan.FromDays(2));
        await h.FireAsync();
        h.Agent.State.Result.Should().Be(CallbackResult.Expired);
        h.Verifier.Requests.Should().BeEmpty();
        h.Publisher.Sent.OfType<ExternalCallbackActionContextRecorded>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task RegistrationDelayedPastExpiry_StillRoutesExpiredOutcomeWithoutCreatingLink()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(expiresAtUnixMs: h.Clock.GetUtcNow().AddSeconds(-1).ToUnixTimeMilliseconds());
        h.Agent.State.Registration.Should().NotBeNull("accepted inbox delivery can be delayed past its deadline");
        await h.AttachAsync();
        h.Agent.State.Result.Should().Be(CallbackResult.Expired);
        h.Creator.Calls.Should().Be(0);
        h.Publisher.Sent.OfType<ExternalCallbackLinkReady>().Should().BeEmpty();
        await h.Agent.HandleContextAcceptedAsync(new() { CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision });
        h.Publisher.Sent.OfType<CallbackCompleted>().Should().ContainSingle().Which.Result.Should().Be(CallbackResult.Expired);
    }

    [Fact]
    public async Task OAuthAcceptedDispatch_DoesNotComplete_BeforeBindingAuthorityDecision()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(ExternalCallbackKind.Oauth);
        await h.Agent.HandleOAuthAsync(h.Submission());
        h.OAuth.Dispatches.Should().Be(1);
        h.Agent.State.Result.Should().Be(CallbackResult.Unspecified);
        await h.Agent.HandleOAuthOutcomeAsync(h.Outcome(true));
        h.Agent.State.Result.Should().Be(CallbackResult.Succeeded);
        h.Agent.State.VerifiedReferences.BindingId.Should().Be("binding-verified");
        h.Agent.State.AuthorizationOwner().Should().Be("owner-a");
    }

    [Fact]
    public async Task OAuthDuplicate_DoesNotExchangeAgain_AndConflictCannotOverwriteSuccess()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(ExternalCallbackKind.Oauth);
        await h.Agent.HandleOAuthAsync(h.Submission());
        await h.Agent.HandleOAuthAsync(h.Submission());
        h.OAuth.Exchanges.Should().Be(1);
        await h.Agent.HandleOAuthOutcomeAsync(h.Outcome(true));
        await h.Agent.HandleOAuthOutcomeAsync(h.Outcome(false));
        h.Agent.State.Result.Should().Be(CallbackResult.Succeeded);
    }

    [Fact]
    public async Task SignedCancellation_UsesContinuation_AndDoesNotExchangeCode()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(ExternalCallbackKind.Oauth);
        var submission = h.Submission();
        submission.OauthError = "access_denied";
        submission.AuthorizationCode = "";
        await h.Agent.HandleOAuthAsync(submission);
        h.Agent.State.Result.Should().Be(CallbackResult.Cancelled);
        h.OAuth.Exchanges.Should().Be(0);
    }

    [Fact]
    public async Task Reactivation_RedeliversPendingContextAndCompletion_UntilConsumptionAck()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync();
        await h.AttachAsync();
        h.Verifier.Result = Success();
        await h.FireAsync();
        await h.ReactivateAsync();
        await h.FireAsync();
        h.Agent.State.VerifiedReferences.ConnectedServiceId.Should().Be("instance-exact");
        await h.Agent.HandleContextAcceptedAsync(new() { CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision - 1 });
        h.Publisher.Sent.OfType<CallbackCompleted>().Should().BeEmpty();
        await h.Agent.HandleContextAcceptedAsync(new() { CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision });
        await h.ReactivateAsync();
        await h.FireAsync();
        h.Publisher.Sent.OfType<CallbackCompleted>().Count().Should().BeGreaterThan(1);
        await h.Agent.HandleConsumedAsync(new() { CallbackId = "cb-a", Result = CallbackResult.Succeeded });
        var count = h.Publisher.Sent.Count;
        await h.FireAsync();
        h.Publisher.Sent.Should().HaveCount(count);
    }

    [Fact]
    public async Task ReactivationAfterExchangeAdmission_FailsSafely_WithoutReexchangingCode()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(ExternalCallbackKind.Oauth);
        h.OAuth.ThrowExchange = true;
        await h.Agent.HandleOAuthAsync(h.Submission());
        await h.ReactivateAsync();
        await h.FireAsync();
        h.Agent.State.Result.Should().Be(CallbackResult.Failed);
        h.OAuth.Exchanges.Should().Be(1);
    }

    [Fact]
    public async Task CrashAfterCommittedExchangeAdmission_RecoversWithoutReplayingOneUseCode()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(ExternalCallbackKind.Oauth);
        await h.CommitInterruptedExchangeAsync();
        await h.ReactivateAsync();
        h.Agent.State.OauthPhase.Should().Be(ExternalCallbackAuthorizationPhase.ExchangeStarted);
        h.Agent.State.OauthPreparation.Should().BeNull();
        await h.FireAsync();
        h.Agent.State.Result.Should().Be(CallbackResult.Failed);
        h.Agent.State.FailureCode.Should().Be("oauth_exchange_unconfirmed");
        h.OAuth.Exchanges.Should().Be(0);
        h.OAuth.Dispatches.Should().Be(0);
    }

    private static ConnectLinkVerificationResult Success() => new()
    {
        Disposition = CallbackVerificationDisposition.Succeeded,
        References = new()
        {
            BindingId = "sender-binding", OwnerScopeId = "owner-a", ConnectLinkId = "link-exact",
            ConnectedServiceId = "instance-exact", ConnectedServiceSlug = "google-mine", CatalogServiceSlug = "google",
        },
    };

    private sealed class Harness : IDisposable
    {
        public ExternalCallbackGAgent Agent { get; private set; } = null!;
        public FakeClock Clock { get; } = new();
        public RecordingPublisher Publisher { get; } = new();
        public VerificationPort Verifier { get; } = new();
        public OAuthPort OAuth { get; } = new();
        public CreationPort Creator { get; private set; } = null!;
        public ConsumingCallbackScheduler Scheduler { get; }
        private ServiceProvider _services = null!;

        private Harness() => Scheduler = new(Clock);

        public static async Task<Harness> CreateAsync()
        {
            var h = new Harness();
            h.Creator = new CreationPort(h.Clock);
            h._services = new ServiceCollection()
                .AddSingleton<IEventStore, IdentityGAgentTestHarness.InMemoryEventStore>()
                .AddSingleton<EventSourcingRuntimeOptions>()
                .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
                .AddSingleton<IActorRuntimeCallbackScheduler>(h.Scheduler)
                .AddSingleton<TimeProvider>(h.Clock)
                .AddSingleton<IConnectLinkVerificationPort>(h.Verifier)
                .AddSingleton<IOAuthContinuationExecutionPort>(h.OAuth)
                .AddSingleton<IConnectLinkCreationPort>(h.Creator)
                .BuildServiceProvider();
            await h.ReactivateAsync();
            return h;
        }

        public async Task ReactivateAsync()
        {
            Agent = new ExternalCallbackGAgent
            {
                Services = _services, EventPublisher = Publisher,
                EventSourcingBehaviorFactory = _services.GetRequiredService<IEventSourcingBehaviorFactory<ExternalCallbackState>>(),
            };
            typeof(Aevatar.Foundation.Core.GAgentBase).GetMethod("SetId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(Agent, ["opaque-operation-a"]);
            await Agent.ActivateAsync();
        }

        public Task RegisterAsync(ExternalCallbackKind kind = ExternalCallbackKind.ConnectLink, long? expiresAtUnixMs = null,
            ExternalCallbackLinkDeliveryMode linkDeliveryMode = ExternalCallbackLinkDeliveryMode.Unspecified) => Agent.HandleRegisterAsync(new()
        {
            CommandId = "register-1",
            Registration = new()
            {
                CallbackId = "cb-a", OperationActorId = "opaque-operation-a", Kind = kind, LinkDeliveryMode = linkDeliveryMode,
                ExpiresAtUnixMs = expiresAtUnixMs ?? Clock.GetUtcNow().AddDays(1).ToUnixTimeMilliseconds(),
                RequestedCatalogServiceSlug = "google", OauthAuthorizeUrl = "https://nyxid.test/authorize?state=signed", ConnectLinkRequest = new() { ExpiresInSeconds = 3600 },
                Authorization = new() { ExternalSubject = new() { Platform = "lark", Tenant = "tenant-a", ExternalUserId = "sender-a" }, BindingId = "sender-binding", OwnerScopeId = "owner-a" },
                Origin = new()
                {
                    ConversationActorId = "opaque-conversation-a", ChannelRegistrationId = "registration-a", ActionId = "action-a",
                    OriginalActivity = new() { Id = "original-message", Conversation = new() { CanonicalKey = "thread-original" }, TransportExtras = new() { NyxUserAccessToken = "must-not-persist" } },
                },
            },
        });

        public Task AttachAsync() => Agent.HandleStartAsync(new() { CallbackId = "cb-a" });
        public async Task CommitInterruptedExchangeAsync()
        {
            var state = Agent.State.Clone();
            state.OauthPhase = ExternalCallbackAuthorizationPhase.ExchangeStarted;
            var store = _services.GetRequiredService<IEventStore>();
            var version = await store.GetVersionAsync(Agent.Id);
            await store.AppendAsync(Agent.Id, [new StateEvent
            {
                EventId = "exchange-admitted-before-process-crash", Version = version + 1,
                EventData = Google.Protobuf.WellKnownTypes.Any.Pack(new ExternalCallbackStateChanged { State = state }),
            }], version);
        }
        public async Task SetVerificationAttemptsAtLimitAsync()
        {
            var state = Agent.State.Clone();
            state.VerificationAttempts = ExternalCallbackGAgent.MaxVerificationAttempts;
            state.NextAttemptAtUnixMs = Clock.GetUtcNow().ToUnixTimeMilliseconds();
            var store = _services.GetRequiredService<IEventStore>();
            var version = await store.GetVersionAsync(Agent.Id);
            await store.AppendAsync(Agent.Id, [new StateEvent
            {
                EventId = "verification-limit-before-process-crash", Version = version + 1,
                EventData = Google.Protobuf.WellKnownTypes.Any.Pack(new ExternalCallbackStateChanged { State = state }),
            }], version);
        }
        public async Task FireAsync()
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            await Agent.HandleRetryAsync(new() { CallbackId = "cb-a", Generation = Agent.State.TimerGeneration });
        }
        public Task FireScheduledAsync() => Agent.HandleEventAsync(Scheduler.ConsumeNext());
        public Task DeliverAsync(IMessage message) => Agent.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"), Payload = Google.Protobuf.WellKnownTypes.Any.Pack(message),
            Route = new EnvelopeRoute { PublisherActorId = "opaque-conversation-a", Direct = new DirectRoute { TargetActorId = Agent.Id } },
        });
        public OAuthContinuationSubmission Submission() => new() { CallbackId = "cb-a", ExternalSubject = Agent.State.Registration.Authorization.ExternalSubject.Clone(), AuthorizationCode = "one-use-code", PkceVerifier = "verifier", ExpiresAtUnixMs = Agent.State.Registration.ExpiresAtUnixMs };
        public OAuthBindingOutcome Outcome(bool succeeded) => new() { CallbackId = "cb-a", BindingId = "binding-verified", OwnerScopeId = "owner-a", ExternalSubject = Agent.State.Registration.Authorization.ExternalSubject.Clone(), Succeeded = succeeded };
        public void Dispose() => _services.Dispose();
    }

    private sealed class ConsumingCallbackScheduler(FakeClock clock) : IActorRuntimeCallbackScheduler
    {
        private RuntimeCallbackLease? _lease;
        private long _generation;
        private DateTimeOffset _dueAt;
        public RuntimeCallbackTimeoutRequest? Pending { get; private set; }
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(RuntimeCallbackTimeoutRequest request, CancellationToken ct = default)
        {
            Pending = request;
            _dueAt = clock.GetUtcNow() + request.DueTime;
            TimeoutRequests.Add(request);
            _lease = new(request.ActorId, request.CallbackId, ++_generation, RuntimeCallbackBackend.InMemory);
            return Task.FromResult(_lease);
        }

        public EventEnvelope ConsumeNext()
        {
            Pending.Should().NotBeNull("no replacement retry may be fabricated by the harness");
            var envelope = Pending!.TriggerEnvelope.Clone();
            if (_dueAt > clock.GetUtcNow())
                clock.Advance(_dueAt - clock.GetUtcNow());
            Pending = null;
            _lease = null;
            return envelope;
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(RuntimeCallbackTimerRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("the operation uses one-shot durable timeouts");
        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            if (_lease == lease) { Pending = null; _lease = null; }
            return Task.CompletedTask;
        }
        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            if (_lease?.ActorId == actorId) { Pending = null; _lease = null; }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    private sealed class CreationPort(FakeClock clock) : IConnectLinkCreationPort
    {
        public Action? BeforeCreate { get; set; }
        public int Calls { get; private set; }
        public Task<ConnectLinkCreationResult> CreateAsync(ExternalCallbackRegistration registration, CancellationToken ct = default)
        {
            Calls++; BeforeCreate?.Invoke();
            return Task.FromResult(new ConnectLinkCreationResult { ExternalRequestId = "link-exact", ConnectUrl = "https://nyxid.test/connect/link-exact", ExpiresAtUnixMs = clock.GetUtcNow().AddHours(1).ToUnixTimeMilliseconds() });
        }
    }
    private sealed class VerificationPort : IConnectLinkVerificationPort
    {
        public List<ExternalCallbackRegistration> Requests { get; } = [];
        public ConnectLinkVerificationResult Result { get; set; } = new();
        public bool Throw { get; set; }
        public Task<ConnectLinkVerificationResult> VerifyAsync(ExternalCallbackRegistration registration, CancellationToken ct = default)
        {
            Requests.Add(registration.Clone());
            return Throw ? Task.FromException<ConnectLinkVerificationResult>(new HttpRequestException("unavailable")) : Task.FromResult(Result.Clone());
        }
    }
    private sealed class OAuthPort : IOAuthContinuationExecutionPort
    {
        public int Exchanges { get; private set; }
        public int Dispatches { get; private set; }
        public int Validations { get; private set; }
        public bool ThrowExchange { get; set; }
        public bool ThrowDispatch { get; set; }
        public bool TransientValidation { get; set; }
        public int Abandons { get; private set; }
        public Task<OAuthBindingPreparation> ExchangeAsync(OAuthContinuationSubmission submission, CancellationToken ct = default)
        {
            Exchanges++;
            return ThrowExchange ? Task.FromException<OAuthBindingPreparation>(new HttpRequestException("lost exchange response")) : Task.FromResult(new OAuthBindingPreparation { ExternalSubject = submission.ExternalSubject.Clone(), BindingId = "binding-verified", OwnerScopeId = "owner-a" });
        }
        public Task<OAuthBindingPreparation> ValidateAsync(OAuthBindingPreparation preparation, CancellationToken ct = default)
        {
            Validations++;
            var verified = preparation.Clone(); verified.VerificationSucceeded = !TransientValidation;
            verified.IsTransientFailure = TransientValidation;
            return Task.FromResult(verified);
        }
        public Task DispatchBindingAsync(OAuthBindingPreparation preparation, string callbackId, string operationActorId, CancellationToken ct = default)
        { Dispatches++; return ThrowDispatch ? Task.FromException(new IOException("binding inbox unavailable")) : Task.CompletedTask; }
        public Task AbandonAsync(OAuthBindingPreparation preparation, string callbackId, string operationActorId, CancellationToken ct = default)
        { Abandons++; return Task.CompletedTask; }
    }
    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<IMessage> Sent { get; } = [];
        public Type? FailNextSendType { get; set; }
        public int FailedSends { get; private set; }
        public Task PublishAsync<T>(T evt, TopologyAudience audience = TopologyAudience.Children, CancellationToken ct = default, EventEnvelope? sourceEnvelope = null, EventEnvelopePublishOptions? options = null) where T : IMessage => Task.CompletedTask;
        public Task SendToAsync<T>(string targetActorId, T evt, CancellationToken ct = default, EventEnvelope? sourceEnvelope = null, EventEnvelopePublishOptions? options = null) where T : IMessage
        {
            if (evt.GetType() == FailNextSendType)
            {
                FailNextSendType = null;
                FailedSends++;
                return Task.FromException(new IOException("transient conversation inbox publication failure"));
            }
            Sent.Add(evt.Descriptor.Parser.ParseFrom(evt.ToByteArray()));
            return Task.CompletedTask;
        }
    }
}

internal static class ExternalCallbackTestStateExtensions
{
    public static string AuthorizationOwner(this ExternalCallbackState state) => state.Registration.Authorization.OwnerScopeId;
}
