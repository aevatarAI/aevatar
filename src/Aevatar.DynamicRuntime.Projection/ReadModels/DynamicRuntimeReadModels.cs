namespace Aevatar.DynamicRuntime.Projection.ReadModels;

public sealed record DynamicRuntimeImageReadModel(string ImageName, IReadOnlyDictionary<string, string> Tags, IReadOnlyList<string> Digests);

public sealed record DynamicRuntimeStackReadModel(string StackId, string ComposeSpecDigest, long DesiredGeneration, long ObservedGeneration, string ReconcileStatus);

public sealed record DynamicRuntimeContainerReadModel(string ContainerId, string StackId, string ServiceName, string ImageDigest, string Status, string RoleActorId);

public sealed record DynamicRuntimeRunReadModel(string RunId, string ContainerId, string Status, string Result, string Error);

public sealed record DynamicRuntimeBuildJobReadModel(string BuildJobId, string StackId, string ServiceName, string SourceBundleDigest, string ResultImageDigest, string Status);
