using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.DynamicRuntime.Abstractions.Contracts;

public enum DynamicServiceMode
{
    Daemon = 1,
    Event = 2,
    Hybrid = 3,
}

public enum DynamicServiceStatus
{
    Inactive = 1,
    Active = 2,
}

public sealed record DynamicCommandContext(string IdempotencyKey, string? IfMatch = null);

public sealed record PublishImageRequest(string ImageName, string Tag, string Digest);

public sealed record BuildImageRequest(string ImageName, string SourceBundleDigest, string? Tag = null);

public sealed record ComposeApplyYamlRequest(
    string StackId,
    string ComposeSpecDigest,
    string ComposeYaml,
    long DesiredGeneration,
    IReadOnlyList<ComposeServiceSpec> Services);

public sealed record ComposeServiceSpec(
    string ServiceName,
    string ImageRef,
    int ReplicasDesired,
    DynamicServiceMode ServiceMode);

public sealed record ComposeServiceScaleRequest(string StackId, string ServiceName, int ReplicasDesired);

public sealed record ComposeServiceRolloutRequest(string StackId, string ServiceName, string ImageRef);

public sealed record RegisterServiceDefinitionRequest(
    string ServiceId,
    string Version,
    string ScriptCode,
    string EntrypointType,
    DynamicServiceMode ServiceMode,
    IReadOnlyList<string> PublicEndpoints,
    IReadOnlyList<string> EventSubscriptions,
    string CapabilitiesHash,
    Any? CustomState = null);

public sealed record UpdateServiceDefinitionRequest(
    string ServiceId,
    string Version,
    string ScriptCode,
    string EntrypointType,
    DynamicServiceMode ServiceMode,
    IReadOnlyList<string> PublicEndpoints,
    IReadOnlyList<string> EventSubscriptions,
    string CapabilitiesHash,
    Any? CustomState = null);

public sealed record CreateContainerRequest(
    string ContainerId,
    string StackId,
    string ServiceName,
    string ServiceId,
    string ImageDigest,
    string RoleActorId);

public sealed record ExecuteContainerRequest(
    string ContainerId,
    string ServiceId,
    EventEnvelope Envelope,
    string? RunId = null);

public sealed record SubmitBuildPlanRequest(
    string BuildJobId,
    string StackId,
    string ServiceName,
    string SourceBundleDigest,
    string RequestedByAgentId = "dynamic-runtime.system");

public sealed record PublishBuildResultRequest(string BuildJobId, string ResultImageDigest);

public sealed record DynamicCommandResult(string AggregateId, string Status, string? ETag = null);

public sealed record ImageSnapshot(string ImageName, IReadOnlyDictionary<string, string> Tags, IReadOnlyList<string> Digests);

public sealed record ImageTagSnapshot(string ImageName, string Tag, string Digest);

public sealed record ImageDigestSnapshot(string ImageName, string Digest, bool Exists);

public sealed record StackSnapshot(
    string StackId,
    string ComposeSpecDigest,
    string ComposeYaml,
    long DesiredGeneration,
    long ObservedGeneration,
    string ReconcileStatus);

public sealed record ServiceDefinitionSnapshot(
    string ServiceId,
    string Version,
    DynamicServiceStatus Status,
    string ScriptCode,
    string EntrypointType,
    DynamicServiceMode ServiceMode,
    IReadOnlyList<string> PublicEndpoints,
    IReadOnlyList<string> EventSubscriptions,
    string CapabilitiesHash,
    DateTime UpdatedAtUtc,
    Any? CustomState);

public sealed record ContainerSnapshot(
    string ContainerId,
    string StackId,
    string ServiceName,
    string ServiceId,
    string ImageDigest,
    string Status,
    string RoleActorId);

public sealed record RunSnapshot(
    string RunId,
    string ContainerId,
    string ServiceId,
    string Status,
    string Result,
    string Error,
    string CancellationReason);

public sealed record BuildJobSnapshot(
    string BuildJobId,
    string StackId,
    string ServiceName,
    string SourceBundleDigest,
    string BuildPlanDigest,
    string PolicyDecision,
    string ResultImageDigest,
    string Status,
    bool RequiresManualApproval,
    string RequestedByAgentId);

public sealed record ComposeServiceSnapshot(
    string StackId,
    string ServiceName,
    string ImageRef,
    int ReplicasDesired,
    int ReplicasReady,
    DynamicServiceMode ServiceMode,
    long Generation,
    string RolloutStatus);

public sealed record ComposeEventSnapshot(
    string StackId,
    long Generation,
    string EventType,
    string Details,
    DateTime OccurredAtUtc);

public sealed record ScriptReadModelDefinition(
    string ReadModelName,
    string KeyField,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<string> Indexes);

public sealed record ScriptReadModelRelation(
    string RelationName,
    string FromReadModel,
    string ToReadModel,
    string FromKeyField,
    string ToKeyField);

