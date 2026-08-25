using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.Foundation.Abstractions.Tools;

namespace Aevatar.AI.Abstractions.ToolProviders;

public sealed record AgentToolCallSafety(
    bool? RequiresApproval,
    bool IsReadOnly,
    bool IsDestructive);

public sealed record AgentToolTerminalOutcome(
    string ResultJson,
    AgentToolReceipt? Receipt = null);

public enum AgentToolPendingOperationStatus
{
    Unspecified = 0,
    SubmissionUncertain = 1,
    Queued = 2,
    Provisioning = 3,
    Preparing = 4,
    Running = 5,
    Collecting = 6,
    Succeeded = 7,
    Failed = 8,
    Cancelled = 9,
    OutcomeUncertain = 10,
}

public sealed record AgentToolPendingOperation(
    string OperationId,
    string ProviderOperationId,
    string StatusPath,
    string ResultPath,
    string CancelPath,
    AgentToolPendingOperationStatus Status,
    string? ETag,
    long RetryAfterMilliseconds,
    long ExpiresAtUnixMs,
    string ServiceSlug,
    string? UserServiceId,
    CodeExecutionRouteIdentitySource RouteIdentitySource);

public sealed record AgentToolOperationStartRequest(
    string OperationId,
    string CallId,
    string ToolName,
    string ArgumentsJson,
    AgentToolExecutionContext ExecutionContext);

public enum AgentToolOperationStartDisposition
{
    Completed = 1,
    Pending = 2,
}

public sealed record AgentToolOperationStartResult(
    AgentToolOperationStartDisposition Disposition,
    AgentToolTerminalOutcome? CompletedOutcome = null,
    AgentToolPendingOperation? PendingOperation = null)
{
    public static AgentToolOperationStartResult Completed(AgentToolTerminalOutcome outcome) =>
        new(AgentToolOperationStartDisposition.Completed, CompletedOutcome: outcome);

    public static AgentToolOperationStartResult Pending(AgentToolPendingOperation operation) =>
        new(AgentToolOperationStartDisposition.Pending, PendingOperation: operation);
}

public enum AgentToolTurnReusePolicy
{
    Reusable = 0,
    RetireAfterSuccess = 1,
}

public enum AgentToolOperationReconciliationDisposition
{
    Completed = 1,
    NotFound = 2,
    Unknown = 3,
    Pending = 4,
}

public sealed record AgentToolOperationReconciliationRequest(
    string OperationId,
    string ArgumentsJson,
    AgentToolExecutionContext ExecutionContext,
    AgentToolPendingOperation? PendingOperation = null);

public sealed record AgentToolOperationReconciliationResult(
    AgentToolOperationReconciliationDisposition Disposition,
    AgentToolTerminalOutcome? CompletedOutcome = null,
    AgentToolPendingOperation? PendingOperation = null)
{
    public static AgentToolOperationReconciliationResult Completed(AgentToolTerminalOutcome outcome) =>
        new(AgentToolOperationReconciliationDisposition.Completed, CompletedOutcome: outcome);

    public static AgentToolOperationReconciliationResult Pending(AgentToolPendingOperation operation) =>
        new(AgentToolOperationReconciliationDisposition.Pending, PendingOperation: operation);

    public static AgentToolOperationReconciliationResult NotFound() =>
        new(AgentToolOperationReconciliationDisposition.NotFound);

    public static AgentToolOperationReconciliationResult Unknown() =>
        new(AgentToolOperationReconciliationDisposition.Unknown);
}

public enum AgentToolOperationCancellationReason
{
    Unspecified = 0,
    WorkflowStopped = 1,
}

public sealed record AgentToolOperationCancellationRequest(
    string OperationId,
    string ArgumentsJson,
    AgentToolExecutionContext ExecutionContext,
    AgentToolPendingOperation PendingOperation,
    AgentToolOperationCancellationReason Reason,
    long DeadlineUnixMs);

public enum AgentToolOperationCancellationDisposition
{
    Completed = 1,
    Pending = 2,
}

public sealed record AgentToolOperationCancellationResult(
    AgentToolOperationCancellationDisposition Disposition,
    AgentToolTerminalOutcome? CompletedOutcome = null,
    AgentToolPendingOperation? PendingOperation = null)
{
    public static AgentToolOperationCancellationResult Completed(AgentToolTerminalOutcome outcome) =>
        new(AgentToolOperationCancellationDisposition.Completed, CompletedOutcome: outcome);

    public static AgentToolOperationCancellationResult Pending(AgentToolPendingOperation operation) =>
        new(AgentToolOperationCancellationDisposition.Pending, PendingOperation: operation);
}

