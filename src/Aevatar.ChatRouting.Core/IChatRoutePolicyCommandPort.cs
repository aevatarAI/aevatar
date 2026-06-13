using Aevatar.ChatRouting.Abstractions;

namespace Aevatar.ChatRouting.Core;

/// <summary>
/// Application command surface for chat route policy mutations.
/// </summary>
// Refactor (iter34/cluster-005-mainnet-host-direct-actor-runtime):
//   Old pattern: Mainnet Host endpoints inject IActorRuntime/IActorDispatchPort and build EventEnvelope + dispatch directly in Host code.
//   New principle: Host calls Application command ports that normalize, resolve target, build envelope, dispatch, return honest accepted receipt.
//   Host endpoint stays minimal (auth + body parsing). NO direct dependency on IActorRuntime/IActorDispatchPort in Host.
public interface IChatRoutePolicyCommandPort
{
    Task<ChatRoutePolicyCommandAcceptedReceipt> UpsertAsync(
        string scopeId,
        UpsertChatRoutePolicyRequested command,
        CancellationToken ct = default);

    Task<ChatRoutePolicyCommandAcceptedReceipt> UpsertRuleAsync(
        string scopeId,
        UpsertChatRouteRuleRequested command,
        CancellationToken ct = default);

    Task<ChatRoutePolicyCommandAcceptedReceipt> RemoveRuleAsync(
        string scopeId,
        RemoveChatRouteRuleRequested command,
        CancellationToken ct = default);
}

public sealed record ChatRoutePolicyCommandAcceptedReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId);
