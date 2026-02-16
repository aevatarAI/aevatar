// ─────────────────────────────────────────────────────────────
// StateGuard - state write protection.
// Uses AsyncLocal to mark whether state mutation is currently allowed.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Core;

/// <summary>
/// State write protection using AsyncLocal scope tracking.
/// Ensures state is mutated only inside EventHandler/EventModule/OnActivateAsync scopes.
/// </summary>
internal static class StateGuard
{
    private static readonly AsyncLocal<bool> Writable = new();

    /// <summary>Whether current scope is writable.</summary>
    public static bool IsWritable => Writable.Value;

    /// <summary>Begins writable scope. Disposing restores previous value.</summary>
    public static WriteScope BeginWriteScope() => new();

    /// <summary>Validates writable scope; otherwise throws InvalidOperationException.</summary>
    public static void EnsureWritable()
    {
        if (!Writable.Value)
            throw new InvalidOperationException(
                "State can only be modified inside EventHandler / EventModule / OnActivateAsync scopes.");
    }

    /// <summary>Disposable writable scope handle that restores previous writable state.</summary>
    public readonly struct WriteScope : IDisposable
    {
        private readonly bool _previous;
        public WriteScope() { _previous = Writable.Value; Writable.Value = true; }
        public void Dispose() => Writable.Value = _previous;
    }
}
