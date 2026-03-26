namespace Aevatar.GroupChat.Abstractions.Queries;

public sealed record GroupParticipantRuntimeBindingSnapshot(
    string ParticipantAgentId,
    string TenantId,
    string AppId,
    string Namespace,
    string ServiceId,
    string EndpointId,
    string ScopeId);
