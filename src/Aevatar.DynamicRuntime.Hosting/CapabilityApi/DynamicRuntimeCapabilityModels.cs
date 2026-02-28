using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Hosting.CapabilityApi;

public sealed record PublishImageInput(string ImageName, string Tag, string Digest);

public sealed record BuildImageInput(string ImageName, string SourceBundleDigest, string? Tag = null);

public sealed record PublishImageTagInput(string Digest);

public sealed record ComposeServiceInput(string ServiceName, string ImageRef, int ReplicasDesired, DynamicServiceMode ServiceMode);

public sealed record ApplyComposeInput(
    string StackId,
    string ComposeSpecDigest,
    string ComposeYaml,
    long DesiredGeneration,
    IReadOnlyList<ComposeServiceInput> Services);

public sealed record ScaleComposeServiceInput(int ReplicasDesired);

public sealed record RolloutComposeServiceInput(string ImageRef);

public sealed record RegisterServiceInput(
    string ServiceId,
    string Version,
    string ScriptCode,
    string EntrypointType,
    DynamicServiceMode ServiceMode,
    IReadOnlyList<string> PublicEndpoints,
    IReadOnlyList<string> EventSubscriptions,
    string CapabilitiesHash);

public sealed record UpdateServiceInput(
    string ServiceId,
    string Version,
    string ScriptCode,
    string EntrypointType,
    DynamicServiceMode ServiceMode,
    IReadOnlyList<string> PublicEndpoints,
    IReadOnlyList<string> EventSubscriptions,
    string CapabilitiesHash);

public sealed record CreateContainerInput(
    string ContainerId,
    string StackId,
    string ServiceName,
    string ServiceId,
    string ImageDigest,
    string RoleActorId);

public sealed record ExecuteContainerInput(
    string ContainerId,
    string ServiceId,
    string Input,
    string? RunId = null,
    string? InputJson = null,
    IReadOnlyDictionary<string, string>? InputMetadata = null,
    string? CorrelationId = null,
    string? CausationId = null,
    string? MessageType = null);

public sealed record CancelRunInput(string Reason);

public sealed record BuildPlanInput(string BuildJobId, string StackId, string ServiceName, string SourceBundleDigest, string RequestedByAgentId = "dynamic-runtime.system");

public sealed record BuildPublishInput(string BuildJobId, string ResultImageDigest);
