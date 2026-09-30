using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.ChannelRuntime.Tests.Identity;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConversationExternalCallbackTests
{
    [Fact]
    public async Task LinkReady_ShouldDeliverOnlyAfterDurableInboxAdmissionAndAbsorbDuplicate()
    {
        var harness = new Harness();
        var actor = await harness.ActivateAsync();
        var context = BuildContext();
        context.ContextRevision = 1;
        context.VerifiedReferences = null;
        await DeliverAsync(actor, context);
        var link = new ExternalCallbackLinkReady
        {
            CallbackId = context.CallbackId, OperationActorId = context.OperationActorId,
            Origin = context.Origin.Clone(), ConnectUrl = "https://nyx.example/connect/exact-link", ContextRevision = 1,
            Kind = ExternalCallbackKind.ConnectLink,
        };
        await DeliverAsync(actor, link);
        harness.Runner.Links.Should().BeEmpty();
        harness.Publisher.Sent.OfType<ExternalCallbackLinkPresented>().Should().BeEmpty();

        actor = await harness.ActivateAsync();
        await DeliverAsync(actor, new ConversationExternalCallbackLinkDeliveryRequested { CallbackId = context.CallbackId }, actor.Id);
        await DeliverAsync(actor, link);
        await DeliverAsync(actor, new ConversationExternalCallbackLinkDeliveryRequested { CallbackId = context.CallbackId }, actor.Id);

        harness.Runner.Links.Should().ContainSingle();
        harness.Runner.Links[0].ConnectUrl.Should().Be(link.ConnectUrl);
        actor.State.ExternalCallbackActions[0].LinkDelivered.Should().BeTrue();
        harness.Publisher.Sent.OfType<ExternalCallbackLinkPresented>().Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalContextUpgrade_ShouldAbsorbStaleLinkBeforeGenericCompletion(bool linkArrivedBeforeUpgrade)
    {
        var harness = new Harness();
        var actor = await harness.ActivateAsync();
        var initial = BuildContext();
        initial.ContextRevision = 1;
        initial.VerifiedReferences = null;
        await DeliverAsync(actor, initial);
        var link = new ExternalCallbackLinkReady
        {
            CallbackId = initial.CallbackId, OperationActorId = initial.OperationActorId, Origin = initial.Origin.Clone(),
            ConnectUrl = "https://nyx.example/connect/stale-link", ContextRevision = 1, Kind = ExternalCallbackKind.ConnectLink,
        };
        if (linkArrivedBeforeUpgrade)
            await DeliverAsync(actor, link);
        await DeliverAsync(actor, BuildContext());
        await DeliverAsync(actor, link);
        await DeliverAsync(actor, new ConversationExternalCallbackLinkDeliveryRequested { CallbackId = initial.CallbackId }, actor.Id);

        harness.Runner.Links.Should().BeEmpty();
        harness.Publisher.Sent.OfType<ExternalCallbackLinkPresented>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task ContextUpgrade_ShouldAcceptAuthoritativeOAuthBindingWhileKeepingOriginalSubject()
    {
        var harness = new Harness();
        var actor = await harness.ActivateAsync();
        var initial = BuildContext();
        initial.ContextRevision = 1;
        initial.Authorization.BindingId = string.Empty;
        initial.Authorization.OwnerScopeId = string.Empty;
        initial.VerifiedReferences = null;
        await DeliverAsync(actor, initial);
        await DeliverAsync(actor, BuildContext());
        await DeliverAsync(actor, new CallbackCompleted { CallbackId = "cb-alpha", Result = CallbackResult.Succeeded });

        actor.State.ExternalCallbackActions[0].Result.Should().Be(CallbackResult.Succeeded);
        actor.State.ExternalCallbackActions[0].Context.Authorization.BindingId.Should().Be("binding-a");
    }

    [Fact]
    public async Task ContextAdmission_ShouldPersistExactOriginAndVerifiedReferencesBeforeAcknowledging()
    {
        var harness = new Harness();
        var actor = await harness.ActivateAsync();
        var context = BuildContext();

        await DeliverAsync(actor, context);

        actor.State.ExternalCallbackActions.Should().ContainSingle();
        actor.State.ExternalCallbackActions[0].Context.Should().BeEquivalentTo(context);
        harness.Publisher.Sent.OfType<ExternalCallbackActionContextAccepted>()
            .Should().ContainSingle().Which.ContextRevision.Should().Be(2);
        (await harness.Store.GetVersionAsync(actor.Id)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Completion_ShouldResumeOnceWithStableRunAndOriginalThreadAndRecoverAfterActivation()
    {
        var harness = new Harness();
        var actor = await harness.ActivateAsync();
        await DeliverAsync(actor, BuildContext());
        await DeliverAsync(actor, new CallbackCompleted { CallbackId = "cb-alpha", Result = CallbackResult.Succeeded });
        harness.Dispatcher.Requests.Should().BeEmpty("completion must enqueue the next actor turn");
        var runId = actor.State.ExternalCallbackActions.Single().ResumeRunId;

        actor = await harness.ActivateAsync();
        await DeliverAsync(actor, new ConversationExternalCallbackResumeRequested { CallbackId = "cb-alpha" }, actor.Id);
        await DeliverAsync(actor, new CallbackCompleted { CallbackId = "cb-alpha", Result = CallbackResult.Succeeded });
        await DeliverAsync(actor, new ConversationExternalCallbackResumeRequested { CallbackId = "cb-alpha" }, actor.Id);

        harness.Dispatcher.Requests.Should().ContainSingle();
        var request = harness.Dispatcher.Requests[0];
        request.RunId.Should().Be(runId);
        request.ExternalCallbackId.Should().Be("cb-alpha");
        request.Activity.Conversation.CanonicalKey.Should().Be("lark:tenant-a:chat-a:thread-a:sender-a");
        request.RegistrationId.Should().Be("registration-a");
        request.Activity.Content.Text.Should().Contain("Create the report");
        request.Activity.Content.Text.Should().NotContain("binding-a").And.NotContain("sender-owner");
        request.ReplyToken.Should().BeEmpty();
        harness.Runner.ContinueCount.Should().Be(0);
        harness.Publisher.Sent.OfType<CallbackCompletionConsumed>().Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsumptionAckFailure_ShouldRecoverSameRunWithoutReactivationOrDuplicatePreparation(bool duplicateCompletionFirst)
    {
        var harness = new Harness();
        var actor = await harness.ActivateAsync();
        var context = BuildContext();
        var completion = new CallbackCompleted { CallbackId = context.CallbackId, Result = CallbackResult.Succeeded };
        await DeliverAsync(actor, context);
        await DeliverAsync(actor, completion);
        var runId = actor.State.ExternalCallbackActions.Single().ResumeRunId;
        var activityId = actor.State.ExternalCallbackActions.Single().ResumeActivityId;
        harness.Publisher.FailNextConsumptionAck = true;

        await Assert.ThrowsAsync<IOException>(() => DeliverAsync(actor,
            new ConversationExternalCallbackResumeRequested { CallbackId = context.CallbackId }, actor.Id));

        actor.State.ExternalCallbackActions.Single().ResumeAdmitted.Should().BeTrue();
        var saved = actor.State.PendingLlmReplyRequests.Should().ContainSingle().Subject.Clone();
        saved.RunId.Should().Be(runId);
        saved.CorrelationId.Should().Be(activityId);
        harness.Dispatcher.Requests.Should().BeEmpty();
        harness.Runner.InboundCount.Should().Be(1);
        harness.Publisher.Sent.OfType<CallbackCompletionConsumed>().Should().BeEmpty();
        var resumeTimer = harness.Scheduler.TimeoutRequests.Last(timeout =>
            timeout.TriggerEnvelope.Payload.Is(ConversationExternalCallbackResumeRequested.Descriptor));

        // Retain this activation and deliver the already-armed inbox wakeup or
        // duplicate completion first, proving each can drain the admitted request.
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        if (duplicateCompletionFirst)
            await DeliverAsync(actor, completion);
        else
            await actor.HandleEventAsync(resumeTimer.TriggerEnvelope.Clone());
        harness.Dispatcher.Requests.Should().ContainSingle();
        var deadline = actor.State.ExternalCallbackActions.Single().RunDispatchRetry.DeadlineAtUnixMs;

        await actor.HandleEventAsync(resumeTimer.TriggerEnvelope.Clone());
        await DeliverAsync(actor, completion);
        var dispatchTimer = harness.Scheduler.TimeoutRequests.Last(timeout =>
            timeout.TriggerEnvelope.Payload.Is(DeferredLlmReplyDispatchRequestedEvent.Descriptor));
        await actor.HandleEventAsync(dispatchTimer.TriggerEnvelope.Clone());

        harness.Dispatcher.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(saved);
        saved.ExternalCallbackId.Should().Be(context.CallbackId);
        saved.RegistrationId.Should().Be(context.Origin.ChannelRegistrationId);
        saved.Activity.Conversation.Should().BeEquivalentTo(context.Origin.OriginalActivity.Conversation);
        harness.Runner.InboundCount.Should().Be(1);
        harness.Runner.ContinueCount.Should().Be(0);
        var action = actor.State.ExternalCallbackActions.Single();
        action.Context.Should().BeEquivalentTo(context);
        action.Result.Should().Be(CallbackResult.Succeeded);
        action.ResumeRetry.Attempts.Should().Be(1);
        action.RunDispatchConfirmed.Should().BeTrue();
        action.RunDispatchRetry.Attempts.Should().Be(1);
        action.RunDispatchRetry.DeadlineAtUnixMs.Should().Be(deadline).And.BeGreaterThan(0);
        action.RunDispatchRetry.NextAttemptAtUnixMs.Should().BeGreaterThan(0);
        action.FailedPhase.Should().Be(ConversationExternalCallbackDeliveryPhase.Unspecified);
        harness.Publisher.Sent.OfType<CallbackCompletionConsumed>().Should().NotBeEmpty();
        var events = await harness.Store.GetEventsAsync(actor.Id);
        events.Should().ContainSingle(evt => evt.EventData.Is(NeedsLlmReplyEvent.Descriptor));
        events.Should().ContainSingle(evt => evt.EventData.Is(ConversationExternalCallbackResumeAdmitted.Descriptor));
        events.Should().ContainSingle(evt => evt.EventData.Is(ConversationExternalCallbackRunDispatchConfirmed.Descriptor));
    }

    [Fact]
    public async Task ResumeDispatchFailure_ShouldRecoverSameCommittedRunWithoutPreparingBusinessTwice()
    {
        var harness = new Harness();
        harness.Dispatcher.FailNext = true;
        var actor = await harness.ActivateAsync();
        actor.State.RetainedHistory.Add(new ConversationHistoryEntry
        {
            Role = "user", Content = "The report must include the October project milestones.",
        });
        await DeliverAsync(actor, BuildContext());
        await DeliverAsync(actor, new CallbackCompleted { CallbackId = "cb-alpha", Result = CallbackResult.Succeeded });
        await DeliverAsync(actor, new ConversationExternalCallbackResumeRequested { CallbackId = "cb-alpha" }, actor.Id);
        var runId = actor.State.ExternalCallbackActions[0].ResumeRunId;
        actor.State.ExternalCallbackActions[0].ResumeAdmitted.Should().BeTrue();

        actor = await harness.ActivateAsync();
        await DeliverAsync(actor, new CallbackCompleted { CallbackId = "cb-alpha", Result = CallbackResult.Succeeded });

        harness.Runner.InboundCount.Should().Be(1);
        harness.Dispatcher.Requests.Should().HaveCount(2);
        harness.Dispatcher.Requests.Should().OnlyContain(request => request.RunId == runId);
        harness.Dispatcher.Requests.Should().OnlyContain(request => request.PriorHistory.Count == 1 &&
            request.PriorHistory[0].Content == "The report must include the October project milestones.");
    }

    [Fact]
    public async Task ParallelOperations_ShouldKeepActionInputAndVerifiedServiceReferencesDistinct()
    {
        var harness = new Harness();
        var actor = await harness.ActivateAsync();
        var first = BuildContext();
        var second = BuildContext();
        second.CallbackId = "cb-beta";
        second.OperationActorId = "operation-beta";
        second.Origin.ActionId = "action-beta";
        second.Origin.OriginalActivity.Id = "activity-beta";
        second.Origin.OriginalActivity.Content.Text = "Create a calendar event";
        second.VerifiedReferences.ConnectedServiceId = "instance-beta";
        await DeliverAsync(actor, first);
        await DeliverAsync(actor, second, second.OperationActorId);
        await DeliverAsync(actor, new CallbackCompleted { CallbackId = first.CallbackId, Result = CallbackResult.Succeeded });
        await DeliverAsync(actor, new CallbackCompleted { CallbackId = second.CallbackId, Result = CallbackResult.Succeeded }, second.OperationActorId);
        await DeliverAsync(actor, new ConversationExternalCallbackResumeRequested { CallbackId = second.CallbackId }, actor.Id);
        await DeliverAsync(actor, new ConversationExternalCallbackResumeRequested { CallbackId = first.CallbackId }, actor.Id);

        harness.Dispatcher.Requests.Should().HaveCount(2);
        var beta = harness.Dispatcher.Requests.Single(request => request.ExternalCallbackId == second.CallbackId);
        var alpha = harness.Dispatcher.Requests.Single(request => request.ExternalCallbackId == first.CallbackId);
        beta.Activity.Content.Text.Should().Contain("Create a calendar event").And.Contain("instance-beta").And.NotContain("Create the report");
        alpha.Activity.Content.Text.Should().Contain("Create the report").And.Contain("instance-a").And.NotContain("instance-beta");
        beta.RunId.Should().NotBe(alpha.RunId);
    }

    [Fact]
    public async Task StaleContextAndConflictingCompletion_ShouldNotChangeTheAdmittedAction()
    {
        var harness = new Harness();
        var actor = await harness.ActivateAsync();
        await DeliverAsync(actor, BuildContext());
        var stale = BuildContext();
        stale.ContextRevision = 1;
        stale.VerifiedReferences.ConnectedServiceId = "wrong-instance";
        await DeliverAsync(actor, stale);
        await DeliverAsync(actor, new CallbackCompleted { CallbackId = "cb-alpha", Result = CallbackResult.Succeeded });
        await DeliverAsync(actor, new CallbackCompleted { CallbackId = "cb-alpha", Result = CallbackResult.Cancelled });

        var action = actor.State.ExternalCallbackActions.Should().ContainSingle().Subject;
        action.Context.VerifiedReferences.ConnectedServiceId.Should().Be("instance-a");
        action.Result.Should().Be(CallbackResult.Succeeded);
    }

    [Fact]
    public async Task UnknownCompletionAndWrongConversationOrigin_ShouldNotResume()
    {
        var harness = new Harness();
        var actor = await harness.ActivateAsync();
        var context = BuildContext();
        context.Origin.ConversationActorId = "another-conversation";
        await DeliverAsync(actor, context);
        await DeliverAsync(actor, new CallbackCompleted { CallbackId = "cb-alpha", Result = CallbackResult.Succeeded });

        actor.State.ExternalCallbackActions.Should().BeEmpty();
        harness.Dispatcher.Requests.Should().BeEmpty();
        harness.Publisher.Sent.Should().NotContain(message => message is CallbackCompletionConsumed);
    }

    [Theory]
    [InlineData("relay_unavailable")]
    [InlineData("callback_agent_key_unavailable")]
    public async Task ProducedReply_TransientOutboundFailure_ShouldRetrySavedPayloadWithoutRunningBusinessAgain(string failureCode)
    {
        var harness = new Harness();
        var actor = await AdmitResumeAsync(harness);
        var ready = BuildReady(harness);
        harness.Runner.ReplyResults.Enqueue(ConversationTurnResult.TransientFailure(failureCode, "temporary"));
        await DeliverAsync(actor, ready, "run-a");
        await DeliverAsync(actor, new ConversationExternalCallbackReplyDeliveryRequested { CallbackId = "cb-alpha" }, actor.Id);
        actor.State.ExternalCallbackActions[0].PendingReplyDelivery.Should().NotBeNull("produced output must survive failed outbound");
        actor.State.LastReplyDelivery?.Delivered.Should().BeNull();

        actor = await harness.ActivateAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await DeliverAsync(actor, new DeferredLlmReplyDispatchRequestedEvent { CorrelationId = ready.CorrelationId }, actor.Id);
        await DeliverAsync(actor, new ConversationExternalCallbackReplyDeliveryRequested { CallbackId = "cb-alpha" }, actor.Id);
        await DeliverAsync(actor, ready, "run-a");

        harness.Dispatcher.Requests.Should().ContainSingle("delivery retry must not redispatch a terminal AgentRun");
        harness.Runner.InboundCount.Should().Be(1);
        harness.Runner.Replies.Should().HaveCount(2);
        harness.Runner.Replies.Should().OnlyContain(reply => reply.Outbound.Text == "The completed report" &&
            reply.RegistrationId == "registration-a" && reply.Activity.Conversation.CanonicalKey == "lark:tenant-a:chat-a:thread-a:sender-a");
        actor.State.ExternalCallbackActions[0].ReplyDelivered.Should().BeTrue();
        actor.State.PendingLlmReplyRequests.Should().BeEmpty();
        actor.State.RetainedHistory.Should().ContainSingle(entry => entry.Content == "The completed report");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProducedReply_ActivationAroundOutbound_ShouldReplayDurablePayload(bool crashAfterRemoteAcceptance)
    {
        var harness = new Harness();
        var actor = await AdmitResumeAsync(harness);
        var ready = BuildReady(harness);
        ready.ReplyToken = "must-not-persist";
        ready.Activity.TransportExtras.NyxUserAccessToken = "must-not-persist";
        if (crashAfterRemoteAcceptance)
            harness.Runner.AfterSend = () => harness.Store.RejectDeliveredCommit = true;
        await DeliverAsync(actor, ready, "run-a");
        if (crashAfterRemoteAcceptance)
            await Assert.ThrowsAsync<IOException>(() => DeliverAsync(actor, new ConversationExternalCallbackReplyDeliveryRequested { CallbackId = "cb-alpha" }, actor.Id));
        else
            harness.Runner.Replies.Should().BeEmpty("ready acceptance must commit its outbox before a self-turn sends it");
        harness.Store.RejectDeliveredCommit = false;
        harness.Runner.AfterSend = null;
        actor = await harness.ActivateAsync();
        var saved = actor.State.ExternalCallbackActions[0].PendingReplyDelivery;
        saved.Should().NotBeNull();
        saved.ReplyToken.Should().BeEmpty();
        saved.Activity.TransportExtras.NyxUserAccessToken.Should().BeEmpty();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await DeliverAsync(actor, new ConversationExternalCallbackReplyDeliveryRequested { CallbackId = "cb-alpha" }, actor.Id);

        harness.Runner.Replies.Should().HaveCount(crashAfterRemoteAcceptance ? 2 : 1);
        harness.Dispatcher.Requests.Should().ContainSingle();
        actor.State.ExternalCallbackActions[0].ReplyDelivered.Should().BeTrue();
        actor.State.LastReplyDelivery.Delivered.Should().NotBeNull();
    }

    [Theory]
    [InlineData("link")]
    [InlineData("resume")]
    [InlineData("reply")]
    public async Task PermanentOriginFailure_ShouldPersistTerminalReceiptAndAbsorbStaleTimers(string phase)
    {
        var harness = new Harness();
        var actor = await harness.ActivateAsync();
        var context = BuildContext();
        context.ContextRevision = phase == "link" ? 1 : 2;
        await DeliverAsync(actor, context);
        if (phase == "link")
        {
            harness.Runner.LinkResult = ConversationTurnResult.PermanentFailure("callback_origin_unavailable", "removed");
            await DeliverAsync(actor, new ExternalCallbackLinkReady
            {
                CallbackId = context.CallbackId, OperationActorId = context.OperationActorId, Origin = context.Origin.Clone(),
                ContextRevision = 1, Kind = ExternalCallbackKind.ConnectLink, ConnectUrl = "https://nyx.example/connect/exact",
            });
            await DeliverAsync(actor, new ConversationExternalCallbackLinkDeliveryRequested { CallbackId = "cb-alpha" }, actor.Id);
        }
        else
        {
            if (phase == "resume")
                harness.Runner.ResumeResult = ConversationTurnResult.PermanentFailure("callback_origin_unavailable", "removed");
            await DeliverAsync(actor, new CallbackCompleted { CallbackId = "cb-alpha", Result = CallbackResult.Succeeded });
            await DeliverAsync(actor, new ConversationExternalCallbackResumeRequested { CallbackId = "cb-alpha" }, actor.Id);
            if (phase == "reply")
            {
                harness.Runner.ReplyResults.Enqueue(ConversationTurnResult.PermanentFailure("callback_registration_mismatch", "removed"));
                await DeliverAsync(actor, BuildReady(harness), "run-a");
                await DeliverAsync(actor, new ConversationExternalCallbackReplyDeliveryRequested { CallbackId = "cb-alpha" }, actor.Id);
            }
        }
        actor.State.ExternalCallbackActions[0].FailedPhase.Should().NotBe(ConversationExternalCallbackDeliveryPhase.Unspecified);
        harness.Publisher.Sent.OfType<ExternalCallbackContinuationRejected>().Should().NotBeEmpty();
        if (phase == "reply")
        {
            actor.State.LastReplyDelivery.Failed.Should().NotBeNull();
            actor.State.RecentDeliveries.Should().ContainSingle(delivery =>
                delivery.Status == DeliveryStatus.FailedPreSend);
        }
        var workCount = harness.Runner.Links.Count + harness.Runner.InboundCount + harness.Runner.Replies.Count;
        actor = await harness.ActivateAsync();
        await DeliverAsync(actor, new ConversationExternalCallbackLinkDeliveryRequested { CallbackId = "cb-alpha" }, actor.Id);
        await DeliverAsync(actor, new ConversationExternalCallbackResumeRequested { CallbackId = "cb-alpha" }, actor.Id);
        await DeliverAsync(actor, new ConversationExternalCallbackReplyDeliveryRequested { CallbackId = "cb-alpha" }, actor.Id);
        (harness.Runner.Links.Count + harness.Runner.InboundCount + harness.Runner.Replies.Count).Should().Be(workCount);
        actor.State.PendingLlmReplyRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("link", false)]
    [InlineData("resume", false)]
    [InlineData("reply", false)]
    [InlineData("link", true)]
    [InlineData("resume", true)]
    [InlineData("reply", true)]
    public async Task TransientContinuationFailure_HasFinitePersistedBudgetOrDeadline(string phase, bool expire)
    {
        var harness = new Harness();
        var actor = await harness.ActivateAsync();
        var context = BuildContext();
        if (phase == "link") context.ContextRevision = 1;
        await DeliverAsync(actor, context);
        IMessage trigger;
        if (phase == "link")
        {
            harness.Runner.LinkResult = ConversationTurnResult.TransientFailure("relay_unavailable", "temporary");
            await DeliverAsync(actor, new ExternalCallbackLinkReady
            {
                CallbackId = context.CallbackId, OperationActorId = context.OperationActorId, Origin = context.Origin.Clone(),
                ContextRevision = 1, Kind = ExternalCallbackKind.ConnectLink, ConnectUrl = "https://nyx.example/connect/exact",
            });
            trigger = new ConversationExternalCallbackLinkDeliveryRequested { CallbackId = "cb-alpha" };
        }
        else
        {
            if (phase == "resume") harness.Runner.ResumeResult = ConversationTurnResult.TransientFailure("registration_unavailable", "temporary");
            await DeliverAsync(actor, new CallbackCompleted { CallbackId = "cb-alpha", Result = CallbackResult.Succeeded });
            if (phase == "reply")
            {
                await DeliverAsync(actor, new ConversationExternalCallbackResumeRequested { CallbackId = "cb-alpha" }, actor.Id);
                for (var i = 0; i < 5; i++) harness.Runner.ReplyResults.Enqueue(ConversationTurnResult.TransientFailure("relay_unavailable", "temporary"));
                await DeliverAsync(actor, BuildReady(harness), "run-a");
                trigger = new ConversationExternalCallbackReplyDeliveryRequested { CallbackId = "cb-alpha" };
            }
            else trigger = new ConversationExternalCallbackResumeRequested { CallbackId = "cb-alpha" };
        }
        await DeliverAsync(actor, trigger, actor.Id);
        harness.Clock.Advance(expire ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1));
        actor = await harness.ActivateAsync();
        for (var i = 0; i < 7; i++)
        {
            await DeliverAsync(actor, trigger, actor.Id);
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        actor.State.ExternalCallbackActions[0].FailedPhase.Should().NotBe(ConversationExternalCallbackDeliveryPhase.Unspecified);
        var attempts = phase switch
        {
            "link" => harness.Runner.Links.Count,
            "resume" => harness.Runner.InboundCount,
            _ => harness.Runner.Replies.Count,
        };
        attempts.Should().Be(expire ? 1 : 5);
        harness.Publisher.Sent.OfType<ExternalCallbackContinuationRejected>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task ProducedReply_SavedRouteAndPayloadWinOverDuplicateReadyAndLateDrop()
    {
        var harness = new Harness();
        var actor = await AdmitResumeAsync(harness);
        var ready = BuildReady(harness);
        ready.RegistrationId = "wrong-registration";
        ready.Activity.Conversation.CanonicalKey = "wrong-conversation";
        ready.UseSourceActivityDeliveryContext = true;
        await DeliverAsync(actor, ready, "run-a");
        var conflict = ready.Clone(); conflict.Outbound.Text = "conflicting output";
        await DeliverAsync(actor, conflict, "run-a");
        await DeliverAsync(actor, new DeferredLlmReplyDroppedEvent { CorrelationId = ready.CorrelationId, Reason = "late cleanup" }, "run-a");
        await DeliverAsync(actor, new ConversationExternalCallbackReplyDeliveryRequested { CallbackId = "cb-alpha" }, actor.Id);
        harness.Runner.Replies.Should().ContainSingle().Which.Outbound.Text.Should().Be("The completed report");
        harness.Runner.Replies[0].RegistrationId.Should().Be("registration-a");
        harness.Runner.Replies[0].Activity.Conversation.CanonicalKey.Should().Be("lark:tenant-a:chat-a:thread-a:sender-a");
        actor.State.ExternalCallbackActions[0].ReplyDelivered.Should().BeTrue();
        harness.Dispatcher.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ProducedReply_RecoversExactRouteWhenTransientRequestWasReaped()
    {
        var harness = new Harness();
        var actor = await AdmitResumeAsync(harness);
        var ready = BuildReady(harness);

        // A late ready event can arrive after an unrelated terminal cleanup has
        // reaped the transient run request. The callback action remains the
        // durable authority for the original route.
        await DeliverAsync(actor, new DeferredLlmReplyDroppedEvent
        {
            CorrelationId = ready.CorrelationId, Reason = "late run cleanup",
        }, "run-a");
        actor.State.PendingLlmReplyRequests.Should().BeEmpty();
        await DeliverAsync(actor, ready, "run-a");

        actor.State.ExternalCallbackActions[0].PendingReplyDelivery.Should().NotBeNull();
        var saved = actor.State.ExternalCallbackActions[0].PendingReplyDelivery!;
        saved.RegistrationId.Should().Be("registration-a");
        saved.RunId.Should().Be(ready.RunId);
        saved.CorrelationId.Should().Be(ready.CorrelationId);
        saved.Outbound.Text.Should().Be("The completed report");
        saved.TerminalState.Should().Be(LlmReplyTerminalState.Completed);
        saved.AppendedHistory.Should().ContainSingle().Which.Content.Should().Be("The completed report");
        saved.Activity.Conversation.CanonicalKey
            .Should().Be("lark:tenant-a:chat-a:thread-a:sender-a");

        await DeliverAsync(actor, new ConversationExternalCallbackReplyDeliveryRequested { CallbackId = "cb-alpha" }, actor.Id);
        harness.Runner.Replies.Should().ContainSingle().Which.Outbound.Text.Should().Be("The completed report");
        harness.Dispatcher.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task CallbackRunDispatch_StopsAtPersistedBudgetAndAcknowledgesOperation()
    {
        var harness = new Harness();
        harness.Dispatcher.AlwaysFail = true;
        var actor = await AdmitResumeAsync(harness);
        var request = harness.Dispatcher.Requests[0];

        for (var attempt = 0; attempt < ConversationGAgent.MaxExternalCallbackDeliveryAttempts + 2; attempt++)
        {
            actor = await harness.ActivateAsync();
            await DeliverAsync(actor, new DeferredLlmReplyDispatchRequestedEvent
            {
                CorrelationId = request.CorrelationId,
            }, actor.Id);
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        actor.State.ExternalCallbackActions[0].FailedPhase.Should()
            .Be(ConversationExternalCallbackDeliveryPhase.RunDispatch);
        harness.Publisher.Sent.OfType<ExternalCallbackContinuationRejected>().Should().NotBeEmpty();
        var calls = harness.Dispatcher.Requests.Count;

        actor = await harness.ActivateAsync();
        await DeliverAsync(actor, new DeferredLlmReplyDispatchRequestedEvent
        {
            CorrelationId = request.CorrelationId,
        }, actor.Id);
        harness.Dispatcher.Requests.Count.Should().Be(calls);
        calls.Should().Be(ConversationGAgent.MaxExternalCallbackDeliveryAttempts);
    }

    [Fact]
    public async Task CallbackRunDispatch_AcceptedHandoffIsNotRedispatchedOnActivation()
    {
        var harness = new Harness();
        var actor = await AdmitResumeAsync(harness);
        var request = harness.Dispatcher.Requests[0];
        harness.Clock.Advance(TimeSpan.FromHours(1));

        actor = await harness.ActivateAsync();
        await DeliverAsync(actor, new DeferredLlmReplyDispatchRequestedEvent
        {
            CorrelationId = request.CorrelationId,
        }, actor.Id);

        harness.Dispatcher.Requests.Should().ContainSingle();
        actor.State.ExternalCallbackActions[0].FailedPhase.Should().Be(ConversationExternalCallbackDeliveryPhase.Unspecified);
    }

    private static async Task<ConversationGAgent> AdmitResumeAsync(Harness harness)
    {
        var actor = await harness.ActivateAsync();
        await DeliverAsync(actor, BuildContext());
        await DeliverAsync(actor, new CallbackCompleted { CallbackId = "cb-alpha", Result = CallbackResult.Succeeded });
        await DeliverAsync(actor, new ConversationExternalCallbackResumeRequested { CallbackId = "cb-alpha" }, actor.Id);
        return actor;
    }

    private static LlmReplyReadyEvent BuildReady(Harness harness)
    {
        var request = harness.Dispatcher.Requests[0];
        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = request.CorrelationId, RunId = request.RunId, RegistrationId = request.RegistrationId,
            Activity = request.Activity.Clone(), Outbound = new MessageContent { Text = "The completed report" },
            TerminalState = LlmReplyTerminalState.Completed,
        };
        ready.AppendedHistory.Add(new ConversationHistoryEntry { Role = "assistant", Content = "The completed report" });
        return ready;
    }

    private static ExternalCallbackActionContextRecorded BuildContext() => new()
    {
        CallbackId = "cb-alpha", OperationActorId = "operation-alpha", ContextRevision = 2,
        Origin = new ChannelCallbackOrigin
        {
            ConversationActorId = "conversation-alpha", ChannelRegistrationId = "registration-a", ActionId = "action-a",
            OriginalActivity = new ChatActivity
            {
                Id = "activity-a", Type = ActivityType.Message, ChannelId = ChannelId.From("lark"),
                Bot = BotInstanceId.From("registration-a"), From = new ParticipantRef { CanonicalId = "sender-a" },
                Content = new MessageContent { Text = "Create the report" },
                Conversation = new ConversationReference
                {
                    CanonicalKey = "lark:tenant-a:chat-a:thread-a:sender-a", Scope = ConversationScope.Thread,
                    Channel = ChannelId.From("lark"), Bot = BotInstanceId.From("registration-a"), Partition = "tenant-a",
                },
                OutboundDelivery = new OutboundDeliveryContext { ReplyMessageId = "relay-message-a" },
                TransportExtras = new TransportExtras { NyxAgentApiKeyId = "key-a", NyxPlatform = "lark" },
            },
        },
        Authorization = new CallbackAuthorizationReference
        {
            BindingId = "binding-a", OwnerScopeId = "sender-owner",
            ExternalSubject = new ExternalSubjectRef { Platform = "lark", Tenant = "tenant-a", ExternalUserId = "sender-a" },
        },
        VerifiedReferences = new ExternalCallbackVerifiedReferences
        {
            BindingId = "binding-a", OwnerScopeId = "sender-owner", ConnectLinkId = "link-a",
            ConnectedServiceId = "instance-a", ConnectedServiceSlug = "google-a", CatalogServiceSlug = "google",
        },
    };

    private static Task DeliverAsync(ConversationGAgent actor, IMessage message, string publisher = "operation-alpha") =>
        actor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"), Payload = Any.Pack(message),
            Route = new EnvelopeRoute { PublisherActorId = publisher, Direct = new DirectRoute { TargetActorId = actor.Id } },
        });

    private sealed class Harness
    {
        public FaultingStore Store { get; } = new();
        public ManualClock Clock { get; } = new();
        public IdentityGAgentTestHarness.NoopCallbackScheduler Scheduler { get; } = new();
        public Publisher Publisher { get; } = new();
        public Dispatcher Dispatcher { get; } = new();
        public Runner Runner { get; } = new();
        public async Task<ConversationGAgent> ActivateAsync()
        {
            var services = new ServiceCollection().AddSingleton<IEventStore>(Store).AddSingleton<TimeProvider>(Clock)
                .AddSingleton<IActorDispatchPort, DispatchPort>()
                .AddSingleton<IActorRuntimeCallbackScheduler>(Scheduler)
                .AddSingleton<IConversationTurnRunner>(Runner).AddSingleton<IChannelLlmReplyRunDispatcher>(Dispatcher)
                .AddSingleton<EventSourcingRuntimeOptions>()
                .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
                .BuildServiceProvider();
            var actor = new ConversationGAgent { Services = services, EventPublisher = Publisher,
                EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<ConversationGAgentState>>() };
            var type = actor.GetType();
            while (type is not null)
            {
                if (type.GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic) is { } method)
                { method.Invoke(actor, ["conversation-alpha"]); break; }
                type = type.BaseType;
            }
            await actor.ActivateAsync();
            return actor;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FaultingStore : IEventStore
    {
        private readonly IdentityGAgentTestHarness.InMemoryEventStore _inner = new();
        public bool RejectDeliveredCommit { get; set; }
        public Task<EventStoreCommitResult> AppendAsync(string agentId, IEnumerable<StateEvent> events, long expectedVersion, CancellationToken ct = default)
        {
            var batch = events.ToArray();
            if (RejectDeliveredCommit && batch.Any(evt => evt.EventData.Is(LlmReplyDeliveredEvent.Descriptor)))
                throw new IOException("simulated process loss after remote acceptance before delivered commit");
            return _inner.AppendAsync(agentId, batch, expectedVersion, ct);
        }
        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(string agentId, long? fromVersion = null, CancellationToken ct = default) =>
            _inner.GetEventsAsync(agentId, fromVersion, ct);
        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) => _inner.GetVersionAsync(agentId, ct);
        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default) => _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private sealed class Publisher : IEventPublisher
    {
        public List<IMessage> Sent { get; } = [];
        public bool FailNextConsumptionAck { get; set; }
        public Task PublishAsync<T>(T message, TopologyAudience audience = TopologyAudience.Children, CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null, EventEnvelopePublishOptions? options = null) where T : IMessage => Task.CompletedTask;
        public Task SendToAsync<T>(string targetActorId, T message, CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null, EventEnvelopePublishOptions? options = null) where T : IMessage
        {
            if (FailNextConsumptionAck && message is CallbackCompletionConsumed)
            {
                FailNextConsumptionAck = false;
                throw new IOException("simulated consumption acknowledgment transport failure");
            }
            Sent.Add(message.Descriptor.Parser.ParseFrom(message.ToByteArray()));
            return Task.CompletedTask;
        }
    }
    private sealed class Dispatcher : IChannelLlmReplyRunDispatcher
    {
        public List<NeedsLlmReplyEvent> Requests { get; } = [];
        public bool FailNext { get; set; }
        public bool AlwaysFail { get; set; }
        public Task DispatchAsync(NeedsLlmReplyEvent request, CancellationToken ct)
        {
            Requests.Add(request.Clone());
            if (AlwaysFail || FailNext)
            { FailNext = false; throw new IOException("simulated dispatch interruption"); }
            return Task.CompletedTask;
        }
    }
    private sealed class DispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }
    private sealed class Runner : IConversationTurnRunner
    {
        public int ContinueCount { get; private set; }
        public int InboundCount { get; private set; }
        public List<ExternalCallbackLinkReady> Links { get; } = [];
        public List<LlmReplyReadyEvent> Replies { get; } = [];
        public Queue<ConversationTurnResult> ReplyResults { get; } = new();
        public ConversationTurnResult? LinkResult { get; set; }
        public ConversationTurnResult? ResumeResult { get; set; }
        public Action? AfterSend { get; set; }
        public Task<ConversationTurnResult> RunExternalCallbackLinkAsync(ExternalCallbackLinkReady link,
            ConversationTurnRuntimeContext context, CancellationToken ct)
        {
            Links.Add(link.Clone());
            return Task.FromResult(LinkResult ?? ConversationTurnResult.Sent("link-sent", new MessageContent { Text = link.ConnectUrl }, "bot"));
        }
        public Task<ConversationTurnResult> RunInboundAsync(ChatActivity activity, ConversationTurnRuntimeContext context, CancellationToken ct)
        {
            InboundCount++;
            return Task.FromResult(ResumeResult ?? ConversationTurnResult.LlmReplyRequested(new NeedsLlmReplyEvent
            { Activity = activity.Clone(), RegistrationId = "registration-a", RunId = "runner-generated", CorrelationId = activity.Id }));
        }
        public Task<ConversationTurnResult> RunLlmReplyAsync(LlmReplyReadyEvent reply, ConversationTurnRuntimeContext context, CancellationToken ct)
        {
            Replies.Add(reply.Clone());
            AfterSend?.Invoke();
            return Task.FromResult(ReplyResults.Count > 0 ? ReplyResults.Dequeue() : ConversationTurnResult.Sent("sent-a", reply.Outbound, "bot"));
        }
        public Task<ConversationTurnResult> RunContinueAsync(ConversationContinueRequestedEvent command, CancellationToken ct)
        { ContinueCount++; return Task.FromResult(ConversationTurnResult.Sent("canned", command.Payload, "bot")); }
        public Task<ConversationStreamChunkResult> RunStreamChunkAsync(LlmReplyStreamChunkEvent chunk, string? messageId,
            NyxRelayTextOperationKind operation, ConversationTurnRuntimeContext context, CancellationToken ct) =>
            Task.FromResult(ConversationStreamChunkResult.Succeeded(messageId));
    }
}
