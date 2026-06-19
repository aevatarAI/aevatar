using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Core.GAgents;

namespace Aevatar.GAgentService.Infrastructure.Activation;

public sealed class LlmRunExecutionTargetProvisioner(IActorRuntime runtime) : ILlmRunExecutionTargetProvisioner
{
    private readonly IActorRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public async Task<string> EnsureExecutionTargetAsync(
        LlmRunExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);

        var actorId = BuildActorId(request.SessionActorId, request.RunId);
        _ = await _runtime.CreateByKindAsync(LlmRunExecutionGAgent.Kind, actorId, ct).ConfigureAwait(false);
        return actorId;
    }

    private static string BuildActorId(string sessionActorId, string runId) =>
        $"gagent-service:llm-run-execution:{Uri.EscapeDataString(sessionActorId.Trim())}:{Uri.EscapeDataString(runId.Trim())}";
}
