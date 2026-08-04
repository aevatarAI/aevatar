using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
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
        AppendRichContentFrames(frames, completed, messageId);

        if (completed.Outcome == RoleChatSessionOutcome.Blocked && completed.AuthorizationRequired != null)
        {
            AppendTextAndUsageFrames(frames, messageId, content, completed.Usage, completed.Model);
            var blocker = completed.AuthorizationRequired.Clone();
            frames.Add(new AGUIEvent
            {
                Custom = new CustomEvent
                {
                    Name = "nyxid.authorization.required",
                    Payload = Any.Pack(blocker),
                },
            });
            frames.Add(new AGUIEvent
            {
                RunFinished = new RunFinishedEvent
                {
                    ThreadId = context.RootActorId,
                    RunId = context.SessionId,
                    Result = Any.Pack(blocker),
                    Status = RunCompletionStatus.Blocked,
                },
            });
            return frames;
        }

        if (completed.Outcome is
            RoleChatSessionOutcome.Failed or
            RoleChatSessionOutcome.OutcomeUncertain)
        {
            AppendTextAndUsageFrames(
                frames,
                messageId,
                ResolveDisplayableContent(content),
                completed.Usage,
                completed.Model);
            frames.Add(BuildRunError(
                string.IsNullOrWhiteSpace(completed.SafeMessage)
                    ? "The chat request failed. Please try again."
                    : completed.SafeMessage,
                context.SessionId,
                string.IsNullOrWhiteSpace(completed.FailureCode)
                    ? "CHAT_REQUEST_FAILED"
                    : completed.FailureCode));
            return frames;
        }

        if (TryBuildFailureFrame(content, context.SessionId, out var failureFrame))
        {
            AppendTextAndUsageFrames(frames, messageId, string.Empty, completed.Usage, completed.Model);
            frames.Add(failureFrame);
            return frames;
        }

        AppendTextAndUsageFrames(frames, messageId, content, completed.Usage, completed.Model);
        frames.Add(new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = context.RootActorId,
                RunId = context.SessionId,
                Result = Any.Pack(new StringValue { Value = content }),
                Status = RunCompletionStatus.Completed,
            },
        });
        return frames;
    }

    private static void AppendRichContentFrames(
        ICollection<AGUIEvent> frames,
        RoleChatSessionCompletedEvent completed,
        string messageId)
    {
        if (!string.IsNullOrEmpty(completed.ReasoningContent))
        {
            frames.Add(new AGUIEvent
            {
                Custom = new CustomEvent
                {
                    Name = "aevatar.llm.reasoning",
                    Payload = Any.Pack(new RoleChatReasoningDeltaProgress
                    {
                        Delta = completed.ReasoningContent,
                    }),
                },
            });
        }

        foreach (var part in completed.OutputParts)
        {
            frames.Add(new AGUIEvent
            {
                Custom = new CustomEvent
                {
                    Name = "MEDIA_CONTENT",
                    Payload = Any.Pack(new MediaContentEvent
                    {
                        SessionId = messageId,
                        AgentId = completed.ActorId ?? string.Empty,
                        Part = part.Clone(),
                    }),
                },
            });
        }
    }

    private static void AppendTextAndUsageFrames(
        ICollection<AGUIEvent> frames,
        string messageId,
        string content,
        TokenUsagePayload? usage,
        string? model)
    {
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

        if (usage != null)
            frames.Add(BuildUsageFrame(usage, model));

        frames.Add(new AGUIEvent
        {
            TextMessageEnd = new AguiTextEnd
            {
                MessageId = messageId,
            },
        });
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
                        ["turnId"] = Value.ForString(pending.SessionId ?? string.Empty),
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
                    Presentation = ToolPresentationDescriptors.Snapshot(
                        toolCall.Presentation,
                        toolName),
                },
            };
        }

        var completedResultCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in completed.ToolResults)
        {
            var callId = result.CallId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(callId))
                continue;

            if (!started.Contains(callId))
            {
                var toolName = completed.ToolReceipts.FirstOrDefault(receipt =>
                    string.Equals(receipt.CallId, callId, StringComparison.Ordinal))?.ToolName ?? string.Empty;
                started.Add(callId);
                yield return new AGUIEvent
                {
                    ToolCallStart = new ToolCallStartEvent
                    {
                        ToolCallId = callId,
                        ToolName = toolName,
                        Presentation = ToolPresentationDescriptors.Snapshot(
                            presentation: null,
                            invocationName: toolName),
                    },
                };
            }

            completedResultCallIds.Add(callId);
            yield return new AGUIEvent
            {
                ToolCallEnd = new ToolCallEndEvent
                {
                    ToolCallId = callId,
                    Result = ResolveToolResult(result),
                },
            };
        }

        foreach (var receipt in completed.ToolReceipts)
        {
            var callId = receipt.CallId ?? string.Empty;
            var toolName = receipt.ToolName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(callId) && string.IsNullOrWhiteSpace(toolName))
                continue;

            if (!string.IsNullOrWhiteSpace(callId) && completedResultCallIds.Contains(callId))
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
                        Presentation = ToolPresentationDescriptors.Snapshot(
                            presentation: null,
                            invocationName: toolName),
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

    private static string ResolveToolResult(ToolResultEvent result)
    {
        if (!string.IsNullOrWhiteSpace(result.ResultJson))
            return result.ResultJson;
        if (!string.IsNullOrWhiteSpace(result.Error))
            return result.Error;
        return result.Success ? "Tool completed." : "Tool failed.";
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
            failureFrame = BuildRunError(content[llmErrorPrefix.Length..].Trim(), runId, "LLM_REQUEST_FAILED");
            return true;
        }

        if (content.StartsWith(llmFailedPrefix, StringComparison.Ordinal))
        {
            failureFrame = BuildRunError(
                ScopeGAgentAguiEventMapper.NormalizeLlmFailureMessage(content),
                runId,
                "LLM_REQUEST_FAILED");
            return true;
        }

        return false;
    }

    private static AGUIEvent BuildRunError(string message, string runId, string code) =>
        new()
        {
            RunError = new RunErrorEvent
            {
                Message = string.IsNullOrWhiteSpace(message) ? "LLM request failed." : message,
                RunId = runId,
                Code = code,
            },
        };

    private static string ResolveDisplayableContent(string content) =>
        content.StartsWith("[[AEVATAR_LLM_ERROR]]", StringComparison.Ordinal) ||
        content.StartsWith("LLM request failed", StringComparison.Ordinal)
            ? string.Empty
            : content;

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
