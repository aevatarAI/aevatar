using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.AGUI.Contracts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using AiTextContent = Aevatar.AI.Abstractions.TextMessageContentEvent;
using AiTextEnd = Aevatar.AI.Abstractions.TextMessageEndEvent;
using AiTextReasoning = Aevatar.AI.Abstractions.TextMessageReasoningEvent;
using AiTextStart = Aevatar.AI.Abstractions.TextMessageStartEvent;
using AiTokenUsage = Aevatar.AI.Abstractions.ChatTokenUsageEvent;
using AiToolCall = Aevatar.AI.Abstractions.ToolCallEvent;
using AiToolResult = Aevatar.AI.Abstractions.ToolResultEvent;
using AiMediaContent = Aevatar.AI.Abstractions.MediaContentEvent;

namespace Aevatar.GAgentService.Abstractions.ScopeGAgents;

public static class ScopeGAgentAguiEventMapper
{
    public static AGUIEvent? TryMap(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var payload = envelope.Payload;
        if (payload is null)
            return null;

        if (payload.Is(AiTextStart.Descriptor))
        {
            var ai = payload.Unpack<AiTextStart>();
            return new AGUIEvent
            {
                TextMessageStart = new Aevatar.AGUI.Contracts.TextMessageStartEvent
                {
                    MessageId = ai.SessionId,
                    Role = "assistant",
                },
            };
        }

        if (payload.Is(AiTextContent.Descriptor))
        {
            var ai = payload.Unpack<AiTextContent>();
            return new AGUIEvent
            {
                TextMessageContent = new Aevatar.AGUI.Contracts.TextMessageContentEvent
                {
                    MessageId = ai.SessionId,
                    Delta = ai.Delta,
                },
            };
        }

        if (payload.Is(AiTextReasoning.Descriptor))
        {
            var ai = payload.Unpack<AiTextReasoning>();
            return new AGUIEvent
            {
                Custom = new CustomEvent
                {
                    Name = "TEXT_MESSAGE_REASONING",
                    Payload = Any.Pack(new Aevatar.AGUI.Contracts.TextMessageContentEvent
                    {
                        MessageId = ai.SessionId,
                        Delta = ai.Delta,
                    }),
                },
            };
        }

        if (payload.Is(AiTextEnd.Descriptor))
        {
            var ai = payload.Unpack<AiTextEnd>();
            return MapTextCompletion(ai.SessionId, ai.Content);
        }

        if (payload.Is(RoleChatSessionErrorEvent.Descriptor))
        {
            var error = payload.Unpack<RoleChatSessionErrorEvent>();
            return new AGUIEvent
            {
                RunError = new RunErrorEvent
                {
                    Message = error.Message,
                    RunId = error.SessionId,
                    Code = error.Reason,
                },
            };
        }

        if (payload.Is(AiToolCall.Descriptor))
        {
            var ai = payload.Unpack<AiToolCall>();
            return new AGUIEvent
            {
                ToolCallStart = new ToolCallStartEvent
                {
                    ToolCallId = ai.CallId,
                    ToolName = ai.ToolName,
                },
            };
        }

        if (payload.Is(AiToolResult.Descriptor))
        {
            var ai = payload.Unpack<AiToolResult>();
            return new AGUIEvent
            {
                ToolCallEnd = new ToolCallEndEvent
                {
                    ToolCallId = ai.CallId,
                    Result = ai.ResultJson,
                },
            };
        }

        if (payload.Is(AiMediaContent.Descriptor))
        {
            return new AGUIEvent
            {
                Custom = new CustomEvent
                {
                    Name = "MEDIA_CONTENT",
                    Payload = payload,
                },
            };
        }

        if (payload.Is(AiTokenUsage.Descriptor))
        {
            var ai = payload.Unpack<AiTokenUsage>();
            return new AGUIEvent
            {
                Usage = new UsageEvent
                {
                    Available = ai.Usage != null,
                    PromptTokens = ai.Usage?.PromptTokens ?? 0,
                    CompletionTokens = ai.Usage?.CompletionTokens ?? 0,
                    TotalTokens = ai.Usage?.TotalTokens ?? 0,
                    Model = string.IsNullOrWhiteSpace(ai.Model) ? null : ai.Model,
                },
            };
        }

        if (payload.Is(ToolApprovalRequestEvent.Descriptor))
        {
            return new AGUIEvent
            {
                Custom = new CustomEvent
                {
                    Name = "TOOL_APPROVAL_REQUEST",
                    Payload = Any.Pack(BuildToolApprovalStruct(payload)),
                },
            };
        }

        if (payload.Is(AGUIEvent.Descriptor))
            return payload.Unpack<AGUIEvent>();

        return null;
    }

    public static AGUIEvent? TryMapExplicitSessionObservation(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // Refactor (iter164/cluster-003-draft-run):
        //   Old pattern: draft-run projection reused the broad live AI/tool mapper and treated raw frames as AGUI observations.
        //   New principle: draft-run session projection only treats a wrapped AGUIEvent as an explicit observation contract.
        var payload = envelope.Payload;
        if (payload?.Is(AGUIEvent.Descriptor) != true)
            return null;

        return payload.Unpack<AGUIEvent>();
    }

    private static AGUIEvent MapTextCompletion(string sessionId, string? content)
    {
        if (!string.IsNullOrEmpty(content))
        {
            const string llmErrorPrefix = "[[AEVATAR_LLM_ERROR]]";
            const string llmFailedPrefix = "LLM request failed";
            if (content.StartsWith(llmErrorPrefix, StringComparison.Ordinal))
            {
                return new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Message = content[llmErrorPrefix.Length..].Trim(),
                    },
                };
            }

            if (content.StartsWith(llmFailedPrefix, StringComparison.Ordinal))
            {
                return new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Message = NormalizeLlmFailureMessage(content),
                    },
                };
            }
        }

        return new AGUIEvent
        {
            TextMessageEnd = new Aevatar.AGUI.Contracts.TextMessageEndEvent
            {
                MessageId = sessionId,
            },
        };
    }

    public static string NormalizeLlmFailureMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "LLM request failed.";

        var trimmed = content.Trim();
        const string toolAnnotatedPrefix = "LLM request failed [tools=";
        if (!trimmed.StartsWith(toolAnnotatedPrefix, StringComparison.Ordinal))
            return trimmed;

        var marker = "]: ";
        var markerIndex = trimmed.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return trimmed;

        var message = trimmed[(markerIndex + marker.Length)..].Trim();
        return string.IsNullOrWhiteSpace(message) ? "LLM request failed." : message;
    }

    public static Struct BuildToolApprovalStruct(Any payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var structure = new Struct();
        try
        {
            var approval = payload.Unpack<ToolApprovalRequestEvent>();
            structure.Fields["requestId"] = Value.ForString(approval.RequestId ?? string.Empty);
            structure.Fields["sessionId"] = Value.ForString(approval.SessionId ?? string.Empty);
            structure.Fields["toolName"] = Value.ForString(approval.ToolName ?? string.Empty);
            structure.Fields["toolCallId"] = Value.ForString(approval.ToolCallId ?? string.Empty);
            structure.Fields["argumentsJson"] = Value.ForString(approval.ArgumentsJson ?? string.Empty);
            structure.Fields["isDestructive"] = Value.ForBool(approval.IsDestructive);
            structure.Fields["timeoutSeconds"] = Value.ForNumber(approval.TimeoutSeconds);
        }
        catch
        {
            structure.Fields["requestId"] = Value.ForString(string.Empty);
            structure.Fields["error"] = Value.ForString("Failed to decode approval request");
        }

        return structure;
    }
}
