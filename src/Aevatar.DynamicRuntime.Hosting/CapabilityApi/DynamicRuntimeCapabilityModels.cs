using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Hosting.CapabilityApi;

public sealed record PublishImageInput(string ImageName, string Tag, string Digest);

public sealed record ComposeServiceInput(string ServiceName, int ReplicasDesired, DynamicServiceMode ServiceMode);

public sealed record ApplyComposeInput(string StackId, string ComposeSpecDigest, long DesiredGeneration, IReadOnlyList<ComposeServiceInput> Services);

public sealed record CreateContainerInput(string ContainerId, string StackId, string ServiceName, string ImageDigest, string RoleActorId);

public sealed record StartRunInput(string RunId, string ContainerId);

public sealed record CompleteRunInput(string RunId, string Result);

public sealed record FailRunInput(string RunId, string Error);

public sealed record BuildPlanInput(string BuildJobId, string StackId, string ServiceName, string SourceBundleDigest);

public sealed record BuildPublishInput(string BuildJobId, string ResultImageDigest);
