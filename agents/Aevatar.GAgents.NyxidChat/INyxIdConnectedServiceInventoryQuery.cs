using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

namespace Aevatar.GAgents.NyxidChat;

public interface INyxIdConnectedServiceInventoryQuery
{
    Task<NyxIdConnectedServiceInventoryQueryResult> QueryAsync(
        AgentToolExecutionContext context,
        CancellationToken ct = default);
}

public sealed record NyxIdConnectedServiceInventoryQueryResult(
    NyxIdServiceInventoryResult? Inventory,
    NyxIdConnectedServiceInventoryQueryFailure Failure)
{
    public static NyxIdConnectedServiceInventoryQueryResult Succeeded(
        NyxIdServiceInventoryResult inventory) =>
        new(inventory, NyxIdConnectedServiceInventoryQueryFailure.None);

    public static NyxIdConnectedServiceInventoryQueryResult Failed(
        NyxIdConnectedServiceInventoryQueryFailure failure) =>
        new(null, failure);
}

public enum NyxIdConnectedServiceInventoryQueryFailure
{
    None = 0,
    CapabilityUnavailable = 1,
    BindingRevoked = 2,
    ScopeUnavailable = 3,
    SourceUnavailable = 4,
    QueryUnavailable = 5,
}
