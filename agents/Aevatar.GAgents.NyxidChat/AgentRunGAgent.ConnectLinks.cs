using System.Globalization;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public sealed partial class AgentRunGAgent
{
    private static readonly TimeSpan ConnectLinkCreationTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ConnectLinkRecoveryInterval = TimeSpan.FromSeconds(15);
    // Duplicate self signals in one activation must not start another model call
    // while its result is already in this actor's inbox. Durable progress lives in State.
    private (int Attempt, int StepIndex)? _connectLinkResumeDispatch;

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        _connectLinkResumeDispatch = null;
        await EnqueueConnectLinkRecoveryAsync();
    }

    protected override async Task OnCommittedStatePublicationRecoveredAsync(EventEnvelope envelope, CancellationToken ct)
    {
        await base.OnCommittedStatePublicationRecoveredAsync(envelope, ct);
        _connectLinkResumeDispatch = null;
        await EnqueueConnectLinkRecoveryAsync();
    }

    private bool HasPendingConnectLinkBatch() => State.ConnectLinkBatches.Any(batch =>
        !batch.Applied && IsCurrentStepResult(batch.RunId, batch.CorrelationId, batch.Attempt, batch.StepIndex));

    private bool IsStaleConnectLinkGenerationTimeout(AgentRunReplyGenerationTimedOut command) =>
        State.ConnectLinkBatches.Any(batch => batch.Applied && batch.RunId == command.RunId &&
            batch.Attempt == command.Attempt &&
            batch.ResumeDeadlineUnixMs > command.TimedOutAtUnixMs);

    private async Task<bool> TrySuspendConnectLinkBatchAsync(
        AgentRunNextToolStepRequestedEvent command, AgentRunToolStepResult result)
    {
        var receipts = result.ToolReceipts.Where(receipt => receipt.ChannelConnectLinkPending is not null).ToArray();
        if (receipts.Length == 0)
            return false;
        var request = command.Request?.Clone() ?? BuildStepRequest(command);
        var step = State.GenerationStep!;
        var batch = new AgentRunConnectLinkBatch
        {
            RunId = State.RunId, CorrelationId = State.CorrelationId,
            Attempt = command.Attempt, StepIndex = command.StepIndex,
            Request = SanitizeConnectLinkRequest(request), ToolStepResult = result.Clone(),
            DeadlineUnixMs = _timeProvider.GetUtcNow().Add(ConnectLinkCreationTimeout).ToUnixTimeMilliseconds(),
        };
        foreach (var receipt in receipts)
        {
            var pending = receipt.ChannelConnectLinkPending;
            var calls = step.PendingToolCalls.Where(call => call.Id == receipt.CallId && call.Name == receipt.ToolName).ToArray();
            if (calls.Length != 1 || receipt.Status != AgentToolReceiptStatus.Success ||
                string.IsNullOrWhiteSpace(pending.CallbackId) || string.IsNullOrWhiteSpace(pending.OperationActorId) ||
                batch.Calls.Any(call => call.CallbackId == pending.CallbackId || call.CallId == receipt.CallId) ||
                result.ResultMessages.Count(message => message.ToolCallId == receipt.CallId) != 1 ||
                request.Activity is null || request.TargetActorId != State.TargetActorId ||
                request.RunId != State.RunId || request.CorrelationId != State.CorrelationId ||
                step.ToolContext?.Channel?.Continuation?.ConversationActorId != State.TargetActorId ||
                !step.PendingToolAuthorizations.Any(authorization => authorization.Call?.Equals(calls[0]) == true))
            {
                await ProduceApprovalContinuationFailureAsync(request,
                    "connect_link_tool_identity_invalid", "The connection link request did not contain an exact resumable tool call.");
                return true;
            }
            batch.Calls.Add(new AgentRunConnectLinkCall
            {
                CallbackId = pending.CallbackId, OperationActorId = pending.OperationActorId,
                CallId = receipt.CallId, ToolName = receipt.ToolName,
                ArgumentsSha256 = AgentToolArgumentsDigest.ComputeSha256(calls[0].ArgumentsJson),
            });
        }
        var existing = FindConnectLinkBatch(batch.RunId, batch.Attempt, batch.StepIndex);
        if (existing is null)
            await PersistDomainEventAsync(new AgentRunConnectLinkBatchChanged { Batch = batch });
        // The subscriber is requested only after its exact pending batch is committed.
        await RecoverConnectLinkBatchAsync(FindConnectLinkBatch(batch.RunId, batch.Attempt, batch.StepIndex)!);
        return true;
    }

    [EventHandler]
    public async Task HandleConnectLinkToolResultAsync(ConnectLinkToolResultProduced message)
    {
        var target = message.Target;
        if (target is null || target.ActorId != Id || target.RunId != State.RunId ||
            string.IsNullOrWhiteSpace(message.OperationActorId) ||
            ActiveInboundEnvelope?.Route?.PublisherActorId != message.OperationActorId)
            return;
        var batch = FindConnectLinkBatch(target.RunId, target.Attempt, target.StepIndex);
        var call = batch?.Calls.SingleOrDefault(candidate => candidate.CallId == target.CallId &&
            candidate.CallbackId == message.CallbackId && candidate.OperationActorId == message.OperationActorId);
        if (batch is null || call is null || message.Result is null)
            return;
        if (call.LocalFailureCode.Length > 0)
        {
            await RejectConnectLinkResultAsync(batch, call);
            await PublishAsync(ConnectLinkRecoverySignal(batch), TopologyAudience.Self, CancellationToken.None);
            return;
        }
        if (call.Result is not null)
        {
            if (call.Result.Equals(message.Result))
            {
                await AcknowledgeConnectLinkResultAsync(batch, call);
                await PublishAsync(ConnectLinkRecoverySignal(batch), TopologyAudience.Self, CancellationToken.None);
            }
            return;
        }
        if (!IsCurrentStepResult(batch.RunId, batch.CorrelationId, batch.Attempt, batch.StepIndex) ||
            batch.Applied || !MatchesPendingConnectLinkCall(call))
            return;
        var next = batch.Clone();
        var accepted = next.Calls.Single(candidate => candidate.CallId == call.CallId);
        accepted.Result = message.Result.Clone();
        accepted.LocalFailureCode = ValidateConnectLinkCreationResult(message.Result);
        await PersistDomainEventAsync(new AgentRunConnectLinkBatchChanged { Batch = next });
        // Receipt consumption proves durable acceptance by this run, not link presentation.
        await SettleConnectLinkResultAsync(next, accepted);
        await PublishAsync(ConnectLinkRecoverySignal(next), TopologyAudience.Self, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleConnectLinkRecoveryAsync(AgentRunConnectLinkRecoveryRequested message)
    {
        var batch = FindConnectLinkBatch(message.RunId, message.Attempt, message.StepIndex);
        if (batch is not null)
            await RecoverConnectLinkBatchAsync(batch);
    }

    private async Task RecoverConnectLinkBatchAsync(AgentRunConnectLinkBatch batch)
    {
        if (IsTerminal() || State.Status != AgentRunStatus.ReplyGenerationRequested ||
            batch.Attempt != State.GenerationAttempt)
            return;
        if (batch.Applied)
        {
            if (HasPendingConnectLinkBatch() || State.ConnectLinkBatches.Any(candidate =>
                candidate.Applied && candidate.Attempt == batch.Attempt && candidate.StepIndex > batch.StepIndex))
                return;
            if (batch.ResumeDeadlineUnixMs > 0 &&
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() >= batch.ResumeDeadlineUnixMs)
            {
                await HandleReplyGenerationTimedOutAsync(new AgentRunReplyGenerationTimedOut
                {
                    RunId = batch.RunId, CorrelationId = batch.CorrelationId, Attempt = batch.Attempt,
                    TargetActorId = State.TargetActorId, TimedOutAtUnixMs = batch.ResumeDeadlineUnixMs,
                });
                return;
            }
            if (batch.ResumeDeadlineUnixMs > 0)
                await ScheduleGenerationTimeoutAsync(batch.Request, batch.RunId, batch.Attempt, batch.ResumeDeadlineUnixMs);
            if (State.GenerationStep?.NextStepIndex != batch.ResumeStepIndex)
                return;
            // Keep a durable wake-up even when a successful executor call loses its
            // continuation message. The fixed model budget prevents a stuck run.
            await ScheduleConnectLinkRecoveryAsync(batch);
            if (_connectLinkResumeDispatch == (batch.Attempt, batch.ResumeStepIndex))
                return;
            var request = await RestoreConnectLinkRequestAsync(batch.Request);
            _connectLinkResumeDispatch = (batch.Attempt, batch.ResumeStepIndex);
            try
            {
                await DispatchLlmStepExecutorAsync(request, State.GenerationStep);
            }
            catch
            {
                _connectLinkResumeDispatch = null;
                throw;
            }
            return;
        }
        if (!IsCurrentStepResult(batch.RunId, batch.CorrelationId, batch.Attempt, batch.StepIndex) ||
            batch.Calls.Any(call => !MatchesPendingConnectLinkCall(call)))
            return;
        if (batch.Calls.Any(call => !IsConnectLinkCallComplete(call)) &&
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() >= batch.DeadlineUnixMs)
        {
            var expired = batch.Clone();
            foreach (var call in expired.Calls.Where(call => !IsConnectLinkCallComplete(call)))
                call.LocalFailureCode = "connect_link_creation_timed_out";
            await PersistDomainEventAsync(new AgentRunConnectLinkBatchChanged { Batch = expired });
            batch = expired;
        }
        if (batch.Calls.All(IsConnectLinkCallComplete))
        {
            // A received URL can expire while another call in the same batch is
            // still pending. Preserve the provider fact and record local rejection.
            var validated = batch.Clone();
            foreach (var call in validated.Calls.Where(call => call.LocalFailureCode.Length == 0))
                call.LocalFailureCode = ValidateConnectLinkCreationResult(call.Result);
            if (!validated.Equals(batch))
            {
                await PersistDomainEventAsync(new AgentRunConnectLinkBatchChanged { Batch = validated });
                batch = validated;
            }
            foreach (var call in batch.Calls.Where(call => call.LocalFailureCode.Length > 0))
                await RejectConnectLinkResultAsync(batch, call);
            var result = CompleteConnectLinkBatch(batch);
            var nextStep = ApplyToolStepResult(State.GenerationStep!, result, batch.StepIndex);
            RedactConnectLinkHistory(nextStep, batch, result);
            if (nextStep.Round >= nextStep.MaxToolRounds && !nextStep.FinalNoToolsStep)
            {
                nextStep.FinalNoToolsStep = true;
                nextStep.NextStepIndex++;
            }
            var applied = batch.Clone();
            applied.Applied = true;
            applied.ResumeStepIndex = nextStep.NextStepIndex;
            var budget = ResolveFallbackTimeout();
            applied.ResumeDeadlineUnixMs = budget > TimeSpan.Zero
                ? _timeProvider.GetUtcNow().Add(budget).ToUnixTimeMilliseconds() : 0;
            await PersistDomainEventsAsync([
                new AgentRunReplyStepStateUpdatedEvent
                {
                    RunId = nextStep.RunId, CorrelationId = nextStep.CorrelationId,
                    TargetActorId = nextStep.TargetActorId, Attempt = nextStep.Attempt,
                    StepState = StripInlineMediaPayloads(AgentRunReplyStepCredentials.StripRuntimeCredentials(nextStep)),
                },
                new AgentRunConnectLinkBatchChanged { Batch = applied },
            ]);
            await PublishAsync(ConnectLinkRecoverySignal(applied), TopologyAudience.Self, CancellationToken.None);
            return;
        }
        await ScheduleConnectLinkRecoveryAsync(batch);
        foreach (var call in batch.Calls)
        {
            if (IsConnectLinkCallComplete(call))
                await SettleConnectLinkResultAsync(batch, call);
            else
                await TrySendConnectLinkMessageAsync(call.OperationActorId, new ConnectLinkToolResultRequested
                {
                    CallbackId = call.CallbackId, OperationActorId = call.OperationActorId,
                    Target = ConnectLinkTarget(batch, call),
                });
        }
    }

    private static bool IsConnectLinkCallComplete(AgentRunConnectLinkCall call) =>
        call.Result is not null || call.LocalFailureCode.Length > 0;

    private bool MatchesPendingConnectLinkCall(AgentRunConnectLinkCall pending) =>
        State.GenerationStep?.PendingToolCalls.Any(call => call.Id == pending.CallId && call.Name == pending.ToolName &&
            AgentToolArgumentsDigest.ComputeSha256(call.ArgumentsJson) == pending.ArgumentsSha256) == true;

    private AgentRunConnectLinkBatch? FindConnectLinkBatch(string runId, int attempt, int stepIndex) =>
        State.ConnectLinkBatches.SingleOrDefault(batch => batch.RunId == runId && batch.Attempt == attempt && batch.StepIndex == stepIndex);

    private static AgentRunGAgentState ApplyConnectLinkBatchChanged(AgentRunGAgentState current, AgentRunConnectLinkBatchChanged message)
    {
        var next = current.Clone();
        if (message.Batch is not { } batch)
            return next;
        var previous = next.ConnectLinkBatches.SingleOrDefault(candidate => candidate.RunId == batch.RunId &&
            candidate.Attempt == batch.Attempt && candidate.StepIndex == batch.StepIndex);
        if (previous is not null)
            next.ConnectLinkBatches[next.ConnectLinkBatches.IndexOf(previous)] = batch.Clone();
        else
            next.ConnectLinkBatches.Add(batch.Clone());
        return next;
    }

    private async Task EnqueueConnectLinkRecoveryAsync()
    {
        if (State.Status != AgentRunStatus.ReplyGenerationRequested)
            return;
        var latestApplied = State.ConnectLinkBatches.Where(batch => batch.Applied && batch.Attempt == State.GenerationAttempt)
            .MaxBy(batch => batch.StepIndex);
        foreach (var batch in State.ConnectLinkBatches.Where(batch => !batch.Applied || batch == latestApplied))
            await PublishAsync(ConnectLinkRecoverySignal(batch), TopologyAudience.Self, CancellationToken.None);
    }

    private static AgentRunConnectLinkRecoveryRequested ConnectLinkRecoverySignal(AgentRunConnectLinkBatch batch) => new()
    {
        RunId = batch.RunId, Attempt = batch.Attempt, StepIndex = batch.StepIndex,
    };

    private ConnectLinkToolContinuationTarget ConnectLinkTarget(AgentRunConnectLinkBatch batch, AgentRunConnectLinkCall call) => new()
    {
        ActorId = Id, RunId = batch.RunId, Attempt = batch.Attempt, StepIndex = batch.StepIndex, CallId = call.CallId,
    };

    private Task AcknowledgeConnectLinkResultAsync(AgentRunConnectLinkBatch batch, AgentRunConnectLinkCall call) =>
        TrySendConnectLinkMessageAsync(call.OperationActorId, new ConnectLinkToolResultConsumed
        {
            CallbackId = call.CallbackId, OperationActorId = call.OperationActorId, Target = ConnectLinkTarget(batch, call),
        });

    private Task RejectConnectLinkResultAsync(AgentRunConnectLinkBatch batch, AgentRunConnectLinkCall call) =>
        TrySendConnectLinkMessageAsync(call.OperationActorId, new ConnectLinkToolResultRejected
        {
            CallbackId = call.CallbackId, OperationActorId = call.OperationActorId,
            Target = ConnectLinkTarget(batch, call), FailureCode = call.LocalFailureCode,
        });

    private Task SettleConnectLinkResultAsync(AgentRunConnectLinkBatch batch, AgentRunConnectLinkCall call) =>
        call.LocalFailureCode.Length > 0
            ? RejectConnectLinkResultAsync(batch, call) : AcknowledgeConnectLinkResultAsync(batch, call);

    private async Task TrySendConnectLinkMessageAsync<T>(string target, T message) where T : IMessage
    {
        try { await SendToAsync(target, message, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "Connect-link tool continuation delivery deferred: runId={RunId}", State.RunId); }
    }

    private async Task ScheduleConnectLinkRecoveryAsync(AgentRunConnectLinkBatch batch)
    {
        var scheduler = _callbackScheduler ?? Services.GetService<Aevatar.Foundation.Abstractions.Runtime.Callbacks.IActorRuntimeCallbackScheduler>();
        if (scheduler is null)
            return;
        await scheduler.ScheduleTimeoutAsync(BuildTimeoutRequest(
            $"agent-run-connect-link:{batch.RunId}:{batch.Attempt}:{batch.StepIndex}",
            ConnectLinkRecoveryInterval, ConnectLinkRecoverySignal(batch)), CancellationToken.None);
    }

    private string ValidateConnectLinkCreationResult(ConnectLinkCreationResult created) =>
        string.IsNullOrWhiteSpace(created.FailureCode) && (string.IsNullOrWhiteSpace(created.ExternalRequestId) ||
            !Uri.TryCreate(created.ConnectUrl, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") ||
            created.ExpiresAtUnixMs <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() ||
            created.ExpiresAtUnixMs > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            ? "connect_link_creation_result_invalid" : string.Empty;

    private static AgentRunToolStepResult CompleteConnectLinkBatch(AgentRunConnectLinkBatch batch)
    {
        var result = batch.ToolStepResult.Clone();
        foreach (var call in batch.Calls)
        {
            var created = call.Result;
            var failure = call.LocalFailureCode.Length > 0 ? call.LocalFailureCode : created.FailureCode;
            string BuildJson(bool redact) => string.IsNullOrWhiteSpace(failure)
                ? JsonSerializer.Serialize(new
                {
                    id = created.ExternalRequestId, connect_url = redact ? "[redacted]" : created.ConnectUrl, callback_id = call.CallbackId,
                    expires_at = DateTimeOffset.FromUnixTimeMilliseconds(created.ExpiresAtUnixMs).ToString("O", CultureInfo.InvariantCulture),
                })
                : JsonSerializer.Serialize(new { error = failure, callback_id = call.CallbackId });
            var receipt = result.ToolReceipts.Single(candidate => candidate.CallId == call.CallId);
            receipt.ChannelConnectLinkPending = null;
            receipt.Status = string.IsNullOrWhiteSpace(failure) ? AgentToolReceiptStatus.Success : AgentToolReceiptStatus.Error;
            receipt.ResultJson = BuildJson(redact: true);
            receipt.MutationStage = AgentToolReceiptMutationStage.Unspecified;
            receipt.ErrorCode = failure;
            var uncertain = call.LocalFailureCode.Length > 0 ||
                            created.FailureOutcome != ConnectLinkCreationFailureOutcome.NotCreated;
            receipt.ErrorMessage = string.IsNullOrWhiteSpace(failure) ? string.Empty : uncertain
                ? "The connection link creation result could not be confirmed." : "The connection link could not be created.";
            if (!string.IsNullOrWhiteSpace(failure))
                receipt.FailureOutcome = uncertain ? AgentToolFailureOutcome.OutcomeUncertain : AgentToolFailureOutcome.CalleeConfirmed;
            var message = result.ResultMessages.Single(candidate => candidate.ToolCallId == call.CallId);
            result.ResultMessages[result.ResultMessages.IndexOf(message)] = AgentRunReplyStepMappers.ToProto(
                ToolCallLoop.BuildToolResultMessage(call.CallId, call.ToolName, BuildJson(redact: false), receipt));
        }
        return result;
    }

    private static void RedactConnectLinkHistory(AgentRunReplyStepState next, AgentRunConnectLinkBatch batch,
        AgentRunToolStepResult result)
    {
        foreach (var call in batch.Calls)
        {
            var receipt = result.ToolReceipts.Single(candidate => candidate.CallId == call.CallId);
            var message = result.ResultMessages.Single(candidate => candidate.ToolCallId == call.CallId);
            var index = result.ResultMessages.IndexOf(message);
            var safe = AgentRunReplyStepMappers.ToProto(
                ToolCallLoop.BuildToolResultMessage(call.CallId, call.ToolName, receipt.ResultJson, receipt));
            next.PendingHistoryMessages[next.PendingHistoryMessages.Count - result.ResultMessages.Count + index] = safe;
            next.AppendedHistory[next.AppendedHistory.Count - result.ResultMessages.Count + index] =
                AgentRunReplyStepMappers.ToConversationHistoryEntry(safe);
        }
    }

    private static NeedsLlmReplyEvent SanitizeConnectLinkRequest(NeedsLlmReplyEvent source)
    {
        var request = source.Clone();
        request.ReplyToken = string.Empty;
        request.ReplyTokenExpiresAtUnixMs = 0;
        request.Activity = CloneForDurableState(request.Activity);
        request.TargetRef = null;
        request.LlmControl = null;
        request.PriorHistory.Clear();
        request.RecentAttachmentActivities.Clear();
        if (request.ToolContext?.Credentials is { } credentials)
        {
            credentials.NyxIdAccessToken = string.Empty;
            credentials.NyxIdOrgToken = string.Empty;
            credentials.SenderNyxIdAccessToken = string.Empty;
            credentials.SourceReadableNyxIdAccessToken = string.Empty;
        }
        foreach (var key in new[] { "nyxid.access_token", "nyxid.org_token", "nyxid.sender_access_token" })
        {
            request.Metadata.Remove(key);
            request.ToolContext?.ExternalMetadata.Remove(key);
        }
        return request;
    }

    private async Task<NeedsLlmReplyEvent> RestoreConnectLinkRequestAsync(NeedsLlmReplyEvent saved)
    {
        var request = saved.Clone();
        if (Services.GetService<IRuntimeSecretStore>() is not { } secrets)
            return request;
        async Task<string?> ResolveAsync(RuntimeSecretReference? reference, string purpose)
        {
            if (reference is null || string.IsNullOrWhiteSpace(reference.Ref) ||
                (reference.ExpiresAtUnixMs > 0 && reference.ExpiresAtUnixMs <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()))
                return null;
            var resolved = await secrets.ResolveAsync(new ResolveRuntimeSecretRequest(reference.Ref, purpose,
                request.RunId, request.CorrelationId, "Resume the originating connect-link tool call."), CancellationToken.None);
            return NormalizeOptional(resolved.Secret);
        }
        if (await ResolveAsync(request.RelayReplyTokenRef, ChannelRelayRuntimeSecretPurposes.ReplyToken) is { } replyToken)
        {
            request.ReplyToken = replyToken;
            request.ReplyTokenExpiresAtUnixMs = request.RelayReplyTokenRef.ExpiresAtUnixMs;
        }
        if (await ResolveAsync(request.RelayUserAccessTokenRef, ChannelRelayRuntimeSecretPurposes.UserAccessToken) is { } userToken)
        {
            request.Activity.TransportExtras ??= new TransportExtras();
            request.Activity.TransportExtras.NyxUserAccessToken = userToken;
        }
        return request;
    }
}
