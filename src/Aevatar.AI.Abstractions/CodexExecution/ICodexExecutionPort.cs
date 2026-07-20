namespace Aevatar.AI.Abstractions.CodexExecution;

/// <summary>
/// Runtime-neutral boundary for one Codex execution target. Implementations own transport and
/// isolation details; the workflow run remains the authority for lifecycle and terminal state.
/// </summary>
public interface ICodexExecutionPort
{
    CodexExecutionTarget.TargetOneofCase TargetKind { get; }

    IAsyncEnumerable<CodexExecutionEvent> ExecuteAsync(
        CodexExecutionRequest request,
        CancellationToken ct = default);
}

public sealed record CodexExecutionRequest(
    CodexExecutionTarget Target,
    CodexExecutionWorkspace? Workspace,
    string Prompt,
    int TimeoutSeconds,
    CodexExecutionCallerContext Caller);

public sealed record CodexExecutionCallerContext(
    string? NyxIdAccessToken,
    CodexExecutionNyxIdAuthority? NyxIdAuthority,
    string? ScopeId,
    string? WorkflowRunId,
    string? WorkflowStepId,
    string? ToolCallId);

public sealed record CodexExecutionNyxIdAuthority(
    string Platform,
    string Tenant,
    string ExternalUserId);

public enum CodexExecutionEventKind
{
    Unspecified = 0,
    Started = 1,
    Output = 2,
    Completed = 3,
    Failed = 4,
}

public sealed record CodexExecutionEvent(
    CodexExecutionEventKind Kind,
    string? OutputChunk = null,
    CodexExecutionResult? Result = null,
    CodexExecutionFailure? Failure = null)
{
    public static CodexExecutionEvent Started() => new(CodexExecutionEventKind.Started);

    public static CodexExecutionEvent Output(string chunk) =>
        new(CodexExecutionEventKind.Output, OutputChunk: chunk);

    public static CodexExecutionEvent Completed(CodexExecutionResult result) =>
        new(CodexExecutionEventKind.Completed, Result: result);

    public static CodexExecutionEvent Failed(CodexExecutionFailure failure) =>
        new(CodexExecutionEventKind.Failed, Failure: failure);
}

public sealed record CodexExecutionResult(
    string Output,
    int? ExitCode = null,
    string? DiagnosticId = null,
    long? ElapsedMilliseconds = null);

public enum CodexExecutionFailureKind
{
    Unspecified = 0,
    TargetNotConfigured = 1,
    AdmissionDenied = 2,
    LlmProviderNotConnected = 3,
    CapacityUnavailable = 4,
    ProvisioningFailed = 5,
    ReadinessFailed = 6,
    IsolationUnavailable = 7,
    MalformedOutput = 8,
    TerminalFailure = 9,
    TimedOut = 10,
    Cancelled = 11,
    CleanupFailed = 12,
}

public sealed record CodexExecutionFailure(
    CodexExecutionFailureKind Kind,
    string Code,
    string Message,
    string? DiagnosticId = null);

public sealed class CodexExecutionException : Exception
{
    public CodexExecutionException(CodexExecutionFailure failure, Exception? innerException = null)
        : base(failure?.Message, innerException)
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public CodexExecutionFailure Failure { get; }
}
