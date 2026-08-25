using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.NyxId.ExactServiceApprovals;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

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
    private readonly HashSet<string> _publishedToolStartCallIds = new(StringComparer.Ordinal);

    internal AgentRunReplyStepState? StepState { get; set; }
    internal NeedsLlmReplyEvent? Request { get; set; }
    internal AgentRunAuthorizedToolStep? AuthorizedToolStep { get; set; }
    internal IReadOnlyList<AgentRunAuthorizedToolCallSafety> AuthorizedToolCallSafeties { get; set; } = [];
    internal NyxIdChatOperationKey? AuthorizationSourceKey { get; set; }
    internal AgentTurnToolCatalog? TurnCatalog { get; set; }
    internal long ProgressSequence { get; set; }
    internal NyxIdChatStreamingProgressBatcher? StreamingProgressBatcher { get; set; }

    internal void ResetStreamingProgress()
    {
        StreamingProgressBatcher = null;
    }
    internal bool TryMarkToolStartPublished(string callId) =>
        _publishedToolStartCallIds.Add(callId);
}

internal sealed class NyxIdChatStreamingProgressBatcher : IAsyncDisposable
{
    private const int CommandCapacity = 32;
    private readonly Channel<BatchCommand> _commands =
        System.Threading.Channels.Channel.CreateBounded<BatchCommand>(
            new BoundedChannelOptions(CommandCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
    private readonly NyxIdChatOperationKey _key;
    private readonly NyxIdChatTransientExecutionSession _session;
    private readonly Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> _report;
    private readonly TimeProvider _timeProvider;
    private readonly Task _worker;

    public NyxIdChatStreamingProgressBatcher(
        NyxIdChatOperationKey key,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> report,
        TimeProvider timeProvider)
    {
        _key = key.Clone();
        _session = session;
        _report = report;
        _timeProvider = timeProvider;
        _worker = RunAsync();
    }

    public Task QueueAsync(
        NyxIdChatOperationProgressSignal.ProgressOneofCase kind,
        string delta,
        CancellationToken ct) => SubmitAsync(new BatchCommand(kind, delta, false), ct);

    public Task FlushAsync(CancellationToken ct) =>
        SubmitAsync(new BatchCommand(default, string.Empty, true), ct);

    public async ValueTask DisposeAsync()
    {
        if (_worker.IsCompleted)
        {
            await _worker.ConfigureAwait(false);
            return;
        }

        await SubmitAsync(new BatchCommand(default, string.Empty, false, Stop: true),
                CancellationToken.None)
            .ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
    }

    private async Task SubmitAsync(BatchCommand command, CancellationToken ct)
    {
        await _commands.Writer.WriteAsync(command, ct).ConfigureAwait(false);
        await command.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        var pending = new List<NyxIdChatStreamingProgressSegment>();
        var pendingBytes = 0;
        var publishedFirst = false;
        DateTimeOffset? deadline = null;
        Task<BatchCommand>? pendingRead = null;
        while (true)
        {
            pendingRead ??= _commands.Reader.ReadAsync(CancellationToken.None).AsTask();
            if (deadline is { } dueAt)
            {
                var delay = dueAt - _timeProvider.GetUtcNow();
                if (delay <= TimeSpan.Zero)
                {
                    await FlushCoreAsync(pending).ConfigureAwait(false);
                    pendingBytes = 0;
                    deadline = null;
                    continue;
                }

                var timer = Task.Delay(delay, _timeProvider, CancellationToken.None);
                if (await Task.WhenAny(pendingRead, timer).ConfigureAwait(false) == timer)
                {
                    await timer.ConfigureAwait(false);
                    await FlushCoreAsync(pending).ConfigureAwait(false);
                    pendingBytes = 0;
                    deadline = null;
                    continue;
                }
            }

            var command = await pendingRead.ConfigureAwait(false);
            pendingRead = null;
            try
            {
                if (command.Stop)
                {
                    await FlushCoreAsync(pending).ConfigureAwait(false);
                    command.Completion.TrySetResult();
                    return;
                }

                if (command.Flush)
                {
                    await FlushCoreAsync(pending).ConfigureAwait(false);
                    pendingBytes = 0;
                    deadline = null;
                }
                else
                {
                    foreach (var deltaPart in SplitByUtf8Bytes(
                                 command.Delta,
                                 NyxIdChatTurnOperationExecutor.StreamingProgressBatchBytes))
                    {
                        var partBytes = Encoding.UTF8.GetByteCount(deltaPart);
                        if (!publishedFirst)
                        {
                            publishedFirst = true;
                            await ReportSingleAsync(command.Kind, deltaPart).ConfigureAwait(false);
                            continue;
                        }

                        if (pendingBytes > 0 &&
                            pendingBytes + partBytes >
                            NyxIdChatTurnOperationExecutor.StreamingProgressBatchBytes)
                        {
                            await FlushCoreAsync(pending).ConfigureAwait(false);
                            pendingBytes = 0;
                            deadline = null;
                        }

                        Append(pending, command.Kind, deltaPart);
                        pendingBytes += partBytes;
                        deadline ??= _timeProvider.GetUtcNow() +
                                     NyxIdChatTurnOperationExecutor.StreamingProgressBatchInterval;
                        if (pendingBytes >= NyxIdChatTurnOperationExecutor.StreamingProgressBatchBytes)
                        {
                            await FlushCoreAsync(pending).ConfigureAwait(false);
                            pendingBytes = 0;
                            deadline = null;
                        }
                    }
                }

                command.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                command.Completion.TrySetException(exception);
                throw;
            }
        }
    }

    private async Task FlushCoreAsync(List<NyxIdChatStreamingProgressSegment> pending)
    {
        if (pending.Count == 0)
            return;
        var batch = new NyxIdChatStreamingProgressBatch();
        batch.Segments.AddRange(pending);
        pending.Clear();
        await _report(new NyxIdChatOperationProgressSignal
        {
            Key = _key.Clone(),
            Sequence = ++_session.ProgressSequence,
            StreamingBatch = batch,
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private Task ReportSingleAsync(
        NyxIdChatOperationProgressSignal.ProgressOneofCase kind,
        string delta)
    {
        var signal = new NyxIdChatOperationProgressSignal
        {
            Key = _key.Clone(),
            Sequence = ++_session.ProgressSequence,
        };
        if (kind == NyxIdChatOperationProgressSignal.ProgressOneofCase.Text)
            signal.Text = new NyxIdChatTextProgress { Delta = delta };
        else
            signal.Reasoning = new NyxIdChatReasoningProgress { Delta = delta };
        return _report(signal, CancellationToken.None);
    }

    private static IEnumerable<string> SplitByUtf8Bytes(string value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
            yield break;

        var start = 0;
        var bytes = 0;
        for (var index = 0; index < value.Length;)
        {
            var rune = Rune.GetRuneAt(value, index);
            if (bytes > 0 && bytes + rune.Utf8SequenceLength > maxBytes)
            {
                yield return value[start..index];
                start = index;
                bytes = 0;
            }

            bytes += rune.Utf8SequenceLength;
            index += rune.Utf16SequenceLength;
        }

        if (start < value.Length)
            yield return value[start..];
    }

    private static void Append(
        List<NyxIdChatStreamingProgressSegment> pending,
        NyxIdChatOperationProgressSignal.ProgressOneofCase kind,
        string delta)
    {
        var last = pending.LastOrDefault();
        if (kind == NyxIdChatOperationProgressSignal.ProgressOneofCase.Text)
        {
            if (last?.ProgressCase == NyxIdChatStreamingProgressSegment.ProgressOneofCase.Text)
                last.Text.Delta += delta;
            else
                pending.Add(new NyxIdChatStreamingProgressSegment
                {
                    Text = new NyxIdChatTextProgress { Delta = delta },
                });
            return;
        }

        if (last?.ProgressCase == NyxIdChatStreamingProgressSegment.ProgressOneofCase.Reasoning)
            last.Reasoning.Delta += delta;
        else
            pending.Add(new NyxIdChatStreamingProgressSegment
            {
                Reasoning = new NyxIdChatReasoningProgress { Delta = delta },
            });
    }

    private sealed record BatchCommand(
        NyxIdChatOperationProgressSignal.ProgressOneofCase Kind,
        string Delta,
        bool Flush,
        bool Stop = false)
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed record NyxIdChatTurnOperationExecution(
    NyxIdChatOperationResultSignal Result);

public sealed class NyxIdChatTurnOperationExecutor
    : INyxIdChatTurnOperationExecutor
{
    internal const string ToolCapabilityLostCode = "NYXID_CHAT_TOOL_CAPABILITY_LOST";
    internal const string ToolAuthorizationMismatchCode = "NYXID_CHAT_TOOL_AUTHORIZATION_MISMATCH";
    internal const string ToolReceiptRequiredCode = "NYXID_CHAT_TOOL_RECEIPT_REQUIRED";
    internal const string ToolApprovalRequestIdRequiredCode =
        "NYXID_CHAT_TOOL_APPROVAL_REQUEST_ID_REQUIRED";
    internal const string DelegationRefreshFailedCode = "NYXID_CHAT_DELEGATION_REFRESH_FAILED";
    internal const string ExactServiceApprovalFailedCode =
        "NYXID_CHAT_EXACT_SERVICE_APPROVAL_FAILED";
    internal const string AuthorizationContinuationCapabilityUnavailableCode =
        NyxIdChatTaskLifecycle.AuthorizationContinuationCapabilityUnavailable;
    private const string InvalidExecutionResultCode = "NYXID_CHAT_INVALID_EXECUTION_RESULT";
    private const string ProfileRequiredToolUnavailableCode =
        "NYXID_CHAT_PROFILE_REQUIRED_TOOL_UNAVAILABLE";
    private const string UnsupportedOperationCode = "NYXID_CHAT_OPERATION_NOT_SUPPORTED";
    private const string ToolCapabilityLostMessage =
        "The authorized tool capability is no longer available. Retry from a safe checkpoint.";
    private const string ToolAuthorizationMismatchMessage =
        "The tool command did not match the exact authorized tool call.";
    private const string ToolReceiptRequiredMessage =
        "The effect-capable tool did not return the required outcome receipt.";
    private const string ToolApprovalRequestIdRequiredMessage =
        "The approval-required tool result did not identify the NyxID approval request.";
    private const string DelegationRefreshFailedMessage =
        "The delegated NyxID credential could not be refreshed.";
    private const string InvalidExecutionResultMessage =
        "The operation executor returned an invalid typed result.";
    private const string ProfileRequiredToolUnavailableMessage =
        "The selected Profile requires typed connected-service readiness, but that bounded capability is unavailable.";
    private const string UnsupportedOperationMessage =
        "This operation kind is not available in the turn executor.";
    private const string InvalidPostconditionInputCode =
        "NYXID_ACTION_POSTCONDITION_INPUT_INVALID";
    private const string InvalidPostconditionInputMessage =
        "The action postcondition input was invalid.";
    internal const string AuthorizationContinuationCapabilityUnavailableMessage =
        NyxIdChatTaskLifecycle.AuthorizationContinuationCapabilityUnavailableMessage;
    private const string PrepareOperationSubstepId = "prepare-operation";
    private const string ExecuteOperationSubstepId = "execute-operation";
    private const string WebSearchToolName = "web_search";
    internal const int StreamingProgressBatchBytes = 64 * 1024;
    internal static readonly TimeSpan StreamingProgressBatchInterval = TimeSpan.FromSeconds(1);

    private readonly IAgentRunReplyGenerationExecutorPort _generationExecutor;
    private readonly INyxIdActionPostconditionPort _actionPostconditionPort;
    private readonly AgentTurnToolCatalogMaterializer? _turnCatalogMaterializer;
    private readonly INyxIdChatDelegationCredentialLifecyclePort _delegationCredentialLifecycle;
    private readonly INyxIdChatToolVerificationPort _toolVerificationPort;
    private readonly INyxIdExactServiceApprovalPort _exactServiceApprovalPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NyxIdChatTurnOperationExecutor> _logger;

    public NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor)
        : this(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            null,
            new NyxIdChatDelegationCredentialLifecyclePort(TimeProvider.System),
            new NyxIdChatToolVerificationPort())
    {
    }

    public NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor,
        INyxIdActionPostconditionPort actionPostconditionPort)
        : this(
            generationExecutor,
            actionPostconditionPort,
            null,
            new NyxIdChatDelegationCredentialLifecyclePort(TimeProvider.System),
            new NyxIdChatToolVerificationPort())
    {
    }

    public NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor,
        INyxIdActionPostconditionPort actionPostconditionPort,
        AgentTurnToolCatalogMaterializer? turnCatalogMaterializer)
        : this(
            generationExecutor,
            actionPostconditionPort,
            turnCatalogMaterializer,
            new NyxIdChatDelegationCredentialLifecyclePort(TimeProvider.System),
            new NyxIdChatToolVerificationPort())
    {
    }

    public NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor,
        INyxIdActionPostconditionPort actionPostconditionPort,
        AgentTurnToolCatalogMaterializer? turnCatalogMaterializer,
        INyxIdChatDelegationCredentialLifecyclePort delegationCredentialLifecycle)
        : this(
            generationExecutor,
            actionPostconditionPort,
            turnCatalogMaterializer,
            delegationCredentialLifecycle,
            new NyxIdChatToolVerificationPort())
    {
    }

    public NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor,
        INyxIdActionPostconditionPort actionPostconditionPort,
        AgentTurnToolCatalogMaterializer? turnCatalogMaterializer,
        INyxIdChatDelegationCredentialLifecyclePort delegationCredentialLifecycle,
        INyxIdChatToolVerificationPort toolVerificationPort)
        : this(
            generationExecutor,
            actionPostconditionPort,
            turnCatalogMaterializer,
            delegationCredentialLifecycle,
            toolVerificationPort,
            TimeProvider.System,
            NullLogger<NyxIdChatTurnOperationExecutor>.Instance)
    {
    }

    public NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor,
        INyxIdActionPostconditionPort actionPostconditionPort,
        AgentTurnToolCatalogMaterializer? turnCatalogMaterializer,
        INyxIdChatDelegationCredentialLifecyclePort delegationCredentialLifecycle,
        INyxIdChatToolVerificationPort toolVerificationPort,
        ILogger<NyxIdChatTurnOperationExecutor> logger)
        : this(
            generationExecutor,
            actionPostconditionPort,
            turnCatalogMaterializer,
            delegationCredentialLifecycle,
            toolVerificationPort,
            TimeProvider.System,
            logger)
    {
    }

