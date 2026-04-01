namespace Aevatar.AI.ToolProviders.Scripting.Ports;

/// <summary>
/// Port for fast-path sandbox execution of compiled scripts.
/// Creates a short-lived actor, dispatches input, returns results, then destroys the actor.
/// Implementation MUST:
///   1. Execute on a separate thread pool (not the grain's main thread).
///   2. Enforce CPU timeout and memory limits.
///   3. Provide restricted runtime capabilities (no cluster publish/send).
///   4. Use a unique, non-reusable actor ID per execution.
///   5. Call DeactivateOnIdle after returning results.
/// </summary>
public interface IScriptToolSandboxExecutionAdapter
{
    /// <summary>Execute a compiled script in a restricted sandbox.</summary>
    Task<ScriptSandboxExecutionResult> ExecuteAsync(
        ScriptSandboxExecutionRequest request,
        CancellationToken ct = default);
}

/// <summary>Request to execute a script in sandbox mode.</summary>
public sealed record ScriptSandboxExecutionRequest
{
    /// <summary>Script ID (must have been compiled previously).</summary>
    public required string ScriptId { get; init; }

    /// <summary>Revision to execute. If null, uses the latest compiled revision.</summary>
    public string? Revision { get; init; }

    /// <summary>Input payload as JSON. Dispatched as the first command to the script behavior.</summary>
    public string? InputJson { get; init; }

    /// <summary>Execution timeout in seconds. Clamped to [1, 60].</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Maximum memory allocation in bytes. Default 128 MB.</summary>
    public long MaxMemoryBytes { get; init; } = 128 * 1024 * 1024;
}

/// <summary>Result of a sandbox execution.</summary>
public sealed record ScriptSandboxExecutionResult
{
    public required bool Success { get; init; }

    /// <summary>Correlation ID for this execution run.</summary>
    public string? ExecutionId { get; init; }

    /// <summary>Emitted domain events as JSON array.</summary>
    public string? EmittedEventsJson { get; init; }

    /// <summary>Final state as JSON.</summary>
    public string? FinalStateJson { get; init; }

    /// <summary>Read model as JSON.</summary>
    public string? ReadModelJson { get; init; }

    /// <summary>Error message if execution failed.</summary>
    public string? Error { get; init; }

    /// <summary>Execution duration in milliseconds.</summary>
    public long DurationMs { get; init; }
}