public interface IAgentToolOperationReconciler
{
    Task<AgentToolOperationReconciliationResult> ReconcileOperationAsync(
        AgentToolOperationReconciliationRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Terminates actor-owned recovery for a previously admitted durable operation. Implementations
/// must return an honest terminal outcome: provider-confirmed cancellation, an observed competing
/// terminal outcome, or an explicit cancellation-unconfirmed failure.
/// </summary>
public interface IAgentToolOperationCanceller
{
    Task<AgentToolOperationCancellationResult> CancelOperationAsync(
        AgentToolOperationCancellationRequest request,
        CancellationToken ct = default);
}

public interface IAgentToolDurableOperation : IAgentToolOperationReconciler, IAgentToolOperationCanceller
{
    Task<AgentToolOperationStartResult> StartOperationAsync(
        AgentToolOperationStartRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Request-local tools use this contract to seal the exact external operation admission that
/// their opaque model-visible name resolves to. The caller supplies argument values only.
/// </summary>
public interface IAgentToolOperationAdmissionOwner
{
    AgentToolOperationAdmission OperationAdmission { get; }

    /// <summary>
    /// Freezes invocation-specific server-owned evidence, such as a post-effect read-back whose
    /// selector arguments are copied from this exact typed call. Implementations must never accept
    /// caller-supplied route identity through this method.
    /// </summary>
    AgentToolOperationAdmission ResolveOperationAdmission(string argumentsJson) => OperationAdmission;
}

/// <summary>Agent 可调用工具接口。LLM 通过 tool_call 触发执行。</summary>
public interface IAgentTool
{
    /// <summary>工具名称，用于 LLM 选择与调用。</summary>
    string Name { get; }

    /// <summary>工具描述，供 LLM 理解用途。</summary>
    string Description { get; }

    /// <summary>工具参数 JSON Schema，描述输入格式。</summary>
    string ParametersSchema { get; }

    /// <summary>Provider-owned presentation identity snapshotted for historical tool cards.</summary>
    ToolPresentationDescriptor Presentation => ToolPresentationDescriptors.Generic(Name, Description);

    /// <summary>
    /// Resolves provider-owned presentation identity for one invocation. Tools
    /// whose card identity depends on structured arguments override this while
    /// ordinary tools retain the static descriptor.
    /// </summary>
    ToolPresentationDescriptor ResolvePresentation(string argumentsJson) => Presentation;

    /// <summary>工具审批模式。默认 NeverRequire（立即执行）。</summary>
    ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    /// <summary>工具是否只读（不修改外部状态）。Auto 模式下只读工具自动放行。</summary>
    bool IsReadOnly => false;

    /// <summary>工具是否有破坏性（删除、覆写等不可逆操作）。Auto 模式下破坏性工具要求审批。</summary>
    bool IsDestructive => false;

    /// <summary>Stable side-effect kind for receipt-worthy tool calls; empty means no declared side effect.</summary>
    string SideEffectKind => "";

    /// <summary>
    /// Controls whether a successful execution remains available to later model rounds in the same chat turn.
    /// A new user turn always starts from the original authorized tool catalog.
    /// </summary>
    AgentToolTurnReusePolicy TurnReusePolicy => AgentToolTurnReusePolicy.Reusable;

    /// <summary>Legacy provider-owned typed success receipt; null leaves the outcome unverified.</summary>
    AgentToolReceipt? CreateSuccessReceipt(string callId, string toolName, string resultJson) => null;

    /// <summary>Provider-owned typed result receipt; null leaves the outcome unverified.</summary>
    AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson) => CreateSuccessReceipt(callId, toolName, resultJson);

    /// <summary>
    /// Executes one call and optionally returns provider-owned typed outcome evidence produced
    /// during execution. Tools that do not override this keep the post-execution receipt path.
    /// </summary>
    async Task<AgentToolTerminalOutcome> ExecuteWithOutcomeAsync(
        string callId,
        string toolName,
        string argumentsJson,
        CancellationToken ct = default) =>
        new(await ExecuteAsync(argumentsJson, ct).ConfigureAwait(false));

    /// <summary>
    /// Runtime approval check: given the actual call arguments, does this specific
    /// invocation require approval? Returns null to fall back to the static
    /// <see cref="IsReadOnly"/>/<see cref="IsDestructive"/> classification.
    /// Override this for tools where destructiveness depends on call-time arguments
    /// (e.g., HTTP proxy: GET is read-only, POST is destructive).
    /// </summary>
    bool? RequiresApproval(string argumentsJson) => null;

    /// <summary>Classifies one concrete invocation without collapsing approval and safety semantics.</summary>
    AgentToolCallSafety GetCallSafety(string argumentsJson) =>
        new(RequiresApproval(argumentsJson), IsReadOnly, IsDestructive);

    /// <summary>
    /// Tool-owned replay contract for one concrete invocation. Mutating tools
    /// are non-replayable unless their implementation explicitly overrides
    /// this contract.
    /// </summary>
    AgentToolReplayPolicy ResolveReplayPolicy(string argumentsJson)
    {
        return IsReadOnly && !IsDestructive
            ? AgentToolReplayPolicy.ReadOnlyRetryable
            : AgentToolReplayPolicy.NonReplayable;
    }

    /// <summary>执行工具。参数为 JSON 字符串，返回结果为 JSON 字符串。</summary>
    /// <param name="argumentsJson">LLM 传入的参数 JSON。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>工具执行结果，通常为 JSON 字符串。</returns>
    Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);
}
