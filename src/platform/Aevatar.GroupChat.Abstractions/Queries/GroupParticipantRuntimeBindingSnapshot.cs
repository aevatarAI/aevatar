using Aevatar.GroupChat.Abstractions;

namespace Aevatar.GroupChat.Abstractions.Queries;

public sealed record GroupServiceRuntimeTargetSnapshot(
    string TenantId,
    string AppId,
    string Namespace,
    string ServiceId,
    string EndpointId,
    string ScopeId);

public sealed record GroupWorkflowRuntimeTargetSnapshot(
    string DefinitionActorId,
    string WorkflowName,
    string ScopeId);

public sealed record GroupScriptRuntimeTargetSnapshot(
    string DefinitionActorId,
    string Revision,
    string RuntimeActorId,
    string RequestedEventType,
    string ScopeId);

public sealed record GroupLocalRuntimeTargetSnapshot(
    string Provider);

public sealed record GroupParticipantRuntimeBindingSnapshot(
    string ParticipantAgentId,
    GroupParticipantRuntimeTargetKind TargetKind,
    GroupServiceRuntimeTargetSnapshot? ServiceTarget = null,
    GroupWorkflowRuntimeTargetSnapshot? WorkflowTarget = null,
    GroupScriptRuntimeTargetSnapshot? ScriptTarget = null,
    GroupLocalRuntimeTargetSnapshot? LocalTarget = null);
