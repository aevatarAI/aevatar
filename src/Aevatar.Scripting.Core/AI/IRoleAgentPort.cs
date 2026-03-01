namespace Aevatar.Scripting.Core.AI;

public interface IRoleAgentPort
{
    Task<string> RunAsync(
        string runId,
        string correlationId,
        string prompt,
        CancellationToken ct);
}
