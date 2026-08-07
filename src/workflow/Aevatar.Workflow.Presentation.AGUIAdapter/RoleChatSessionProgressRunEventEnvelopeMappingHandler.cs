using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class RoleChatSessionProgressRunEventEnvelopeMappingHandler
    : IWorkflowRunEventEnvelopeMappingHandler
{
    public int Order => 25;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<WorkflowRunEventEnvelope> events)
    {
        events = [];
        if (envelope.Payload?.Is(RoleChatSessionProgressedEvent.Descriptor) == true)
        {
            var progress = envelope.Payload.Unpack<RoleChatSessionProgressedEvent>();
            events = MapProgress(envelope, progress);
            return true;
        }

        if (envelope.Payload?.Is(RoleChatSessionCompletedEvent.Descriptor) != true)
            return false;

        var completion = envelope.Payload.Unpack<RoleChatSessionCompletedEvent>();
        events = completion.TerminalProgress
            .Where(progress => string.Equals(
                progress.SessionId,
                completion.SessionId,
                StringComparison.Ordinal))
            .SelectMany(progress => MapProgress(envelope, progress))
            .ToArray();
        return true;
    }

    private static IReadOnlyList<WorkflowRunEventEnvelope> MapProgress(
        EventEnvelope envelope,
        RoleChatSessionProgressedEvent progress)
    {
        if (string.IsNullOrWhiteSpace(progress.SessionId) || progress.Sequence <= 0)
            return [];

        var timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp);
        var messageId = AGUIEventEnvelopeMappingHelpers.ResolveMessageId(
            progress.SessionId,
            envelope.Id);
        switch (progress.PayloadCase)
        {
            case RoleChatSessionProgressedEvent.PayloadOneofCase.TextStarted:
                return
                [
                    new WorkflowRunEventEnvelope
                    {
                        Timestamp = timestamp,
                        TextMessageStart = new WorkflowTextMessageStartEventPayload
                        {
                            MessageId = messageId,
                            Role = "assistant",
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.TextDelta:
                if (string.IsNullOrEmpty(progress.TextDelta.Delta))
                    return [];
                return
                [
                    new WorkflowRunEventEnvelope
                    {
                        Timestamp = timestamp,
                        TextMessageContent = new WorkflowTextMessageContentEventPayload
                        {
                            MessageId = messageId,
                            Delta = progress.TextDelta.Delta,
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.ReasoningDelta:
                if (string.IsNullOrEmpty(progress.ReasoningDelta.Delta))
                    return [];
                return
                [
                    new WorkflowRunEventEnvelope
                    {
                        Timestamp = timestamp,
                        Custom = new WorkflowCustomEventPayload
                        {
                            Name = "aevatar.llm.reasoning",
                            Payload = Any.Pack(new WorkflowReasoningCustomPayload
                            {
                                SessionId = progress.SessionId,
                                Delta = progress.ReasoningDelta.Delta,
                                Role = AGUIEventEnvelopeMappingHelpers.ResolveRoleFromEnvelope(envelope),
                            }),
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.Media:
                if (progress.Media.Part == null)
                    return [];
                return
                [
                    new WorkflowRunEventEnvelope
                    {
                        Timestamp = timestamp,
                        Custom = new WorkflowCustomEventPayload
                        {
                            Name = "aevatar.media.chunk",
                            Payload = Any.Pack(new MediaContentEvent
                            {
                                SessionId = progress.SessionId,
                                AgentId = progress.Media.AgentId,
                                Part = progress.Media.Part.Clone(),
                            }),
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.ToolStarted:
                return
                [
                    new WorkflowRunEventEnvelope
                    {
                        Timestamp = timestamp,
                        ToolCallStart = new WorkflowToolCallStartEventPayload
                        {
                            ToolCallId = progress.ToolStarted.CallId,
                            ToolName = progress.ToolStarted.ToolName,
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.ToolCompleted:
                if (progress.ToolCompleted.Result == null)
                    return [];
                return
                [
                    new WorkflowRunEventEnvelope
                    {
                        Timestamp = timestamp,
                        ToolCallEnd = new WorkflowToolCallEndEventPayload
                        {
                            ToolCallId = progress.ToolCompleted.Result.CallId,
                            Result = progress.ToolCompleted.Result.ResultJson,
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.Usage:
                if (progress.Usage.Usage == null)
                    return [];
                return
                [
                    new WorkflowRunEventEnvelope
                    {
                        Timestamp = timestamp,
                        Usage = new WorkflowUsageEventPayload
                        {
                            Available = true,
                            PromptTokens = progress.Usage.Usage.PromptTokens,
                            CompletionTokens = progress.Usage.Usage.CompletionTokens,
                            TotalTokens = progress.Usage.Usage.TotalTokens,
                            Model = string.IsNullOrWhiteSpace(progress.Usage.Model)
                                ? null
                                : progress.Usage.Model,
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.TextEnded:
                return
                [
                    new WorkflowRunEventEnvelope
                    {
                        Timestamp = timestamp,
                        TextMessageEnd = new WorkflowTextMessageEndEventPayload
                        {
                            MessageId = messageId,
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.AuthorizationRequired:
                if (progress.AuthorizationRequired.AuthorizationRequired == null)
                    return [];
                return
                [
                    new WorkflowRunEventEnvelope
                    {
                        Timestamp = timestamp,
                        Custom = new WorkflowCustomEventPayload
                        {
                            Name = "nyxid.authorization.required",
                            Payload = Any.Pack(progress.AuthorizationRequired.AuthorizationRequired),
                        },
                    },
                ];
            default:
                return [];
        }
    }
}
