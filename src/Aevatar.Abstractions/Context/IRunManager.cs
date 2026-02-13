// ─────────────────────────────────────────────────────────────
// IRunManager - run lifecycle management contract.
// Interruptible run context with latest-wins semantics.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Context;

/// <summary>
/// Run manager. A new run automatically cancels the previous run in the same scope.
/// </summary>
public interface IRunManager
{
    /// <summary>Starts a new run in a scope and cancels any previous run in that scope.</summary>
    RunContext StartNewRun(string scopeId);

    /// <summary>Gets the current run in a scope, or null if none exists.</summary>
    RunContext? GetCurrentRun(string scopeId);

    /// <summary>Cancels the current run in a scope.</summary>
    void CancelRun(string scopeId);
}

/// <summary>
/// Interruptible run context with latest-wins semantics.
/// </summary>
public sealed class RunContext
{
    /// <summary>Unique identifier of this run.</summary>
    public string RunId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>CancellationTokenSource used to cancel this run.</summary>
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Cancellation token for this run.</summary>
    public CancellationToken Token => Cts.Token;

    /// <summary>Whether this run has already been cancelled.</summary>
    public bool IsCancelled => Cts.IsCancellationRequested;

    /// <summary>Cancels this run.</summary>
    public void Cancel() => Cts.Cancel();
}