using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Tools;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using System.Text.Json;

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
    internal AgentProfileTurnCatalog? TurnCatalog { get; set; }
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
    private const string InvalidPostconditionInputCode =
        "NYXID_ACTION_POSTCONDITION_INPUT_INVALID";
    private const string InvalidPostconditionInputMessage =
        "The action postcondition input was invalid.";

    private readonly IAgentRunReplyGenerationExecutorPort _generationExecutor;
    private readonly INyxIdActionPostconditionPort _actionPostconditionPort;
    private readonly AgentProfileTurnCatalogMaterializer? _turnCatalogMaterializer;

    public NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor)
        : this(generationExecutor, new UnavailableNyxIdActionPostconditionPort(), null)
    {
    }

    public NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor,
        INyxIdActionPostconditionPort actionPostconditionPort)
        : this(generationExecutor, actionPostconditionPort, null)
    {
    }

    public NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor,
        INyxIdActionPostconditionPort actionPostconditionPort,
        AgentProfileTurnCatalogMaterializer? turnCatalogMaterializer)
    {
        _generationExecutor = generationExecutor ?? throw new ArgumentNullException(nameof(generationExecutor));
        _actionPostconditionPort = actionPostconditionPort ??
                                   throw new ArgumentNullException(nameof(actionPostconditionPort));
        _turnCatalogMaterializer = turnCatalogMaterializer;
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
            NyxIdChatOperationDispatchCommand.InputOneofCase.ActionPostcondition =>
                await ExecuteActionPostconditionAsync(command, ct).ConfigureAwait(false),
            NyxIdChatOperationDispatchCommand.InputOneofCase.InputContinuation =>
                await ExecuteInputContinuationAsync(command, session, reportProgressAsync, ct)
                    .ConfigureAwait(false),
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolApprovalContinuation =>
                await ExecuteToolApprovalContinuationAsync(command, session, reportProgressAsync, ct)
                    .ConfigureAwait(false),
            _ => Failure(
                command.Key,
                UnsupportedOperationCode,
                UnsupportedOperationMessage,
                NyxIdChatEffectEvidence.NotStarted),
        };
    }

    private async Task<NyxIdChatTurnOperationExecution> ExecuteActionPostconditionAsync(
        NyxIdChatOperationDispatchCommand command,
        CancellationToken ct)
    {
        var input = command.ActionPostcondition;
        if (input is null ||
            input.ReportedDisposition is not
                (NyxIdChatActionDisposition.Completed or
                 NyxIdChatActionDisposition.Unspecified) ||
            string.IsNullOrWhiteSpace(input.ScopeId) ||
            string.IsNullOrWhiteSpace(input.OwnerSubject) ||
            string.IsNullOrWhiteSpace(input.OriginTurnId) ||
            string.IsNullOrWhiteSpace(input.ActionRequestId) ||
            input.Action == NyxIdAssistantActionKind.Unspecified ||
            input.Params?.ParamsCase == NyxIdAssistantActionParams.ParamsOneofCase.None)
        {
            return Postcondition(
                command.Key,
                input,
                verified: false,
                InvalidPostconditionInputCode,
                InvalidPostconditionInputMessage);
        }

        var result = await _actionPostconditionPort
            .VerifyAsync(input.Clone(), ct)
            .ConfigureAwait(false);
        if (result is null ||
            !string.Equals(
                result.ActionRequestId,
                input.ActionRequestId,
                StringComparison.Ordinal) ||
            (result.Disposition != input.ReportedDisposition &&
             (input.ReportedDisposition != NyxIdChatActionDisposition.Unspecified ||
              result.Disposition != NyxIdChatActionDisposition.Completed)))
        {
            return Postcondition(
                command.Key,
                input,
                verified: false,
                InvalidExecutionResultCode,
                InvalidExecutionResultMessage);
        }

        return new NyxIdChatTurnOperationExecution(new NyxIdChatOperationResultSignal
        {
            Key = command.Key.Clone(),
            ActionPostcondition = result.Clone(),
        });
    }

    private static NyxIdChatTurnOperationExecution Postcondition(
        NyxIdChatOperationKey key,
        NyxIdChatActionPostconditionInput? input,
        bool verified,
        string code,
        string safeMessage) =>
        new(new NyxIdChatOperationResultSignal
        {
            Key = key?.Clone(),
            ActionPostcondition = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = input?.ActionRequestId ?? string.Empty,
                Disposition = input?.ReportedDisposition ??
                              NyxIdChatActionDisposition.Unspecified,
                Verified = verified,
                Resource = input?.ResourceHint?.Clone(),
                FailureCode = code,
                SafeMessage = safeMessage,
            },
        });

    private async Task<NyxIdChatTurnOperationExecution> ExecuteLlmAsync(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        var isContinuation = command.Llm.ContinueSession;
        if (isContinuation && (session.StepState is null || session.Request is null))
        {
            ClearAuthorization(session);
            return Failure(
                command.Key,
                ToolCapabilityLostCode,
                ToolCapabilityLostMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        var request = isContinuation
            ? session.Request!.Clone()
            : BuildReplyRequest(command);
        if (!isContinuation && session.TurnCatalog is null)
        {
            session.TurnCatalog = command.Key.OperationGeneration > 1 &&
                                  (command.Llm.AgentProfile is not null ||
                                   command.Llm.AgentProfileTurnAuthority is not null)
                ? RestrictedEmptyCatalog()
                : await MaterializeTurnCatalogAsync(command.Llm, ct).ConfigureAwait(false);
        }
        var runId = isContinuation
            ? session.StepState!.RunId
            : command.Key.TaskId;
        var attempt = isContinuation
            ? session.StepState!.Attempt
            : checked((int)Math.Clamp(command.Key.OperationGeneration, 1, int.MaxValue));
        var runActorId = NyxIdChatTurnActorIds.ForTurn(
            command.Key.ConversationActorId,
            command.Key.TurnId);
        var stepState = isContinuation
            ? session.StepState!.Clone()
            : await _generationExecutor.BuildInitialStepStateAsync(
                    new AgentRunReplyGenerationExecutionRequest(
                        runId,
                        runActorId,
                        attempt,
                        request.Clone(),
                        session.TurnCatalog),
                    ct)
                .ConfigureAwait(false);
        if (!isContinuation)
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
                        token),
                    session.TurnCatalog),
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
        result.ToolCalls.AddRange(facts.ToolCalls.Select(call =>
            BuildToolCall(call, execution.AuthorizedToolCallSafeties)));
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
        CancellationToken ct,
        AgentRunAuthorizedToolStep? authorizedToolStep = null)
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
            session.StepState.Clone(),
            TurnCatalog: session.TurnCatalog);
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

        var capability = (authorizedToolStep ?? session.AuthorizedToolStep)
            .WithChatOperation(command.Key);
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

        if (receipt.Status == AgentToolReceiptStatus.ApprovalRequired)
        {
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

        ClearAuthorization(session);
        session.StepState = ApplyToolFacts(
            session.StepState,
            toolResult,
            continuation.StepIndex);

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

    private async Task<NyxIdChatTurnOperationExecution> ExecuteInputContinuationAsync(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        var input = command.InputContinuation;
        if (input?.Answer is null ||
            session.StepState is null ||
            session.Request is null ||
            session.StepState.PendingToolCalls.Count != 1 ||
            !string.Equals(
                session.StepState.PendingToolCalls[0].Id,
                input.ToolCallId,
                StringComparison.Ordinal) ||
            !string.Equals(
                session.StepState.PendingToolCalls[0].Name,
                NyxIdChatAskUserContract.ToolName,
                StringComparison.Ordinal))
        {
            return Failure(
                command.Key,
                ToolCapabilityLostCode,
                ToolCapabilityLostMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        var responseJson = BuildInputResponseJson(input);
        RefreshCredentials(session, input.ToolContext?.Credentials);
        var result = new AgentRunToolStepResult { AdvanceRound = true };
        result.ResultMessages.Add(AgentRunReplyStepMappers.ToProto(
            ToolCallLoop.BuildToolResultMessage(
                input.ToolCallId,
                NyxIdChatAskUserContract.ToolName,
                responseJson)));
        session.StepState = ApplyToolFacts(
            session.StepState,
            result,
            checked(session.StepState.NextStepIndex + 1));
        ClearAuthorization(session);

        return await ExecuteLlmAsync(
                new NyxIdChatOperationDispatchCommand
                {
                    Key = command.Key.Clone(),
                    Llm = new NyxIdChatLLMOperationInput { ContinueSession = true },
                },
                session,
                reportProgressAsync,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<NyxIdChatTurnOperationExecution> ExecuteToolApprovalContinuationAsync(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        var approval = command.ToolApprovalContinuation;
        if (approval is null || string.IsNullOrWhiteSpace(approval.ApprovalRequestId))
        {
            return Failure(
                command.Key,
                ToolAuthorizationMismatchCode,
                ToolAuthorizationMismatchMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        if (!approval.Approved)
        {
            var pendingCall = session.StepState?.PendingToolCalls.Count == 1
                ? session.StepState.PendingToolCalls[0]
                : null;
            ClearAuthorization(session);
            return new NyxIdChatTurnOperationExecution(new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
                Tool = new NyxIdChatToolOperationResult
                {
                    Receipt = new AgentToolReceipt
                    {
                        CallId = pendingCall?.Id ?? string.Empty,
                        ToolName = pendingCall?.Name ?? string.Empty,
                        ApprovalRequestId = approval.ApprovalRequestId,
                        Status = AgentToolReceiptStatus.Denied,
                        ErrorCode = "approval_denied",
                        ErrorMessage = "Tool approval denied.",
                    },
                    ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                },
            });
        }

        if (session.AuthorizedToolStep is null ||
            session.StepState is null ||
            session.Request is null ||
            session.StepState.PendingToolCalls.Count != 1)
        {
            return Failure(
                command.Key,
                ToolCapabilityLostCode,
                ToolCapabilityLostMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        AgentRunAuthorizedToolStep approvedCapability;
        try
        {
            approvedCapability = session.AuthorizedToolStep.WithApprovalGrant(
                approval.ApprovalRequestId,
                approval.ToolContext?.Credentials);
        }
        catch (InvalidOperationException)
        {
            return Failure(
                command.Key,
                ToolAuthorizationMismatchCode,
                ToolAuthorizationMismatchMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        var pending = session.StepState.PendingToolCalls[0];
        return await ExecuteToolAsync(
                new NyxIdChatOperationDispatchCommand
                {
                    Key = command.Key.Clone(),
                    Tool = new NyxIdChatToolOperationInput
                    {
                        ToolName = pending.Name,
                        CallId = pending.Id,
                        ArgumentsJson = pending.ArgumentsJson,
                        MayChangeExternalState = approval.MayChangeExternalState,
                    },
                },
                session,
                reportProgressAsync,
                ct,
                approvedCapability)
            .ConfigureAwait(false);
    }

    private static string BuildInputResponseJson(NyxIdChatInputContinuationInput input) =>
        input.Answer.AnswerCase switch
        {
            NyxIdChatInputAnswer.AnswerOneofCase.FreeText => JsonSerializer.Serialize(new
            {
                type = "ask_user_response",
                free_text = input.Answer.FreeText,
            }),
            NyxIdChatInputAnswer.AnswerOneofCase.Selection => JsonSerializer.Serialize(new
            {
                type = "ask_user_response",
                selected_options = input.SelectedOptions.Select(static option => new
                {
                    option_id = option.OptionId,
                    label = option.Label,
                }),
            }),
            _ => JsonSerializer.Serialize(new
            {
                type = "ask_user_response",
                error = "invalid_input_answer",
            }),
        };

    private static void RefreshCredentials(
        NyxIdChatTransientExecutionSession session,
        AgentToolCredentialsPayload? credentials)
    {
        if (credentials is null || session.Request is null || session.StepState is null)
            return;

        session.Request = session.Request.Clone();
        session.Request.ToolContext ??= new AgentToolExecutionContextPayload();
        session.Request.ToolContext.Credentials = credentials.Clone();
        session.StepState = session.StepState.Clone();
        session.StepState.ToolContext ??= new AgentToolExecutionContextPayload();
        session.StepState.ToolContext.Credentials = credentials.Clone();
    }

    private static NyxIdChatToolCall BuildToolCall(
        AgentRunToolCall call,
        IReadOnlyList<AgentRunAuthorizedToolCallSafety>? safetySnapshots)
    {
        var result = new NyxIdChatToolCall
        {
            CallId = call.Id,
            ToolName = call.Name,
            ArgumentsJson = call.ArgumentsJson,
        };
        var snapshot = safetySnapshots?.FirstOrDefault(candidate =>
            string.Equals(candidate.CallId, call.Id, StringComparison.Ordinal) &&
            string.Equals(candidate.ToolName, call.Name, StringComparison.Ordinal) &&
            string.Equals(candidate.ArgumentsJson, call.ArgumentsJson, StringComparison.Ordinal));
        if (snapshot is null)
            return result;

        var callSafety = snapshot.CallSafety;
        result.Safety = new NyxIdChatToolCallSafety
        {
            IsReadOnly = callSafety.IsReadOnly,
            IsDestructive = callSafety.IsDestructive,
            SideEffectKind = snapshot.SideEffectKind,
            MayChangeExternalState = !callSafety.IsReadOnly ||
                                     callSafety.IsDestructive ||
                                     !string.IsNullOrWhiteSpace(snapshot.SideEffectKind),
        };
        return result;
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
            ToolContext = MergeDirectInputFileRefs(chat.ToolContext, chat.InputParts),
            LlmControl = chat.LlmControl?.Clone(),
        };
        foreach (var pair in chat.Metadata)
            request.Metadata[pair.Key] = pair.Value;
        return request;
    }

    private static AgentToolExecutionContextPayload? MergeDirectInputFileRefs(
        AgentToolExecutionContextPayload? toolContext,
        IReadOnlyList<ChatContentPart> inputParts)
    {
        if (inputParts.Count == 0)
            return toolContext?.Clone();

        var explicitFileRefs = inputParts
            .Where(static part => part.FileRef is not null && HasFileRefIdentity(part.FileRef))
            .Select(static part => part.FileRef!)
            .ToArray();
        if (explicitFileRefs.Length == 0)
            return toolContext?.Clone();

        var context = AgentToolExecutionContextMapper.FromPayload(toolContext);
        var merged = new List<Aevatar.AI.Abstractions.ChatFileRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fileRef in context.InputFileRefs.Concat(explicitFileRefs))
        {
            var key = FileRefIdentityKey(fileRef);
            if (key is null || !seen.Add(key))
                continue;

            merged.Add(fileRef.Clone());
        }

        return (context with { InputFileRefs = merged }).ToPayload();
    }

    private static bool HasFileRefIdentity(Aevatar.AI.Abstractions.ChatFileRef fileRef) =>
        !string.IsNullOrWhiteSpace(fileRef.FileId) ||
        !string.IsNullOrWhiteSpace(fileRef.ArtifactId);

    private static string? FileRefIdentityKey(Aevatar.AI.Abstractions.ChatFileRef fileRef)
    {
        if (!string.IsNullOrWhiteSpace(fileRef.ArtifactId))
            return $"artifact:{fileRef.ArtifactId.Trim()}";

        if (!string.IsNullOrWhiteSpace(fileRef.FileId))
            return $"file:{fileRef.FileId.Trim()}";

        return null;
    }

    private async Task<AgentProfileTurnCatalog?> MaterializeTurnCatalogAsync(
        NyxIdChatLLMOperationInput input,
        CancellationToken ct)
    {
        var profile = input.AgentProfile;
        var authority = input.AgentProfileTurnAuthority;
        if (profile is null && authority is null)
            return null;
        if (profile is null || authority is null || _turnCatalogMaterializer is null)
            return RestrictedEmptyCatalog();

        var request = input.Request;
        var toolContext = LLMControlContextMapper.FromPayload(request?.LlmControl)
            .ToToolContext(AgentToolExecutionContextMapper.FromPayload(request?.ToolContext));
        try
        {
            return (await _turnCatalogMaterializer.MaterializeCommittedAsync(
                    profile,
                    authority,
                    toolContext.Credentials.NyxIdAccessToken,
                    registeredTools: [],
                    toolContext,
                    ct)
                .ConfigureAwait(false)).Catalog;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return RestrictedEmptyCatalog();
        }
    }

    private static AgentProfileTurnCatalog RestrictedEmptyCatalog() =>
        new(
            [],
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: null,
            candidateIntentId: null);

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

    private static AgentRunReplyStepState ApplyToolFacts(
        AgentRunReplyStepState current,
        AgentRunToolStepResult result,
        int completedStepIndex)
    {
        var next = current.Clone();
        next.NextStepIndex = completedStepIndex;
        next.PendingToolCalls.Clear();
        next.Messages.AddRange(result.ResultMessages.Select(static message => message.Clone()));
        next.AppendedHistory.AddRange(result.ResultMessages.Select(
            AgentRunReplyStepMappers.ToConversationHistoryEntry));
        next.ToolReceipts.AddRange(result.ToolReceipts.Select(static receipt => receipt.Clone()));
        if (result.OutboundIntent is not null)
            next.OutboundIntent = result.OutboundIntent.Clone();
        if (result.AdvanceRound)
            next.Round++;
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
