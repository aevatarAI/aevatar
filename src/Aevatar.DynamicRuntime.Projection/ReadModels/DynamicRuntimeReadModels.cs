using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.DynamicRuntime.Projection.ReadModels;

public sealed record DynamicRuntimeImageReadModel(
    string Id,
    string ImageName,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyList<string> Digests) : IProjectionReadModel;

public sealed record DynamicRuntimeStackReadModel(
    string Id,
    string StackId,
    string ComposeSpecDigest,
    string ComposeYaml,
    long DesiredGeneration,
    long ObservedGeneration,
    string ReconcileStatus) : IProjectionReadModel;

public sealed record DynamicRuntimeComposeServiceReadModel(
    string Id,
    string StackId,
    string ServiceName,
    string ImageRef,
    int ReplicasDesired,
    int ReplicasReady,
    string ServiceMode,
    long Generation,
    string RolloutStatus) : IProjectionReadModel;

public sealed record DynamicRuntimeComposeEventReadModel(
    string Id,
    string StackId,
    long Generation,
    string EventType,
    string Details,
    DateTime OccurredAtUtc) : IProjectionReadModel;

public sealed record DynamicRuntimeServiceDefinitionReadModel(
    string Id,
    string ServiceId,
    string Version,
    string Status,
    string ScriptCode,
    string EntrypointType,
    string ServiceMode,
    IReadOnlyList<string> PublicEndpoints,
    IReadOnlyList<string> EventSubscriptions,
    string CapabilitiesHash,
    DateTime UpdatedAtUtc,
    Any? CustomState) : IProjectionReadModel;

public sealed record DynamicRuntimeContainerReadModel(
    string Id,
    string ContainerId,
    string StackId,
    string ServiceName,
    string ServiceId,
    string ImageDigest,
    string Status,
    string RoleActorId) : IProjectionReadModel;

public sealed record DynamicRuntimeRunReadModel(
    string Id,
    string RunId,
    string ContainerId,
    string ServiceId,
    string Status,
    string Result,
    string Error,
    string CancellationReason) : IProjectionReadModel;

public sealed record DynamicRuntimeBuildJobReadModel(
    string Id,
    string BuildJobId,
    string StackId,
    string ServiceName,
    string SourceBundleDigest,
    string BuildPlanDigest,
    string PolicyDecision,
    string ResultImageDigest,
    string Status,
    bool RequiresManualApproval,
    string RequestedByAgentId) : IProjectionReadModel;

public sealed record DynamicRuntimeScriptReadModelDefinitionReadModel(
    string Id,
    string ServiceId,
    string ReadModelName,
    string KeyField,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<string> Indexes,
    DateTime UpdatedAtUtc) : IProjectionReadModel;

public sealed record DynamicRuntimeScriptReadModelRelationReadModel(
    string Id,
    string ServiceId,
    string RelationName,
    string FromReadModel,
    string ToReadModel,
    string FromKeyField,
    string ToKeyField,
    DateTime UpdatedAtUtc) : IProjectionReadModel;

public sealed record DynamicRuntimeScriptReadModelDocumentReadModel(
    string Id,
    string ServiceId,
    string ReadModelName,
    string DocumentId,
    Any Document,
    IReadOnlyDictionary<string, Any> IndexValues,
    DateTime UpdatedAtUtc) : IProjectionReadModel;
