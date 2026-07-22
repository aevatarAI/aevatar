using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IAgentProfileNamespaceQueryPort
{
    Task<AgentProfileNamespaceEntrySnapshot?> GetOwnedAsync(
        AgentProfileOwnerIdentity owner,
        string owningScopeId,
        string profileSlug,
        CancellationToken ct = default);

    Task<AgentProfileNamespaceEntrySnapshot?> GetByReferenceAsync(
        AgentProfileReference reference,
        CancellationToken ct = default);
}

public interface IAgentProfileManagementQueryPort
{
    Task<AgentProfileManagementSnapshot?> GetAsync(
        string profileId,
        CancellationToken ct = default);
}

public interface IAgentProfileExecutionSnapshotQueryPort
{
    Task<AgentProfileExecutionSnapshot?> GetAsync(
        string profileId,
        CancellationToken ct = default);
}
