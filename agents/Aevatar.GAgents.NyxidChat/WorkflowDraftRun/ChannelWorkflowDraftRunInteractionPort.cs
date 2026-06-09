using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

public sealed class ChannelWorkflowDraftRunInteractionPort : IChannelWorkflowDraftRunInteractionPort
{
    private readonly IWorkflowChatRunInteractionPort? _workflowInteractionPort;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly WorkflowDraftRunReplyRenderer _renderer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChannelWorkflowDraftRunInteractionPort> _logger;

    public ChannelWorkflowDraftRunInteractionPort(
        IActorDispatchPort actorDispatchPort,
        WorkflowDraftRunReplyRenderer renderer,
        ILogger<ChannelWorkflowDraftRunInteractionPort> logger,
        IWorkflowChatRunInteractionPort? workflowInteractionPort = null,
        TimeProvider? timeProvider = null)
    {
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workflowInteractionPort = workflowInteractionPort;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task DispatchAsync(NeedsWorkflowDraftRunEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_workflowInteractionPort is null)
        {
            await DispatchReadyAsync(
                request,
                BuildFailure("workflow_interaction_port_unavailable", "Workflow interaction service is unavailable."),
                ct).ConfigureAwait(false);
            return;
        }

        var command = BuildCommand(request);
        var accumulatedText = string.Empty;
        try
        {
            var result = await _workflowInteractionPort.ExecuteAsync(
                    command,
                    async (frame, token) =>
                    {
                        var rendered = _renderer.Render(frame, accumulatedText);
                        if (rendered is null)
                            return;

                        accumulatedText = rendered.Text;
                        if (rendered.IsTerminal)
                        {
                            await DispatchReadyAsync(request, rendered, token).ConfigureAwait(false);
                            return;
                        }

                        await DispatchChunkAsync(request, accumulatedText, token).ConfigureAwait(false);
                    },
                    onAcceptedAsync: null,
                    ct)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                await DispatchReadyAsync(
                    request,
                    BuildFailure(
                        $"workflow_start_failed:{result.Error}",
                        $"Workflow start failed: {result.Error}"),
                    ct).ConfigureAwait(false);
                return;
            }

            if (result.FinalizeResult is null || !result.FinalizeResult.Completed)
            {
                await DispatchReadyAsync(
                    request,
                    BuildFailure("workflow_completion_unknown", "Workflow ended without a terminal frame."),
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Workflow draft-run interaction failed: runId={RunId} correlation={CorrelationId}",
                request.RunId,
                request.CorrelationId);
            await DispatchReadyAsync(
                request,
                BuildFailure("workflow_draft_run_exception", "Workflow draft-run failed."),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static WorkflowChatRunRequest BuildCommand(NeedsWorkflowDraftRunEvent request)
    {
        var source = request.WorkflowSource ?? new ChannelWorkflowDraftRunSource();
        var headers = request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return new WorkflowChatRunRequest(
            Prompt: request.Prompt ?? string.Empty,
            Source: WorkflowChatSource.DefinitionActor(source.DefinitionActorId, source.WorkflowName),
            SessionId: request.RunId,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["channel.registration_id"] = request.RegistrationId ?? string.Empty,
                ["channel.correlation_id"] = request.CorrelationId ?? string.Empty,
            },
            ScopeId: source.ScopeId,
            CallerCredential: new WorkflowCallerCredential(request.NyxUserAccessToken),
            Headers: headers,
            CommandIdSeed: request.RunId,
            CorrelationIdSeed: request.CorrelationId);
    }

    private async Task DispatchChunkAsync(
        NeedsWorkflowDraftRunEvent request,
        string accumulatedText,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accumulatedText))
            return;

        await DispatchToConversationAsync(
            request,
            new LlmReplyStreamChunkEvent
            {
                CorrelationId = request.CorrelationId,
                RegistrationId = request.RegistrationId,
                Activity = request.Activity?.Clone() ?? new ChatActivity(),
                AccumulatedText = accumulatedText,
                ChunkAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                ReplyToken = request.ReplyToken,
                ReplyTokenExpiresAtUnixMs = request.ReplyTokenExpiresAtUnixMs,
            },
            ct).ConfigureAwait(false);
    }

    private async Task DispatchReadyAsync(
        NeedsWorkflowDraftRunEvent request,
        WorkflowDraftRunRenderedFrame rendered,
        CancellationToken ct)
    {
        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = request.CorrelationId,
            RunId = request.RunId,
            RegistrationId = request.RegistrationId,
            Activity = request.Activity?.Clone() ?? new ChatActivity(),
            Outbound = new MessageContent { Text = rendered.Text },
            TerminalState = rendered.IsFailure ? LlmReplyTerminalState.Failed : LlmReplyTerminalState.Completed,
            ErrorCode = rendered.ErrorCode,
            ErrorSummary = rendered.IsFailure ? rendered.Text : string.Empty,
            ReadyAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            ReplyToken = request.ReplyToken,
            ReplyTokenExpiresAtUnixMs = request.ReplyTokenExpiresAtUnixMs,
        };

        if (!rendered.IsFailure)
        {
            ready.AppendedHistory.Add(new ConversationHistoryEntry
            {
                Role = "assistant",
                Content = rendered.Text,
            });
        }

        await DispatchToConversationAsync(request, ready, ct).ConfigureAwait(false);
    }

    private async Task DispatchToConversationAsync(
        NeedsWorkflowDraftRunEvent request,
        IMessage payload,
        CancellationToken ct)
    {
        var targetActorId = request.TargetActorId;
        if (string.IsNullOrWhiteSpace(targetActorId))
            throw new InvalidOperationException("Workflow draft-run request target actor id is required.");

        await _actorDispatchPort.DispatchAsync(
                targetActorId,
                new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                    Payload = Any.Pack(payload),
                    Route = EnvelopeRouteSemantics.CreateDirect(
                        "channel-workflow-draft-run-interaction-port",
                        targetActorId),
                    Propagation = new EnvelopePropagation
                    {
                        CorrelationId = request.CorrelationId,
                    },
                },
                ct)
            .ConfigureAwait(false);
    }

    private static WorkflowDraftRunRenderedFrame BuildFailure(string errorCode, string text) =>
        new(text, true, true, errorCode);
}
