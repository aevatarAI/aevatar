namespace Aevatar.DynamicRuntime.Abstractions.Contracts;

public enum DynamicServiceMode
{
    Daemon = 1,
    Event = 2,
    Hybrid = 3,
}

public sealed record DynamicCommandContext(string IdempotencyKey, string? IfMatch = null);

public sealed record PublishImageRequest(string ImageName, string Tag, string Digest);

public sealed record ApplyComposeRequest(string StackId, string ComposeSpecDigest, long DesiredGeneration, IReadOnlyList<ComposeServiceSpec> Services);

public sealed record ComposeServiceSpec(string ServiceName, int ReplicasDesired, DynamicServiceMode ServiceMode);

public sealed record CreateContainerRequest(string ContainerId, string StackId, string ServiceName, string ImageDigest, string RoleActorId);

public sealed record StartRunRequest(string RunId, string ContainerId);

public sealed record CompleteRunRequest(string RunId, string Result);

public sealed record FailRunRequest(string RunId, string Error);

public sealed record SubmitBuildPlanRequest(string BuildJobId, string StackId, string ServiceName, string SourceBundleDigest);

public sealed record PublishBuildResultRequest(string BuildJobId, string ResultImageDigest);

public sealed record DynamicCommandResult(string AggregateId, string Status, string? ETag = null);

public sealed record ImageSnapshot(string ImageName, IReadOnlyDictionary<string, string> Tags, IReadOnlyList<string> Digests);

public sealed record StackSnapshot(string StackId, string ComposeSpecDigest, long DesiredGeneration, long ObservedGeneration, string ReconcileStatus);

public sealed record ContainerSnapshot(string ContainerId, string StackId, string ServiceName, string ImageDigest, string Status, string RoleActorId);

public sealed record RunSnapshot(string RunId, string ContainerId, string Status, string Result, string Error);

public sealed record BuildJobSnapshot(string BuildJobId, string StackId, string ServiceName, string SourceBundleDigest, string ResultImageDigest, string Status);
