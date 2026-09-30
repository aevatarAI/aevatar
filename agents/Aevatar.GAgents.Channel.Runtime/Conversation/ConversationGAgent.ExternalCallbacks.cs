using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed partial class ConversationGAgent
{
    public const int MaxExternalCallbackDeliveryAttempts = 5;
    private static readonly TimeSpan ExternalCallbackDeliveryRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ExternalCallbackDeliveryDeadline = TimeSpan.FromMinutes(15);

    private static bool ExternalCallbackDeliveryStopped(ConversationExternalCallbackAction action) =>
        action.FailedPhase != ConversationExternalCallbackDeliveryPhase.Unspecified;

    private async Task<bool> BeginExternalCallbackAttemptAsync(ConversationExternalCallbackAction action,
        ConversationExternalCallbackDeliveryPhase phase, bool forceRecoveryAttempt = false)
    {
        if (ExternalCallbackDeliveryStopped(action)) return false;
        var retry = ExternalCallbackRetry(action, phase);
        if (retry.Attempts >= MaxExternalCallbackDeliveryAttempts ||
            (retry.DeadlineAtUnixMs > 0 && AppendNow >= retry.DeadlineAtUnixMs))
        {
            await FailExternalCallbackDeliveryAsync(action, phase, "callback_delivery_retry_exhausted",
                "The original Channel continuation exceeded its delivery retry limit or deadline.");
            return false;
        }
        if (!forceRecoveryAttempt && AppendNow < retry.NextAttemptAtUnixMs)
        {
            switch (phase)
            {
                case ConversationExternalCallbackDeliveryPhase.Link:
                    await ScheduleExternalLinkDeliveryAsync(action.Context.CallbackId);
                    break;
                case ConversationExternalCallbackDeliveryPhase.Resume:
                    await ScheduleExternalCallbackResumeAsync(action.Context.CallbackId);
                    break;
                case ConversationExternalCallbackDeliveryPhase.Reply:
                    await ScheduleExternalCallbackReplyDeliveryAsync(action.Context.CallbackId);
                    break;
            }
            return false;
        }
        await PersistDomainEventAsync(new ConversationExternalCallbackAttemptStarted
        {
            CallbackId = action.Context.CallbackId, Phase = phase, AttemptCount = retry.Attempts + 1,
            DeadlineAtUnixMs = retry.DeadlineAtUnixMs > 0 ? retry.DeadlineAtUnixMs :
                AppendNow + (long)ExternalCallbackDeliveryDeadline.TotalMilliseconds,
            NextAttemptAtUnixMs = AppendNow + (long)ExternalCallbackDeliveryRetryDelay.TotalMilliseconds,
        });
        return true;
    }

    private static ConversationExternalCallbackRetryState ExternalCallbackRetry(ConversationExternalCallbackAction action,
        ConversationExternalCallbackDeliveryPhase phase) => (phase switch
        {
            ConversationExternalCallbackDeliveryPhase.Link => action.LinkRetry,
            ConversationExternalCallbackDeliveryPhase.Resume => action.ResumeRetry,
            ConversationExternalCallbackDeliveryPhase.Reply => action.ReplyRetry,
            ConversationExternalCallbackDeliveryPhase.RunDispatch => action.RunDispatchRetry,
            _ => null,
        }) ?? new ConversationExternalCallbackRetryState();

    private async Task<bool> StopExternalCallbackFailureAsync(ConversationExternalCallbackAction action,
        ConversationExternalCallbackDeliveryPhase phase, ConversationTurnResult result)
    {
        var current = FindExternalCallbackAction(action.Context.CallbackId)!;
        var retry = ExternalCallbackRetry(current, phase);
        if (result.FailureKind == FailureKind.PermanentAdapterError ||
            retry.Attempts >= MaxExternalCallbackDeliveryAttempts || AppendNow >= retry.DeadlineAtUnixMs)
        {
            await FailExternalCallbackDeliveryAsync(current, phase,
                string.IsNullOrWhiteSpace(result.ErrorCode) ? "callback_delivery_unavailable" : result.ErrorCode,
                string.IsNullOrWhiteSpace(result.ErrorSummary) ? "The original Channel continuation could not be delivered." : result.ErrorSummary);
            return true;
        }
        return false;
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleExternalCallbackReplyDeliveryRequestedAsync(ConversationExternalCallbackReplyDeliveryRequested message)
    {
        var action = FindExternalCallbackAction(message.CallbackId);
        if (action?.PendingReplyDelivery is null || action.ReplyDelivered || ExternalCallbackDeliveryStopped(action)) return;
        if (!await BeginExternalCallbackAttemptAsync(action, ConversationExternalCallbackDeliveryPhase.Reply)) return;
        // Reserve a crash-safe attempt and arm its lease before any vault/relay I/O.
        await ScheduleExternalCallbackReplyDeliveryAsync(message.CallbackId);
        var reply = action.PendingReplyDelivery.Clone();
        ConversationTurnResult result;
        try
        {
            result = await ResolveRunner().RunLlmReplyAsync(reply,
                ConversationTurnRuntimeContext.Empty with { ConversationActorId = Id, UseRegistrationOutbound = true },
                CancellationToken.None);
        }
        catch (Exception)
        {
            Logger.LogWarning("Callback reply delivery unavailable: callback={CallbackId}", message.CallbackId);
            result = ConversationTurnResult.TransientFailure("callback_reply_delivery_unavailable", "The original Channel reply could not be delivered.");
        }
        if (result.Success)
        {
            await CompleteExternalCallbackReplyDeliveryAsync(action, reply, result);
            return;
        }
        if (await StopExternalCallbackFailureAsync(action, ConversationExternalCallbackDeliveryPhase.Reply, result)) return;
        await PersistDomainEventAsync(new LlmReplyDeliveryFailedEvent
        {
            CorrelationId = reply.CorrelationId, RunId = reply.RunId, FailedAtUnixMs = AppendNow,
            ErrorCode = result.ErrorCode, ErrorMessage = result.ErrorSummary,
        });
    }

    private async Task PersistExternalCallbackReplyReadyAsync(ConversationExternalCallbackAction action, LlmReplyReadyEvent ready)
    {
        if (!action.ResumeAdmitted || action.ReplyDelivered || ExternalCallbackDeliveryStopped(action) ||
            ready.RunId != action.ResumeRunId || ready.CorrelationId != action.ResumeActivityId) return;
        if (action.PendingReplyDelivery is null)
        {
            var request = FindPendingLlmReplyRequest(ready.CorrelationId);
            var durable = ready.Clone();
            // The authority is the saved original route, never a producer-supplied replacement.
            if (request is not null)
                durable.Activity = CloneForDurableState(request.Activity);
            else
            {
                if (ready.RunId != action.ResumeRunId || ready.CorrelationId != action.ResumeActivityId)
                    return;
                durable.Activity = action.Context.Origin.OriginalActivity.Clone();
                durable.Activity.Id = action.ResumeActivityId;
                if (durable.Activity.OutboundDelivery is not null)
                    durable.Activity.OutboundDelivery.CorrelationId = action.ResumeActivityId;
            }
            durable.RegistrationId = action.Context.Origin.ChannelRegistrationId;
            durable.UseSourceActivityDeliveryContext = false;
            durable.ReplyToken = string.Empty;
            durable.ReplyTokenExpiresAtUnixMs = 0;
            durable.RelayReplyTokenRef = null;
            durable.RelayUserAccessTokenRef = null;
            await PersistDomainEventAsync(new ConversationExternalCallbackReplyReady
            { CallbackId = action.Context.CallbackId, Reply = durable });
        }
        await ScheduleExternalCallbackReplyDeliveryAsync(action.Context.CallbackId);
        await SendToAsync(Id, new ConversationExternalCallbackReplyDeliveryRequested { CallbackId = action.Context.CallbackId });
    }

    private async Task CompleteExternalCallbackReplyDeliveryAsync(ConversationExternalCallbackAction action,
        LlmReplyReadyEvent ready, ConversationTurnResult result)
    {
        var commandId = BuildLlmReplyCommandId(ready.CorrelationId);
        var completed = new ConversationTurnCompletedEvent
        {
            CausationCommandId = commandId, SentActivityId = result.SentActivityId,
            AuthPrincipal = string.IsNullOrWhiteSpace(result.AuthPrincipal) ? "bot" : result.AuthPrincipal,
            Conversation = ready.Activity.Conversation.Clone(),
            Outbound = result.Outbound?.Clone() ?? ready.Outbound?.Clone() ?? new MessageContent(),
            CompletedAtUnixMs = AppendNow, OutboundDelivery = ToOutboundDeliveryReceipt(result.OutboundDelivery),
        };
        completed.AppendedHistory.AddRange(ready.AppendedHistory.Select(entry => entry.Clone()));
        await PersistDomainEventsAsync([
            new ConversationExternalCallbackReplyDelivered { CallbackId = action.Context.CallbackId },
            new LlmReplyDeliveredEvent
            {
                CorrelationId = ready.CorrelationId, RunId = ready.RunId, AckedAtUnixMs = AppendNow,
                ChannelMessageId = result.OutboundDelivery?.ReplyMessageId ?? string.Empty,
            },
            BuildDeliveryProducedEvent(DeliveryKind.TextMessage, DeliveryStatus.Succeeded,
                ready.Activity, ready.RunId, ready.CorrelationId, commandId, ready.CorrelationId,
                result.OutboundDelivery?.ReplyMessageId, string.Empty),
            completed,
        ]);
        await ClearReplyLifecyclesAsync(ready.CorrelationId, ready.Activity, "external_callback_reply_delivered");
    }

    private async Task FailExternalCallbackDeliveryAsync(ConversationExternalCallbackAction action,
        ConversationExternalCallbackDeliveryPhase phase, string errorCode, string errorSummary)
    {
        var facts = new List<Google.Protobuf.IMessage>
        {
            new ConversationExternalCallbackDeliveryFailed
            {
                CallbackId = action.Context.CallbackId, Phase = phase, ErrorCode = errorCode, ErrorSummary = errorSummary,
            },
        };
        if (action.ResumeAdmitted)
        {
            facts.Add(new LlmReplyDeliveryFailedEvent
            {
                CorrelationId = action.ResumeActivityId, RunId = action.ResumeRunId, FailedAtUnixMs = AppendNow,
                ErrorCode = errorCode, ErrorMessage = errorSummary,
            });
            facts.Add(new ConversationContinueFailedEvent
            {
                CommandId = BuildLlmReplyCommandId(action.ResumeActivityId), CorrelationId = action.ResumeActivityId,
                Kind = FailureKind.PermanentAdapterError, ErrorCode = errorCode, ErrorSummary = errorSummary,
                NotRetryable = new Google.Protobuf.WellKnownTypes.Empty(), FailedAtUnixMs = AppendNow,
            });
            if (phase == ConversationExternalCallbackDeliveryPhase.Reply)
            {
                facts.Add(BuildDeliveryProducedEvent(
                    DeliveryKind.TextMessage,
                    DeliveryStatus.FailedPreSend,
                    action.Context.Origin.OriginalActivity,
                    action.ResumeRunId,
                    action.ResumeActivityId,
                    BuildLlmReplyCommandId(action.ResumeActivityId),
                    action.ResumeActivityId,
                    string.Empty,
                    string.Empty));
            }
        }
        await PersistDomainEventsAsync(facts);
        await AcknowledgeExternalCallbackRejectionAsync(FindExternalCallbackAction(action.Context.CallbackId)!, errorCode);
    }

    private Task AcknowledgeExternalCallbackRejectionAsync(ConversationExternalCallbackAction action, string errorCode) =>
        SendToAsync(action.Context.OperationActorId, new ExternalCallbackContinuationRejected
        {
            CallbackId = action.Context.CallbackId, ContextRevision = action.Context.ContextRevision,
            FailureCode = errorCode,
        });

    private Task ScheduleExternalCallbackReplyDeliveryAsync(string callbackId) =>
        ScheduleSelfDurableTimeoutAsync($"external-callback-reply:{callbackId}", ExternalCallbackDeliveryRetryDelay,
            new ConversationExternalCallbackReplyDeliveryRequested { CallbackId = callbackId });

    private ConversationExternalCallbackAction? FindExternalCallbackReplyAction(LlmReplyReadyEvent ready) =>
        State.ExternalCallbackActions.FirstOrDefault(action => action.ResumeAdmitted &&
            !string.IsNullOrWhiteSpace(action.ResumeActivityId) && action.ResumeActivityId == ready.CorrelationId);

    private static ConversationGAgentState ApplyExternalCallbackReplyReady(
        ConversationGAgentState current, ConversationExternalCallbackReplyReady message)
    {
        var next = current.Clone();
        var action = next.ExternalCallbackActions.FirstOrDefault(item => item.Context.CallbackId == message.CallbackId);
        if (action is not null && action.PendingReplyDelivery is null)
            action.PendingReplyDelivery = message.Reply.Clone();
        return next;
    }

    private static ConversationGAgentState ApplyExternalCallbackAttemptStarted(
        ConversationGAgentState current, ConversationExternalCallbackAttemptStarted message)
    {
        var next = current.Clone();
        var action = next.ExternalCallbackActions.FirstOrDefault(item => item.Context.CallbackId == message.CallbackId);
        if (action is null) return next;
        var retry = message.Phase switch
        {
            ConversationExternalCallbackDeliveryPhase.Link => action.LinkRetry,
            ConversationExternalCallbackDeliveryPhase.Resume => action.ResumeRetry,
            ConversationExternalCallbackDeliveryPhase.Reply => action.ReplyRetry,
            ConversationExternalCallbackDeliveryPhase.RunDispatch => action.RunDispatchRetry,
            _ => null,
        } ?? new ConversationExternalCallbackRetryState();
        retry.Attempts = message.AttemptCount;
        retry.DeadlineAtUnixMs = message.DeadlineAtUnixMs;
        retry.NextAttemptAtUnixMs = message.NextAttemptAtUnixMs;
        switch (message.Phase)
        {
            case ConversationExternalCallbackDeliveryPhase.Link: action.LinkRetry = retry; break;
            case ConversationExternalCallbackDeliveryPhase.Resume: action.ResumeRetry = retry; break;
            case ConversationExternalCallbackDeliveryPhase.Reply: action.ReplyRetry = retry; break;
            case ConversationExternalCallbackDeliveryPhase.RunDispatch: action.RunDispatchRetry = retry; break;
        }
        return next;
    }

    private static ConversationGAgentState ApplyExternalCallbackReplyDelivered(
        ConversationGAgentState current, ConversationExternalCallbackReplyDelivered message)
    {
        var next = current.Clone();
        var action = next.ExternalCallbackActions.FirstOrDefault(item => item.Context.CallbackId == message.CallbackId);
        if (action is not null)
        {
            action.PendingReplyDelivery = null;
            action.ReplyDelivered = true;
        }
        return next;
    }

    private static ConversationGAgentState ApplyExternalCallbackRunDispatchConfirmed(
        ConversationGAgentState current, ConversationExternalCallbackRunDispatchConfirmed message)
    {
        var next = current.Clone();
        var action = next.ExternalCallbackActions.FirstOrDefault(item => item.Context.CallbackId == message.CallbackId);
        if (action is not null)
            action.RunDispatchConfirmed = true;
        return next;
    }

    private static ConversationGAgentState ApplyExternalCallbackDeliveryFailed(
        ConversationGAgentState current, ConversationExternalCallbackDeliveryFailed message)
    {
        var next = current.Clone();
        var action = next.ExternalCallbackActions.FirstOrDefault(item => item.Context.CallbackId == message.CallbackId);
        if (action is not null)
        {
            action.FailedPhase = message.Phase;
            action.DeliveryFailureCode = message.ErrorCode;
            action.DeliveryFailureSummary = message.ErrorSummary;
            if (message.Phase == ConversationExternalCallbackDeliveryPhase.Reply)
                action.PendingReplyDelivery = null;
        }
        return next;
    }

    [EventHandler]
    public async Task HandleExternalCallbackLinkReadyAsync(ExternalCallbackLinkReady message)
    {
        var action = FindExternalCallbackAction(message.CallbackId);
        if (action is null || message.OperationActorId != action.Context.OperationActorId ||
            !Equals(message.Origin, action.Context.Origin) || message.ContextRevision <= 0 ||
            !Uri.TryCreate(message.ConnectUrl, UriKind.Absolute, out var link) ||
            link.Scheme is not ("https" or "http"))
            return;
        if (message.ContextRevision > action.Context.ContextRevision)
            return;
        if (ExternalCallbackDeliveryStopped(action))
        {
            await AcknowledgeExternalCallbackRejectionAsync(action, action.DeliveryFailureCode);
            return;
        }
        if (message.ContextRevision < action.Context.ContextRevision ||
            action.Result != CallbackResult.Unspecified || action.LinkDelivered)
        {
            await AcknowledgeExternalLinkAsync(message);
            return;
        }
        if (action.Link is not null && !action.Link.Equals(message))
            return;
        if (action.Link is null)
            await PersistDomainEventAsync(new ConversationExternalCallbackLinkReceived { Link = message.Clone() });
        await ScheduleExternalLinkDeliveryAsync(message.CallbackId);
        await SendToAsync(Id, new ConversationExternalCallbackLinkDeliveryRequested { CallbackId = message.CallbackId });
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleExternalCallbackLinkDeliveryRequestedAsync(ConversationExternalCallbackLinkDeliveryRequested message)
    {
        var action = FindExternalCallbackAction(message.CallbackId);
        if (action?.Link is null)
            return;
        if (action.Link.ContextRevision < action.Context.ContextRevision ||
            action.Result != CallbackResult.Unspecified || action.LinkDelivered)
        {
            await AcknowledgeExternalLinkAsync(action.Link);
            return;
        }
        if (!await BeginExternalCallbackAttemptAsync(action, ConversationExternalCallbackDeliveryPhase.Link)) return;
        await ScheduleExternalLinkDeliveryAsync(message.CallbackId);
        ConversationTurnResult result;
        try
        {
            result = await ResolveRunner().RunExternalCallbackLinkAsync(action.Link,
                ConversationTurnRuntimeContext.Empty with { ConversationActorId = Id, UseRegistrationOutbound = true },
                CancellationToken.None);
        }
        catch (Exception)
        {
            Logger.LogWarning("External callback link delivery preparation failed: callback={CallbackId}", message.CallbackId);
            var unavailable = ConversationTurnResult.TransientFailure(
                "callback_link_delivery_unavailable", "The original Channel link could not be delivered.");
            if (!await StopExternalCallbackFailureAsync(action, ConversationExternalCallbackDeliveryPhase.Link, unavailable))
                await ScheduleExternalLinkDeliveryAsync(message.CallbackId);
            return;
        }
        if (!result.Success)
        {
            if (!await StopExternalCallbackFailureAsync(action, ConversationExternalCallbackDeliveryPhase.Link, result))
                await ScheduleExternalLinkDeliveryAsync(message.CallbackId);
            return;
        }
        await PersistDomainEventAsync(new ConversationExternalCallbackLinkDelivered { CallbackId = message.CallbackId });
        await AcknowledgeExternalLinkAsync(action.Link);
    }

    [EventHandler]
    public async Task HandleExternalCallbackActionContextAsync(ExternalCallbackActionContextRecorded message)
    {
        if (string.IsNullOrWhiteSpace(message.CallbackId) || string.IsNullOrWhiteSpace(message.OperationActorId) ||
            message.ContextRevision <= 0 || message.Origin?.OriginalActivity?.Conversation is null ||
            message.Origin.ConversationActorId != Id || string.IsNullOrWhiteSpace(message.Origin.ChannelRegistrationId))
            return;

        var existing = FindExternalCallbackAction(message.CallbackId);
        if (existing is not null)
        {
            if (existing.Context.OperationActorId != message.OperationActorId ||
                !existing.Context.Origin.Equals(message.Origin) ||
                !AcceptsAuthorizationUpdate(existing.Context, message) ||
                message.ContextRevision < existing.Context.ContextRevision)
                return;
            if (message.ContextRevision == existing.Context.ContextRevision && !existing.Context.Equals(message))
                return;
            if (existing.Result != CallbackResult.Unspecified && !existing.Context.Equals(message))
                return;
        }

        if (existing is null || message.ContextRevision > existing.Context.ContextRevision)
        {
            var durable = message.Clone();
            durable.Origin.OriginalActivity = CloneForDurableState(durable.Origin.OriginalActivity);
            await PersistDomainEventAsync(new ConversationExternalCallbackContextCommitted { Context = durable });
        }
        var committed = FindExternalCallbackAction(message.CallbackId)!;
        if (ExternalCallbackDeliveryStopped(committed))
        {
            await AcknowledgeExternalCallbackRejectionAsync(committed, committed.DeliveryFailureCode);
            return;
        }
        await SendToAsync(message.OperationActorId, new ExternalCallbackActionContextAccepted
        {
            CallbackId = message.CallbackId, ContextRevision = message.ContextRevision,
        });
    }

    [EventHandler]
    public async Task HandleExternalCallbackCompletedAsync(CallbackCompleted message)
    {
        var action = FindExternalCallbackAction(message.CallbackId);
        if (action is null || message.Result is not (CallbackResult.Succeeded or CallbackResult.Failed or
            CallbackResult.Cancelled or CallbackResult.Expired))
            return;
        if (ExternalCallbackDeliveryStopped(action))
        {
            await AcknowledgeExternalCallbackRejectionAsync(action, action.DeliveryFailureCode);
            return;
        }
        if (action.Result != CallbackResult.Unspecified && action.Result != message.Result)
        {
            Logger.LogWarning("Conflicting external completion ignored: callback={CallbackId}", message.CallbackId);
            return;
        }
        if (message.Result == CallbackResult.Succeeded &&
            (string.IsNullOrWhiteSpace(action.Context.VerifiedReferences?.BindingId) ||
             string.IsNullOrWhiteSpace(action.Context.VerifiedReferences?.OwnerScopeId)))
            return;

        if (action.Result == CallbackResult.Unspecified)
        {
            await PersistDomainEventAsync(new ConversationExternalCallbackCompletionCommitted
            {
                CallbackId = message.CallbackId, Result = message.Result,
                ResumeRunId = Guid.NewGuid().ToString("N"),
                ResumeActivityId = Guid.NewGuid().ToString("N"),
            });
            action = FindExternalCallbackAction(message.CallbackId)!;
        }
        if (action.ResumeAdmitted)
        {
            await AcknowledgeExternalCallbackAsync(action);
            await DispatchAdmittedExternalCallbackAsync(action);
            return;
        }
        await ScheduleExternalCallbackResumeAsync(message.CallbackId);
        await SendToAsync(Id, new ConversationExternalCallbackResumeRequested { CallbackId = message.CallbackId });
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleExternalCallbackResumeRequestedAsync(ConversationExternalCallbackResumeRequested message)
    {
        var action = FindExternalCallbackAction(message.CallbackId);
        if (action is null || action.Result == CallbackResult.Unspecified)
            return;
        if (action.ResumeAdmitted)
        {
            // The timeout armed before preparation also recovers dispatch when
            // the consumption acknowledgment interrupts the admission turn.
            await DispatchAdmittedExternalCallbackAsync(action);
            return;
        }

        var origin = action.Context.Origin;
        var activity = origin.OriginalActivity.Clone();
        activity.Id = action.ResumeActivityId;
        activity.Type = ActivityType.Message;
        activity.Content = new MessageContent { Text = BuildExternalCallbackBusinessInput(action) };
        if (activity.OutboundDelivery is not null)
            activity.OutboundDelivery.CorrelationId = action.ResumeActivityId;
        activity = CloneForDurableState(activity)!;
        var runtimeContext = ConversationTurnRuntimeContext.Empty with
        {
            ConversationActorId = Id, UseRegistrationOutbound = true,
        };
        if (!await BeginExternalCallbackAttemptAsync(action, ConversationExternalCallbackDeliveryPhase.Resume)) return;
        await ScheduleExternalCallbackResumeAsync(message.CallbackId);
        ConversationTurnResult result;
        try
        {
            result = await ResolveRunner().RunExternalCallbackAsync(activity, origin,
                action.Context.Authorization ?? new CallbackAuthorizationReference(),
                action.Context.VerifiedReferences ?? new ExternalCallbackVerifiedReferences(),
                action.Result, runtimeContext, CancellationToken.None);
        }
        catch (Exception)
        {
            Logger.LogWarning("External callback business resumption preparation failed: callback={CallbackId}", message.CallbackId);
            var unavailable = ConversationTurnResult.TransientFailure(
                "callback_resume_unavailable", "The original Channel continuation could not be prepared.");
            if (!await StopExternalCallbackFailureAsync(action, ConversationExternalCallbackDeliveryPhase.Resume, unavailable))
                await ScheduleExternalCallbackResumeAsync(message.CallbackId);
            return;
        }
        if (result.LlmReplyRequest is not { } generated)
        {
            Logger.LogWarning("External callback resumption did not produce a streamed run: callback={CallbackId}, error={ErrorCode}",
                message.CallbackId, result.ErrorCode);
            if (!await StopExternalCallbackFailureAsync(action, ConversationExternalCallbackDeliveryPhase.Resume, result))
                await ScheduleExternalCallbackResumeAsync(message.CallbackId);
            return;
        }

        var request = generated.Clone();
        request.RunId = action.ResumeRunId;
        request.CorrelationId = action.ResumeActivityId;
        request.ExternalCallbackId = message.CallbackId;
        request.TargetActorId = Id;
        request.RegistrationId = origin.ChannelRegistrationId;
        request.Activity ??= activity;
        request.Activity.Id = action.ResumeActivityId;
        request.Activity.Conversation = activity.Conversation?.Clone();
        request.Activity.From = activity.From?.Clone();
        request.Activity.OutboundDelivery = activity.OutboundDelivery?.Clone();
        request.RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        request.ReplyToken = string.Empty;
        request.ReplyTokenExpiresAtUnixMs = 0;
        request.RelayReplyTokenRef = null;
        request.RelayUserAccessTokenRef = null;
        request.ContextAttachments = State.ContextAttachments?.Clone();
        request.PriorHistory.Clear();
        request.PriorHistory.AddRange(State.RetainedHistory.Select(entry => entry.Clone()));
        StampToolContinuationOwner(request);
        var durable = request.Clone();
        durable.Activity = CloneForDurableState(durable.Activity);
        durable.TargetRef = null;
        durable.LlmControl = null;
        durable.RecentAttachmentActivities.Clear();
        StripRuntimeCredentialsFromToolContext(durable);
        LlmReplyCredentialMetadataKeys.StripFrom(durable.Metadata);
        await PersistDomainEventsAsync([
            durable,
            new ConversationExternalCallbackResumeAdmitted { CallbackId = message.CallbackId },
        ]);
        await AcknowledgeExternalCallbackAsync(FindExternalCallbackAction(message.CallbackId)!);
        await DispatchPendingLlmReplyAsync(request, CancellationToken.None);
    }

    private Task ScheduleExternalCallbackResumeAsync(string callbackId) =>
        ScheduleSelfDurableTimeoutAsync($"external-callback-resume:{callbackId}", TimeSpan.FromSeconds(60),
            new ConversationExternalCallbackResumeRequested { CallbackId = callbackId });

    private async Task DispatchAdmittedExternalCallbackAsync(ConversationExternalCallbackAction action)
    {
        var request = FindPendingLlmReplyRequest(action.ResumeActivityId);
        if (request is not null)
            await DispatchPendingLlmReplyAsync(request, CancellationToken.None);
    }

    private async Task RecoverExternalCallbackActionsAsync(CancellationToken ct)
    {
        foreach (var action in State.ExternalCallbackActions.ToArray())
        {
            if (ExternalCallbackDeliveryStopped(action))
            {
                await AcknowledgeExternalCallbackRejectionAsync(action, action.DeliveryFailureCode);
                continue;
            }
            if (action.Link is not null && action.Link.ContextRevision == action.Context.ContextRevision &&
                !action.LinkDelivered && action.Result == CallbackResult.Unspecified)
            {
                await ScheduleExternalLinkDeliveryAsync(action.Context.CallbackId);
                await SendToAsync(Id, new ConversationExternalCallbackLinkDeliveryRequested { CallbackId = action.Context.CallbackId }, ct);
            }
            if (action.PendingReplyDelivery is not null && !action.ReplyDelivered && !ExternalCallbackDeliveryStopped(action))
            {
                await ScheduleExternalCallbackReplyDeliveryAsync(action.Context.CallbackId);
                await SendToAsync(Id, new ConversationExternalCallbackReplyDeliveryRequested { CallbackId = action.Context.CallbackId }, ct);
            }
            if (action.Result == CallbackResult.Unspecified)
                continue;
            if (action.ResumeAdmitted)
                await AcknowledgeExternalCallbackAsync(action);
            else
            {
                await ScheduleExternalCallbackResumeAsync(action.Context.CallbackId);
                await SendToAsync(Id, new ConversationExternalCallbackResumeRequested { CallbackId = action.Context.CallbackId }, ct);
            }
        }
    }

    private Task AcknowledgeExternalCallbackAsync(ConversationExternalCallbackAction action) =>
        SendToAsync(action.Context.OperationActorId, new CallbackCompletionConsumed
        { CallbackId = action.Context.CallbackId, Result = action.Result });

    private Task AcknowledgeExternalLinkAsync(ExternalCallbackLinkReady link) =>
        SendToAsync(link.OperationActorId, new ExternalCallbackLinkPresented
        { CallbackId = link.CallbackId, ContextRevision = link.ContextRevision });

    private Task ScheduleExternalLinkDeliveryAsync(string callbackId) =>
        ScheduleSelfDurableTimeoutAsync($"external-callback-link:{callbackId}", TimeSpan.FromSeconds(60),
            new ConversationExternalCallbackLinkDeliveryRequested { CallbackId = callbackId });

    private ConversationExternalCallbackAction? FindExternalCallbackAction(string callbackId) =>
        State.ExternalCallbackActions.FirstOrDefault(action => action.Context.CallbackId == callbackId);

    private static bool AcceptsAuthorizationUpdate(ExternalCallbackActionContextRecorded previous,
        ExternalCallbackActionContextRecorded next) =>
        Equals(previous.Authorization, next.Authorization) ||
        (next.ContextRevision > previous.ContextRevision &&
         Equals(previous.Authorization?.ExternalSubject, next.Authorization?.ExternalSubject) &&
         !string.IsNullOrWhiteSpace(next.Authorization?.BindingId) &&
         !string.IsNullOrWhiteSpace(next.Authorization.OwnerScopeId) &&
         next.VerifiedReferences?.BindingId == next.Authorization.BindingId &&
         next.VerifiedReferences.OwnerScopeId == next.Authorization.OwnerScopeId);

    private static string BuildExternalCallbackBusinessInput(ConversationExternalCallbackAction action)
    {
        var references = action.Context.VerifiedReferences;
        var evidence = action.Result == CallbackResult.Succeeded
            ? "The original user's authorization has been verified. " +
              (string.IsNullOrWhiteSpace(references.ConnectedServiceId) ? string.Empty :
                  $"Verified connected service: {references.ConnectedServiceId} ({references.ConnectedServiceSlug}); " +
                  $"catalog service: {references.CatalogServiceSlug}; Connect Link: {references.ConnectLinkId}.")
            : "The external operation did not succeed. Explain the outcome and decide the appropriate next step; do not assume authorization or a connection was established.";
        return $"{action.Context.Origin.OriginalActivity.Content?.Text}\n\n" +
               $"[External operation result: {action.Result}]\n{evidence}\n" +
               "Resume the original business request using this verified result and the preceding conversation. Do not repeat the completed external operation.";
    }

    private void StampToolContinuationOwner(NeedsLlmReplyEvent request)
    {
        if (request.ToolContext?.Channel?.Continuation is { } continuation)
            continuation.ConversationActorId = Id;
    }

    private static ConversationGAgentState ApplyExternalCallbackContext(ConversationGAgentState current,
        ConversationExternalCallbackContextCommitted message)
    {
        var next = current.Clone();
        var action = next.ExternalCallbackActions.FirstOrDefault(item => item.Context.CallbackId == message.Context.CallbackId);
        if (action is null)
            next.ExternalCallbackActions.Add(new ConversationExternalCallbackAction { Context = message.Context.Clone() });
        else
            action.Context = message.Context.Clone();
        return next;
    }

    private static ConversationGAgentState ApplyExternalCallbackCompletion(ConversationGAgentState current,
        ConversationExternalCallbackCompletionCommitted message)
    {
        var next = current.Clone();
        var action = next.ExternalCallbackActions.FirstOrDefault(item => item.Context.CallbackId == message.CallbackId);
        if (action is not null)
        {
            action.Result = message.Result;
            action.ResumeRunId = message.ResumeRunId;
            action.ResumeActivityId = message.ResumeActivityId;
        }
        return next;
    }

    private static ConversationGAgentState ApplyExternalCallbackResumeAdmitted(ConversationGAgentState current,
        ConversationExternalCallbackResumeAdmitted message)
    {
        var next = current.Clone();
        var action = next.ExternalCallbackActions.FirstOrDefault(item => item.Context.CallbackId == message.CallbackId);
        if (action is not null)
            action.ResumeAdmitted = true;
        return next;
    }

    private static ConversationGAgentState ApplyExternalCallbackLinkReceived(ConversationGAgentState current,
        ConversationExternalCallbackLinkReceived message)
    {
        var next = current.Clone();
        var action = next.ExternalCallbackActions.FirstOrDefault(item => item.Context.CallbackId == message.Link.CallbackId);
        if (action is not null)
            action.Link = message.Link.Clone();
        return next;
    }

    private static ConversationGAgentState ApplyExternalCallbackLinkDelivered(ConversationGAgentState current,
        ConversationExternalCallbackLinkDelivered message)
    {
        var next = current.Clone();
        var action = next.ExternalCallbackActions.FirstOrDefault(item => item.Context.CallbackId == message.CallbackId);
        if (action is not null)
            action.LinkDelivered = true;
        return next;
    }
}
