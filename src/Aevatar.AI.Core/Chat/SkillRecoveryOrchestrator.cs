using System.Runtime.CompilerServices;
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
    private readonly string _sessionId;
    private int _searchAttempts;
    private int _directiveSequence;
    private bool _primarySkillAttempted;

    public SkillRecoveryOrchestrator(
        AgentSkillRecoveryContext recovery,
        Func<AgentToolExecutionContext?, StreamingToolExecutor> executorFactory,
        string sessionId = "")
    {
        _recovery = recovery;
        _executorFactory = executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
        _sessionId = sessionId;
    }

    public bool RequiresInitialSearch => _recovery.RequireInitialOrnnSearch;

    public IAsyncEnumerable<SkillRecoveryToolProgress> ApplyInitialDirectivesAsync(
        AgentToolExecutionContext? toolContext,
        List<ChatMessage> messages,
        List<ChatMessage> pendingHistoryMessages,
        string? callIdPrefix,
        CancellationToken ct) =>
        StreamDirectivesAsync(
            toolContext,
            messages,
            pendingHistoryMessages,
            finalContent: null,
            callIdPrefix,
            ct);

    public bool ShouldRecoverFinalAnswer(
        IReadOnlyList<ChatMessage> pendingHistoryMessages,
        string? finalContent,
        string? callIdPrefix) =>
        SkillRecoveryPlanner.TryPlanNextDirective(
            _recovery,
            pendingHistoryMessages,
            finalContent,
            _searchAttempts,
            callIdPrefix,
            _primarySkillAttempted,
            out _);

    public IAsyncEnumerable<SkillRecoveryToolProgress> RecoverFinalAnswerAsync(
        AgentToolExecutionContext? toolContext,
        List<ChatMessage> messages,
        List<ChatMessage> pendingHistoryMessages,
        string? finalContent,
        string? callIdPrefix,
        CancellationToken ct) =>
        StreamDirectivesAsync(
            toolContext,
            messages,
            pendingHistoryMessages,
            finalContent,
            callIdPrefix,
            ct);

    private async IAsyncEnumerable<SkillRecoveryToolProgress> StreamDirectivesAsync(
        AgentToolExecutionContext? toolContext,
        List<ChatMessage> messages,
        List<ChatMessage> pendingHistoryMessages,
        string? finalContent,
        string? callIdPrefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var executor = _executorFactory(toolContext);
        for (var i = 0; i < MaxChainedDirectives; i++)
        {
            if (!SkillRecoveryPlanner.TryPlanNextDirective(
                    _recovery,
                    pendingHistoryMessages,
                    finalContent,
                    _searchAttempts,
                    callIdPrefix,
                    _primarySkillAttempted,
                    out var directive))
            {
                break;
            }

            var sequencedDirective = WithUniqueToolCallId(directive);
            if (sequencedDirective.AttemptsPrimarySkill)
                _primarySkillAttempted = true;

            await foreach (var progress in ApplyDirectiveAsync(
                               sequencedDirective,
                               executor,
                               messages,
                               pendingHistoryMessages,
                               ct))
            {
                yield return progress;
            }
            if (directive.ConsumesOrnnSearchAttempt)
                _searchAttempts++;

            finalContent = null;
        }
    }

    private SkillRecoveryPlanner.RecoveryDirective WithUniqueToolCallId(
        SkillRecoveryPlanner.RecoveryDirective directive)
    {
        if (directive.ToolCall is not { } toolCall)
            return directive;

        _directiveSequence++;
        var uniqueToolCall = new ToolCall
        {
            Id = SkillRecoveryPlanner.BuildSequencedCallId(toolCall.Id, _directiveSequence),
            Name = toolCall.Name,
            ArgumentsJson = toolCall.ArgumentsJson,
        };

        return directive with { ToolCall = uniqueToolCall };
    }

    private async IAsyncEnumerable<SkillRecoveryToolProgress> ApplyDirectiveAsync(
        SkillRecoveryPlanner.RecoveryDirective directive,
        StreamingToolExecutor executor,
        List<ChatMessage> messages,
        List<ChatMessage> pendingHistoryMessages,
        [EnumeratorCancellation] CancellationToken ct)
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
            var prepared = await executor.PrepareBatchAsync(
                _sessionId,
                round: 0,
                [toolCall],
                ct).ConfigureAwait(false);
            var operation = prepared.Single();
            yield return SkillRecoveryToolProgress.Starting(operation);
            executor.AddTool(state, operation);
            var currentAssistantToolCallMessage = assistantToolCallMessage;
            await foreach (var result in executor.GetRemainingResultsAsync(state, ct))
            {
                currentAssistantToolCallMessage = FailedToolCallArgumentRedactor.Redact(
                    messages,
                    pendingHistoryMessages,
                    currentAssistantToolCallMessage,
                    result);
                var toolMsg = ToolCallLoop.BuildToolResultMessage(
                    result.CallId,
                    result.ToolName,
                    ToolExecutionResultHistory.ResolveSafeContent(result),
                    result.Receipt);
                messages.Add(toolMsg);
                pendingHistoryMessages.Add(toolMsg);
                yield return SkillRecoveryToolProgress.Completed(result, operation.OperationId);
            }

            yield break;
        }

        if (!string.IsNullOrWhiteSpace(directive.Nudge))
        {
            var nudge = ChatMessage.User(directive.Nudge);
            messages.Add(nudge);
            pendingHistoryMessages.Add(nudge);
        }
    }
}

internal sealed record SkillRecoveryToolProgress(
    ToolCall? StartedToolCall,
    ToolExecutionResult? CompletedResult,
    string OperationId)
{
    public static SkillRecoveryToolProgress Starting(PreparedChatToolOperation operation) =>
        new(CloneToolCall(operation.ToolCall), null, operation.OperationId);

    public static SkillRecoveryToolProgress Completed(ToolExecutionResult result, string operationId) =>
        new(null, result, operationId);

    private static ToolCall CloneToolCall(ToolCall toolCall) => new()
    {
        Id = toolCall.Id,
        Name = toolCall.Name,
        ArgumentsJson = toolCall.ArgumentsJson,
    };
}
