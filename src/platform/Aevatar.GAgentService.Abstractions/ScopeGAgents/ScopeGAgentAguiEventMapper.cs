using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using AiTextContent = Aevatar.AI.Abstractions.TextMessageContentEvent;
using AiTextEnd = Aevatar.AI.Abstractions.TextMessageEndEvent;
using AiTextReasoning = Aevatar.AI.Abstractions.TextMessageReasoningEvent;
using AiTextStart = Aevatar.AI.Abstractions.TextMessageStartEvent;
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
                TextMessageStart = new Aevatar.Presentation.AGUI.TextMessageStartEvent
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
                TextMessageContent = new Aevatar.Presentation.AGUI.TextMessageContentEvent
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
                    Payload = Any.Pack(new Aevatar.Presentation.AGUI.TextMessageContentEvent
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
            if (!string.IsNullOrEmpty(ai.Content))
            {
                const string llmErrorPrefix = "[[AEVATAR_LLM_ERROR]]";
                const string llmFailedPrefix = "LLM request failed:";
                if (ai.Content.StartsWith(llmErrorPrefix, StringComparison.Ordinal))
                {
                    return new AGUIEvent
                    {
                        RunError = new RunErrorEvent
                        {
                            Message = ai.Content[llmErrorPrefix.Length..].Trim(),
                        },
                    };
                }

                if (ai.Content.StartsWith(llmFailedPrefix, StringComparison.Ordinal))
                {
                    return new AGUIEvent
                    {
                        RunError = new RunErrorEvent
                        {
                            Message = ai.Content.Trim(),
                        },
                    };
                }
            }

            return new AGUIEvent
            {
                TextMessageEnd = new Aevatar.Presentation.AGUI.TextMessageEndEvent
                {
                    MessageId = ai.SessionId,
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

        if (payload.TypeUrl.EndsWith("ToolApprovalRequestEvent", StringComparison.Ordinal))
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

    public static Struct BuildToolApprovalStruct(Any payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var structure = new Struct();
        try
        {
            var input = new CodedInputStream(payload.Value.ToByteArray());
            string requestId = string.Empty;
            string toolName = string.Empty;
            string toolCallId = string.Empty;
            string argumentsJson = string.Empty;
            var isDestructive = false;
            var timeoutSeconds = 15;

            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1:
                        requestId = input.ReadString();
                        break;
                    case 3:
                        toolName = input.ReadString();
                        break;
                    case 4:
                        toolCallId = input.ReadString();
                        break;
                    case 5:
                        argumentsJson = input.ReadString();
                        break;
                    case 7:
                        isDestructive = input.ReadBool();
                        break;
                    case 8:
                        timeoutSeconds = input.ReadInt32();
                        break;
                    default:
                        input.SkipLastField();
                        break;
                }
            }

            structure.Fields["requestId"] = Value.ForString(requestId);
            structure.Fields["toolName"] = Value.ForString(toolName);
            structure.Fields["toolCallId"] = Value.ForString(toolCallId);
            structure.Fields["argumentsJson"] = Value.ForString(argumentsJson);
            structure.Fields["isDestructive"] = Value.ForBool(isDestructive);
            structure.Fields["timeoutSeconds"] = Value.ForNumber(timeoutSeconds);
        }
        catch
        {
            structure.Fields["requestId"] = Value.ForString(string.Empty);
            structure.Fields["error"] = Value.ForString("Failed to decode approval request");
        }

        return structure;
    }
}
