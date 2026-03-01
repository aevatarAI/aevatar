namespace Aevatar.Scripting.Core.AI;

public interface IAICapability
{
    Task<string> AskAsync(
        string runId,
        string prompt,
        CancellationToken ct);
}
