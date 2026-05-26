namespace Aevatar.Foundation.VoicePresence.Abstractions;

/// <summary>
/// Narrow tool-execution port used by voice sessions to satisfy provider-side function calls.
/// </summary>
public interface IVoiceToolInvoker
{
    /// <summary>
    /// Executes one named tool and returns the result JSON that should be sent back to the provider.
    /// </summary>
    Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct = default);
}
