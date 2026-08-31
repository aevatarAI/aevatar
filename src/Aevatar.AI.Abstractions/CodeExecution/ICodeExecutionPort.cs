using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.AI.Abstractions.CodeExecution;

/// <summary>
/// Runtime-neutral boundary for executing caller-provided source code in an isolated runtime.
/// Target resolution and transport remain infrastructure concerns.
/// </summary>
public interface ICodeExecutionPort
{
    Task<CodeExecutionOutcome> ExecuteAsync(
        CodeExecutionRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Durable boundary for executions that may outlive one actor turn. Each call performs at most
/// one short provider exchange; scheduling and retry ownership remain with the workflow actor.
/// </summary>
public interface IDurableCodeExecutionPort
{
    Task<DurableCodeExecutionSubmitOutcome> SubmitAsync(
        DurableCodeExecutionSubmitRequest request,
        CancellationToken ct = default);

    Task<DurableCodeExecutionStatusOutcome> GetStatusAsync(
        DurableCodeExecutionOperationRequest request,
        CancellationToken ct = default);

    Task<DurableCodeExecutionResultOutcome> GetResultAsync(
        DurableCodeExecutionOperationRequest request,
        CancellationToken ct = default);

    Task<DurableCodeExecutionCancelOutcome> CancelAsync(
        DurableCodeExecutionOperationRequest request,
        CancellationToken ct = default);
}

public static class CodeExecutionContract
{
    public const string ServiceSlug = "chrono-sandbox";
    public const string PersonalServiceSlug = "chrono-sandbox-aevatar";
    public const int MinimumTimeoutSeconds = 1;
    public const int DefaultTimeoutSeconds = 180;
    public const int MaximumTimeoutSeconds = 600;

    public static bool IsValidTimeoutSeconds(int timeoutSeconds) =>
        timeoutSeconds is >= MinimumTimeoutSeconds and <= MaximumTimeoutSeconds;

    public static bool IsValidServiceSlug(string? serviceSlug) =>
        Aevatar.AI.Abstractions.LLMProviders.NyxIdServiceSlugPolicy.IsCanonical(serviceSlug);

    public static bool IsSupportedServiceSlug(string? serviceSlug) =>
        string.Equals(serviceSlug, ServiceSlug, StringComparison.Ordinal) ||
        string.Equals(serviceSlug, PersonalServiceSlug, StringComparison.Ordinal);
}

public sealed record CodeExecutionRequest(
    CodeExecutionLanguage Language,
    string Source,
    int TimeoutSeconds,
    CodeExecutionRouteIdentity Route,
    CodeExecutionCallerContext Caller);

public enum CodeExecutionLanguage
{
    Unspecified = 0,
    Python = 1,
    JavaScript = 2,
    TypeScript = 3,
    Bash = 4,
}

public sealed record CodeExecutionCallerContext(
    string? ExecutionNyxIdCredential,
    string? SourceReadableNyxIdAccessToken,
    CodeExecutionNyxIdCredentialKind ExecutionCredentialKind =
        CodeExecutionNyxIdCredentialKind.Unspecified,
    NyxIdDurableOperationGrantRef? DurableOperationGrant = null);

public enum CodeExecutionNyxIdCredentialKind
{
    Unspecified = 0,
    Bearer = 1,
    // Vault-backed unattended Agent Key with producer-issued durable authority.
    AgentKey = 2,
    // Agent Key forwarded by the current authenticated interactive request.
    InteractiveAgentKey = 3,
}

public enum CodeExecutionRouteIdentitySource
{
    Unspecified = 0,
    CodeExecutionContract = 1,
    NyxIdUserServiceCatalog = 2,
    WorkflowCapabilityAdmission = 3,
}

public sealed record CodeExecutionRouteIdentity(
    string ServiceSlug,
    string? UserServiceId,
    CodeExecutionRouteIdentitySource Source);

public sealed record CodeExecutionResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    string? DiagnosticId = null,
    long? ElapsedMilliseconds = null);

public enum CodeExecutionFailureKind
{
    Unspecified = 0,
    AdmissionDenied = 1,
    TargetNotConfigured = 2,
    TransportUnavailable = 3,
    TimedOut = 4,
    ResponseTooLarge = 5,
    MalformedOutput = 6,
    ExecutionFailed = 7,
    OutcomeUncertain = 8,
}

public sealed record CodeExecutionFailure(
    CodeExecutionFailureKind Kind,
    string Code,
    string Message,
    string? DiagnosticId = null,
    DurableCodeExecutionPhase ProviderPhase = DurableCodeExecutionPhase.Unspecified);

/// <summary>
/// A non-zero process exit carries both the completed execution result and a failure. Failures
/// before execution have no result.
/// </summary>
public sealed record CodeExecutionOutcome(
    CodeExecutionResult? Result,
    CodeExecutionFailure? Failure,
    CodeExecutionRouteIdentity? ResolvedRoute)
{
    public static CodeExecutionOutcome Succeeded(
        CodeExecutionResult result,
        CodeExecutionRouteIdentity resolvedRoute) =>
        new(result, null, resolvedRoute);

    public static CodeExecutionOutcome CompletedWithFailure(
        CodeExecutionResult result,
        CodeExecutionFailure failure,
        CodeExecutionRouteIdentity resolvedRoute) =>
        new(result, failure, resolvedRoute);

    public static CodeExecutionOutcome Failed(CodeExecutionFailure failure) =>
        new(null, failure, null);
}

public sealed record DurableCodeExecutionSubmitRequest(
    CodeExecutionRequest Execution,
    string IdempotencyKey);

/// <summary>
/// Identifies an already accepted provider operation. Provider paths are deliberately absent:
/// adapters rebuild their closed route set from the validated opaque operation identifier.
/// </summary>
public sealed record DurableCodeExecutionOperationRequest(
    string ProviderOperationId,
    CodeExecutionRouteIdentity Route,
    CodeExecutionCallerContext Caller,
    string? ETag = null);

public enum DurableCodeExecutionState
{
    Unspecified = 0,
    Queued = 1,
    Provisioning = 2,
    Preparing = 3,
    Running = 4,
    Collecting = 5,
    Succeeded = 6,
    Failed = 7,
    Cancelled = 8,
    OutcomeUncertain = 9,
}

public enum DurableCodeExecutionPhase
{
    Unspecified = 0,
    Queued = 1,
    SandboxCreate = 2,
    SandboxReady = 3,
    InputWrite = 4,
    DependencyInstall = 5,
    Execute = 6,
    Collect = 7,
    CleaningUp = 8,
    Complete = 9,
}

public enum DurableCodeExecutionCleanupState
{
    Unspecified = 0,
    NotStarted = 1,
    Pending = 2,
    Running = 3,
    Retry = 4,
    Complete = 5,
}

public sealed record DurableCodeExecutionProviderFailure(
    string Code,
    string Message);

/// <summary>
/// A durable receipt. All three paths are canonical adapter-owned paths derived from
/// <c>ProviderOperationId</c>, never trusted copies of provider response URLs.
/// </summary>
public sealed record DurableCodeExecutionReceipt(
    string ProviderOperationId,
    string StatusPath,
    string ResultPath,
    string CancelPath,
    DurableCodeExecutionState State,
    CodeExecutionRouteIdentity ResolvedRoute,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    TimeSpan? RetryAfter = null);

public sealed record DurableCodeExecutionSnapshot(
    string ProviderOperationId,
    DurableCodeExecutionState State,
    DurableCodeExecutionPhase Phase,
    DurableCodeExecutionCleanupState CleanupState,
    long Version,
    bool CancelRequested,
    bool ResultAvailable,
    CodeExecutionRouteIdentity ResolvedRoute,
    string ETag,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? TerminalAt = null,
    TimeSpan? RetryAfter = null,
    DurableCodeExecutionProviderFailure? ProviderFailure = null);

public enum DurableCodeExecutionFailureKind
{
    Unspecified = 0,
    AdmissionDenied = 1,
    TargetNotConfigured = 2,
    TransportUnavailable = 3,
    TimedOut = 4,
    ResponseTooLarge = 5,
    MalformedOutput = 6,
    ProviderRejected = 7,
    IdempotencyConflict = 8,
    OperationNotFound = 9,
    Expired = 10,
    RateLimited = 11,
    ServiceUnavailable = 12,
    Cancelled = 13,
    OutcomeUncertain = 14,
    SubmissionUncertain = 15,
    ExecutionFailed = 16,
}

public sealed record DurableCodeExecutionFailure(
    DurableCodeExecutionFailureKind Kind,
    string Code,
    string Message,
    bool Retryable = false,
    TimeSpan? RetryAfter = null,
    string? DiagnosticId = null,
    DurableCodeExecutionPhase ProviderPhase = DurableCodeExecutionPhase.Unspecified);

public sealed record DurableCodeExecutionSubmitOutcome(
    DurableCodeExecutionReceipt? Receipt,
    DurableCodeExecutionFailure? Failure);

public sealed record DurableCodeExecutionStatusOutcome(
    DurableCodeExecutionSnapshot? Snapshot,
    bool NotModified,
    string? ETag,
    TimeSpan? RetryAfter,
    DurableCodeExecutionFailure? Failure);

public sealed record DurableCodeExecutionResultOutcome(
    CodeExecutionOutcome? Outcome,
    bool Pending,
    TimeSpan? RetryAfter,
    DurableCodeExecutionFailure? Failure);

public sealed record DurableCodeExecutionCancelOutcome(
    DurableCodeExecutionSnapshot? Snapshot,
    DurableCodeExecutionFailure? Failure);