public sealed record ScriptReadModelDocument(
    string ReadModelName,
    string DocumentId,
    Any Document,
    IReadOnlyDictionary<string, Any> IndexValues);

public sealed record ScriptReadModelDefinitionSnapshot(
    string ServiceId,
    string ReadModelName,
    string KeyField,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<string> Indexes,
    DateTime UpdatedAtUtc);

public sealed record ScriptReadModelRelationSnapshot(
    string ServiceId,
    string RelationName,
    string FromReadModel,
    string ToReadModel,
    string FromKeyField,
    string ToKeyField,
    DateTime UpdatedAtUtc);

public sealed record ScriptReadModelDocumentSnapshot(
    string ServiceId,
    string ReadModelName,
    string DocumentId,
    Any Document,
    IReadOnlyDictionary<string, Any> IndexValues,
    DateTime UpdatedAtUtc);

public sealed record IdempotencyAcquireResult(bool Acquired, bool IsReplay, string? ErrorCode = null);

public sealed record ConcurrencyCheckResult(bool Passed, string CurrentVersion, string NextVersion, string? ErrorCode = null);

public sealed record ImageDigestResolveResult(bool Found, string Digest, string? ErrorCode = null);

public sealed record ComposeSpecValidationResult(bool IsValid, string? ErrorCode = null, string? Reason = null);

public sealed record ComposeReconcileResult(bool Converged, long ObservedGeneration, string? ErrorCode = null);

public sealed record BuildPlanRequest(string BuildJobId, string StackId, string ServiceName, string SourceBundleDigest);

public sealed record BuildPlanResult(bool Accepted, string BuildPlanDigest, string? ErrorCode = null, string? Reason = null);

public sealed record BuildPolicyRequest(
    string BuildJobId,
    string StackId,
    string ServiceName,
    string SourceBundleDigest,
    string BuildPlanDigest,
    DynamicServiceMode? ServiceMode = null);

public sealed record BuildPolicyDecision(bool Allowed, bool RequiresManualApproval, string PolicyDecision, string? ErrorCode = null, string? Reason = null);

public sealed record BuildExecutionRequest(
    string BuildJobId,
    string ImageName,
    string SourceBundleDigest,
    string BuildPlanDigest);

public sealed record BuildExecutionResult(bool Succeeded, string ResultImageDigest, string? ErrorCode = null, string? Reason = null);

public sealed record ScriptSourceBundle(string ServiceId, string ScriptCode, string EntrypointType);

public sealed record PolicyValidationResult(bool Allowed, string? ErrorCode = null, string? Reason = null);

public sealed record CompiledScriptArtifact(string ArtifactDigest, string ServiceId, string ScriptCode, string EntrypointType);

public sealed record ScriptExecutionContext(string ServiceId, string EntrypointType, EventEnvelope Envelope, long StartedAtUnixMs);

public sealed record ScriptAssemblyHandle(string ArtifactDigest, IScriptRoleEntrypoint Entrypoint, object? RuntimeHandle = null);

public sealed record UnloadResult(bool Success, string? ErrorCode = null, string? Reason = null);

public sealed record SandboxPrepareResult(bool Allowed, string? ErrorCode = null, string? Reason = null);

public sealed record ResourceQuotaDecision(bool Allowed, string? ErrorCode = null, string? Reason = null);

public sealed record ScriptNetworkRequest(string ServiceId, string Destination, string Method);

public sealed record NetworkAccessDecision(bool Allowed, string? ErrorCode = null, string? Reason = null);

public sealed record ScriptEventEnvelope(
    string EnvelopeId,
    string StackId,
    string ServiceName,
    string InstanceSelector,
    EventEnvelope Envelope);

public sealed record EnvelopeSubscribeRequest(string StackId, string ServiceName, string SubscriberId, string LeaseId, int MaxInFlight);

public sealed record EnvelopeLeaseResult(bool Success, string LeaseId, string? ErrorCode = null, string? Reason = null);

public sealed record EnvelopeDedupResult(bool Allowed, bool IsDuplicate, string? ErrorCode = null, string? Reason = null);

public sealed record ServiceModePolicyRequest(
    string StackId,
    string ServiceName,
    DynamicServiceMode ServiceMode,
    int ReplicasDesired);

public sealed record ServiceModeDecision(bool Allowed, string? Reason = null);

public sealed record BuildApprovalRequest(string BuildJobId, string StackId, string ServiceName, string SourceBundleDigest);

public sealed record BuildApprovalDecision(bool Approved, bool RequiresManualApproval, string? Reason = null);

public sealed record ScriptRoleCapabilitySnapshot(
    string ServiceId,
    string Version,
    string EntrypointType,
    DynamicServiceMode ServiceMode,
    string CapabilitiesHash);
