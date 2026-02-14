// ─────────────────────────────────────────────────────────────
// RunManager + RunContextScope - run management with AsyncLocal scope propagation.
// latest-wins: a new run cancels the previous run in the same scope.
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions.Context;

namespace Aevatar.Foundation.Core;

/// <summary>
/// Run manager with latest-wins policy for each scopeId.
/// </summary>
public sealed class RunManager : IRunManager
{
    private readonly ConcurrentDictionary<string, RunContext> _runs = new();

    /// <summary>Starts a new run and replaces/cancels existing run in the same scopeId.</summary>
    public RunContext StartNewRun(string scopeId)
    {
        var newRun = new RunContext();
        var old = _runs.AddOrUpdate(scopeId, newRun, (_, prev) => { prev.Cancel(); return newRun; });
        return newRun;
    }

    /// <summary>Gets the current run for the specified scopeId, if any.</summary>
    public RunContext? GetCurrentRun(string scopeId) =>
        _runs.GetValueOrDefault(scopeId);

    /// <summary>Cancels and removes the run for the specified scopeId.</summary>
    public void CancelRun(string scopeId)
    {
        if (_runs.TryRemove(scopeId, out var run))
            run.Cancel();
    }
}

/// <summary>
/// AsyncLocal scope. Begin injects RunContext into the current call chain.
/// </summary>
public static class RunContextScope
{
    private static readonly AsyncLocal<RunContext?> Current = new();

    /// <summary>RunContext in the current call chain.</summary>
    public static RunContext? CurrentRun => Current.Value;

    /// <summary>Begins scope, sets CurrentRun, and restores previous value on dispose.</summary>
    public static IDisposable Begin(RunContext run)
    {
        var prev = Current.Value;
        Current.Value = run;
        return new Scope(prev);
    }

    private sealed class Scope(RunContext? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