    public NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor,
        INyxIdActionPostconditionPort actionPostconditionPort,
        AgentTurnToolCatalogMaterializer? turnCatalogMaterializer,
        INyxIdChatDelegationCredentialLifecyclePort delegationCredentialLifecycle,
        INyxIdChatToolVerificationPort toolVerificationPort,
        INyxIdExactServiceApprovalPort exactServiceApprovalPort,
        ILogger<NyxIdChatTurnOperationExecutor> logger)
        : this(
            generationExecutor,
            actionPostconditionPort,
            turnCatalogMaterializer,
            delegationCredentialLifecycle,
            toolVerificationPort,
            exactServiceApprovalPort,
            TimeProvider.System,
            logger)
    {
    }

    internal NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor,
        INyxIdActionPostconditionPort actionPostconditionPort,
        AgentTurnToolCatalogMaterializer? turnCatalogMaterializer,
        INyxIdChatDelegationCredentialLifecyclePort delegationCredentialLifecycle,
        INyxIdChatToolVerificationPort toolVerificationPort,
        TimeProvider timeProvider)
        : this(
            generationExecutor,
            actionPostconditionPort,
            turnCatalogMaterializer,
            delegationCredentialLifecycle,
            toolVerificationPort,
            timeProvider,
            NullLogger<NyxIdChatTurnOperationExecutor>.Instance)
    {
    }

    internal NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor,
        INyxIdActionPostconditionPort actionPostconditionPort,
        AgentTurnToolCatalogMaterializer? turnCatalogMaterializer,
        INyxIdChatDelegationCredentialLifecyclePort delegationCredentialLifecycle,
        INyxIdChatToolVerificationPort toolVerificationPort,
        TimeProvider timeProvider,
        ILogger<NyxIdChatTurnOperationExecutor> logger)
        : this(
            generationExecutor,
            actionPostconditionPort,
            turnCatalogMaterializer,
            delegationCredentialLifecycle,
            toolVerificationPort,
            new UnavailableNyxIdExactServiceApprovalPort(),
            timeProvider,
            logger)
    {
    }

    internal NyxIdChatTurnOperationExecutor(
        IAgentRunReplyGenerationExecutorPort generationExecutor,
        INyxIdActionPostconditionPort actionPostconditionPort,
        AgentTurnToolCatalogMaterializer? turnCatalogMaterializer,
        INyxIdChatDelegationCredentialLifecyclePort delegationCredentialLifecycle,
        INyxIdChatToolVerificationPort toolVerificationPort,
        INyxIdExactServiceApprovalPort exactServiceApprovalPort,
        TimeProvider timeProvider,
        ILogger<NyxIdChatTurnOperationExecutor> logger)
    {
        _generationExecutor = generationExecutor ?? throw new ArgumentNullException(nameof(generationExecutor));
        _actionPostconditionPort = actionPostconditionPort ??
                                   throw new ArgumentNullException(nameof(actionPostconditionPort));
        _turnCatalogMaterializer = turnCatalogMaterializer;
        _delegationCredentialLifecycle = delegationCredentialLifecycle ??
                                         throw new ArgumentNullException(nameof(delegationCredentialLifecycle));
        _toolVerificationPort = toolVerificationPort ??
                                throw new ArgumentNullException(nameof(toolVerificationPort));
        _exactServiceApprovalPort = exactServiceApprovalPort ??
                                    throw new ArgumentNullException(nameof(exactServiceApprovalPort));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                await ExecuteActionPostconditionAsync(command, session, ct).ConfigureAwait(false),
            NyxIdChatOperationDispatchCommand.InputOneofCase.InputContinuation =>
                await ExecuteInputContinuationAsync(command, session, reportProgressAsync, ct)
                    .ConfigureAwait(false),
            NyxIdChatOperationDispatchCommand.InputOneofCase.ConditionContinuation =>
                await ExecuteConditionContinuationAsync(command, session, reportProgressAsync, ct)
                    .ConfigureAwait(false),
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolApprovalContinuation =>
                await ExecuteToolApprovalContinuationAsync(command, session, reportProgressAsync, ct)
                    .ConfigureAwait(false),
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolVerification =>
                await ExecuteToolVerificationAsync(command, session, ct).ConfigureAwait(false),
            _ => Failure(
                command.Key,
                UnsupportedOperationCode,
                UnsupportedOperationMessage,
                NyxIdChatEffectEvidence.NotStarted),
        };
    }

    private async Task<NyxIdChatTurnOperationExecution> ExecuteToolVerificationAsync(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        CancellationToken ct)
    {
        var input = command.ToolVerification?.Clone();
        if (input?.ReadBack is null || string.IsNullOrWhiteSpace(input.EffectStepId))
        {
            return new NyxIdChatTurnOperationExecution(new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
                ToolVerification = new NyxIdChatToolVerificationResult
                {
                    EffectStepId = input?.EffectStepId ?? string.Empty,
                    Disposition = NyxIdChatToolVerificationDisposition.Unavailable,
                    FailureCode = NyxIdChatToolVerificationPort.UnavailableCode,
                    SafeMessage = "The typed verification contract was invalid.",
                },
            });
        }

        input.ToolContext ??= session.Request?.ToolContext?.Clone();
        var result = await _toolVerificationPort.VerifyAsync(command.Key, input, ct)
            .ConfigureAwait(false);
        return new NyxIdChatTurnOperationExecution(new NyxIdChatOperationResultSignal
        {
            Key = command.Key.Clone(),
            ToolVerification = result,
        });
    }

    private async Task<NyxIdChatTurnOperationExecution> ExecuteActionPostconditionAsync(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
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

        if (input.ToolContext is not null &&
            !RefreshToolCredentials(session, input.ToolContext.Credentials))
        {
            return Postcondition(
                command.Key,
                input,
                verified: false,
                ToolAuthorizationMismatchCode,
                ToolAuthorizationMismatchMessage);
        }

        var verificationInput = input.Clone();
        verificationInput.ToolContext = null;
        var result = await _actionPostconditionPort
            .VerifyAsync(
                verificationInput,
                session.Request?.ToolContext?.Clone(),
                ct)
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
        session.ResetStreamingProgress();
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
        if (await EnsureDelegationCredentialAsync(command.Key, session, request, ct).ConfigureAwait(false) is
            { } credentialFailure)
        {
            return credentialFailure;
        }
        var verifiedAuthorization = command.Llm.VerifiedAuthorizationContinuation;
        if (verifiedAuthorization is not null)
        {
            var hasExactVerifiedUserService =
                verifiedAuthorization.VerifiedResource?.ResourceCase ==
                NyxIdChatSafeResourceRef.ResourceOneofCase.UserService &&
                !string.IsNullOrWhiteSpace(
                    verifiedAuthorization.VerifiedResource.UserService.UserServiceId) &&
                !string.IsNullOrWhiteSpace(verifiedAuthorization.ServiceSlug);
            if (!isContinuation || !hasExactVerifiedUserService)
            {
                ClearAuthorization(session);
                return Failure(
                    command.Key,
                    AuthorizationContinuationCapabilityUnavailableCode,
                    AuthorizationContinuationCapabilityUnavailableMessage,
                    NyxIdChatEffectEvidence.NotStarted);
            }

            switch (verifiedAuthorization.ResumeRequirement)
            {
                case NyxIdChatAuthorizationResumeRequirement.CompleteOriginalServiceRequest:
                    var catalogToolContext = ResolveCatalogToolContext(request);
                    var materializedCatalog = _turnCatalogMaterializer is null
                        ? RestrictedEmptyCatalog()
                        : await _turnCatalogMaterializer
                            .MaterializeVerifiedAuthorizationContinuationAsync(
                                command.Llm.AgentProfile,
                                command.Llm.AgentProfileTurnAuthority,
                                verifiedAuthorization,
                                request.Activity?.Content?.Text ?? string.Empty,
                                LLMControlContextMapper.FromPayload(request.LlmControl),
                                catalogToolContext,
                                ct)
                            .ConfigureAwait(false);
                    LogVerifiedAuthorizationCatalogDiagnostic(
                        catalogToolContext,
                        materializedCatalog,
                        verifiedAuthorization);
                    session.TurnCatalog = AgentTurnToolCatalogMaterializer
                        .NarrowToVerifiedUserService(
                            materializedCatalog,
                            verifiedAuthorization);
                    if (session.TurnCatalog.FinalAllowedToolNames.Count == 0)
                    {
                        ClearAuthorization(session);
                        return Failure(
                            command.Key,
                            AuthorizationContinuationCapabilityUnavailableCode,
                            AuthorizationContinuationCapabilityUnavailableMessage,
                            NyxIdChatEffectEvidence.NotStarted);
                    }

                    break;
                case NyxIdChatAuthorizationResumeRequirement.CommunicateAuthorizationCompletion:
                    session.TurnCatalog = RestrictedEmptyCatalog();
                    break;
                default:
                    ClearAuthorization(session);
                    return Failure(
                        command.Key,
                        AuthorizationContinuationCapabilityUnavailableCode,
                        AuthorizationContinuationCapabilityUnavailableMessage,
                        NyxIdChatEffectEvidence.NotStarted);
            }

            RepinRematerializedTurnCatalog(session, session.TurnCatalog);
        }
        else if (isContinuation && command.Llm.RematerializeTurnCatalog)
        {
            session.TurnCatalog = await MaterializeTurnCatalogAsync(command.Llm, request, ct)
                                      .ConfigureAwait(false) ?? RestrictedEmptyCatalog();
            RepinRematerializedTurnCatalog(session, session.TurnCatalog);
        }
        else if (!isContinuation && session.TurnCatalog is null)
        {
            session.TurnCatalog = command.Key.OperationGeneration > 1 &&
                                  (command.Llm.AgentProfile is not null ||
                                   command.Llm.AgentProfileTurnAuthority is not null)
                ? RestrictedEmptyCatalog()
                : await MaterializeTurnCatalogAsync(command.Llm, request, ct).ConfigureAwait(false);
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
        if (verifiedAuthorization is not null)
            AppendVerifiedAuthorizationInstruction(stepState, verifiedAuthorization);

        var outputParts = new List<ChatContentPart>();
        AgentRunLlmStepExecution execution;
        await using (var batcher = new NyxIdChatStreamingProgressBatcher(
                         command.Key,
                         session,
                         reportProgressAsync,
                         _timeProvider))
        {
            session.StreamingProgressBatcher = batcher;
            try
            {
                execution = await _generationExecutor.BuildLlmStepExecutionAsync(
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
                            session.TurnCatalog,
                            AllowMultipleToolCalls: false),
                        ct)
                    .ConfigureAwait(false);
                await batcher.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (AgentProfileRequiredToolUnavailableException)
            {
                ClearAuthorization(session);
                return Failure(
                    command.Key,
                    ProfileRequiredToolUnavailableCode,
                    ProfileRequiredToolUnavailableMessage,
                    NyxIdChatEffectEvidence.NotStarted);
            }
            finally
            {
                session.StreamingProgressBatcher = null;
            }
        }

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
        session.AuthorizedToolCallSafeties = execution.AuthorizedToolCallSafeties?
            .Select(SealDurableAuthorization)
            .ToArray() ?? [];
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
        result.ToolCatalogCaptured = facts.ToolCatalogCaptured;
        result.AvailableToolNames.AddRange(facts.AvailableToolNames);
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
        var toolInput = command.Tool;
        if (toolInput is null)
        {
            return Failure(
                command.Key,
                ToolAuthorizationMismatchCode,
                ToolAuthorizationMismatchMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }
        var durableRetry = toolInput.RematerializeDurableAuthorization;
        if (durableRetry)
        {
            var hasAgentProfile = toolInput.AgentProfile is not null;
            var hasAgentProfileTurnAuthority =
                toolInput.AgentProfileTurnAuthority is not null;
            if (hasAgentProfile != hasAgentProfileTurnAuthority)
            {
                ClearAuthorization(session);
                return Failure(
                    command.Key,
                    ToolAuthorizationMismatchCode,
                    ToolAuthorizationMismatchMessage,
                    NyxIdChatEffectEvidence.NotStarted);
            }

            if (!NyxIdChatDurableRetryAuthority.IsValid(command.Key, toolInput.ToolContext))
            {
                ClearAuthorization(session);
                return Failure(
                    command.Key,
                    ToolAuthorizationMismatchCode,
                    ToolAuthorizationMismatchMessage,
                    NyxIdChatEffectEvidence.NotStarted);
            }

            if (!TryRestoreDurableRetrySession(command, session))
            {
                ClearAuthorization(session);
                return Failure(
                    command.Key,
                    ToolAuthorizationMismatchCode,
                    ToolAuthorizationMismatchMessage,
                    NyxIdChatEffectEvidence.NotStarted);
            }

            if (await EnsureDelegationCredentialAsync(
                    command.Key,
                    session,
                    session.Request!,
                    ct)
                .ConfigureAwait(false) is { } durableCredentialFailure)
            {
                return durableCredentialFailure;
            }
            toolInput.ToolContext = session.Request!.ToolContext!.Clone();

            if (hasAgentProfile)
            {
                var turnCatalog = await MaterializeDurableRetryTurnCatalogAsync(toolInput, ct)
                    .ConfigureAwait(false);
                if (turnCatalog is null ||
                    !turnCatalog.FinalAllowedToolNames.Contains(toolInput.ToolName) ||
                    !turnCatalog.ExactTools.ContainsKey(toolInput.ToolName))
                {
                    ClearAuthorization(session);
                    return Failure(
                        command.Key,
                        ToolAuthorizationMismatchCode,
                        ToolAuthorizationMismatchMessage,
                        NyxIdChatEffectEvidence.NotStarted);
                }

                session.TurnCatalog = turnCatalog;
            }
            else
            {
                session.TurnCatalog = null;
            }
        }
        if ((!durableRetry && session.AuthorizedToolStep is null) ||
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

        var matchingAuthorizations = session.AuthorizedToolCallSafeties.Where(candidate =>
            string.Equals(candidate.CallId, toolInput.CallId, StringComparison.Ordinal) &&
            string.Equals(candidate.ToolName, toolInput.ToolName, StringComparison.Ordinal) &&
            string.Equals(candidate.ArgumentsJson, toolInput.ArgumentsJson, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        var authorization = matchingAuthorizations.Length == 1
            ? matchingAuthorizations[0]
            : null;
        if (!SameTask(session.AuthorizationSourceKey, command.Key) ||
            session.StepState.PendingToolCalls.Count != 1 ||
            !ToolCallMatches(session.StepState.PendingToolCalls[0], authorization, toolInput))
        {
            return Failure(
                command.Key,
                ToolAuthorizationMismatchCode,
                ToolAuthorizationMismatchMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        var phaseTitles = ResolveToolPhaseTitles(toolInput.ToolName);
        await ReportPhaseAsync(
                command.Key,
                PrepareOperationSubstepId,
                phaseTitles.Prepare,
                NyxIdChatSubstepStatus.Running,
                session,
                        reportProgressAsync,
                        ct)
            .ConfigureAwait(false);
        if (!durableRetry &&
            await EnsureDelegationCredentialAsync(
                command.Key,
                session,
                session.Request,
                ct)
            .ConfigureAwait(false) is { } credentialFailure)
        {
            return credentialFailure;
        }
        if (authorizedToolStep is not null &&
            session.StepState?.ToolContext?.Credentials is { } currentCredentials)
        {
            authorizedToolStep = authorizedToolStep.WithRefreshedCredentials(currentCredentials);
        }

        var currentStepState = session.StepState!;
        var currentRequest = session.Request!;
        var workItem = new AgentRunReplyStepExecutionRequest(
            currentStepState.RunId,
            NyxIdChatTurnActorIds.ForTurn(
                command.Key.ConversationActorId,
                command.Key.TurnId),
            currentStepState.Attempt,
            currentStepState.NextStepIndex,
            currentRequest.Clone(),
            currentStepState.Clone(),
            TurnCatalog: session.TurnCatalog);
        if (!durableRetry && !session.AuthorizedToolStep!.Matches(workItem))
        {
            return Failure(
                command.Key,
                ToolAuthorizationMismatchCode,
                ToolAuthorizationMismatchMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        await ReportPhaseAsync(
                command.Key,
                PrepareOperationSubstepId,
                phaseTitles.Prepare,
                NyxIdChatSubstepStatus.Done,
                session,
                reportProgressAsync,
                ct)
            .ConfigureAwait(false);

        if (await TryCreateExactServiceApprovalAsync(command, session, toolInput, ct)
                .ConfigureAwait(false) is { } exactApproval)
        {
            return exactApproval;
        }

        await ReportToolStartedOnceAsync(
                command.Key,
                new NyxIdChatToolProgress
                {
                    CallId = toolInput.CallId,
                    ToolName = toolInput.ToolName,
                    Presentation = NyxIdChatDurableToolPresentation.Snapshot(
                        authorization?.Presentation,
                        toolInput.ToolName),
                },
                session,
                reportProgressAsync,
                ct)
            .ConfigureAwait(false);

        var capability = durableRetry
            ? null
            : (authorizedToolStep ?? session.AuthorizedToolStep)!
                .WithChatOperation(
                    command.Key,
                    toolInput.IdempotencyKey,
                    toolInput.OperationAdmission);
        await ReportPhaseAsync(
                command.Key,
                ExecuteOperationSubstepId,
                phaseTitles.Execute,
                NyxIdChatSubstepStatus.Running,
                session,
                reportProgressAsync,
                ct)
            .ConfigureAwait(false);
        var continuation = await _generationExecutor.BuildToolStepContinuationAsync(
                durableRetry
                    ? workItem with { AllowDurableToolAuthorization = true }
                    : workItem,
                capability,
                ct)
            .ConfigureAwait(false);
        if (!IsValidToolContinuation(continuation, workItem))
        {
            return Failure(
                command.Key,
                InvalidExecutionResultCode,
                InvalidExecutionResultMessage,
                toolInput.MayChangeExternalState
                    ? NyxIdChatEffectEvidence.MayHaveChanged
                    : NyxIdChatEffectEvidence.NotApplied);
        }
        if (durableRetry &&
            continuation.ToolStepResult.AuthorizationOutcome !=
            AgentRunToolAuthorizationOutcome.DurableMatched)
        {
            return Failure(
                command.Key,
                ToolAuthorizationMismatchCode,
                ToolAuthorizationMismatchMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        await ReportPhaseAsync(
                command.Key,
                ExecuteOperationSubstepId,
                phaseTitles.Execute,
                NyxIdChatSubstepStatus.Done,
                session,
                reportProgressAsync,
                ct)
            .ConfigureAwait(false);

        var toolResult = continuation.ToolStepResult!;
        var resultMessages = toolResult.ResultMessages
            .Where(message => string.Equals(
                message.ToolCallId,
                toolInput.CallId,
                StringComparison.Ordinal))
            .ToArray();
        if (resultMessages.Length != 1)
        {
            return Failure(
                command.Key,
                InvalidExecutionResultCode,
                InvalidExecutionResultMessage,
                toolInput.MayChangeExternalState
                    ? NyxIdChatEffectEvidence.MayHaveChanged
                    : NyxIdChatEffectEvidence.NotApplied);
        }

        var receipt = toolResult.ToolReceipts.LastOrDefault(candidate =>
            string.Equals(candidate.CallId, toolInput.CallId, StringComparison.Ordinal));
        if (receipt is null && toolInput.MayChangeExternalState)
        {
            return Failure(
                command.Key,
                ToolReceiptRequiredCode,
                ToolReceiptRequiredMessage,
                NyxIdChatEffectEvidence.MayHaveChanged);
        }

        receipt = receipt?.Clone() ?? new AgentToolReceipt
        {
            CallId = toolInput.CallId,
            ToolName = toolInput.ToolName,
            Status = AgentToolReceiptStatus.Success,
        };
        receipt.Effect = toolInput.MayChangeExternalState
            ? AgentToolReceiptEffect.Mutating
            : AgentToolReceiptEffect.ReadOnly;
        var resultJson = resultMessages[0].Content;
        if (string.IsNullOrWhiteSpace(receipt.ResultJson))
            receipt.ResultJson = resultJson;

        if (receipt.Status is AgentToolReceiptStatus.ApprovalRequired or
            AgentToolReceiptStatus.Denied)
        {
            if (string.IsNullOrWhiteSpace(receipt.ApprovalRequestId) ||
                string.Equals(receipt.ApprovalRequestId.Trim(), "tool_approval", StringComparison.Ordinal))
            {
                return Failure(
                    command.Key,
                    ToolApprovalRequestIdRequiredCode,
                    ToolApprovalRequestIdRequiredMessage,
                    NyxIdChatEffectEvidence.NotStarted);
            }

            return new NyxIdChatTurnOperationExecution(new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
                Tool = new NyxIdChatToolOperationResult
                {
                    ResultJson = resultJson,
                    Receipt = receipt,
                    ExternalEffect = ResolveExternalEffect(toolInput, receipt),
                },
            });
        }

        ClearAuthorization(session);
        session.StepState = ApplyToolFacts(
            session.StepState!,
            toolResult,
            continuation.StepIndex);

        return new NyxIdChatTurnOperationExecution(new NyxIdChatOperationResultSignal
        {
            Key = command.Key.Clone(),
            Tool = new NyxIdChatToolOperationResult
            {
                ResultJson = resultJson,
                Receipt = receipt,
                ExternalEffect = ResolveExternalEffect(toolInput, receipt),
            },
        });
    }

    private async Task<NyxIdChatTurnOperationExecution?> TryCreateExactServiceApprovalAsync(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        NyxIdChatToolOperationInput toolInput,
        CancellationToken ct)
    {
        if (!toolInput.MayChangeExternalState || toolInput.OperationAdmission is null)
            return null;

        var admission = AgentToolOperationAdmissionPayloadMapper.FromPayload(
            toolInput.OperationAdmission);
        if (admission?.Identity is not AgentToolOperationIdentity.PublishedEndpoint)
            return null;

        // The explicit unavailable adapter must be allowed to declare Tier A
        // absent before credentials are required. The real adapter validates
        // the token and therefore still fails closed when Tier A is present.
        var token = Normalize(session.Request?.ToolContext?.Credentials?.NyxIdAccessToken) ??
                    string.Empty;

        JsonNode? arguments;
        try
        {
            arguments = JsonNode.Parse(toolInput.ArgumentsJson);
        }
        catch (JsonException)
        {
            arguments = null;
        }
        if (arguments is not JsonObject)
        {
            return Failure(
                command.Key,
                ExactServiceApprovalFailedCode,
                "The exact-service operation arguments were invalid.",
                NyxIdChatEffectEvidence.NotStarted);
        }

        NyxIdExactServiceApprovalCreateResult created;
        try
        {
            created = await _exactServiceApprovalPort.CreateAsync(
                    token,
                    admission,
                    arguments,
                    command.Key.OperationId,
                    command.Key.OperationGeneration,
                    toolInput.IdempotencyKey,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Exact-service approval creation failed before effect admission: operationId={OperationId}",
                command.Key.OperationId);
            return Failure(
                command.Key,
                ExactServiceApprovalFailedCode,
                "The exact-service approval authority could not be requested.",
                NyxIdChatEffectEvidence.NotStarted);
        }

        if (created.Disposition is
            NyxIdExactServiceApprovalCreateDisposition.TierAUnavailable or
            NyxIdExactServiceApprovalCreateDisposition.ApprovalNotRequired)
        {
            return null;
        }
        if (created.Disposition != NyxIdExactServiceApprovalCreateDisposition.Created ||
            created.Snapshot is null)
        {
            return Failure(
                command.Key,
                ExactServiceApprovalFailedCode,
                "The exact-service approval authority was rejected.",
                NyxIdChatEffectEvidence.NotStarted);
        }

        ClearAuthorization(session);
        return BuildExactServiceApprovalExecution(
            command.Key,
            toolInput.CallId,
            toolInput.ToolName,
            created.Snapshot,
            toolInput.OperationAdmission?.ReadBack,
            session);
    }

    internal static NyxIdChatTurnOperationExecution BuildExactServiceApprovalExecution(
        NyxIdChatOperationKey key,
        string callId,
        string toolName,
        NyxIdExactServiceApprovalSnapshot snapshot,
        AgentToolOperationReadBackPayload? readBack,
        NyxIdChatTransientExecutionSession session)
    {
        var authority = snapshot.Authority.Clone();
        var receipt = new AgentToolReceipt
        {
            CallId = callId,
            ToolName = toolName,
            ApprovalRequestId = authority.RequestId,
            ApprovalMode = AgentToolReceiptApprovalMode.AlwaysRequire,
            NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.PerRequest,
            Effect = AgentToolReceiptEffect.Mutating,
            SubjectKind = "user_service",
            SubjectId = authority.UserServiceId,
            SideEffectKind = toolName,
            ExactServiceApproval = authority,
        };
        var externalEffect = NyxIdChatEffectEvidence.NotStarted;
        var resultJson = string.Empty;

        switch (snapshot.State)
        {
            case NyxIdExactServiceApprovalState.Pending:
            case NyxIdExactServiceApprovalState.Approved:
                receipt.Status = AgentToolReceiptStatus.ApprovalRequired;
                resultJson = JsonSerializer.Serialize(new
                {
                    type = "exact_service_approval_required",
                    approval_request_id = authority.RequestId,
                });
                break;
            case NyxIdExactServiceApprovalState.Denied:
                receipt.Status = AgentToolReceiptStatus.Denied;
                receipt.NyxIdApprovalTerminalOutcome =
                    NyxIdApprovalTerminalOutcome.Rejected;
                receipt.ErrorCode = "approval_denied";
                receipt.ErrorMessage = "Exact-service approval denied.";
                externalEffect = NyxIdChatEffectEvidence.NotApplied;
                break;
            case NyxIdExactServiceApprovalState.Expired:
                receipt.Status = AgentToolReceiptStatus.Denied;
                receipt.NyxIdApprovalTerminalOutcome =
                    NyxIdApprovalTerminalOutcome.Expired;
                receipt.ErrorCode = "approval_expired";
                receipt.ErrorMessage = "Exact-service approval expired.";
                externalEffect = NyxIdChatEffectEvidence.NotApplied;
                break;
            case NyxIdExactServiceApprovalState.Redeemed when snapshot.Receipt is not null:
                if (!NyxIdExactServiceApprovalReceiptValidator.HasValidDigest(snapshot.Receipt))
                {
                    receipt.Status = AgentToolReceiptStatus.Error;
                    receipt.ErrorCode = "exact_service_receipt_digest_mismatch";
                    receipt.ErrorMessage =
                        "Exact-service receipt integrity validation failed.";
                    externalEffect = NyxIdChatEffectEvidence.MayHaveChanged;
                    break;
                }
                if (!NyxIdExactServiceApprovalReceiptValidator.IsSuccessfulHttpStatus(
                        snapshot.Receipt))
                {
                    receipt.Status = AgentToolReceiptStatus.Error;
                    receipt.ErrorCode = "exact_service_provider_http_error";
                    receipt.ErrorMessage =
                        "Exact-service provider returned an unsuccessful response.";
                    externalEffect = NyxIdChatEffectEvidence.MayHaveChanged;
                    break;
                }
                receipt.Status = AgentToolReceiptStatus.Success;
                resultJson = snapshot.Receipt.ResponseBody;
                receipt.ResultJson = resultJson;
                receipt.ProviderResourceId =
                    NyxIdEffectResultIdentityExtractor.Extract(readBack, resultJson) ??
                    string.Empty;
                externalEffect = NyxIdChatEffectEvidence.MayHaveChanged;
                ApplyExactServiceResultToSession(session, callId, toolName, resultJson, receipt);
                break;
            case NyxIdExactServiceApprovalState.Redeemed:
                receipt.Status = AgentToolReceiptStatus.Error;
                receipt.ErrorCode = "exact_service_receipt_missing";
                receipt.ErrorMessage = "Exact-service receipt was unavailable.";
                externalEffect = NyxIdChatEffectEvidence.MayHaveChanged;
                break;
            case NyxIdExactServiceApprovalState.Redeeming:
                receipt.Status = AgentToolReceiptStatus.Error;
                receipt.ErrorCode = "exact_service_redemption_in_progress";
                receipt.ErrorMessage = "Exact-service effect admission is still in progress.";
                externalEffect = NyxIdChatEffectEvidence.MayHaveChanged;
                break;
            case NyxIdExactServiceApprovalState.Failed:
                receipt.Status = AgentToolReceiptStatus.Error;
                receipt.ErrorCode = snapshot.FailureCode ?? "exact_service_approval_failed";
                receipt.ErrorMessage = "Exact-service approval redemption failed.";
                externalEffect = NyxIdChatEffectEvidence.MayHaveChanged;
                break;
            default:
                receipt.Status = AgentToolReceiptStatus.Error;
                receipt.ErrorCode = snapshot.FailureCode ?? "exact_service_approval_failed";
                receipt.ErrorMessage = "Exact-service approval could not be used.";
                externalEffect = NyxIdChatEffectEvidence.NotApplied;
                break;
        }

        return new NyxIdChatTurnOperationExecution(new NyxIdChatOperationResultSignal
        {
            Key = key.Clone(),
            Tool = new NyxIdChatToolOperationResult
            {
                ResultJson = resultJson,
                Receipt = receipt,
                ExternalEffect = externalEffect,
            },
        });
    }

    private static void ApplyExactServiceResultToSession(
        NyxIdChatTransientExecutionSession session,
        string callId,
        string toolName,
        string resultJson,
        AgentToolReceipt receipt)
    {
        if (session.StepState is null)
            return;

        var result = new AgentRunToolStepResult { AdvanceRound = true };
        result.ResultMessages.Add(AgentRunReplyStepMappers.ToProto(
            ToolCallLoop.BuildToolResultMessage(callId, toolName, resultJson)));
        result.ToolReceipts.Add(receipt.Clone());
        session.StepState = ApplyToolFacts(
            session.StepState,
            result,
            checked(session.StepState.NextStepIndex + 1));
        ClearAuthorization(session);
    }

    private async Task<NyxIdChatTurnOperationExecution> ExecuteInputContinuationAsync(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        var input = command.InputContinuation;
        if (input?.Answer is null ||
            string.IsNullOrWhiteSpace(input.RequestId) ||
            input.Answer.AnswerCase is not
                (NyxIdChatInputAnswer.AnswerOneofCase.FreeText or
                 NyxIdChatInputAnswer.AnswerOneofCase.Selection) ||
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
        if (!RefreshCredentials(session, input.ToolContext?.Credentials))
        {
            ClearAuthorization(session);
            return Failure(
                command.Key,
                ToolAuthorizationMismatchCode,
                ToolAuthorizationMismatchMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }
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

    private async Task<NyxIdChatTurnOperationExecution> ExecuteConditionContinuationAsync(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        var continuation = command.ConditionContinuation;
        var pending = session.StepState?.PendingToolCalls.Count == 1
            ? session.StepState.PendingToolCalls[0]
            : null;
        if (continuation?.Condition is not { } condition ||
            session.Request is null ||
            pending is null ||
            !string.Equals(pending.Id, continuation.ToolCallId, StringComparison.Ordinal) ||
            !string.Equals(pending.Name, NyxIdChatConditionEvaluateContract.ToolName,
                StringComparison.Ordinal) ||
            !NyxIdChatConditionEvaluateContract.TryParse(
                pending.ArgumentsJson,
                out var proposal) ||
            !MatchesConditionProposal(condition, proposal))
        {
            return Failure(
                command.Key,
                ToolCapabilityLostCode,
                ToolCapabilityLostMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        var resultJson = JsonSerializer.Serialize(new
        {
            type = "condition_evaluate_response",
            condition_id = condition.ConditionId,
            comparison = "gte",
            outcome = condition.Outcome == NyxIdChatConditionOutcome.True,
            observed_value = condition.ObservedValue,
            effective_threshold = condition.EffectiveThreshold,
            guarded_tool_name = condition.GuardedToolName,
        });
        var result = new AgentRunToolStepResult { AdvanceRound = true };
        result.ResultMessages.Add(AgentRunReplyStepMappers.ToProto(
            ToolCallLoop.BuildToolResultMessage(
                continuation.ToolCallId,
                NyxIdChatConditionEvaluateContract.ToolName,
                resultJson)));
        session.StepState = ApplyToolFacts(
            session.StepState!,
            result,
            checked(session.StepState!.NextStepIndex + 1));
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

    private static bool MatchesConditionProposal(
        NyxIdChatNumericConditionState condition,
        NyxIdChatConditionProposal proposal)
    {
        if (string.IsNullOrWhiteSpace(condition.ConditionId) ||
            condition.Comparison != NyxIdChatIntegerComparison.Gte ||
            condition.Outcome is not
                (NyxIdChatConditionOutcome.True or NyxIdChatConditionOutcome.False) ||
            condition.ThresholdOrigin is not
                (NyxIdChatThresholdOrigin.Suggested or NyxIdChatThresholdOrigin.UserOverride) ||
            condition.EvaluatedAt is null ||
            !string.Equals(condition.SourceInputRequestId, proposal.SourceInputRequestId,
                StringComparison.Ordinal) ||
            condition.ObservedValue != proposal.ObservedValue ||
            !string.Equals(condition.GuardedToolName, proposal.GuardedToolName,
                StringComparison.Ordinal) ||
            (condition.ThresholdOrigin == NyxIdChatThresholdOrigin.Suggested &&
             condition.EffectiveThreshold != condition.SuggestedThreshold) ||
            (condition.ThresholdOrigin == NyxIdChatThresholdOrigin.UserOverride &&
             condition.EffectiveThreshold == condition.SuggestedThreshold))
        {
            return false;
        }

        return condition.Outcome ==
               (condition.ObservedValue >= condition.EffectiveThreshold
                   ? NyxIdChatConditionOutcome.True
                   : NyxIdChatConditionOutcome.False);
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

        if (approval.ExactServiceApproval is not null)
        {
            return await ExecuteExactServiceApprovalContinuationAsync(
                    command,
                    session,
                    approval,
                    ct)
                .ConfigureAwait(false);
        }

        if (session.StepState is null ||
            session.Request is null ||
            session.StepState.PendingToolCalls.Count != 1 ||
            (approval.Approved && session.AuthorizedToolStep is null))
        {
            return Failure(
                command.Key,
                ToolCapabilityLostCode,
                ToolCapabilityLostMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        if (!RefreshCredentials(session, approval.ToolContext?.Credentials))
        {
            ClearAuthorization(session);
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
                        Effect = approval.MayChangeExternalState
                            ? AgentToolReceiptEffect.Mutating
                            : AgentToolReceiptEffect.ReadOnly,
                        ErrorCode = "approval_denied",
                        ErrorMessage = "Tool approval denied.",
                    },
                    ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                },
            });
        }

        AgentRunAuthorizedToolStep approvedCapability;
        try
        {
            approvedCapability = session.AuthorizedToolStep!.WithApprovalGrant(
                approval.ApprovalRequestId,
                session.StepState.ToolContext?.Credentials);
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
        var execution = await ExecuteToolAsync(
                new NyxIdChatOperationDispatchCommand
                {
                    Key = command.Key.Clone(),
                    Tool = new NyxIdChatToolOperationInput
                    {
                        ToolName = pending.Name,
                        CallId = pending.Id,
                        ArgumentsJson = pending.ArgumentsJson,
                        MayChangeExternalState = approval.MayChangeExternalState,
                        Idempotent = !approval.MayChangeExternalState,
                        IdempotencyKey = approval.IdempotencyKey,
                        OperationAdmission = approval.OperationAdmission?.Clone(),
                        Presentation = NyxIdChatDurableToolPresentation.Snapshot(
                            approval.Presentation,
                            pending.Name),
                    },
                },
                session,
                reportProgressAsync,
                ct,
                approvedCapability)
            .ConfigureAwait(false);
        if (execution.Result.Tool?.Receipt?.Status == AgentToolReceiptStatus.Success)
            execution.Result.Tool.Receipt.ApprovalRequestId = approval.ApprovalRequestId;
        return execution;
    }

    private async Task<NyxIdChatTurnOperationExecution> ExecuteExactServiceApprovalContinuationAsync(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        NyxIdChatToolApprovalContinuationInput approval,
        CancellationToken ct)
    {
        var authority = approval.ExactServiceApproval;
        if (!string.Equals(
                approval.ApprovalRequestId,
                authority.RequestId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(approval.ToolCallId) ||
            string.IsNullOrWhiteSpace(approval.ToolName))
        {
            return Failure(
                command.Key,
                ToolAuthorizationMismatchCode,
                ToolAuthorizationMismatchMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        var token = Normalize(approval.ToolContext?.Credentials?.NyxIdAccessToken);
        if (token is null)
        {
            return Failure(
                command.Key,
                ToolAuthorizationMismatchCode,
                ToolAuthorizationMismatchMessage,
                NyxIdChatEffectEvidence.NotStarted);
        }

        NyxIdExactServiceApprovalSnapshot decision;
        try
        {
            decision = await _exactServiceApprovalPort.DecideAsync(
                    token,
                    authority,
                    approval.Approved,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Exact-service approval decision failed: requestId={RequestId}",
                authority.RequestId);
            return Failure(
                command.Key,
                ExactServiceApprovalFailedCode,
                "The exact-service approval decision could not be observed.",
                NyxIdChatEffectEvidence.NotStarted);
        }

        if (!approval.Approved ||
            decision.State != NyxIdExactServiceApprovalState.Approved)
        {
            return BuildExactServiceApprovalExecution(
                command.Key,
                approval.ToolCallId,
                approval.ToolName,
                decision,
                approval.OperationAdmission?.ReadBack,
                session);
        }

        NyxIdExactServiceApprovalSnapshot redeemed;
        try
        {
            redeemed = await _exactServiceApprovalPort.RedeemAsync(
                    token,
                    authority,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Exact-service approval redemption outcome is uncertain: requestId={RequestId}",
                authority.RequestId);
            return Failure(
                command.Key,
                ExactServiceApprovalFailedCode,
                "The exact-service effect admission outcome is uncertain.",
                NyxIdChatEffectEvidence.MayHaveChanged);
        }

        return BuildExactServiceApprovalExecution(
            command.Key,
            approval.ToolCallId,
            approval.ToolName,
            redeemed,
            approval.OperationAdmission?.ReadBack,
            session);
    }

    private static string BuildInputResponseJson(NyxIdChatInputContinuationInput input) =>
        input.Answer.AnswerCase switch
        {
            NyxIdChatInputAnswer.AnswerOneofCase.FreeText => JsonSerializer.Serialize(new
            {
                type = "ask_user_response",
                source_input_request_id = input.RequestId,
                free_text = input.Answer.FreeText,
            }),
            NyxIdChatInputAnswer.AnswerOneofCase.Selection => JsonSerializer.Serialize(new
            {
                type = "ask_user_response",
                source_input_request_id = input.RequestId,
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

    private async Task<NyxIdChatTurnOperationExecution?> EnsureDelegationCredentialAsync(
        NyxIdChatOperationKey key,
        NyxIdChatTransientExecutionSession session,
        NeedsLlmReplyEvent request,
        CancellationToken ct)
    {
        var credentials = request.ToolContext?.Credentials ??
                          session.StepState?.ToolContext?.Credentials;
        if (credentials?.NyxIdCredentialKind !=
            AgentToolNyxIdCredentialKindPayload.ProxyDelegation)
        {
            return null;
        }

        var toolToken = Normalize(credentials.NyxIdAccessToken);
        var llmToken = Normalize(request.LlmControl?.NyxIdAccessToken);
        if (toolToken is not null &&
            llmToken is not null &&
            !string.Equals(toolToken, llmToken, StringComparison.Ordinal))
        {
            ClearAuthorization(session);
            return Failure(
                key,
                DelegationRefreshFailedCode,
                DelegationRefreshFailedMessage,
                NyxIdChatEffectEvidence.NotApplied);
        }

        var token = toolToken ?? llmToken;
        if (token is null)
        {
            ClearAuthorization(session);
            return Failure(
                key,
                DelegationRefreshFailedCode,
                DelegationRefreshFailedMessage,
                NyxIdChatEffectEvidence.NotApplied);
        }

        var resolution = await _delegationCredentialLifecycle
            .ResolveAsync(token, ct)
            .ConfigureAwait(false);
        if (!resolution.Succeeded || string.IsNullOrWhiteSpace(resolution.AccessToken))
        {
            ClearAuthorization(session);
            return Failure(
                key,
                DelegationRefreshFailedCode,
                DelegationRefreshFailedMessage,
                NyxIdChatEffectEvidence.NotApplied);
        }

        ApplyDelegationCredential(
            session,
            request,
            credentials,
            resolution.AccessToken.Trim());
        return null;
    }

    private static void ApplyDelegationCredential(
        NyxIdChatTransientExecutionSession session,
        NeedsLlmReplyEvent request,
        AgentToolCredentialsPayload sourceCredentials,
        string accessToken)
    {
        var credentials = sourceCredentials.Clone();
        credentials.NyxIdAccessToken = accessToken;
        credentials.NyxIdCredentialKind = AgentToolNyxIdCredentialKindPayload.ProxyDelegation;

        request.ToolContext ??= new AgentToolExecutionContextPayload();
        request.ToolContext.Credentials = credentials.Clone();
        request.LlmControl ??= new LLMControlContextPayload();
        request.LlmControl.NyxIdAccessToken = accessToken;

        if (session.Request is not null)
            session.Request = request.Clone();
        if (session.StepState is not null)
        {
            session.StepState = session.StepState.Clone();
            session.StepState.ToolContext ??= new AgentToolExecutionContextPayload();
            session.StepState.ToolContext.Credentials = credentials.Clone();
            session.StepState.LlmControl ??= new LLMControlContextPayload();
            session.StepState.LlmControl.NyxIdAccessToken = accessToken;
        }
        if (session.AuthorizedToolStep is not null)
            session.AuthorizedToolStep = session.AuthorizedToolStep.WithRefreshedCredentials(credentials);
    }

    private static bool RefreshCredentials(
        NyxIdChatTransientExecutionSession session,
        AgentToolCredentialsPayload? credentials) =>
        RefreshCredentials(session, credentials, refreshLlmAuthorization: true);

    private static bool RefreshToolCredentials(
        NyxIdChatTransientExecutionSession session,
        AgentToolCredentialsPayload? credentials) =>
        RefreshCredentials(session, credentials, refreshLlmAuthorization: false);

    private static bool RefreshCredentials(
        NyxIdChatTransientExecutionSession session,
        AgentToolCredentialsPayload? credentials,
        bool refreshLlmAuthorization)
    {
        if (credentials is null || session.Request is null || session.StepState is null)
            return false;

        var current = session.Request.ToolContext?.Credentials ??
                      session.StepState.ToolContext?.Credentials;
        if (current is null ||
            current.NyxIdCredentialKind == AgentToolNyxIdCredentialKindPayload.Unspecified ||
            credentials.NyxIdCredentialKind != current.NyxIdCredentialKind ||
            string.IsNullOrWhiteSpace(credentials.NyxIdAccessToken))
        {
            return false;
        }

        var refreshedCredentials = credentials.Clone();
        refreshedCredentials.NyxIdCredentialAuthority = refreshLlmAuthorization
            ? AgentToolNyxIdCredentialAuthorityPayload.Unspecified
            : AgentToolNyxIdCredentialAuthorityPayload.ToolExecutionContext;

        session.Request = session.Request.Clone();
        session.Request.ToolContext ??= new AgentToolExecutionContextPayload();
        session.Request.ToolContext.Credentials = refreshedCredentials.Clone();
        if (refreshLlmAuthorization)
        {
            session.Request.LlmControl ??= new LLMControlContextPayload();
            session.Request.LlmControl.NyxIdAccessToken = refreshedCredentials.NyxIdAccessToken;
        }
        session.StepState = session.StepState.Clone();
        session.StepState.ToolContext ??= new AgentToolExecutionContextPayload();
        session.StepState.ToolContext.Credentials = refreshedCredentials.Clone();
        if (refreshLlmAuthorization)
        {
            session.StepState.LlmControl ??= new LLMControlContextPayload();
            session.StepState.LlmControl.NyxIdAccessToken = refreshedCredentials.NyxIdAccessToken;
        }
        return true;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
        result.Presentation = NyxIdChatDurableToolPresentation.Snapshot(
            snapshot.Presentation,
            call.Name);
        result.Safety = new NyxIdChatToolCallSafety
        {
            IsReadOnly = callSafety.IsReadOnly,
            IsDestructive = callSafety.IsDestructive,
            SideEffectKind = snapshot.SideEffectKind,
            MayChangeExternalState = !callSafety.IsReadOnly ||
                                     callSafety.IsDestructive ||
                                     !string.IsNullOrWhiteSpace(snapshot.SideEffectKind),
            RequiresApproval = snapshot.RequiresApproval,
        };
        if (snapshot.Presentation?.SourceRefCase ==
            ToolPresentationDescriptor.SourceRefOneofCase.NyxIdOperation)
        {
            result.NyxIdProvenance = SnapshotNyxIdIdentity(
                snapshot.Presentation.NyxIdOperation);
        }
        if (snapshot.OperationAdmission is not null)
            result.OperationAdmission = SealDurableAuthorization(snapshot).OperationAdmission;
        return result;
    }

    private static AgentRunAuthorizedToolCallSafety SealDurableAuthorization(
        AgentRunAuthorizedToolCallSafety snapshot)
    {
        var operationAdmission = snapshot.OperationAdmission?.Clone();
        if (operationAdmission is not null)
        {
            operationAdmission.DurableAuthorization =
                new AgentToolDurableAuthorizationSnapshotPayload
                {
                    HasRequiresApproval = snapshot.CallSafety.RequiresApproval.HasValue,
                    RequiresApproval = snapshot.CallSafety.RequiresApproval ?? false,
                    IsReadOnly = snapshot.CallSafety.IsReadOnly,
                    IsDestructive = snapshot.CallSafety.IsDestructive,
                    SideEffectKind = snapshot.SideEffectKind ?? string.Empty,
                    ToolDefinitionFingerprint = snapshot.ToolDefinitionFingerprint ?? string.Empty,
                };
        }

        return snapshot with
        {
            Presentation = NyxIdChatDurableToolPresentation.Snapshot(
                snapshot.Presentation,
                snapshot.ToolName),
            OperationAdmission = operationAdmission,
        };
    }

    private static bool TryRestoreDurableRetrySession(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session)
    {
        var input = command.Tool;
        var admission = input?.OperationAdmission;
        var authorization = admission?.DurableAuthorization;
        if (input is null ||
            authorization is null ||
            string.IsNullOrWhiteSpace(input.CallId) ||
            string.IsNullOrWhiteSpace(input.ToolName) ||
            string.IsNullOrWhiteSpace(input.ArgumentsJson) ||
            string.IsNullOrWhiteSpace(authorization.ToolDefinitionFingerprint) ||
            !IsValidDurableRetrySourceKey(command.Key, input.RetryAuthorizationSourceKey) ||
            admission is null ||
            input.ToolContext is null ||
            !NyxIdChatDurableRetryAuthority.IsValid(command.Key, input.ToolContext))
        {
            return false;
        }

        var safety = new NyxIdChatToolCallSafety
        {
            IsReadOnly = authorization.IsReadOnly,
            IsDestructive = authorization.IsDestructive,
            SideEffectKind = authorization.SideEffectKind,
            MayChangeExternalState = !authorization.IsReadOnly ||
                                     authorization.IsDestructive ||
                                     !string.IsNullOrWhiteSpace(authorization.SideEffectKind),
            RequiresApproval = authorization.RequiresApproval,
        };
        if (input.MayChangeExternalState != safety.MayChangeExternalState ||
            !NyxIdChatOperationAdmissionPolicy.IsValid(admission, safety))
        {
            return false;
        }

        var mappedAdmission = AgentToolOperationAdmissionPayloadMapper.FromPayload(admission);
        if (mappedAdmission is null)
            return false;

        var mappedToolContext = AgentToolExecutionContextMapper.FromPayload(input.ToolContext);
        var toolContext = mappedToolContext with
        {
            Request = mappedToolContext.Request with
            {
                RequestId = command.Key.OperationId,
                OperationId = command.Key.OperationId,
                OperationGeneration = command.Key.OperationGeneration,
                IdempotencyKey = input.IdempotencyKey,
            },
            Chat = mappedToolContext.Chat with
            {
                Surface = AgentChatInvocationSurface.NyxIdAssistant,
                ConversationId = command.Key.ConversationActorId,
                TurnId = command.Key.TurnId,
                TaskId = command.Key.TaskId,
                StepId = command.Key.StepId,
            },
            OperationAdmission = mappedAdmission,
        };
        if (string.IsNullOrWhiteSpace(toolContext.Credentials.NyxIdAccessToken) &&
            string.IsNullOrWhiteSpace(toolContext.Credentials.NyxIdOrgToken))
        {
            return false;
        }

        var request = BuildDurableToolReplyRequest(command, toolContext.ToPayload());
        var stepState = new AgentRunReplyStepState
        {
            RunId = command.Key.TaskId,
            CorrelationId = command.Key.OperationId,
            TargetActorId = command.Key.ConversationActorId,
            Attempt = checked((int)Math.Clamp(command.Key.OperationGeneration, 1, int.MaxValue)),
            NextStepIndex = 1,
            MaxToolRounds = 1,
            ToolContext = toolContext.ToPayload(),
            PendingToolAuthorizationConsumed = true,
        };
        stepState.PendingToolCalls.Add(new AgentRunToolCall
        {
            Id = input.CallId,
            Name = input.ToolName,
            ArgumentsJson = input.ArgumentsJson,
        });
        var frozenAdmission = admission.Clone();
        frozenAdmission.DurableAuthorization = null;
        stepState.PendingToolAuthorizations.Add(new AgentRunPendingToolAuthorization
        {
            Call = stepState.PendingToolCalls[0].Clone(),
            HasRequiresApproval = authorization.HasRequiresApproval,
            RequiresApproval = authorization.RequiresApproval,
            IsReadOnly = authorization.IsReadOnly,
            IsDestructive = authorization.IsDestructive,
            SideEffectKind = authorization.SideEffectKind,
            ToolDefinitionFingerprint = authorization.ToolDefinitionFingerprint,
            OperationAdmission = frozenAdmission,
        });

        ClearAuthorization(session);
        session.StepState = stepState;
        session.Request = request;
        session.AuthorizationSourceKey = command.Key.Clone();
        session.AuthorizedToolCallSafeties =
        [
            new AgentRunAuthorizedToolCallSafety(
                input.CallId,
                input.ToolName,
                input.ArgumentsJson,
                new AgentToolCallSafety(
                    authorization.HasRequiresApproval
                        ? authorization.RequiresApproval
                        : null,
                    authorization.IsReadOnly,
                    authorization.IsDestructive),
                authorization.SideEffectKind,
                authorization.ToolDefinitionFingerprint,
                Presentation: NyxIdChatDurableToolPresentation.Snapshot(
                    input.Presentation,
                    input.ToolName),
                OperationAdmission: admission.Clone()),
        ];
        return true;
    }

    private static bool IsValidDurableRetrySourceKey(
        NyxIdChatOperationKey key,
        NyxIdChatOperationKey? source) =>
        source is not null &&
        key.OperationGeneration > 1 &&
        source.OperationGeneration == key.OperationGeneration - 1 &&
        string.Equals(
            source.ConversationActorId,
            key.ConversationActorId,
            StringComparison.Ordinal) &&
        string.Equals(source.TurnId, key.TurnId, StringComparison.Ordinal) &&
        string.Equals(source.TaskId, key.TaskId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(source.StepId) &&
        !string.IsNullOrWhiteSpace(source.OperationId);

    private static NeedsLlmReplyEvent BuildDurableToolReplyRequest(
        NyxIdChatOperationDispatchCommand command,
        AgentToolExecutionContextPayload toolContext)
    {
        var channel = new ChannelId { Value = NyxIdChatServiceDefaults.ServiceId };
        var bot = new BotInstanceId { Value = command.Key.ConversationActorId };
        return new NeedsLlmReplyEvent
        {
            RunId = command.Key.TaskId,
            CorrelationId = command.Key.OperationId,
            TargetActorId = command.Key.ConversationActorId,
            Activity = new ChatActivity
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
                Content = new MessageContent { Text = "Resume the exact admitted tool operation." },
            },
            ToolContext = toolContext.Clone(),
        };
    }

    private async Task<AgentTurnToolCatalog?> MaterializeDurableRetryTurnCatalogAsync(
        NyxIdChatToolOperationInput input,
        CancellationToken ct)
    {
        if (input.AgentProfile is null ||
            input.AgentProfileTurnAuthority is null ||
            input.ToolContext is null ||
            _turnCatalogMaterializer is null)
        {
            return null;
        }

        var toolContext = AgentToolExecutionContextMapper.FromPayload(input.ToolContext);
        try
        {
            return (await _turnCatalogMaterializer.MaterializeCommittedAsync(
                    input.AgentProfile,
                    input.AgentProfileTurnAuthority,
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
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Durable NyxID retry authorization catalog materialization failed closed");
            return null;
        }
    }

    private static NyxIdOperationRef SnapshotNyxIdIdentity(NyxIdOperationRef source)
    {
        var snapshot = new NyxIdOperationRef
        {
            ConnectedServiceId = source.ConnectedServiceId,
            ServiceSlug = source.ServiceSlug,
            CatalogServiceSlug = source.CatalogServiceSlug,
            OperationId = source.OperationId,
            HttpMethod = source.HttpMethod,
            PathTemplate = source.PathTemplate,
        };
        if (source.HasReadinessCapabilityId &&
            !string.IsNullOrWhiteSpace(source.ReadinessCapabilityId))
        {
            snapshot.ReadinessCapabilityId = source.ReadinessCapabilityId;
        }
        return snapshot;
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

    private void LogVerifiedAuthorizationCatalogDiagnostic(
        AgentToolExecutionContext toolContext,
        AgentTurnToolCatalog catalog,
        NyxIdChatVerifiedAuthorizationContinuation continuation)
    {
        var expectedUserServiceId = continuation.VerifiedResource?.ResourceCase ==
                                    NyxIdChatSafeResourceRef.ResourceOneofCase.UserService
            ? continuation.VerifiedResource.UserService.UserServiceId?.Trim()
            : null;
        var expectedServiceSlug = continuation.ServiceSlug?.Trim();
        var admittedTools = catalog.ExactTools.Values
            .OfType<IAgentToolOperationAdmissionOwner>()
            .ToArray();
        var exactServiceIdMatchCount = admittedTools.Count(owner => string.Equals(
            owner.OperationAdmission.ServiceInstanceId,
            expectedUserServiceId,
            StringComparison.Ordinal));
        var exactSlugMatchCount = admittedTools.Count(owner => string.Equals(
            owner.OperationAdmission.ServiceSlug,
            expectedServiceSlug,
            StringComparison.Ordinal));
        var exactBothMatchCount = admittedTools.Count(owner =>
            string.Equals(
                owner.OperationAdmission.ServiceInstanceId,
                expectedUserServiceId,
                StringComparison.Ordinal) &&
            string.Equals(
                owner.OperationAdmission.ServiceSlug,
                expectedServiceSlug,
                StringComparison.Ordinal));
        var sourceReadableBearer =
            AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(toolContext.Credentials);

        _logger.LogInformation(
            "NyxID verified-authorization continuation catalog diagnostic. credentialKind={CredentialKind}, hasSourceReadableBearer={HasSourceReadableBearer}, sourceSharesExecutionCredential={SourceSharesExecutionCredential}, toolVisibilityRestricted={ToolVisibilityRestricted}, finalAllowedToolCount={FinalAllowedToolCount}, routeOwnedToolCount={ExactToolCount}, admittedToolCount={AdmittedToolCount}, exactServiceIdMatchCount={ExactServiceIdMatchCount}, exactSlugMatchCount={ExactSlugMatchCount}, exactBothMatchCount={ExactBothMatchCount}",
            toolContext.Credentials.NyxIdCredentialKind,
            !string.IsNullOrWhiteSpace(sourceReadableBearer),
            !string.IsNullOrWhiteSpace(sourceReadableBearer) && string.Equals(
                sourceReadableBearer,
                toolContext.Credentials.NyxIdAccessToken,
                StringComparison.Ordinal),
            toolContext.ToolVisibility.IsRestricted,
            catalog.FinalAllowedToolNames.Count,
            catalog.ExactTools.Count,
            admittedTools.Length,
            exactServiceIdMatchCount,
            exactSlugMatchCount,
            exactBothMatchCount);
    }

    // Ordinary, unprofiled turns carry the reviewed nyxid.chat.default
    // baseline; a materialization failure degrades to restricted empty
    // instead of failing the turn.
    private async Task<AgentTurnToolCatalog> MaterializeUnprofiledBaselineCatalogAsync(
        NeedsLlmReplyEvent request,
        CancellationToken ct)
    {
        if (_turnCatalogMaterializer is null)
            return RestrictedEmptyCatalog();

        var toolContext = ResolveCatalogToolContext(request);
        try
        {
            return await _turnCatalogMaterializer
                .MaterializeUnprofiledBaselineAsync(toolContext, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Unprofiled NyxID chat baseline catalog materialization failed closed.");
            return RestrictedEmptyCatalog();
        }
    }

    private async Task<AgentTurnToolCatalog?> MaterializeTurnCatalogAsync(
        NyxIdChatLLMOperationInput input,
        NeedsLlmReplyEvent request,
        CancellationToken ct)
    {
        var profile = input.AgentProfile;
        var authority = input.AgentProfileTurnAuthority;
        if (IsBuiltInIntent(input.Intent) &&
            !IsProfileSelectedBuiltInIntent(input.Intent, authority))
        {
            if (_turnCatalogMaterializer is null ||
                (profile is null) != (authority is null))
            {
                return RestrictedEmptyCatalog();
            }

            var builtInToolContext = ResolveCatalogToolContext(request);
            try
            {
                var builtInCatalog = await _turnCatalogMaterializer.MaterializeBuiltInIntentAsync(
                        input.Intent,
                        builtInToolContext,
                        ct)
                    .ConfigureAwait(false);
                return AgentTurnToolCatalogMaterializer.NarrowToBuiltInIntent(
                    input.Intent,
                    builtInCatalog,
                    authority?.AuthorityCeilingToolNames);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Built-in NyxID chat intent catalog materialization failed closed. intent={Intent}",
                    input.Intent);
                return RestrictedEmptyCatalog();
            }
        }

        if (profile is null && authority is null)
        {
            return input.Intent == NyxIdChatTurnIntent.Unspecified
                ? await MaterializeUnprofiledBaselineCatalogAsync(request, ct).ConfigureAwait(false)
                : RestrictedEmptyCatalog();
        }
        if (profile is null || authority is null || _turnCatalogMaterializer is null)
            return RestrictedEmptyCatalog();

        var toolContext = ResolveCatalogToolContext(request);
        try
        {
            var catalog = (await _turnCatalogMaterializer.MaterializeCommittedAsync(
                    profile,
                    authority,
                    toolContext.Credentials.NyxIdAccessToken,
                    registeredTools: [],
                    toolContext,
                    ct)
                .ConfigureAwait(false)).Catalog;
            return IsBuiltInIntent(input.Intent)
                ? AgentTurnToolCatalogMaterializer.NarrowToBuiltInIntent(
                    input.Intent,
                    catalog,
                    authority.AuthorityCeilingToolNames)
                : catalog;
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

    private static void RepinRematerializedTurnCatalog(
        NyxIdChatTransientExecutionSession session,
        AgentTurnToolCatalog catalog)
    {
        if (session.StepState is null)
            return;

        // A verified authorization continuation starts a new user-facing model
        // generation with a newly materialized exact catalog. Keep the same turn
        // authority, but replace the prior generation's proof before execution so
        // retries/replay verify this catalog rather than accepting catalog drift.
        session.StepState = session.StepState.Clone();
        session.StepState.ToolCatalogProof = catalog.Proof.ToPayload();
        session.StepState.ToolCatalogPolicyVersion =
            AgentRunReplyGenerationExecutor.ToolCatalogPolicyVersion;
    }

    private static bool IsBuiltInIntent(NyxIdChatTurnIntent intent) =>
        intent is NyxIdChatTurnIntent.ServiceConnect or
            NyxIdChatTurnIntent.KeyCreate or
            NyxIdChatTurnIntent.KeyRotate or
            NyxIdChatTurnIntent.WorkflowAuthoring;

    private static AgentToolExecutionContext ResolveCatalogToolContext(
        NeedsLlmReplyEvent request)
    {
        var control = LLMControlContextMapper.FromPayload(request.LlmControl);
        var context = AgentToolExecutionContextMapper.FromPayload(request.ToolContext);
        var controlCredentials = control.ToToolContext().Credentials;
        return context with
        {
            Credentials = context.Credentials with
            {
                NyxIdAccessToken = Normalize(context.Credentials.NyxIdAccessToken) ??
                                   controlCredentials.NyxIdAccessToken,
                NyxIdOrgToken = Normalize(context.Credentials.NyxIdOrgToken) ??
                                controlCredentials.NyxIdOrgToken,
                SenderNyxIdAccessToken = Normalize(context.Credentials.SenderNyxIdAccessToken) ??
                                         controlCredentials.SenderNyxIdAccessToken,
            },
            Routing = control.ToRoutingContext(context.Routing),
        };
    }

    private static bool IsProfileSelectedBuiltInIntent(
        NyxIdChatTurnIntent intent,
        AgentProfileTurnAuthorityState? authority)
    {
        var intentId = intent switch
        {
            NyxIdChatTurnIntent.ServiceConnect =>
                NyxIdChatTurnIntentClassifier.ServiceConnectIntentId,
            NyxIdChatTurnIntent.KeyCreate =>
                NyxIdChatTurnIntentClassifier.KeyCreateIntentId,
            NyxIdChatTurnIntent.KeyRotate =>
                NyxIdChatTurnIntentClassifier.KeyRotateIntentId,
            NyxIdChatTurnIntent.WorkflowAuthoring =>
                NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId,
            _ => null,
        };
        return intentId is not null && string.Equals(
            authority?.CandidateRoute?.IntentId,
            intentId,
            StringComparison.Ordinal);
    }

    private static AgentTurnToolCatalog RestrictedEmptyCatalog() =>
        new(
            [],
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: null,
            candidateIntentId: null);

    private static void AppendVerifiedAuthorizationInstruction(
        AgentRunReplyStepState stepState,
        NyxIdChatVerifiedAuthorizationContinuation continuation)
    {
        var instruction = BuildVerifiedAuthorizationInstruction(continuation);
        if (string.IsNullOrWhiteSpace(instruction) ||
            stepState.Messages.Any(message =>
                string.Equals(message.Role, "system", StringComparison.Ordinal) &&
                string.Equals(message.Content, instruction, StringComparison.Ordinal)))
        {
            return;
        }

        stepState.Messages.Add(new AgentRunChatMessage
        {
            Role = "system",
            Content = instruction,
        });
    }

    private static string BuildVerifiedAuthorizationInstruction(
        NyxIdChatVerifiedAuthorizationContinuation continuation)
    {
        var serviceSlug = continuation.ServiceSlug?.Trim();
        var userServiceId = continuation.VerifiedResource?.ResourceCase ==
                            NyxIdChatSafeResourceRef.ResourceOneofCase.UserService
            ? continuation.VerifiedResource.UserService.UserServiceId?.Trim()
            : null;
        var verifiedSubject = !string.IsNullOrWhiteSpace(serviceSlug) &&
                              !string.IsNullOrWhiteSpace(userServiceId)
            ? $" for connected service '{serviceSlug}' with UserServiceId '{userServiceId}'"
            : string.Empty;
        return continuation.ResumeRequirement switch
        {
            NyxIdChatAuthorizationResumeRequirement.CompleteOriginalServiceRequest =>
                $"NyxID authorization has been verified{verifiedSubject}. " +
                "Continue the original request now using only an available operation from this " +
                "exact verified service. Do not stop after merely reporting that authorization succeeded.",
            NyxIdChatAuthorizationResumeRequirement.CommunicateAuthorizationCompletion =>
                $"NyxID authorization has been verified{verifiedSubject}. " +
                "Communicate that authorization is complete without inventing or performing an " +
                "unrelated service operation.",
            _ => string.Empty,
        };
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
            next.PendingHistoryMessages.Add(assistant.Clone());
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
        next.PendingHistoryMessages.AddRange(result.ResultMessages.Select(static message => message.Clone()));
        next.AppendedHistory.AddRange(result.ResultMessages.Select(
            AgentRunReplyStepMappers.ToConversationHistoryEntry));
        next.ToolReceipts.AddRange(result.ToolReceipts.Select(static receipt => receipt.Clone()));
        if (result.OutboundIntent is not null)
            next.OutboundIntent = result.OutboundIntent.Clone();
        if (result.AdvanceRound)
            next.Round++;
        return next;
    }

    private async Task HandleLlmChunkAsync(
        NyxIdChatOperationKey key,
        LLMStreamChunk chunk,
        List<ChatContentPart> outputParts,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        if (chunk.LLMInvocationStarted is { } modelStarted)
        {
            await FlushStreamingProgressAsync(key, session, reportProgressAsync, ct)
                .ConfigureAwait(false);
            await ReportModelStartedAsync(
                    key,
                    modelStarted,
                    session,
                    reportProgressAsync,
                    ct)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(chunk.DeltaContent))
        {
            await QueueStreamingProgressAsync(
                    key,
                    NyxIdChatOperationProgressSignal.ProgressOneofCase.Text,
                    chunk.DeltaContent,
                    session,
                    reportProgressAsync,
                    ct)
                .ConfigureAwait(false);
        }
        if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
        {
            await QueueStreamingProgressAsync(
                    key,
                    NyxIdChatOperationProgressSignal.ProgressOneofCase.Reasoning,
                    chunk.DeltaReasoningContent,
                    session,
                    reportProgressAsync,
                    ct)
                .ConfigureAwait(false);
        }
        if (chunk.DeltaContentPart is not null)
            outputParts.Add(ContentPartProtoMapper.ToProto(chunk.DeltaContentPart));
        if (chunk.ToolCallStarted?.ToolCall is { } started)
        {
            await FlushStreamingProgressAsync(key, session, reportProgressAsync, ct)
                .ConfigureAwait(false);
            await ReportToolStartedOnceAsync(
                    key,
                    new NyxIdChatToolProgress
                    {
                        CallId = started.Id,
                        ToolName = started.Name,
                        Presentation = ToolPresentationDescriptors.Snapshot(
                            chunk.ToolCallStarted.Presentation,
                            started.Name),
                    },
                    session,
                    reportProgressAsync,
                    ct)
                .ConfigureAwait(false);
        }

        if (chunk.LLMInvocationCompleted is { } modelCompleted)
        {
            await FlushStreamingProgressAsync(key, session, reportProgressAsync, ct)
                .ConfigureAwait(false);
            await ReportModelCompletedAsync(
                    key,
                    modelCompleted,
                    session,
                    reportProgressAsync,
                    ct)
                .ConfigureAwait(false);
        }
    }

    private async Task QueueStreamingProgressAsync(
        NyxIdChatOperationKey key,
        NyxIdChatOperationProgressSignal.ProgressOneofCase kind,
        string delta,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        _ = key;
        _ = reportProgressAsync;
        var batcher = session.StreamingProgressBatcher ??
                      throw new InvalidOperationException(
                          "The streaming progress batcher is unavailable.");
        await batcher.QueueAsync(kind, delta, ct).ConfigureAwait(false);
    }

    private static Task FlushStreamingProgressAsync(
        NyxIdChatOperationKey key,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        _ = key;
        _ = reportProgressAsync;
        return session.StreamingProgressBatcher?.FlushAsync(ct) ?? Task.CompletedTask;
    }

    private static Task ReportToolStartedOnceAsync(
        NyxIdChatOperationKey key,
        NyxIdChatToolProgress progress,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct) =>
        session.TryMarkToolStartPublished(progress.CallId)
            ? ReportProgressAsync(key, progress, session, reportProgressAsync, ct)
            : Task.CompletedTask;

    private static Task ReportModelStartedAsync(
        NyxIdChatOperationKey key,
        LLMInvocationStartedChunk started,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        var progress = new NyxIdChatModelStartedProgress
        {
            OperationId = started.OperationId,
            Round = started.Round,
            Model = started.Model,
            Provider = started.Provider,
            InputSummary = started.InputSummary,
        };
        progress.AvailableToolNames.AddRange(started.AvailableToolNames);
        return reportProgressAsync(new NyxIdChatOperationProgressSignal
        {
            Key = key.Clone(),
            Sequence = ++session.ProgressSequence,
            ModelStarted = progress,
        }, ct);
    }

    private static Task ReportModelCompletedAsync(
        NyxIdChatOperationKey key,
        LLMInvocationCompletedChunk completed,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct)
    {
        var progress = new NyxIdChatModelCompletedProgress
        {
            OperationId = completed.OperationId,
            Round = completed.Round,
            Model = completed.Model,
            Content = completed.Content,
            ReasoningContent = completed.ReasoningContent,
            FinishReason = completed.FinishReason,
            Success = completed.Success,
            Error = completed.Error,
        };
        if (completed.Usage is { } usage)
        {
            progress.Usage = new TokenUsagePayload
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
            };
        }

        return reportProgressAsync(new NyxIdChatOperationProgressSignal
        {
            Key = key.Clone(),
            Sequence = ++session.ProgressSequence,
            ModelCompleted = progress,
        }, ct);
    }

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

    private static Task ReportPhaseAsync(
        NyxIdChatOperationKey key,
        string substepId,
        string title,
        NyxIdChatSubstepStatus status,
        NyxIdChatTransientExecutionSession session,
        Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
        CancellationToken ct) =>
        reportProgressAsync(new NyxIdChatOperationProgressSignal
        {
            Key = key.Clone(),
            Sequence = ++session.ProgressSequence,
            Phase = new NyxIdChatOperationPhaseProgress
            {
                SubstepId = substepId,
                Title = title,
                Status = status,
            },
        }, ct);

    private static (string Prepare, string Execute) ResolveToolPhaseTitles(string toolName) =>
        string.Equals(toolName, WebSearchToolName, StringComparison.Ordinal)
            ? ("Build search query", "Search current web results")
            : ("Prepare operation", "Execute operation");

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
        AgentRunAuthorizedToolCallSafety? authorization,
        NyxIdChatToolOperationInput command)
    {
        if (!string.Equals(authorized.Id, command.CallId, StringComparison.Ordinal) ||
            !string.Equals(authorized.Name, command.ToolName, StringComparison.Ordinal) ||
            !string.Equals(authorized.ArgumentsJson, command.ArgumentsJson, StringComparison.Ordinal) ||
            authorization is null ||
            !NyxIdChatOperationAdmissionPolicy.Matches(
                authorization.OperationAdmission,
                command.OperationAdmission))
        {
            return false;
        }

        if (command.OperationAdmission is null)
            return true;

        return NyxIdChatOperationAdmissionPolicy.IsValid(
            command.OperationAdmission,
            new NyxIdChatToolCallSafety
            {
                IsReadOnly = authorization.CallSafety.IsReadOnly,
                IsDestructive = authorization.CallSafety.IsDestructive,
                SideEffectKind = authorization.SideEffectKind,
                MayChangeExternalState = !authorization.CallSafety.IsReadOnly ||
                                         authorization.CallSafety.IsDestructive ||
                                         !string.IsNullOrWhiteSpace(authorization.SideEffectKind),
            });
    }

    private static NyxIdChatEffectEvidence ResolveExternalEffect(
        NyxIdChatToolOperationInput command,
        AgentToolReceipt receipt)
    {
        if (!command.MayChangeExternalState)
            return NyxIdChatEffectEvidence.NotApplied;

        return receipt.Status switch
        {
            AgentToolReceiptStatus.Success => NyxIdChatEffectEvidence.MayHaveChanged,
            AgentToolReceiptStatus.ApprovalRequired or
                AgentToolReceiptStatus.AuthorizationRequired => NyxIdChatEffectEvidence.NotStarted,
            AgentToolReceiptStatus.Denied => NyxIdChatEffectEvidence.NotApplied,
            _ => NyxIdChatEffectEvidence.MayHaveChanged,
        };
    }

    private static void ClearAuthorization(NyxIdChatTransientExecutionSession session)
    {
        session.AuthorizedToolStep = null;
        session.AuthorizedToolCallSafeties = [];
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
