using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.AGUI.Contracts;
using Google.Protobuf.WellKnownTypes;
using RoleChatCommandAttemptRejectedEvent = Aevatar.AI.Abstractions.RoleChatCommandAttemptRejectedEvent;
using RoleChatCommandAttemptRejectionReason = Aevatar.AI.Abstractions.RoleChatCommandAttemptRejectionReason;
using RoleChatSessionCompletedEvent = Aevatar.AI.Abstractions.RoleChatSessionCompletedEvent;
using RoleChatSessionOutcome = Aevatar.AI.Abstractions.RoleChatSessionOutcome;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class GAgentDraftRunSessionEventProjector
    : ProjectionSessionEventProjectorBase<GAgentDraftRunProjectionContext, AGUIEvent>
{
    public GAgentDraftRunSessionEventProjector(
        IProjectionSessionEventHub<AGUIEvent> sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<AGUIEvent>> ResolveSessionEventEntries(
        GAgentDraftRunProjectionContext context,
        EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrWhiteSpace(context.SessionId))
            return EmptyEntries;

        var committedRejectionEntries = TryResolveCommittedRejectionEntries(context, envelope);
        if (committedRejectionEntries.Count > 0)
            return committedRejectionEntries;

        if (!string.Equals(envelope.Propagation?.CorrelationId, context.SessionId, StringComparison.Ordinal))
            return EmptyEntries;

        var committedEntries = TryResolveCommittedTerminalEntries(context, envelope);
        if (committedEntries.Count > 0)
            return committedEntries;

        // Refactor (iter164/cluster-003-draft-run):
        //   Old pattern: raw TextMessage*/Tool* envelopes were projected as draft-run AGUI session events.
        //   New principle: draft-run session observation only accepts committed completions above or explicit AGUI observation frames here.
        var mapped = ScopeGAgentAguiEventMapper.TryMapExplicitSessionObservation(envelope);
        if (mapped == null)
            return EmptyEntries;

        CompleteRunFinishedFrame(context, mapped);
        if (mapped.EventCase == AGUIEvent.EventOneofCase.TextMessageEnd)
        {
            return
            [
                new ProjectionSessionEventEntry<AGUIEvent>(
                    context.RootActorId,
                    context.SessionId,
                    mapped),
                new ProjectionSessionEventEntry<AGUIEvent>(
                    context.RootActorId,
                    context.SessionId,
                    new AGUIEvent
                    {
                        RunFinished = new RunFinishedEvent
                        {
                            ThreadId = context.RootActorId,
                            RunId = context.SessionId,
                        },
                    }),
            ];
        }

        return
        [
            new ProjectionSessionEventEntry<AGUIEvent>(
                context.RootActorId,
                context.SessionId,
                mapped),
        ];
    }

    private static IReadOnlyList<ProjectionSessionEventEntry<AGUIEvent>> TryResolveCommittedTerminalEntries(
        GAgentDraftRunProjectionContext context,
        EventEnvelope envelope)
    {
        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) ||
            payload == null)
        {
            return EmptyEntries;
        }

        if (!payload.Is(RoleChatSessionCompletedEvent.Descriptor))
            return EmptyEntries;

        var completed = payload.Unpack<RoleChatSessionCompletedEvent>();
        if (TryBuildFailureFrame(completed, out var failureFrame))
        {
            return
            [
                new ProjectionSessionEventEntry<AGUIEvent>(
                    context.RootActorId,
                    context.SessionId,
                    failureFrame),
            ];
        }

        var messageId = string.IsNullOrWhiteSpace(completed.SessionId)
            ? context.SessionId
            : completed.SessionId;
        var content = completed.Content ?? string.Empty;
        return BuildTerminalEntries(context, messageId, content, completed.ContentEmitted, completed.Usage, completed.Model);
    }

    private static IReadOnlyList<ProjectionSessionEventEntry<AGUIEvent>> TryResolveCommittedRejectionEntries(
        GAgentDraftRunProjectionContext context,
        EventEnvelope envelope)
    {
        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) ||
            payload == null ||
            !payload.Is(RoleChatCommandAttemptRejectedEvent.Descriptor))
        {
            return EmptyEntries;
        }

        var rejection = payload.Unpack<RoleChatCommandAttemptRejectedEvent>();
        if (rejection.Reason != RoleChatCommandAttemptRejectionReason.CapacityExhausted)
            return EmptyEntries;

        if (!string.Equals(rejection.CommandAttemptId, context.SessionId, StringComparison.Ordinal))
            return EmptyEntries;

        var safeMessage = rejection.SafeMessage?.Trim() ?? string.Empty;
        return
        [
            new ProjectionSessionEventEntry<AGUIEvent>(
                context.RootActorId,
                context.SessionId,
                new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Message = string.IsNullOrWhiteSpace(safeMessage)
                            ? GAgentRunFailureCodes.CapacityExhausted
                            : safeMessage,
                        Code = GAgentRunFailureCodes.CapacityExhausted,
                        RunId = context.SessionId,
                    },
                }),
        ];
    }

    private static void CompleteRunFinishedFrame(
        GAgentDraftRunProjectionContext context,
        AGUIEvent aguiEvent)
    {
        if (aguiEvent.EventCase != AGUIEvent.EventOneofCase.RunFinished)
            return;

        aguiEvent.RunFinished.ThreadId = string.IsNullOrWhiteSpace(aguiEvent.RunFinished.ThreadId)
            ? context.RootActorId
            : aguiEvent.RunFinished.ThreadId;
        aguiEvent.RunFinished.RunId = string.IsNullOrWhiteSpace(aguiEvent.RunFinished.RunId)
            ? context.SessionId
            : aguiEvent.RunFinished.RunId;
    }

    private static bool TryBuildFailureFrame(
        RoleChatSessionCompletedEvent completed,
        out AGUIEvent failureFrame)
    {
        failureFrame = null!;
        if (completed.Outcome is RoleChatSessionOutcome.Failed or RoleChatSessionOutcome.OutcomeUncertain)
        {
            var failureCode = completed.FailureCode?.Trim() ?? string.Empty;
            if (completed.Outcome == RoleChatSessionOutcome.OutcomeUncertain &&
                string.IsNullOrWhiteSpace(failureCode))
            {
                failureCode = GAgentRunFailureCodes.OutcomeUncertain;
            }
            var safeMessage = completed.SafeMessage?.Trim() ?? string.Empty;
            failureFrame = new AGUIEvent
            {
                RunError = new RunErrorEvent
                {
                    Message = string.IsNullOrWhiteSpace(safeMessage) ? failureCode : safeMessage,
                    Code = string.IsNullOrWhiteSpace(failureCode) ? null : failureCode,
                },
            };
            return true;
        }

        if (completed.Outcome != RoleChatSessionOutcome.Unspecified)
            return false;

        var content = completed.Content;
        if (string.IsNullOrEmpty(content))
            return false;

        const string llmErrorPrefix = "[[AEVATAR_LLM_ERROR]]";
        const string llmFailedPrefix = "LLM request failed";
        if (content.StartsWith(llmErrorPrefix, StringComparison.Ordinal))
        {
            failureFrame = new AGUIEvent
            {
                RunError = new RunErrorEvent
                {
                    Message = content[llmErrorPrefix.Length..].Trim(),
                },
            };
            return true;
        }

        if (content.StartsWith(llmFailedPrefix, StringComparison.Ordinal))
        {
            failureFrame = new AGUIEvent
            {
                RunError = new RunErrorEvent
                {
                    Message = ScopeGAgentAguiEventMapper.NormalizeLlmFailureMessage(content),
                },
            };
            return true;
        }

        return false;
    }

    private static IReadOnlyList<ProjectionSessionEventEntry<AGUIEvent>> BuildTerminalEntries(
        GAgentDraftRunProjectionContext context,
        string messageId,
        string output,
        bool contentEmitted,
        Aevatar.AI.Abstractions.TokenUsagePayload? usage,
        string? model)
    {
        var entries = new List<ProjectionSessionEventEntry<AGUIEvent>>();
        if (!contentEmitted && !string.IsNullOrEmpty(output))
        {
            entries.Add(new ProjectionSessionEventEntry<AGUIEvent>(
                context.RootActorId,
                context.SessionId,
                new AGUIEvent
                {
                    TextMessageStart = new TextMessageStartEvent
                    {
                        MessageId = messageId,
                        Role = "assistant",
                    },
                }));
            entries.Add(new ProjectionSessionEventEntry<AGUIEvent>(
                context.RootActorId,
                context.SessionId,
                new AGUIEvent
                {
                    TextMessageContent = new TextMessageContentEvent
                    {
                        MessageId = messageId,
                        Delta = output,
                    },
                }));
        }

        entries.Add(new ProjectionSessionEventEntry<AGUIEvent>(
            context.RootActorId,
            context.SessionId,
            BuildUsageEvent(usage, model)));
        entries.Add(new ProjectionSessionEventEntry<AGUIEvent>(
            context.RootActorId,
            context.SessionId,
            BuildTextMessageEnd(messageId)));
        entries.Add(new ProjectionSessionEventEntry<AGUIEvent>(
            context.RootActorId,
            context.SessionId,
            BuildRunFinished(context, output)));
        return entries;
    }

    private static AGUIEvent BuildTextMessageEnd(string messageId) =>
        new()
        {
            TextMessageEnd = new TextMessageEndEvent
            {
                MessageId = messageId,
            },
        };

    private static AGUIEvent BuildUsageEvent(
        Aevatar.AI.Abstractions.TokenUsagePayload? usage,
        string? model) =>
        new()
        {
            Usage = new UsageEvent
            {
                Available = usage != null,
                PromptTokens = usage?.PromptTokens ?? 0,
                CompletionTokens = usage?.CompletionTokens ?? 0,
                TotalTokens = usage?.TotalTokens ?? 0,
                Model = string.IsNullOrWhiteSpace(model) ? null : model,
            },
        };

    private static AGUIEvent BuildRunFinished(GAgentDraftRunProjectionContext context, string output) =>
        new()
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = context.RootActorId,
                RunId = context.SessionId,
                // Refactor (iter98/cluster-790): Old: committed completion fallback synthesized TextMessageContent when live deltas were missed. New: RunFinished.result carries typed GAgentDraftRunResultPayload and consumers read result.output without extra stream frames.
                Result = Any.Pack(new GAgentDraftRunResultPayload
                {
                    Output = output ?? string.Empty,
                }),
            },
        };
}
