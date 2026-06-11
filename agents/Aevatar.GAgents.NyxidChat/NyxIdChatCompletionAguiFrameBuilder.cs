using Aevatar.AI.Abstractions;
using Aevatar.AGUI.Contracts;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Google.Protobuf.WellKnownTypes;
using AguiTextContent = Aevatar.AGUI.Contracts.TextMessageContentEvent;
using AguiTextEnd = Aevatar.AGUI.Contracts.TextMessageEndEvent;
using AguiTextStart = Aevatar.AGUI.Contracts.TextMessageStartEvent;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatCompletionAguiFrameBuilder
{
    public static IReadOnlyList<AGUIEvent> Build(
        NyxIdChatSessionProjectionContext context,
        RoleChatSessionCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(completed);

        var messageId = string.IsNullOrWhiteSpace(completed.SessionId)
            ? context.SessionId
            : completed.SessionId.Trim();
        var content = completed.Content ?? string.Empty;

        var frames = new List<AGUIEvent>();
        frames.AddRange(BuildToolFrames(completed));
        if (TryBuildFailureFrame(content, context.SessionId, out var failureFrame))
        {
            frames.Add(failureFrame);
            return frames;
        }

        if (!string.IsNullOrEmpty(content))
        {
            frames.Add(new AGUIEvent
            {
                TextMessageStart = new AguiTextStart
                {
                    MessageId = messageId,
                    Role = "assistant",
                },
            });
            frames.Add(new AGUIEvent
            {
                TextMessageContent = new AguiTextContent
                {
                    MessageId = messageId,
                    Delta = content,
                },
            });
        }

        if (completed.Usage != null)
            frames.Add(BuildUsageFrame(completed.Usage, completed.Model));

        frames.Add(new AGUIEvent
        {
            TextMessageEnd = new AguiTextEnd
            {
                MessageId = messageId,
            },
        });
        frames.Add(new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = context.RootActorId,
                RunId = context.SessionId,
                Result = Any.Pack(new StringValue { Value = content }),
            },
        });
        return frames;
    }

    public static AGUIEvent? BuildPendingApprovalFrame(PendingToolApprovalState? pending)
    {
        if (pending == null || string.IsNullOrWhiteSpace(pending.RequestId))
            return null;

        return new AGUIEvent
        {
            Custom = new CustomEvent
            {
                Name = "TOOL_APPROVAL_REQUEST",
                Payload = Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["requestId"] = Value.ForString(pending.RequestId),
                        ["sessionId"] = Value.ForString(pending.SessionId ?? string.Empty),
                        ["toolName"] = Value.ForString(pending.ToolName ?? string.Empty),
                        ["toolCallId"] = Value.ForString(pending.ToolCallId ?? string.Empty),
                        ["argumentsJson"] = Value.ForString(pending.ArgumentsJson ?? string.Empty),
                        ["isDestructive"] = Value.ForBool(pending.IsDestructive),
                        ["timeoutSeconds"] = Value.ForNumber(0),
                    },
                }),
            },
        };
    }

    private static IEnumerable<AGUIEvent> BuildToolFrames(RoleChatSessionCompletedEvent completed)
    {
        var started = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolCall in completed.ToolCalls)
        {
            var callId = toolCall.CallId ?? string.Empty;
            var toolName = toolCall.ToolName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(callId) && string.IsNullOrWhiteSpace(toolName))
                continue;

            if (!string.IsNullOrWhiteSpace(callId))
                started.Add(callId);

            yield return new AGUIEvent
            {
                ToolCallStart = new ToolCallStartEvent
                {
                    ToolCallId = callId,
                    ToolName = toolName,
                },
            };
        }

        foreach (var receipt in completed.ToolReceipts)
        {
            var callId = receipt.CallId ?? string.Empty;
            var toolName = receipt.ToolName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(callId) && string.IsNullOrWhiteSpace(toolName))
                continue;

            if (!string.IsNullOrWhiteSpace(callId) && !started.Contains(callId))
            {
                started.Add(callId);
                yield return new AGUIEvent
                {
                    ToolCallStart = new ToolCallStartEvent
                    {
                        ToolCallId = callId,
                        ToolName = toolName,
                    },
                };
            }

            yield return new AGUIEvent
            {
                ToolCallEnd = new ToolCallEndEvent
                {
                    ToolCallId = callId,
                    Result = ResolveToolResult(receipt),
                },
            };
        }
    }

    private static string ResolveToolResult(AgentToolReceipt receipt)
    {
        if (!string.IsNullOrWhiteSpace(receipt.ResultJson))
            return receipt.ResultJson;

        if (!string.IsNullOrWhiteSpace(receipt.ErrorMessage))
            return receipt.ErrorMessage;

        return receipt.Status switch
        {
            AgentToolReceiptStatus.Success => "Tool completed.",
            AgentToolReceiptStatus.ApprovalRequired => "Approval pending.",
            AgentToolReceiptStatus.Denied => "Approval denied.",
            AgentToolReceiptStatus.Error => "Tool failed.",
            _ => string.Empty,
        };
    }

    private static bool TryBuildFailureFrame(string content, string runId, out AGUIEvent failureFrame)
    {
        failureFrame = null!;
        if (string.IsNullOrEmpty(content))
            return false;

        const string llmErrorPrefix = "[[AEVATAR_LLM_ERROR]]";
        const string llmFailedPrefix = "LLM request failed";
        if (content.StartsWith(llmErrorPrefix, StringComparison.Ordinal))
        {
            failureFrame = BuildRunError(content[llmErrorPrefix.Length..].Trim(), runId);
            return true;
        }

        if (content.StartsWith(llmFailedPrefix, StringComparison.Ordinal))
        {
            failureFrame = BuildRunError(ScopeGAgentAguiEventMapper.NormalizeLlmFailureMessage(content), runId);
            return true;
        }

        return false;
    }

    private static AGUIEvent BuildRunError(string message, string runId) =>
        new()
        {
            RunError = new RunErrorEvent
            {
                Message = string.IsNullOrWhiteSpace(message) ? "LLM request failed." : message,
                RunId = runId,
            },
        };

    private static AGUIEvent BuildUsageFrame(TokenUsagePayload usage, string? model) =>
        new()
        {
            Usage = new UsageEvent
            {
                Available = true,
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
                Model = string.IsNullOrWhiteSpace(model) ? null : model,
            },
        };
}
