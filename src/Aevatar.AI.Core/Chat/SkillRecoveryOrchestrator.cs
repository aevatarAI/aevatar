using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;

namespace Aevatar.AI.Core.Chat;

internal sealed class SkillRecoveryOrchestrator
{
    private const int MaxChainedDirectives = 4;
    private const string SyntheticToolCallReasoningContent =
        "Aevatar is invoking a recovery tool required to continue this request.";

    private readonly AgentSkillRecoveryContext _recovery;
    private readonly Func<AgentToolExecutionContext?, StreamingToolExecutor> _executorFactory;
    private int _searchAttempts;
    private int _directiveSequence;

    public SkillRecoveryOrchestrator(
        AgentSkillRecoveryContext recovery,
        Func<AgentToolExecutionContext?, StreamingToolExecutor> executorFactory)
    {
        _recovery = recovery;
        _executorFactory = executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
    }

    public bool RequiresInitialSearch => _recovery.RequireInitialOrnnSearch;

    public Task<bool> ApplyInitialDirectivesAsync(
        AgentToolExecutionContext? toolContext,
        List<ChatMessage> messages,
        List<ChatMessage> pendingHistoryMessages,
        string? callIdPrefix,
        CancellationToken ct) =>
        TryApplyDirectivesAsync(
            toolContext,
            messages,
            pendingHistoryMessages,
            finalContent: null,
            callIdPrefix,
            ct);

    public async Task<bool> TryRecoverFinalAnswerAsync(
        AgentToolExecutionContext? toolContext,
        List<ChatMessage> messages,
        List<ChatMessage> pendingHistoryMessages,
        string? finalContent,
        string? callIdPrefix,
        CancellationToken ct)
    {
        if (!SkillRecoveryPlanner.TryPlanNextDirective(
                _recovery,
                pendingHistoryMessages,
                finalContent,
                _searchAttempts,
                callIdPrefix,
                out _))
        {
            return false;
        }

        await TryApplyDirectivesAsync(
                toolContext,
                messages,
                pendingHistoryMessages,
                finalContent,
                callIdPrefix,
                ct)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryApplyDirectivesAsync(
        AgentToolExecutionContext? toolContext,
        List<ChatMessage> messages,
        List<ChatMessage> pendingHistoryMessages,
        string? finalContent,
        string? callIdPrefix,
        CancellationToken ct)
    {
        var executor = _executorFactory(toolContext);
        var applied = false;
        for (var i = 0; i < MaxChainedDirectives; i++)
        {
            if (!SkillRecoveryPlanner.TryPlanNextDirective(
                    _recovery,
                    pendingHistoryMessages,
                    finalContent,
                    _searchAttempts,
                    callIdPrefix,
                    out var directive))
            {
                break;
            }

            await ApplyDirectiveAsync(WithUniqueToolCallId(directive), executor, messages, pendingHistoryMessages, ct)
                .ConfigureAwait(false);
            if (directive.ConsumesOrnnSearchAttempt)
                _searchAttempts++;

            applied = true;
            finalContent = null;
        }

        return applied;
    }

    private SkillRecoveryPlanner.RecoveryDirective WithUniqueToolCallId(
        SkillRecoveryPlanner.RecoveryDirective directive)
    {
        if (directive.ToolCall is not { } toolCall)
            return directive;

        _directiveSequence++;
        var uniqueToolCall = new ToolCall
        {
            Id = string.IsNullOrWhiteSpace(toolCall.Id)
                ? $"skill-recovery:{_directiveSequence}"
                : $"{toolCall.Id}:recovery:{_directiveSequence}",
            Name = toolCall.Name,
            ArgumentsJson = toolCall.ArgumentsJson,
        };

        return directive with { ToolCall = uniqueToolCall };
    }

    private static async Task ApplyDirectiveAsync(
        SkillRecoveryPlanner.RecoveryDirective directive,
        StreamingToolExecutor executor,
        List<ChatMessage> messages,
        List<ChatMessage> pendingHistoryMessages,
        CancellationToken ct)
    {
        if (directive.ToolCall is { } toolCall)
        {
            var assistantToolCallMessage = new ChatMessage
            {
                Role = "assistant",
                Content = null,
                ReasoningContent = SyntheticToolCallReasoningContent,
                ToolCalls = [toolCall],
            };
            messages.Add(assistantToolCallMessage);
            pendingHistoryMessages.Add(assistantToolCallMessage);

            using var state = executor.CreateExecutionState();
            executor.AddTool(state, toolCall);
            await foreach (var result in executor.GetRemainingResultsAsync(state, ct))
            {
                var toolMsg = ToolCallLoop.BuildToolResultMessage(result.CallId, result.Result);
                messages.Add(toolMsg);
                pendingHistoryMessages.Add(toolMsg);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(directive.Nudge))
        {
            var nudge = ChatMessage.User(directive.Nudge);
            messages.Add(nudge);
            pendingHistoryMessages.Add(nudge);
        }
    }
}
