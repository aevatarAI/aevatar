using Aevatar.Scripting.Core.AI;

namespace Aevatar.Scripting.Application.AI;

public sealed class NoopAICapability : IAICapability
{
    public Task<string> AskAsync(
        string runId,
        string correlationId,
        string prompt,
        CancellationToken ct)
    {
        _ = runId;
        _ = correlationId;
        _ = prompt;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(string.Empty);
    }
}
