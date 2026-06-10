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

        if (TryBuildFailureFrame(content, context.SessionId, out var failureFrame))
            return [failureFrame];

        var frames = new List<AGUIEvent>();
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
