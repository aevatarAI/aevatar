using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Core.Agents;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.DynamicRuntime.Application;

public sealed class DynamicRuntimeApplicationService : IDynamicRuntimeCommandService, IDynamicRuntimeQueryService
{
    private const string PublisherId = "dynamic-runtime.application";

    private readonly IActorRuntime _runtime;
    private readonly IDynamicRuntimeReadStore _readStore;
    private readonly IIdempotencyPort _idempotencyPort;
    private readonly IConcurrencyTokenPort _concurrencyTokenPort;
    private readonly IImageReferenceResolver _imageReferenceResolver;
    private readonly IScriptComposeSpecValidator _composeSpecValidator;
    private readonly IScriptComposeReconcilePort _composeReconcilePort;

    public DynamicRuntimeApplicationService(
        IActorRuntime runtime,
        IDynamicRuntimeReadStore readStore,
        IIdempotencyPort idempotencyPort,
        IConcurrencyTokenPort concurrencyTokenPort,
        IImageReferenceResolver imageReferenceResolver,
        IScriptComposeSpecValidator composeSpecValidator,
        IScriptComposeReconcilePort composeReconcilePort)
    {
        _runtime = runtime;
        _readStore = readStore;
        _idempotencyPort = idempotencyPort;
        _concurrencyTokenPort = concurrencyTokenPort;
        _imageReferenceResolver = imageReferenceResolver;
        _composeSpecValidator = composeSpecValidator;
        _composeReconcilePort = composeReconcilePort;
    }

    public async Task<DynamicCommandResult> PublishImageAsync(PublishImageRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        await EnsureIdempotentAsync("image.publish", context.IdempotencyKey, ct);

        var actorId = $"dynamic:image:{request.ImageName}";
        var actor = await EnsureActorAsync<ScriptImageCatalogGAgent>(actorId, ct);
        await actor.HandleEventAsync(CreateEnvelope(new ScriptImagePublishedEvent
        {
            ImageName = request.ImageName,
            Tag = request.Tag,
            Digest = request.Digest,
        }), ct);

        var etag = await _concurrencyTokenPort.CheckAndAdvanceAsync(actorId, context.IfMatch, ct);

        await _readStore.UpsertImageAsync(new ImageSnapshot(
            request.ImageName,
            new Dictionary<string, string> { [request.Tag] = request.Digest },
            [request.Digest]), ct);

        return new DynamicCommandResult(actorId, "PUBLISHED", etag);
    }

    public async Task<DynamicCommandResult> ApplyComposeAsync(ApplyComposeRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        await EnsureIdempotentAsync("compose.apply", context.IdempotencyKey, ct);
        await _composeSpecValidator.ValidateAsync(request, ct);

        var stackActorId = $"dynamic:stack:{request.StackId}";
        var stackActor = await EnsureActorAsync<ScriptComposeStackGAgent>(stackActorId, ct);
        await stackActor.HandleEventAsync(CreateEnvelope(new ScriptComposeAppliedEvent
        {
            StackId = request.StackId,
            ComposeSpecDigest = request.ComposeSpecDigest,
            DesiredGeneration = request.DesiredGeneration,
        }), ct);

        foreach (var svc in request.Services)
        {
            var serviceActorId = $"dynamic:stack:{request.StackId}:service:{svc.ServiceName}";
            var serviceActor = await EnsureActorAsync<ScriptComposeServiceGAgent>(serviceActorId, ct);
            await serviceActor.HandleEventAsync(CreateEnvelope(new ScriptComposeServiceScaledEvent
            {
                StackId = request.StackId,
                ServiceName = svc.ServiceName,
                ReplicasDesired = svc.ReplicasDesired,
                ServiceMode = svc.ServiceMode.ToString().ToLowerInvariant(),
            }), ct);
        }

        var observed = await _composeReconcilePort.ReconcileAsync(request.StackId, request.DesiredGeneration, ct);

        await _readStore.UpsertStackAsync(new StackSnapshot(
            request.StackId,
            request.ComposeSpecDigest,
            request.DesiredGeneration,
            observed,
            "Converged"), ct);

        await stackActor.HandleEventAsync(CreateEnvelope(new ScriptComposeConvergedEvent
        {
            StackId = request.StackId,
            ObservedGeneration = observed,
        }), ct);

        var etag = await _concurrencyTokenPort.CheckAndAdvanceAsync(stackActorId, context.IfMatch, ct);
        return new DynamicCommandResult(stackActorId, "APPLIED", etag);
    }

