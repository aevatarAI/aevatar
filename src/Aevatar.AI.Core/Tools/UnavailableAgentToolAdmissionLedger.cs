using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Tools;

public sealed class UnavailableAgentToolAdmissionLedger : IAgentToolAdmissionLedger
{
    public Task<AgentToolAdmissionResult> TryStartAsync(
        AgentToolAdmissionFact fact,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentToolAdmissionResult(
            AgentToolAdmissionStatus.StoreUnavailable,
            "The durable tool admission ledger is not configured."));
    }
}
