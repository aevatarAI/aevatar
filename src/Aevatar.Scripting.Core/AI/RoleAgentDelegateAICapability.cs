namespace Aevatar.Scripting.Core.AI;

public sealed class RoleAgentDelegateAICapability : IAICapability
{
    private readonly IRoleAgentPort _roleAgentPort;

    public RoleAgentDelegateAICapability(IRoleAgentPort roleAgentPort)
    {
        _roleAgentPort = roleAgentPort;
    }

    public Task<string> AskAsync(
        string runId,
        string correlationId,
        string prompt,
        CancellationToken ct) =>
        _roleAgentPort.RunAsync(runId, correlationId, prompt, ct);
}
