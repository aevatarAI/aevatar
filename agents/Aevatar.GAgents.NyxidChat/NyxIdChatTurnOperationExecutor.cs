using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.NyxidChat;

public interface INyxIdChatTurnOperationExecutor
{
    Task<NyxIdChatTurnOperationExecution> ExecuteAsync(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct);
}

public sealed class NyxIdChatTransientExecutionSession
{
    internal AgentRunReplyStepState? StepState { get; set; }
    internal NeedsLlmReplyEvent? Request { get; set; }
    internal AgentRunAuthorizedToolStep? AuthorizedToolStep { get; set; }
    internal NyxIdChatOperationKey? AuthorizationSourceKey { get; set; }
    internal long ProgressSequence { get; set; }
}

public sealed record NyxIdChatTurnOperationExecution(
    NyxIdChatOperationResultSignal Result);

public sealed class NyxIdChatTurnOperationExecutor
    : INyxIdChatTurnOperationExecutor
{
    internal const string ToolCapabilityLostCode = "NYXID_CHAT_TOOL_CAPABILITY_LOST";
    internal const string ToolAuthorizationMismatchCode = "NYXID_CHAT_TOOL_AUTHORIZATION_MISMATCH";
    internal const string ToolReceiptRequiredCode = "NYXID_CHAT_TOOL_RECEIPT_REQUIRED";
    private const string InvalidExecutionResultCode = "NYXID_CHAT_INVALID_EXECUTION_RESULT";
    private const string UnsupportedOperationCode = "NYXID_CHAT_OPERATION_NOT_SUPPORTED";
    private const string ToolCapabilityLostMessage =
        "The authorized tool capability is no longer available. Retry from a safe checkpoint.";
    private const string ToolAuthorizationMismatchMessage =
        "The tool command did not match the exact authorized tool call.";
    private const string ToolReceiptRequiredMessage =
        "The effect-capable tool did not return the required outcome receipt.";
    private const string InvalidExecutionResultMessage =
        "The operation executor returned an invalid typed result.";
    private const string UnsupportedOperationMessage =
        "This operation kind is not available in the turn executor.";

    private readonly IAgentRunReplyGenerationExecutorPort _generationExecutor;

    public NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor)
    {
        _generationExecutor = generationExecutor ?? throw new ArgumentNullException(nameof(generationExecutor));
    }

    public async Task<NyxIdChatTurnOperationExecution> ExecuteAsync(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(reportProgressAsync);
        ct.ThrowIfCancellationRequested();

        return command.InputCase switch
        {
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm =>
                await ExecuteLlmAsync(command, session, reportProgressAsync, ct).ConfigureAwait(false),
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool =>
                await ExecuteToolAsync(command, session, reportProgressAsync, ct).ConfigureAwait(false),
            _ => Failure(
                command.Key,
                UnsupportedOperationCode,
                UnsupportedOperationMessage,
                NyxIdChatEffectEvidence.NotStarted),
        };
    }

    private async Task<NyxIdChatTurnOperationExecution> ExecuteLlmAsync(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        var request = BuildReplyRequest(command);
        var runId = command.Key.TaskId;
        var attempt = checked((int)Math.Clamp(command.Key.OperationGeneration, 1, int.MaxValue));
        var runActorId = NyxIdChatTurnActorIds.ForTurn(
            command.Key.ConversationActorId,
            command.Key.TurnId);
        var stepState = await _generationExecutor.BuildInitialStepStateAsync(
                new AgentRunReplyGenerationExecutionRequest(
                    runId,
                    runActorId,
                    attempt,
                    request.Clone()),
                ct)
            .ConfigureAwait(false);
        OverlayDirectInputParts(stepState, command.Llm.Request);

        var outputParts = new List<ChatContentPart>();
        var execution = await _generationExecutor.BuildLlmStepExecutionAsync(
                new AgentRunReplyStepExecutionRequest(
                    runId,
                    runActorId,
                    attempt,
                    stepState.NextStepIndex,
                    request.Clone(),
                    stepState.Clone(),
                    (chunk, token) => HandleLlmChunkAsync(
                        command.Key,
                        chunk,
                        outputParts,
                        session,
                        reportProgressAsync,
                        token)),
                ct)
            .ConfigureAwait(false);

        if (!IsValidLlmExecution(execution, runId, request, attempt, stepState.NextStepIndex))
        {
            ClearAuthorization(session);
            return Failure(
                command.Key,
                InvalidExecutionResultCode,
                InvalidExecutionResultMessage,
                NyxIdChatEffectEvidence.NotApplied);
        }

        var facts = execution.Continuation.LlmStepResult!;
        session.StepState = ApplyLlmFacts(stepState, facts, execution.Continuation.StepIndex, outputParts);
        session.Request = request.Clone();
        session.AuthorizedToolStep = execution.AuthorizedToolStep;
        session.AuthorizationSourceKey = execution.AuthorizedToolStep is null
            ? null
            : command.Key.Clone();

        var result = new NyxIdChatLLMOperationResult
        {
            Content = facts.Content,
            ReasoningContent = facts.ReasoningContent,
            FinishReason = facts.FinishReason,
        };
        result.ContentParts.AddRange(outputParts.Select(static part => part.Clone()));
        result.ToolCalls.AddRange(facts.ToolCalls.Select(static call => new NyxIdChatToolCall
        {
            CallId = call.Id,
            ToolName = call.Name,
            ArgumentsJson = call.ArgumentsJson,
        }));
        if (facts.Usage is not null)
        {
            result.Usage = new TokenUsagePayload
            {
                PromptTokens = facts.Usage.PromptTokens,
                CompletionTokens = facts.Usage.CompletionTokens,
                TotalTokens = facts.Usage.TotalTokens,
            };
        }

        return new NyxIdChatTurnOperationExecution(new NyxIdChatOperationResultSignal
        {
            Key = command.Key.Clone(),
            Llm = result,
        });
    }

    private async Task<NyxIdChatTurnOperationExecution> ExecuteToolAsync(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        if (session.AuthorizedToolStep is null ||
            session.StepState is null ||
            session.Request is null ||
            session.AuthorizationSourceKey is null)
        {
            return Failure(
                command.Key,
                ToolCapabilityLostCode,
                ToolCapabilityLostMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        if (!SameTask(session.AuthorizationSourceKey, command.Key) ||
            session.StepState.PendingToolCalls.Count != 1 ||
            !ToolCallMatches(session.StepState.PendingToolCalls[0], command.Tool))
        {
            return Failure(
                command.Key,
                ToolAuthorizationMismatchCode,
                ToolAuthorizationMismatchMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        var workItem = new AgentRunReplyStepExecutionRequest(
            session.StepState.RunId,
            NyxIdChatTurnActorIds.ForTurn(
                command.Key.ConversationActorId,
                command.Key.TurnId),
            session.StepState.Attempt,
            session.StepState.NextStepIndex,
            session.Request.Clone(),
            session.StepState.Clone());
        if (!session.AuthorizedToolStep.Matches(workItem))
        {
            return Failure(
                command.Key,
                ToolAuthorizationMismatchCode,
                ToolAuthorizationMismatchMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        await ReportProgressAsync(
                command.Key,
                new NyxIdChatToolProgress
                {
                    CallId = command.Tool.CallId,
                    ToolName = command.Tool.ToolName,
                },
                session,
                reportProgressAsync,
                ct)
            .ConfigureAwait(false);

        var capability = session.AuthorizedToolStep;
        ClearAuthorization(session);
        var continuation = await _generationExecutor.BuildToolStepContinuationAsync(
                workItem,
                capability,
                ct)
            .ConfigureAwait(false);
        if (!IsValidToolContinuation(continuation, workItem))
        {
            return Failure(
                command.Key,
                InvalidExecutionResultCode,
                InvalidExecutionResultMessage,
                command.Tool.MayChangeExternalState
                    ? NyxIdChatEffectEvidence.MayHaveChanged
                    : NyxIdChatEffectEvidence.NotApplied);
        }

        var toolResult = continuation.ToolStepResult!;
        var resultMessages = toolResult.ResultMessages
            .Where(message => string.Equals(
                message.ToolCallId,
                command.Tool.CallId,
                StringComparison.Ordinal))
            .ToArray();
        if (resultMessages.Length != 1)
        {
            return Failure(
                command.Key,
                InvalidExecutionResultCode,
                InvalidExecutionResultMessage,
                command.Tool.MayChangeExternalState
                    ? NyxIdChatEffectEvidence.MayHaveChanged
                    : NyxIdChatEffectEvidence.NotApplied);
        }

        var receipt = toolResult.ToolReceipts.LastOrDefault(candidate =>
            string.Equals(candidate.CallId, command.Tool.CallId, StringComparison.Ordinal));
        if (receipt is null && command.Tool.MayChangeExternalState)
        {
            return Failure(
                command.Key,
                ToolReceiptRequiredCode,
                ToolReceiptRequiredMessage,
                NyxIdChatEffectEvidence.MayHaveChanged);
        }

        receipt = receipt?.Clone() ?? new AgentToolReceipt
        {
            CallId = command.Tool.CallId,
            ToolName = command.Tool.ToolName,
            Status = AgentToolReceiptStatus.Success,
        };
        var resultJson = resultMessages[0].Content;
        if (string.IsNullOrWhiteSpace(receipt.ResultJson))
            receipt.ResultJson = resultJson;

        return new NyxIdChatTurnOperationExecution(new NyxIdChatOperationResultSignal
        {
            Key = command.Key.Clone(),
            Tool = new NyxIdChatToolOperationResult
            {
                ResultJson = resultJson,
                Receipt = receipt,
                ExternalEffect = ResolveExternalEffect(command.Tool, receipt),
            },
        });
    }

    private static NeedsLlmReplyEvent BuildReplyRequest(NyxIdChatOperationDispatchCommand command)
    {
        var chat = command.Llm.Request ?? new ChatRequestEvent();
        var channel = new ChannelId { Value = NyxIdChatServiceDefaults.ServiceId };
        var bot = new BotInstanceId { Value = command.Key.ConversationActorId };
        var activity = new ChatActivity
        {
            Id = command.Key.OperationId,
            Type = ActivityType.Message,
            ChannelId = channel.Clone(),
            Bot = bot.Clone(),
            Conversation = new ConversationReference
            {
                Channel = channel,
                Bot = bot,
                Scope = ConversationScope.DirectMessage,
                CanonicalKey = command.Key.ConversationActorId,
            },
            Content = new MessageContent { Text = chat.Prompt },
        };
        var request = new NeedsLlmReplyEvent
        {
            RunId = command.Key.TaskId,
            CorrelationId = command.Key.OperationId,
            TargetActorId = command.Key.ConversationActorId,
            Activity = activity,
            ToolContext = chat.ToolContext?.Clone(),
            LlmControl = chat.LlmControl?.Clone(),
        };
        foreach (var pair in chat.Metadata)
            request.Metadata[pair.Key] = pair.Value;
        return request;
    }

    private static void OverlayDirectInputParts(
        AgentRunReplyStepState stepState,
        ChatRequestEvent request)
    {
        if (request.InputParts.Count == 0)
            return;

        var userMessage = stepState.Messages.LastOrDefault(message =>
            string.Equals(message.Role, "user", StringComparison.Ordinal));
        if (userMessage is null)
        {
            userMessage = new AgentRunChatMessage
            {
                Role = "user",
                Content = request.Prompt,
            };
            stepState.Messages.Add(userMessage);
        }

        userMessage.Content = request.Prompt;
        userMessage.ContentParts.Clear();
        userMessage.ContentParts.AddRange(request.InputParts.Select(static part => part.Clone()));
    }

    private static AgentRunReplyStepState ApplyLlmFacts(
        AgentRunReplyStepState current,
        AgentRunLlmStepResult result,
        int nextStepIndex,
        IReadOnlyList<ChatContentPart> outputParts)
    {
        var next = current.Clone();
        next.NextStepIndex = nextStepIndex;
        next.AccumulatedText = result.AccumulatedText;
        next.LastFinishReason = result.FinishReason;
        next.HasStreamedTextContent = result.HasStreamedTextContent;
        next.PendingToolCalls.Clear();
        next.PendingToolCalls.AddRange(result.ToolCalls.Select(static call => call.Clone()));
        if (result.Usage is not null)
        {
            next.AggregatedUsage ??= new AgentRunReplyTokenUsage();
            next.AggregatedUsage.PromptTokens += result.Usage.PromptTokens;
            next.AggregatedUsage.CompletionTokens += result.Usage.CompletionTokens;
            next.AggregatedUsage.TotalTokens += result.Usage.TotalTokens;
        }

        if (!string.IsNullOrEmpty(result.Content) ||
            !string.IsNullOrEmpty(result.ReasoningContent) ||
            outputParts.Count > 0 ||
            result.ToolCalls.Count > 0)
        {
            var assistant = new AgentRunChatMessage
            {
                Role = "assistant",
                Content = result.Content,
                ReasoningContent = result.ReasoningContent,
            };
            assistant.ContentParts.AddRange(outputParts.Select(static part => part.Clone()));
            assistant.ToolCalls.AddRange(result.ToolCalls.Select(static call => call.Clone()));
            next.Messages.Add(assistant);
        }

        return next;
    }

    private static async Task HandleLlmChunkAsync(
        NyxIdChatOperationKey key,
        LLMStreamChunk chunk,
        List<ChatContentPart> outputParts,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(chunk.DeltaContent))
        {
            await ReportProgressAsync(
                    key,
                    new NyxIdChatTextProgress { Delta = chunk.DeltaContent },
                    session,
                    reportProgressAsync,
                    ct)
                .ConfigureAwait(false);
        }
        if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
        {
            await ReportProgressAsync(
                    key,
                    new NyxIdChatReasoningProgress { Delta = chunk.DeltaReasoningContent },
                    session,
                    reportProgressAsync,
                    ct)
                .ConfigureAwait(false);
        }
        if (chunk.DeltaContentPart is not null)
            outputParts.Add(ContentPartProtoMapper.ToProto(chunk.DeltaContentPart));
        if (chunk.ToolCallStarted?.ToolCall is { } started)
        {
            await ReportProgressAsync(
                    key,
                    new NyxIdChatToolProgress
                    {
                        CallId = started.Id,
                        ToolName = started.Name,
                    },
                    session,
                    reportProgressAsync,
                    ct)
                .ConfigureAwait(false);
        }
    }

    private static Task ReportProgressAsync(
        NyxIdChatOperationKey key,
        NyxIdChatTextProgress progress,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct) =>
        reportProgressAsync(new NyxIdChatOperationProgressSignal
        {
            Key = key.Clone(),
            Sequence = ++session.ProgressSequence,
            Text = progress,
        }, ct);

    private static Task ReportProgressAsync(
        NyxIdChatOperationKey key,
        NyxIdChatReasoningProgress progress,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct) =>
        reportProgressAsync(new NyxIdChatOperationProgressSignal
        {
            Key = key.Clone(),
            Sequence = ++session.ProgressSequence,
            Reasoning = progress,
        }, ct);

    private static Task ReportProgressAsync(
        NyxIdChatOperationKey key,
        NyxIdChatToolProgress progress,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct) =>
        reportProgressAsync(new NyxIdChatOperationProgressSignal
        {
            Key = key.Clone(),
            Sequence = ++session.ProgressSequence,
            ToolStarted = progress,
        }, ct);

    private static bool IsValidLlmExecution(
        AgentRunLlmStepExecution execution,
        string runId,
        NeedsLlmReplyEvent request,
        int attempt,
        int completedStepIndex) =>
        execution.Continuation is
        {
            LlmStepResult: not null,
            StepIndex: > 0,
        } continuation &&
        continuation.StepIndex == completedStepIndex + 1 &&
        continuation.Attempt == attempt &&
        string.Equals(continuation.RunId, runId, StringComparison.Ordinal) &&
        string.Equals(continuation.CorrelationId, request.CorrelationId, StringComparison.Ordinal) &&
        string.Equals(continuation.TargetActorId, request.TargetActorId, StringComparison.Ordinal);

    private static bool IsValidToolContinuation(
        AgentRunNextToolStepRequestedEvent continuation,
        AgentRunReplyStepExecutionRequest workItem) =>
        continuation.ToolStepResult is not null &&
        continuation.StepIndex == workItem.StepIndex + 1 &&
        continuation.Attempt == workItem.Attempt &&
        string.Equals(continuation.RunId, workItem.RunId, StringComparison.Ordinal) &&
        string.Equals(continuation.CorrelationId, workItem.Request.CorrelationId, StringComparison.Ordinal) &&
        string.Equals(continuation.TargetActorId, workItem.Request.TargetActorId, StringComparison.Ordinal);

    private static bool SameTask(NyxIdChatOperationKey left, NyxIdChatOperationKey right) =>
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal);

    private static bool ToolCallMatches(
        AgentRunToolCall authorized,
        NyxIdChatToolOperationInput command) =>
        string.Equals(authorized.Id, command.CallId, StringComparison.Ordinal) &&
        string.Equals(authorized.Name, command.ToolName, StringComparison.Ordinal) &&
        string.Equals(authorized.ArgumentsJson, command.ArgumentsJson, StringComparison.Ordinal);

    private static NyxIdChatEffectEvidence ResolveExternalEffect(
        NyxIdChatToolOperationInput command,
        AgentToolReceipt receipt)
    {
        if (!command.MayChangeExternalState)
            return NyxIdChatEffectEvidence.NotApplied;

        return receipt.Status switch
        {
            AgentToolReceiptStatus.Success => NyxIdChatEffectEvidence.Confirmed,
            AgentToolReceiptStatus.ApprovalRequired or
                AgentToolReceiptStatus.AuthorizationRequired => NyxIdChatEffectEvidence.NotStarted,
            AgentToolReceiptStatus.Denied => NyxIdChatEffectEvidence.NotApplied,
            _ => NyxIdChatEffectEvidence.MayHaveChanged,
        };
    }

    private static void ClearAuthorization(NyxIdChatTransientExecutionSession session)
    {
        session.AuthorizedToolStep = null;
        session.AuthorizationSourceKey = null;
    }

    private static NyxIdChatTurnOperationExecution Failure(
        NyxIdChatOperationKey key,
        string code,
        string safeMessage,
        NyxIdChatEffectEvidence effect) =>
        new(new NyxIdChatOperationResultSignal
        {
            Key = key?.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = code,
                SafeMessage = safeMessage,
                ExternalEffect = effect,
            },
        });
}
