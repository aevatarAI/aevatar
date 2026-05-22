namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Command surface for admitting voice demo agent initialization.
/// </summary>
// Refactor (iter34/cluster-005-mainnet-host-direct-actor-runtime):
//   Old pattern: Mainnet Host endpoints inject IActorRuntime/IActorDispatchPort and build EventEnvelope + dispatch directly in Host code.
//   New principle: Host calls Application command ports that normalize, resolve target, build envelope, dispatch, return honest accepted receipt.
//   Host endpoint stays minimal (auth + body parsing). NO direct dependency on IActorRuntime/IActorDispatchPort in Host.
public interface IVoiceDemoAgentCommandPort
{
    Task<VoiceDemoAgentCommandAcceptedReceipt> EnsureAsync(
        string scopeId,
        string voiceModuleName,
        CancellationToken ct = default);
}

public sealed record VoiceDemoAgentCommandAcceptedReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId);
