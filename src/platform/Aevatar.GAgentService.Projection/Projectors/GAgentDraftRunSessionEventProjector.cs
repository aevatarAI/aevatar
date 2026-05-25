using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.Presentation.AGUI;
using Google.Protobuf.WellKnownTypes;
using RoleChatSessionCompletedEvent = Aevatar.AI.Abstractions.RoleChatSessionCompletedEvent;

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

        if (!string.Equals(envelope.Propagation?.CorrelationId, context.SessionId, StringComparison.Ordinal))
            return EmptyEntries;

        var committedEntries = TryResolveCommittedCompletionEntries(context, envelope);
        if (committedEntries.Count > 0)
            return committedEntries;

        var mapped = ScopeGAgentAguiEventMapper.TryMap(envelope);
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

    private static IReadOnlyList<ProjectionSessionEventEntry<AGUIEvent>> TryResolveCommittedCompletionEntries(
        GAgentDraftRunProjectionContext context,
        EventEnvelope envelope)
    {
        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) ||
            payload?.Is(RoleChatSessionCompletedEvent.Descriptor) != true)
        {
            return EmptyEntries;
        }

        var completed = payload.Unpack<RoleChatSessionCompletedEvent>();
        if (TryBuildFailureFrame(completed.Content, out var failureFrame))
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
        if (string.IsNullOrEmpty(content) || completed.ContentEmitted)
        {
            return BuildTerminalEntries(context, messageId);
        }

        return
        [
            new ProjectionSessionEventEntry<AGUIEvent>(
                context.RootActorId,
                context.SessionId,
                new AGUIEvent
                {
                    TextMessageStart = new TextMessageStartEvent
                    {
                        MessageId = messageId,
                        Role = "assistant",
                    },
                }),
            new ProjectionSessionEventEntry<AGUIEvent>(
                context.RootActorId,
                context.SessionId,
                new AGUIEvent
                {
                    TextMessageContent = new TextMessageContentEvent
                    {
                        MessageId = messageId,
                        Delta = content,
                    },
                }),
            new ProjectionSessionEventEntry<AGUIEvent>(
                context.RootActorId,
                context.SessionId,
                BuildTextMessageEnd(messageId)),
            new ProjectionSessionEventEntry<AGUIEvent>(
                context.RootActorId,
                context.SessionId,
                BuildRunFinished(context)),
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

    private static bool TryBuildFailureFrame(string? content, out AGUIEvent failureFrame)
    {
        failureFrame = null!;
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
        string messageId) =>
        [
            new ProjectionSessionEventEntry<AGUIEvent>(
                context.RootActorId,
                context.SessionId,
                BuildTextMessageEnd(messageId)),
            new ProjectionSessionEventEntry<AGUIEvent>(
                context.RootActorId,
                context.SessionId,
                BuildRunFinished(context)),
        ];

    private static AGUIEvent BuildTextMessageEnd(string messageId) =>
        new()
        {
            TextMessageEnd = new TextMessageEndEvent
            {
                MessageId = messageId,
            },
        };

    private static AGUIEvent BuildRunFinished(GAgentDraftRunProjectionContext context) =>
        new()
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = context.RootActorId,
                RunId = context.SessionId,
            },
        };
}
