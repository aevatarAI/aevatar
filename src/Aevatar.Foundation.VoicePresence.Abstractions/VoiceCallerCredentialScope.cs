namespace Aevatar.Foundation.VoicePresence.Abstractions;

/// <summary>
/// Propagates the caller's NyxID access token from the actor-side voice tool-call boundary
/// (<c>VoicePresenceModule.ExecuteToolCallAsync</c>) to in-call-stack consumers — the voice tool
/// invoker and the realtime provider credential resolver — via AsyncLocal.
///
/// Layering: this lower (Foundation) layer can only carry a neutral token string. The AI layer
/// reads <see cref="CurrentNyxIdAccessToken" /> and maps it onto <c>AgentToolContextScope</c>
/// (which Foundation may not reference). The token is never persisted.
/// </summary>
public static class VoiceCallerCredentialScope
{
    private static readonly AsyncLocal<string?> Token = new();

    /// <summary>The caller's NyxID access token for the current voice tool call, or null when none is in scope.</summary>
    public static string? CurrentNyxIdAccessToken => Token.Value;

    /// <summary>Set the caller token for the current async flow; dispose to restore the previous value.</summary>
    public static IDisposable Push(string? nyxIdAccessToken)
    {
        var previous = Token.Value;
        Token.Value = nyxIdAccessToken;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Token.Value = previous;
        }
    }
}
