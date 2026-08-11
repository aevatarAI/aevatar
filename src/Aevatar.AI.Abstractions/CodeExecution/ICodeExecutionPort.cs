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

public static class CodeExecutionContract
{
    public const string ServiceSlug = "chrono-sandbox";
}

public sealed record CodeExecutionRequest(
    CodeExecutionLanguage Language,
    string Source,
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
    string? ExecutionNyxIdAccessToken,
    string? SourceReadableNyxIdAccessToken);

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
}

public sealed record CodeExecutionFailure(
    CodeExecutionFailureKind Kind,
    string Code,
    string Message,
    string? DiagnosticId = null);

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
