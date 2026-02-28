namespace Aevatar.DynamicRuntime.Abstractions.Contracts;

public interface IDynamicRuntimeCommandService
{
    Task<DynamicCommandResult> PublishImageAsync(PublishImageRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> ApplyComposeAsync(ApplyComposeRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> CreateContainerAsync(CreateContainerRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> StartRunAsync(StartRunRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> CompleteRunAsync(CompleteRunRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> FailRunAsync(FailRunRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> SubmitBuildPlanAsync(SubmitBuildPlanRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> ValidateBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> ApproveBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> ExecuteBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> RollbackBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> PublishBuildResultAsync(PublishBuildResultRequest request, DynamicCommandContext context, CancellationToken ct = default);
}

public interface IDynamicRuntimeQueryService
{
    Task<ImageSnapshot?> GetImageAsync(string imageName, CancellationToken ct = default);
    Task<StackSnapshot?> GetStackAsync(string stackId, CancellationToken ct = default);
    Task<ContainerSnapshot?> GetContainerAsync(string containerId, CancellationToken ct = default);
    Task<RunSnapshot?> GetRunAsync(string runId, CancellationToken ct = default);
    Task<BuildJobSnapshot?> GetBuildJobAsync(string buildJobId, CancellationToken ct = default);
}

public interface IIdempotencyPort
{
    Task<bool> TryAcquireAsync(string scope, string key, CancellationToken ct = default);
}

public interface IConcurrencyTokenPort
{
    Task<string> CheckAndAdvanceAsync(string aggregateId, string? expectedETag, CancellationToken ct = default);
}

public interface IImageReferenceResolver
{
    Task<string> ResolveDigestAsync(string imageName, string tagOrDigest, CancellationToken ct = default);
}

public interface IScriptComposeSpecValidator
{
    Task ValidateAsync(ApplyComposeRequest request, CancellationToken ct = default);
}

public interface IScriptComposeReconcilePort
{
    Task<long> ReconcileAsync(string stackId, long desiredGeneration, CancellationToken ct = default);
}

public interface IDynamicRuntimeReadStore
{
    Task UpsertImageAsync(ImageSnapshot snapshot, CancellationToken ct = default);
    Task UpsertStackAsync(StackSnapshot snapshot, CancellationToken ct = default);
    Task UpsertContainerAsync(ContainerSnapshot snapshot, CancellationToken ct = default);
    Task UpsertRunAsync(RunSnapshot snapshot, CancellationToken ct = default);
    Task UpsertBuildJobAsync(BuildJobSnapshot snapshot, CancellationToken ct = default);

    Task<ImageSnapshot?> GetImageAsync(string imageName, CancellationToken ct = default);
    Task<StackSnapshot?> GetStackAsync(string stackId, CancellationToken ct = default);
    Task<ContainerSnapshot?> GetContainerAsync(string containerId, CancellationToken ct = default);
    Task<RunSnapshot?> GetRunAsync(string runId, CancellationToken ct = default);
    Task<BuildJobSnapshot?> GetBuildJobAsync(string buildJobId, CancellationToken ct = default);
}
