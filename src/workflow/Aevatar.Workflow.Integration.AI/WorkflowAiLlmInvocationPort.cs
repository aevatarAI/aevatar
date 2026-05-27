using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Integration.AI;

// Refactor (iter129/cluster-triage-workflow-llm-nyx-coupling): Old pattern: Integration.AI buffered ChatStreamAsync into one completed event before workflow could observe it. New principle: provider chunks are mapped to workflow-owned typed stream events as they arrive.
public sealed class WorkflowAiLlmInvocationPort(
    ILLMProviderFactory providerFactory,
    ILogger<WorkflowAiLlmInvocationPort> logger) : IWorkflowLlmInvocationPort
{
    public IAsyncEnumerable<WorkflowLlmInvocationEvent> InvokeAsync(
        WorkflowLlmExecutionIntent intent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        return InvokeCoreAsync(intent, ct);
    }

    private async IAsyncEnumerable<WorkflowLlmInvocationEvent> InvokeCoreAsync(
        WorkflowLlmExecutionIntent intent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var toolCalls = new List<WorkflowLlmToolCall>();

        var provider = string.IsNullOrWhiteSpace(intent.ProviderName)
            ? providerFactory.GetDefault()
            : providerFactory.GetProvider(intent.ProviderName.Trim());

        await using var enumerator = provider.ChatStreamAsync(ToRequest(intent), ct).GetAsyncEnumerator(ct);
        while (true)
        {
            LLMStreamChunk chunk;
            WorkflowLlmInvocationCompletedEvent? failure = null;
            try
            {
                if (!await enumerator.MoveNextAsync())
                    break;
                chunk = enumerator.Current;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Workflow LLM invocation failed run={RunId} step={StepId} session={SessionId}",
                    intent.RunId,
                    intent.StepId,
                    intent.SessionId);
                failure = new WorkflowLlmInvocationCompletedEvent
                {
                    RunId = intent.RunId ?? string.Empty,
                    StepId = intent.StepId ?? string.Empty,
                    SessionId = intent.SessionId ?? string.Empty,
                    Success = false,
                    Error = ex.Message,
                    WorkerId = intent.TargetRole ?? string.Empty,
                };
                chunk = new LLMStreamChunk();
            }

            if (failure != null)
            {
                yield return new WorkflowLlmInvocationEvent(failure);
                yield break;
            }

            if (!string.IsNullOrEmpty(chunk.DeltaContent))
            {
                content.Append(chunk.DeltaContent);
                yield return new WorkflowLlmInvocationEvent(new WorkflowLlmTextDeltaEvent
                {
                    RunId = intent.RunId ?? string.Empty,
                    StepId = intent.StepId ?? string.Empty,
                    SessionId = intent.SessionId ?? string.Empty,
                    Delta = chunk.DeltaContent,
                    WorkerId = intent.TargetRole ?? string.Empty,
                });
            }

            if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
            {
                reasoning.Append(chunk.DeltaReasoningContent);
                yield return new WorkflowLlmInvocationEvent(new WorkflowLlmReasoningDeltaEvent
                {
                    RunId = intent.RunId ?? string.Empty,
                    StepId = intent.StepId ?? string.Empty,
                    SessionId = intent.SessionId ?? string.Empty,
                    Delta = chunk.DeltaReasoningContent,
                    WorkerId = intent.TargetRole ?? string.Empty,
                });
            }

            if (chunk.DeltaToolCall != null)
            {
                var toolCall = new WorkflowLlmToolCall
                {
                    ToolName = chunk.DeltaToolCall.Name ?? string.Empty,
                    ArgumentsJson = chunk.DeltaToolCall.ArgumentsJson ?? string.Empty,
                    CallId = chunk.DeltaToolCall.Id ?? string.Empty,
                };
                toolCalls.Add(toolCall);
                yield return new WorkflowLlmInvocationEvent(new WorkflowLlmToolCallDeltaEvent
                {
                    RunId = intent.RunId ?? string.Empty,
                    StepId = intent.StepId ?? string.Empty,
                    SessionId = intent.SessionId ?? string.Empty,
                    ToolName = toolCall.ToolName,
                    ArgumentsJson = toolCall.ArgumentsJson,
                    CallId = toolCall.CallId,
                    WorkerId = intent.TargetRole ?? string.Empty,
                });
            }
        }

        var completed = new WorkflowLlmInvocationCompletedEvent
        {
            RunId = intent.RunId ?? string.Empty,
            StepId = intent.StepId ?? string.Empty,
            SessionId = intent.SessionId ?? string.Empty,
            Success = true,
            Content = content.ToString(),
            ReasoningContent = reasoning.ToString(),
            WorkerId = intent.TargetRole ?? string.Empty,
            ContentEmitted = content.Length > 0,
        };
        completed.ToolCalls.Add(toolCalls);
        yield return new WorkflowLlmInvocationEvent(completed);
    }

    private static LLMRequest ToRequest(WorkflowLlmExecutionIntent intent)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(intent.SystemPrompt))
            messages.Add(ChatMessage.System(intent.SystemPrompt));

        if (intent.InputParts.Count > 0)
        {
            messages.Add(ChatMessage.User(
                intent.InputParts.Select(ToContentPart).ToList(),
                string.IsNullOrWhiteSpace(intent.Prompt) ? null : intent.Prompt));
        }
        else
        {
            messages.Add(ChatMessage.User(intent.Prompt ?? string.Empty));
        }

        var model = FirstNonBlank(intent.ModelOverride, intent.Model);
        int? maxToolRounds = intent.HasMaxToolRoundsOverride
            ? intent.MaxToolRoundsOverride
            : intent.MaxToolRounds > 0 ? intent.MaxToolRounds : null;

        return new LLMRequest
        {
            Messages = messages,
            RequestId = intent.SessionId,
            Metadata = intent.Annotations.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            Model = model,
            Temperature = intent.HasTemperature ? intent.Temperature : null,
            MaxTokens = intent.MaxTokens > 0 ? intent.MaxTokens : null,
            RoutingContext = new LLMRequestRoutingContext(
                ModelOverride: string.IsNullOrWhiteSpace(intent.ModelOverride) ? null : intent.ModelOverride.Trim(),
                NyxIdRoutePreference: null,
                MaxToolRoundsOverride: maxToolRounds,
                UserMemoryPrompt: string.IsNullOrWhiteSpace(intent.UserMemoryPrompt) ? null : intent.UserMemoryPrompt.Trim()),
            LlmControl = new LLMControlContext(
                NyxIdAccessToken: null,
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                ModelOverride: string.IsNullOrWhiteSpace(intent.ModelOverride) ? null : intent.ModelOverride.Trim(),
                NyxIdRoutePreference: null,
                MaxToolRoundsOverride: maxToolRounds,
                UserMemoryPrompt: string.IsNullOrWhiteSpace(intent.UserMemoryPrompt) ? null : intent.UserMemoryPrompt.Trim()),
        };
    }

    private static ContentPart ToContentPart(WorkflowChatContentPart part) =>
        new()
        {
            Kind = part.Kind switch
            {
                WorkflowChatContentPartKind.Text => ContentPartKind.Text,
                WorkflowChatContentPartKind.Image => ContentPartKind.Image,
                WorkflowChatContentPartKind.Audio => ContentPartKind.Audio,
                WorkflowChatContentPartKind.Video => ContentPartKind.Video,
                _ => ContentPartKind.Unspecified,
            },
            Text = part.Text ?? string.Empty,
            DataBase64 = part.DataBase64 ?? string.Empty,
            MediaType = part.MediaType ?? string.Empty,
            Uri = part.Uri ?? string.Empty,
            Name = part.Name ?? string.Empty,
        };

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();
}