    public async Task<DynamicCommandResult> CreateContainerAsync(CreateContainerRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        await EnsureIdempotentAsync("container.create", context.IdempotencyKey, ct);

        var actorId = $"dynamic:container:{request.ContainerId}";
        var actor = await EnsureActorAsync<ScriptContainerGAgent>(actorId, ct);
        await actor.HandleEventAsync(CreateEnvelope(new ScriptContainerCreatedEvent
        {
            ContainerId = request.ContainerId,
            StackId = request.StackId,
            ServiceName = request.ServiceName,
            ImageDigest = request.ImageDigest,
            RoleActorId = request.RoleActorId,
        }), ct);

        await actor.HandleEventAsync(CreateEnvelope(new ScriptContainerStartedEvent { ContainerId = request.ContainerId }), ct);

        await _readStore.UpsertContainerAsync(new ContainerSnapshot(
            request.ContainerId,
            request.StackId,
            request.ServiceName,
            request.ImageDigest,
            "Running",
            request.RoleActorId), ct);

        var etag = await _concurrencyTokenPort.CheckAndAdvanceAsync(actorId, context.IfMatch, ct);
        return new DynamicCommandResult(actorId, "RUNNING", etag);
    }

    public async Task<DynamicCommandResult> StartRunAsync(StartRunRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        await EnsureIdempotentAsync("run.start", context.IdempotencyKey, ct);

        var actorId = $"dynamic:run:{request.RunId}";
        var actor = await EnsureActorAsync<ScriptRunGAgent>(actorId, ct);
        await actor.HandleEventAsync(CreateEnvelope(new ScriptRunStartedEvent
        {
            RunId = request.RunId,
            ContainerId = request.ContainerId,
        }), ct);

        await _readStore.UpsertRunAsync(new RunSnapshot(request.RunId, request.ContainerId, "Running", string.Empty, string.Empty), ct);
        var etag = await _concurrencyTokenPort.CheckAndAdvanceAsync(actorId, context.IfMatch, ct);
        return new DynamicCommandResult(actorId, "RUNNING", etag);
    }

    public async Task<DynamicCommandResult> CompleteRunAsync(CompleteRunRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        await EnsureIdempotentAsync("run.complete", context.IdempotencyKey, ct);

        var actorId = $"dynamic:run:{request.RunId}";
        var actor = await EnsureActorAsync<ScriptRunGAgent>(actorId, ct);
        await actor.HandleEventAsync(CreateEnvelope(new ScriptRunCompletedEvent
        {
            RunId = request.RunId,
            Result = request.Result,
        }), ct);

        var existing = await _readStore.GetRunAsync(request.RunId, ct);
        await _readStore.UpsertRunAsync(new RunSnapshot(
            request.RunId,
            existing?.ContainerId ?? string.Empty,
            "Succeeded",
            request.Result,
            string.Empty), ct);

        var etag = await _concurrencyTokenPort.CheckAndAdvanceAsync(actorId, context.IfMatch, ct);
        return new DynamicCommandResult(actorId, "SUCCEEDED", etag);
    }

    public async Task<DynamicCommandResult> FailRunAsync(FailRunRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        await EnsureIdempotentAsync("run.fail", context.IdempotencyKey, ct);

        var actorId = $"dynamic:run:{request.RunId}";
        var actor = await EnsureActorAsync<ScriptRunGAgent>(actorId, ct);
        await actor.HandleEventAsync(CreateEnvelope(new ScriptRunFailedEvent
        {
            RunId = request.RunId,
            Error = request.Error,
        }), ct);

        var existing = await _readStore.GetRunAsync(request.RunId, ct);
        await _readStore.UpsertRunAsync(new RunSnapshot(
            request.RunId,
            existing?.ContainerId ?? string.Empty,
            "Failed",
            string.Empty,
            request.Error), ct);

        var etag = await _concurrencyTokenPort.CheckAndAdvanceAsync(actorId, context.IfMatch, ct);
        return new DynamicCommandResult(actorId, "FAILED", etag);
    }

    public async Task<DynamicCommandResult> SubmitBuildPlanAsync(SubmitBuildPlanRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        await EnsureIdempotentAsync("build.plan", context.IdempotencyKey, ct);

        var actorId = $"dynamic:build:{request.BuildJobId}";
        var actor = await EnsureActorAsync<ScriptBuildJobGAgent>(actorId, ct);
        await actor.HandleEventAsync(CreateEnvelope(new ScriptBuildPlanSubmittedEvent
        {
            BuildJobId = request.BuildJobId,
            StackId = request.StackId,
            ServiceName = request.ServiceName,
            SourceBundleDigest = request.SourceBundleDigest,
        }), ct);

        await _readStore.UpsertBuildJobAsync(new BuildJobSnapshot(
            request.BuildJobId,
            request.StackId,
            request.ServiceName,
            request.SourceBundleDigest,
            string.Empty,
            "Planned"), ct);

        var etag = await _concurrencyTokenPort.CheckAndAdvanceAsync(actorId, context.IfMatch, ct);
        return new DynamicCommandResult(actorId, "PLANNED", etag);
    }

    public async Task<DynamicCommandResult> ValidateBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default)
    {
        await EnsureIdempotentAsync("build.validate", context.IdempotencyKey, ct);
        return await UpdateBuildStatusAsync(buildJobId, "Validated", context.IfMatch, ct);
    }

    public async Task<DynamicCommandResult> ApproveBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default)
    {
        await EnsureIdempotentAsync("build.approve", context.IdempotencyKey, ct);

        var actorId = $"dynamic:build:{buildJobId}";
        var actor = await EnsureActorAsync<ScriptBuildJobGAgent>(actorId, ct);
        await actor.HandleEventAsync(CreateEnvelope(new ScriptBuildApprovedEvent
        {
            BuildJobId = buildJobId,
        }), ct);

        return await UpdateBuildStatusAsync(buildJobId, "Approved", context.IfMatch, ct);
    }

    public async Task<DynamicCommandResult> ExecuteBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default)
    {
        await EnsureIdempotentAsync("build.execute", context.IdempotencyKey, ct);

        var build = await _readStore.GetBuildJobAsync(buildJobId, ct)
            ?? throw new InvalidOperationException($"Build job '{buildJobId}' does not exist.");

        var digest = await _imageReferenceResolver.ResolveDigestAsync("dynamic-build", build.SourceBundleDigest, ct);
        await PublishBuildResultAsync(new PublishBuildResultRequest(buildJobId, digest), context, ct);
        return await UpdateBuildStatusAsync(buildJobId, "Executed", context.IfMatch, ct);
    }

    public async Task<DynamicCommandResult> RollbackBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default)
    {
        await EnsureIdempotentAsync("build.rollback", context.IdempotencyKey, ct);
        return await UpdateBuildStatusAsync(buildJobId, "RolledBack", context.IfMatch, ct);
    }

    public async Task<DynamicCommandResult> PublishBuildResultAsync(PublishBuildResultRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        await EnsureIdempotentAsync("build.publish", context.IdempotencyKey, ct);

        var actorId = $"dynamic:build:{request.BuildJobId}";
        var actor = await EnsureActorAsync<ScriptBuildJobGAgent>(actorId, ct);
        await actor.HandleEventAsync(CreateEnvelope(new ScriptBuildPublishedEvent
        {
            BuildJobId = request.BuildJobId,
            ResultImageDigest = request.ResultImageDigest,
        }), ct);

        var existing = await _readStore.GetBuildJobAsync(request.BuildJobId, ct);
        if (existing != null)
            await _readStore.UpsertBuildJobAsync(existing with { ResultImageDigest = request.ResultImageDigest, Status = "Published" }, ct);

        var etag = await _concurrencyTokenPort.CheckAndAdvanceAsync(actorId, context.IfMatch, ct);
        return new DynamicCommandResult(actorId, "PUBLISHED", etag);
    }

    public Task<ImageSnapshot?> GetImageAsync(string imageName, CancellationToken ct = default) => _readStore.GetImageAsync(imageName, ct);
    public Task<StackSnapshot?> GetStackAsync(string stackId, CancellationToken ct = default) => _readStore.GetStackAsync(stackId, ct);
    public Task<ContainerSnapshot?> GetContainerAsync(string containerId, CancellationToken ct = default) => _readStore.GetContainerAsync(containerId, ct);
    public Task<RunSnapshot?> GetRunAsync(string runId, CancellationToken ct = default) => _readStore.GetRunAsync(runId, ct);
    public Task<BuildJobSnapshot?> GetBuildJobAsync(string buildJobId, CancellationToken ct = default) => _readStore.GetBuildJobAsync(buildJobId, ct);

    private async Task<DynamicCommandResult> UpdateBuildStatusAsync(string buildJobId, string status, string? ifMatch, CancellationToken ct)
    {
        var actorId = $"dynamic:build:{buildJobId}";
        var existing = await _readStore.GetBuildJobAsync(buildJobId, ct)
            ?? throw new InvalidOperationException($"Build job '{buildJobId}' does not exist.");

        await _readStore.UpsertBuildJobAsync(existing with { Status = status }, ct);
        var etag = await _concurrencyTokenPort.CheckAndAdvanceAsync(actorId, ifMatch, ct);
        return new DynamicCommandResult(actorId, status.ToUpperInvariant(), etag);
    }

    private async Task<IActor> EnsureActorAsync<TAgent>(string actorId, CancellationToken ct) where TAgent : IAgent
    {
        var existing = await _runtime.GetAsync(actorId);
        if (existing != null)
            return existing;

        return await _runtime.CreateAsync<TAgent>(actorId, ct);
    }

    private static EventEnvelope CreateEnvelope(Google.Protobuf.IMessage payload)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            PublisherId = PublisherId,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }

    private async Task EnsureIdempotentAsync(string scope, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Idempotency key is required.");

        var acquired = await _idempotencyPort.TryAcquireAsync(scope, key, ct);
        if (!acquired)
            throw new InvalidOperationException($"Duplicate command detected for scope '{scope}'.");
    }
}
