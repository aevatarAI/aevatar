using System.Security.Cryptography;
using System.Text;
using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Core.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.DynamicRuntime.Application;

public sealed class DynamicRuntimeApplicationService : IDynamicRuntimeCommandService, IDynamicRuntimeQueryService
{
    private const string PublisherId = "dynamic-runtime.application";
    private static readonly TimeSpan EnvelopeDedupTtl = TimeSpan.FromMinutes(10);

    private readonly IActorRuntime _runtime;
    private readonly IDynamicRuntimeReadStore _readStore;
    private readonly IStateStore<ScriptServiceDefinitionState> _serviceDefinitionStateStore;
    private readonly IIdempotencyPort _idempotencyPort;
    private readonly IConcurrencyTokenPort _concurrencyTokenPort;
    private readonly IImageReferenceResolver _imageReferenceResolver;
    private readonly IScriptComposeSpecValidator _composeSpecValidator;
    private readonly IScriptComposeReconcilePort _composeReconcilePort;
    private readonly IAgentBuildPlanPort _buildPlanPort;
    private readonly IAgentBuildPolicyPort _buildPolicyPort;
    private readonly IAgentBuildExecutionPort _buildExecutionPort;
    private readonly IServiceModePolicyPort _serviceModePolicyPort;
    private readonly IBuildApprovalPort _buildApprovalPort;
    private readonly IEventEnvelopePublisherPort _eventEnvelopePublisherPort;
    private readonly IEventEnvelopeSubscriberPort _eventEnvelopeSubscriberPort;
    private readonly IEventEnvelopeDedupPort _eventEnvelopeDedupPort;
    private readonly IDynamicScriptExecutionService _scriptExecutionService;

    public DynamicRuntimeApplicationService(
        IActorRuntime runtime,
        IDynamicRuntimeReadStore readStore,
        IStateStore<ScriptServiceDefinitionState> serviceDefinitionStateStore,
        IIdempotencyPort idempotencyPort,
        IConcurrencyTokenPort concurrencyTokenPort,
        IImageReferenceResolver imageReferenceResolver,
        IScriptComposeSpecValidator composeSpecValidator,
        IScriptComposeReconcilePort composeReconcilePort,
        IAgentBuildPlanPort buildPlanPort,
        IAgentBuildPolicyPort buildPolicyPort,
        IAgentBuildExecutionPort buildExecutionPort,
        IServiceModePolicyPort serviceModePolicyPort,
        IBuildApprovalPort buildApprovalPort,
        IEventEnvelopePublisherPort eventEnvelopePublisherPort,
        IEventEnvelopeSubscriberPort eventEnvelopeSubscriberPort,
        IEventEnvelopeDedupPort eventEnvelopeDedupPort,
        IDynamicScriptExecutionService scriptExecutionService)
    {
        _runtime = runtime;
        _readStore = readStore;
        _serviceDefinitionStateStore = serviceDefinitionStateStore;
        _idempotencyPort = idempotencyPort;
        _concurrencyTokenPort = concurrencyTokenPort;
        _imageReferenceResolver = imageReferenceResolver;
        _composeSpecValidator = composeSpecValidator;
        _composeReconcilePort = composeReconcilePort;
        _buildPlanPort = buildPlanPort;
        _buildPolicyPort = buildPolicyPort;
        _buildExecutionPort = buildExecutionPort;
        _serviceModePolicyPort = serviceModePolicyPort;
        _buildApprovalPort = buildApprovalPort;
        _eventEnvelopePublisherPort = eventEnvelopePublisherPort;
        _eventEnvelopeSubscriberPort = eventEnvelopeSubscriberPort;
        _eventEnvelopeDedupPort = eventEnvelopeDedupPort;
        _scriptExecutionService = scriptExecutionService;
    }

    public async Task<DynamicCommandResult> BuildImageAsync(BuildImageRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("image.build", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        if (string.IsNullOrWhiteSpace(request.ImageName))
            throw new InvalidOperationException("IMAGE_BUILD_INVALID");
        if (string.IsNullOrWhiteSpace(request.SourceBundleDigest))
            throw new InvalidOperationException("IMAGE_BUILD_INVALID");

        var digest = BuildDigest(request.ImageName, request.SourceBundleDigest);
        var tag = string.IsNullOrWhiteSpace(request.Tag) ? "latest" : request.Tag;
        var actorId = await PublishImageInternalAsync(request.ImageName, tag, digest, ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "BUILT", etag);
        await CommitIdempotencyAsync("image.build", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> PublishImageAsync(PublishImageRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("image.publish", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var actorId = await PublishImageInternalAsync(request.ImageName, request.Tag, request.Digest, ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "PUBLISHED", etag);
        await CommitIdempotencyAsync("image.publish", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ApplyComposeAsync(ComposeApplyYamlRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("compose.apply", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var validation = await _composeSpecValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            throw new InvalidOperationException(validation.ErrorCode ?? "COMPOSE_SPEC_INVALID");

        var stackActorId = $"dynamic:stack:{request.StackId}";
        var stackActor = await EnsureActorAsync<ScriptComposeStackGAgent>(stackActorId, ct);
        await PublishActorEventAsync(
            stackActor,
            request.StackId,
            serviceName: "_stack",
            instanceSelector: "stack-controller",
            new ScriptComposeAppliedEvent
        {
            StackId = request.StackId,
            ComposeSpecDigest = request.ComposeSpecDigest,
            DesiredGeneration = request.DesiredGeneration,
        },
            ct);

        foreach (var service in request.Services)
        {
            await ValidateServiceModeAsync(request.StackId, service, ct);

            var serviceActorId = $"dynamic:stack:{request.StackId}:service:{service.ServiceName}";
            var serviceActor = await EnsureActorAsync<ScriptComposeServiceGAgent>(serviceActorId, ct);
            await PublishActorEventAsync(
                serviceActor,
                request.StackId,
                service.ServiceName,
                "all",
                new ScriptComposeServiceScaledEvent
            {
                StackId = request.StackId,
                ServiceName = service.ServiceName,
                ReplicasDesired = service.ReplicasDesired,
                ServiceMode = service.ServiceMode.ToString().ToLowerInvariant(),
                ImageRef = service.ImageRef,
            },
                ct);

            await _readStore.UpsertComposeServiceAsync(new ComposeServiceSnapshot(
                request.StackId,
                service.ServiceName,
                service.ImageRef,
                service.ReplicasDesired,
                service.ReplicasDesired,
                service.ServiceMode,
                request.DesiredGeneration,
                "RolledOut"), ct);

            await EnsureServiceEnvelopeSubscriptionAsync(
                request.StackId,
                service.ServiceName,
                service.ServiceMode,
                request.DesiredGeneration,
                ct);
        }

        var reconcile = await _composeReconcilePort.ReconcileAsync(request.StackId, request.DesiredGeneration, ct);
        if (!reconcile.Converged)
            throw new InvalidOperationException(reconcile.ErrorCode ?? "COMPOSE_GENERATION_CONFLICT");
        await PublishActorEventAsync(
            stackActor,
            request.StackId,
            serviceName: "_stack",
            instanceSelector: "stack-controller",
            new ScriptComposeConvergedEvent
        {
            StackId = request.StackId,
            ObservedGeneration = reconcile.ObservedGeneration,
        },
            ct);

        await _readStore.UpsertStackAsync(new StackSnapshot(
            request.StackId,
            request.ComposeSpecDigest,
            request.ComposeYaml,
            request.DesiredGeneration,
            reconcile.ObservedGeneration,
            "Converged"), ct);

        await _readStore.AppendComposeEventAsync(new ComposeEventSnapshot(
            request.StackId,
            request.DesiredGeneration,
            "ComposeApplied",
            $"services={request.Services.Count}",
            DateTime.UtcNow), ct);

        var etag = await AdvanceVersionAsync(stackActorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(stackActorId, "APPLIED", etag);
        await CommitIdempotencyAsync("compose.apply", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ComposeUpAsync(string stackId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("compose.up", context.IdempotencyKey, new { stackId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var snapshot = await _readStore.GetStackAsync(stackId, ct)
            ?? throw new InvalidOperationException($"Stack '{stackId}' does not exist.");

        var stackActorId = $"dynamic:stack:{stackId}";
        var stackActor = await EnsureActorAsync<ScriptComposeStackGAgent>(stackActorId, ct);
        await PublishActorEventAsync(
            stackActor,
            stackId,
            serviceName: "_stack",
            instanceSelector: "stack-controller",
            new ScriptComposeConvergedEvent
        {
            StackId = stackId,
            ObservedGeneration = snapshot.DesiredGeneration,
        },
            ct);

        await _readStore.UpsertStackAsync(snapshot with { ReconcileStatus = "Converged", ObservedGeneration = snapshot.DesiredGeneration }, ct);
        await _readStore.AppendComposeEventAsync(new ComposeEventSnapshot(stackId, snapshot.DesiredGeneration, "ComposeUp", "stack up requested", DateTime.UtcNow), ct);

        var etag = await AdvanceVersionAsync(stackActorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(stackActorId, "UP", etag);
        await CommitIdempotencyAsync("compose.up", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ComposeDownAsync(string stackId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("compose.down", context.IdempotencyKey, new { stackId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var snapshot = await _readStore.GetStackAsync(stackId, ct)
            ?? throw new InvalidOperationException($"Stack '{stackId}' does not exist.");
        var nextGeneration = snapshot.DesiredGeneration + 1;

        var stackActorId = $"dynamic:stack:{stackId}";
        var services = await _readStore.GetComposeServicesAsync(stackId, ct);
        foreach (var service in services)
        {
            var serviceActorId = $"dynamic:stack:{stackId}:service:{service.ServiceName}";
            var serviceActor = await EnsureActorAsync<ScriptComposeServiceGAgent>(serviceActorId, ct);
            await PublishActorEventAsync(
                serviceActor,
                stackId,
                service.ServiceName,
                "all",
                new ScriptComposeServiceScaledEvent
            {
                StackId = stackId,
                ServiceName = service.ServiceName,
                ReplicasDesired = 0,
                ServiceMode = service.ServiceMode.ToString().ToLowerInvariant(),
                ImageRef = service.ImageRef,
            },
                ct);

            await _readStore.UpsertComposeServiceAsync(service with
            {
                ReplicasDesired = 0,
                ReplicasReady = 0,
                Generation = nextGeneration,
                RolloutStatus = "Stopped",
            }, ct);
        }

        await _readStore.UpsertStackAsync(snapshot with
        {
            DesiredGeneration = nextGeneration,
            ObservedGeneration = nextGeneration,
            ReconcileStatus = "Converged",
        }, ct);

        await _readStore.AppendComposeEventAsync(new ComposeEventSnapshot(stackId, nextGeneration, "ComposeDown", "stack down requested", DateTime.UtcNow), ct);
        var etag = await AdvanceVersionAsync(stackActorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(stackActorId, "DOWN", etag);
        await CommitIdempotencyAsync("compose.down", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ScaleComposeServiceAsync(ComposeServiceScaleRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("compose.service.scale", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var serviceSnapshot = await GetComposeServiceAsync(request.StackId, request.ServiceName, ct);
        await ValidateServiceModeAsync(request.StackId, new ComposeServiceSpec(serviceSnapshot.ServiceName, serviceSnapshot.ImageRef, request.ReplicasDesired, serviceSnapshot.ServiceMode), ct);

        var serviceActorId = $"dynamic:stack:{request.StackId}:service:{request.ServiceName}";
        var serviceActor = await EnsureActorAsync<ScriptComposeServiceGAgent>(serviceActorId, ct);
        await PublishActorEventAsync(
            serviceActor,
            request.StackId,
            request.ServiceName,
            "all",
            new ScriptComposeServiceScaledEvent
        {
            StackId = request.StackId,
            ServiceName = request.ServiceName,
            ReplicasDesired = request.ReplicasDesired,
            ServiceMode = serviceSnapshot.ServiceMode.ToString().ToLowerInvariant(),
            ImageRef = serviceSnapshot.ImageRef,
        },
            ct);

        await _readStore.UpsertComposeServiceAsync(serviceSnapshot with
        {
            ReplicasDesired = request.ReplicasDesired,
            ReplicasReady = request.ReplicasDesired,
            Generation = serviceSnapshot.Generation + 1,
            RolloutStatus = "Scaled",
        }, ct);

        await _readStore.AppendComposeEventAsync(new ComposeEventSnapshot(
            request.StackId,
            serviceSnapshot.Generation + 1,
            "ComposeServiceScaled",
            $"service={request.ServiceName},replicas={request.ReplicasDesired}",
            DateTime.UtcNow), ct);

        var etag = await AdvanceVersionAsync(serviceActorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(serviceActorId, "SCALED", etag);
        await CommitIdempotencyAsync("compose.service.scale", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> RolloutComposeServiceAsync(ComposeServiceRolloutRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("compose.service.rollout", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var serviceSnapshot = await GetComposeServiceAsync(request.StackId, request.ServiceName, ct);
        var resolvedImageDigest = await ResolveImageDigestAsync($"{request.StackId}/{request.ServiceName}", request.ImageRef, ct);
        var nextGeneration = serviceSnapshot.Generation + 1;

        var serviceActorId = $"dynamic:stack:{request.StackId}:service:{request.ServiceName}";
        var serviceActor = await EnsureActorAsync<ScriptComposeServiceGAgent>(serviceActorId, ct);
        await PublishActorEventAsync(
            serviceActor,
            request.StackId,
            request.ServiceName,
            "all",
            new ScriptComposeServiceRolledOutEvent
        {
            StackId = request.StackId,
            ServiceName = request.ServiceName,
            ImageRef = resolvedImageDigest,
            Generation = nextGeneration,
        },
            ct);

        await _readStore.UpsertComposeServiceAsync(serviceSnapshot with
        {
            ImageRef = resolvedImageDigest,
            Generation = nextGeneration,
            RolloutStatus = "RolledOut",
        }, ct);

        await _readStore.AppendComposeEventAsync(new ComposeEventSnapshot(
            request.StackId,
            nextGeneration,
            "ComposeServiceRolledOut",
            $"service={request.ServiceName},image={resolvedImageDigest}",
            DateTime.UtcNow), ct);

        var etag = await AdvanceVersionAsync(serviceActorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(serviceActorId, "ROLLED_OUT", etag);
        await CommitIdempotencyAsync("compose.service.rollout", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> RegisterServiceAsync(RegisterServiceDefinitionRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("service.register", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        ValidateServiceRequest(request.ServiceId, request.Version, request.ScriptCode, request.EntrypointType);

        var updatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var actorId = $"dynamic:service:{request.ServiceId}";
        var actor = await EnsureActorAsync<ScriptServiceDefinitionGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            stackId: "_services",
            serviceName: request.ServiceId,
            instanceSelector: "definition",
            new ScriptServiceRegisteredEvent
        {
            ServiceId = request.ServiceId,
            Version = request.Version,
            ScriptCode = request.ScriptCode,
            EntrypointType = request.EntrypointType,
            ServiceMode = request.ServiceMode.ToString().ToLowerInvariant(),
            CapabilitiesHash = request.CapabilitiesHash,
            UpdatedAtUnixMs = updatedAtUnixMs,
        },
            request.PublicEndpoints,
            request.EventSubscriptions,
            ct);

        var state = new ScriptServiceDefinitionState
        {
            ServiceId = request.ServiceId,
            Version = request.Version,
            Status = DynamicServiceStatus.Inactive.ToString(),
            ScriptCode = request.ScriptCode,
            EntrypointType = request.EntrypointType,
            ServiceMode = request.ServiceMode.ToString().ToLowerInvariant(),
            CapabilitiesHash = request.CapabilitiesHash,
            UpdatedAtUnixMs = updatedAtUnixMs,
        };
        state.PublicEndpoints.AddRange(request.PublicEndpoints);
        state.EventSubscriptions.AddRange(request.EventSubscriptions);
        await _serviceDefinitionStateStore.SaveAsync(actorId, state, ct);

        await _readStore.UpsertServiceDefinitionAsync(ToServiceSnapshot(state), ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "REGISTERED", etag);
        await CommitIdempotencyAsync("service.register", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> UpdateServiceAsync(UpdateServiceDefinitionRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("service.update", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        ValidateServiceRequest(request.ServiceId, request.Version, request.ScriptCode, request.EntrypointType);

        var updatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var actorId = $"dynamic:service:{request.ServiceId}";
        var actor = await EnsureActorAsync<ScriptServiceDefinitionGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            stackId: "_services",
            serviceName: request.ServiceId,
            instanceSelector: "definition",
            new ScriptServiceUpdatedEvent
        {
            ServiceId = request.ServiceId,
            Version = request.Version,
            ScriptCode = request.ScriptCode,
            EntrypointType = request.EntrypointType,
            ServiceMode = request.ServiceMode.ToString().ToLowerInvariant(),
            CapabilitiesHash = request.CapabilitiesHash,
            UpdatedAtUnixMs = updatedAtUnixMs,
        },
            request.PublicEndpoints,
            request.EventSubscriptions,
            ct);

        var existingState = await _serviceDefinitionStateStore.LoadAsync(actorId, ct);
        var nextState = existingState?.Clone() ?? new ScriptServiceDefinitionState();
        nextState.ServiceId = request.ServiceId;
        nextState.Version = request.Version;
        nextState.ScriptCode = request.ScriptCode;
        nextState.EntrypointType = request.EntrypointType;
        nextState.ServiceMode = request.ServiceMode.ToString().ToLowerInvariant();
        nextState.CapabilitiesHash = request.CapabilitiesHash;
        nextState.UpdatedAtUnixMs = updatedAtUnixMs;
        if (string.IsNullOrWhiteSpace(nextState.Status))
            nextState.Status = DynamicServiceStatus.Inactive.ToString();
        nextState.PublicEndpoints.Clear();
        nextState.PublicEndpoints.AddRange(request.PublicEndpoints);
        nextState.EventSubscriptions.Clear();
        nextState.EventSubscriptions.AddRange(request.EventSubscriptions);
        await _serviceDefinitionStateStore.SaveAsync(actorId, nextState, ct);

        await _readStore.UpsertServiceDefinitionAsync(ToServiceSnapshot(nextState), ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "UPDATED", etag);
        await CommitIdempotencyAsync("service.update", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ActivateServiceAsync(string serviceId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("service.activate", context.IdempotencyKey, new { serviceId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var result = await SetServiceStatusAsync(serviceId, DynamicServiceStatus.Active, context, ct);
        await CommitIdempotencyAsync("service.activate", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> DeactivateServiceAsync(string serviceId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("service.deactivate", context.IdempotencyKey, new { serviceId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var result = await SetServiceStatusAsync(serviceId, DynamicServiceStatus.Inactive, context, ct);
        await CommitIdempotencyAsync("service.deactivate", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> CreateContainerAsync(CreateContainerRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("container.create", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        var resolvedImageDigest = await ResolveImageDigestAsync(request.ServiceId, request.ImageDigest, ct);

        var actorId = $"dynamic:container:{request.ContainerId}";
        var actor = await EnsureActorAsync<ScriptContainerGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            request.StackId,
            request.ServiceName,
            request.ContainerId,
            new ScriptContainerCreatedEvent
        {
            ContainerId = request.ContainerId,
            StackId = request.StackId,
            ServiceName = request.ServiceName,
            ImageDigest = resolvedImageDigest,
            RoleActorId = request.RoleActorId,
        },
            ct);

        await _readStore.UpsertContainerAsync(new ContainerSnapshot(
            request.ContainerId,
            request.StackId,
            request.ServiceName,
            request.ServiceId,
            resolvedImageDigest,
            "Created",
            request.RoleActorId), ct);

        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "CREATED", etag);
        await CommitIdempotencyAsync("container.create", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> StartContainerAsync(string containerId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("container.start", context.IdempotencyKey, new { containerId }, ct);
        if (replay != null)
            return replay;
        var snapshot = await _readStore.GetContainerAsync(containerId, ct)
            ?? throw new InvalidOperationException($"Container '{containerId}' does not exist.");
        var serviceState = await GetServiceStateAsync(snapshot.ServiceId, ct);

        var actorId = $"dynamic:container:{containerId}";
        var actor = await EnsureActorAsync<ScriptContainerGAgent>(actorId, ct);
        await PublishActorEventAsync(actor, snapshot.StackId, snapshot.ServiceName, snapshot.ContainerId, new ScriptContainerStartedEvent { ContainerId = containerId }, ct);

        var roleActor = await EnsureActorAsync<ScriptRoleContainerAgent>(snapshot.RoleActorId, ct);
        await PublishActorEventAsync(
            roleActor,
            snapshot.StackId,
            snapshot.ServiceName,
            snapshot.ContainerId,
            new ConfigureScriptRoleCapabilitiesEvent
            {
                ServiceId = serviceState.ServiceId,
                Version = serviceState.Version,
                ImageDigest = snapshot.ImageDigest,
                EntrypointType = serviceState.EntrypointType,
                CapabilitiesHash = serviceState.CapabilitiesHash,
            },
            ct);

        await EnsureServiceEnvelopeSubscriptionAsync(
            snapshot.StackId,
            snapshot.ServiceName,
            ParseServiceMode(serviceState.ServiceMode),
            generation: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ct);

        await _readStore.UpsertContainerAsync(snapshot with { Status = "Running" }, ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "RUNNING", etag);
        await CommitIdempotencyAsync("container.start", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> StopContainerAsync(string containerId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("container.stop", context.IdempotencyKey, new { containerId }, ct);
        if (replay != null)
            return replay;
        var snapshot = await _readStore.GetContainerAsync(containerId, ct)
            ?? throw new InvalidOperationException($"Container '{containerId}' does not exist.");

        var actorId = $"dynamic:container:{containerId}";
        var actor = await EnsureActorAsync<ScriptContainerGAgent>(actorId, ct);
        await PublishActorEventAsync(actor, snapshot.StackId, snapshot.ServiceName, snapshot.ContainerId, new ScriptContainerStoppedEvent { ContainerId = containerId }, ct);

        await _readStore.UpsertContainerAsync(snapshot with { Status = "Stopped" }, ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "STOPPED", etag);
        await CommitIdempotencyAsync("container.stop", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> DestroyContainerAsync(string containerId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("container.destroy", context.IdempotencyKey, new { containerId }, ct);
        if (replay != null)
            return replay;
        var snapshot = await _readStore.GetContainerAsync(containerId, ct)
            ?? throw new InvalidOperationException($"Container '{containerId}' does not exist.");

        var actorId = $"dynamic:container:{containerId}";
        var actor = await EnsureActorAsync<ScriptContainerGAgent>(actorId, ct);
        await PublishActorEventAsync(actor, snapshot.StackId, snapshot.ServiceName, snapshot.ContainerId, new ScriptContainerDestroyedEvent { ContainerId = containerId }, ct);

        await _readStore.UpsertContainerAsync(snapshot with { Status = "Destroyed" }, ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "DESTROYED", etag);
        await CommitIdempotencyAsync("container.destroy", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ExecuteContainerAsync(ExecuteContainerRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("container.exec", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;

        var container = await _readStore.GetContainerAsync(request.ContainerId, ct)
            ?? throw new InvalidOperationException($"Container '{request.ContainerId}' does not exist.");
        if (!string.Equals(container.Status, "Running", StringComparison.Ordinal))
            throw new InvalidOperationException("CONTAINER_STATE_CONFLICT");

        var service = await GetServiceStateAsync(request.ServiceId, ct);
        if (!string.Equals(service.Status, DynamicServiceStatus.Active.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException("SERVICE_MODE_CONFLICT");

        var runId = string.IsNullOrWhiteSpace(request.RunId) ? Guid.NewGuid().ToString("N") : request.RunId;
        var runActorId = $"dynamic:run:{runId}";
        var runActor = await EnsureActorAsync<ScriptRunGAgent>(runActorId, ct);
        await PublishActorEventAsync(
            runActor,
            container.StackId,
            container.ServiceName,
            container.ContainerId,
            new ScriptRunStartedEvent
        {
            RunId = runId,
            ContainerId = request.ContainerId,
        },
            ct);

        await _readStore.UpsertRunAsync(new RunSnapshot(runId, request.ContainerId, request.ServiceId, "Running", string.Empty, string.Empty, string.Empty), ct);

        var scriptInput = BuildScriptInput(
            request.Input ?? ScriptRoleRequest.FromText(string.Empty),
            runId,
            request.ContainerId,
            request.ServiceId,
            container.StackId,
            container.ServiceName);

        var scriptResult = await _scriptExecutionService.ExecuteAsync(
            new DynamicScriptExecutionRequest(service.ScriptCode, scriptInput, service.EntrypointType),
            ct);

        RunSnapshot finalSnapshot;
        string status;

        if (scriptResult.Success)
        {
            await PublishActorEventAsync(
                runActor,
                container.StackId,
                container.ServiceName,
                container.ContainerId,
                new ScriptRunCompletedEvent
            {
                RunId = runId,
                Result = scriptResult.Output,
            },
                ct);

            finalSnapshot = new RunSnapshot(runId, request.ContainerId, request.ServiceId, "Succeeded", scriptResult.Output, string.Empty, string.Empty);
            status = "SUCCEEDED";
        }
        else
        {
            await PublishActorEventAsync(
                runActor,
                container.StackId,
                container.ServiceName,
                container.ContainerId,
                new ScriptRunFailedEvent
            {
                RunId = runId,
                Error = scriptResult.Error ?? "Unknown script failure.",
            },
                ct);

            finalSnapshot = new RunSnapshot(runId, request.ContainerId, request.ServiceId, "Failed", string.Empty, scriptResult.Error ?? "Unknown script failure.", string.Empty);
            status = "FAILED";
        }

        await _readStore.UpsertRunAsync(finalSnapshot, ct);
        var etag = await AdvanceVersionAsync(runActorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(runActorId, status, etag);
        await CommitIdempotencyAsync("container.exec", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> CancelRunAsync(string runId, string reason, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("run.cancel", context.IdempotencyKey, new { runId, reason }, ct);
        if (replay != null)
            return replay;
        var snapshot = await _readStore.GetRunAsync(runId, ct)
            ?? throw new InvalidOperationException($"Run '{runId}' does not exist.");
        if (IsRunTerminal(snapshot.Status))
            throw new InvalidOperationException("RUN_ALREADY_TERMINAL");

        var actorId = $"dynamic:run:{runId}";
        var actor = await EnsureActorAsync<ScriptRunGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            stackId: "_runs",
            serviceName: snapshot.ServiceId,
            instanceSelector: snapshot.ContainerId,
            new ScriptRunCanceledEvent
        {
            RunId = runId,
            Reason = reason ?? string.Empty,
        },
            ct);

        await _readStore.UpsertRunAsync(snapshot with
        {
            Status = "Canceled",
            CancellationReason = reason ?? string.Empty,
            Error = string.Empty,
            Result = string.Empty,
        }, ct);

        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "CANCELED", etag);
        await CommitIdempotencyAsync("run.cancel", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> SubmitBuildPlanAsync(SubmitBuildPlanRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("build.plan", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        var plan = await _buildPlanPort.PlanAsync(new BuildPlanRequest(request.BuildJobId, request.StackId, request.ServiceName, request.SourceBundleDigest), ct);
        if (!plan.Accepted)
            throw new InvalidOperationException(plan.ErrorCode ?? "BUILD_POLICY_REJECTED");

        var actorId = $"dynamic:build:{request.BuildJobId}";
        var actor = await EnsureActorAsync<ScriptBuildJobGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            request.StackId,
            request.ServiceName,
            request.BuildJobId,
            new ScriptBuildPlanSubmittedEvent
        {
            BuildJobId = request.BuildJobId,
            StackId = request.StackId,
            ServiceName = request.ServiceName,
            SourceBundleDigest = request.SourceBundleDigest,
        },
            ct);

        await _readStore.UpsertBuildJobAsync(new BuildJobSnapshot(
            request.BuildJobId,
            request.StackId,
            request.ServiceName,
            request.SourceBundleDigest,
            plan.BuildPlanDigest,
            "Planned",
            string.Empty,
            "Planned",
            RequiresManualApproval: false,
            request.RequestedByAgentId), ct);

        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "PLANNED", etag);
        await CommitIdempotencyAsync("build.plan", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ValidateBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("build.validate", context.IdempotencyKey, new { buildJobId }, ct);
        if (replay != null)
            return replay;
        var build = await _readStore.GetBuildJobAsync(buildJobId, ct)
            ?? throw new InvalidOperationException($"Build job '{buildJobId}' does not exist.");
        var policyDecision = await _buildPolicyPort.ValidateAsync(new BuildPolicyRequest(
            build.BuildJobId,
            build.StackId,
            build.ServiceName,
            build.SourceBundleDigest,
            build.BuildPlanDigest), ct);
        if (!policyDecision.Allowed)
        {
            throw new InvalidOperationException(policyDecision.ErrorCode ?? "BUILD_POLICY_REJECTED");
        }

        var actorId = $"dynamic:build:{buildJobId}";
        var actor = await EnsureActorAsync<ScriptBuildJobGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            build.StackId,
            build.ServiceName,
            build.BuildJobId,
            new ScriptBuildPolicyValidatedEvent
            {
                BuildJobId = build.BuildJobId,
                PolicyDecision = policyDecision.PolicyDecision,
                RequiresManualApproval = policyDecision.RequiresManualApproval,
            },
            ct);

        var status = policyDecision.RequiresManualApproval ? "ApprovalRequired" : "Validated";
        await _readStore.UpsertBuildJobAsync(build with
        {
            PolicyDecision = policyDecision.PolicyDecision,
            RequiresManualApproval = policyDecision.RequiresManualApproval,
            Status = status,
        }, ct);

        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, status.ToUpperInvariant(), etag);
        await CommitIdempotencyAsync("build.validate", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ApproveBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("build.approve", context.IdempotencyKey, new { buildJobId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var build = await _readStore.GetBuildJobAsync(buildJobId, ct)
            ?? throw new InvalidOperationException($"Build job '{buildJobId}' does not exist.");
        var approval = await _buildApprovalPort.DecideAsync(new BuildApprovalRequest(build.BuildJobId, build.StackId, build.ServiceName, build.SourceBundleDigest), ct);
        if (!approval.Approved)
            throw new InvalidOperationException(approval.Reason ?? "BUILD_APPROVAL_REQUIRED");

        var actorId = $"dynamic:build:{buildJobId}";
        var actor = await EnsureActorAsync<ScriptBuildJobGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            build.StackId,
            build.ServiceName,
            build.BuildJobId,
            new ScriptBuildApprovedEvent
        {
            BuildJobId = buildJobId,
        },
            ct);

        await _readStore.UpsertBuildJobAsync(build with
        {
            RequiresManualApproval = approval.RequiresManualApproval,
            Status = "Approved",
        }, ct);

        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "APPROVED", etag);
        await CommitIdempotencyAsync("build.approve", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ExecuteBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("build.execute", context.IdempotencyKey, new { buildJobId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);

        var build = await _readStore.GetBuildJobAsync(buildJobId, ct)
            ?? throw new InvalidOperationException($"Build job '{buildJobId}' does not exist.");
        if (!string.Equals(build.Status, "Approved", StringComparison.OrdinalIgnoreCase) &&
            !(string.Equals(build.Status, "Validated", StringComparison.OrdinalIgnoreCase) && !build.RequiresManualApproval))
            throw new InvalidOperationException("BUILD_APPROVAL_REQUIRED");

        var executionResult = await _buildExecutionPort.ExecuteAsync(new BuildExecutionRequest(
            build.BuildJobId,
            $"{build.StackId}/{build.ServiceName}",
            build.SourceBundleDigest,
            build.BuildPlanDigest), ct);
        if (!executionResult.Succeeded)
            throw new InvalidOperationException(executionResult.ErrorCode ?? "BUILD_POLICY_REJECTED");

        var publishResult = await PublishBuildResultInternalAsync(new PublishBuildResultRequest(buildJobId, executionResult.ResultImageDigest), context.IfMatch, ct);
        await TryDeployBuildResultToComposeAsync(build.StackId, build.ServiceName, executionResult.ResultImageDigest, ct);
        var updated = await _readStore.GetBuildJobAsync(buildJobId, ct)
            ?? throw new InvalidOperationException($"Build job '{buildJobId}' does not exist.");
        await _readStore.UpsertBuildJobAsync(updated with { Status = "Executed" }, ct);

        var actorId = $"dynamic:build:{buildJobId}";
        var result = new DynamicCommandResult(actorId, "EXECUTED", publishResult.ETag);
        await CommitIdempotencyAsync("build.execute", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> RollbackBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("build.rollback", context.IdempotencyKey, new { buildJobId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var build = await _readStore.GetBuildJobAsync(buildJobId, ct)
            ?? throw new InvalidOperationException($"Build job '{buildJobId}' does not exist.");
        var actorId = $"dynamic:build:{buildJobId}";
        var actor = await EnsureActorAsync<ScriptBuildJobGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            build.StackId,
            build.ServiceName,
            build.BuildJobId,
            new ScriptBuildRolledBackEvent { BuildJobId = buildJobId },
            ct);
        await _readStore.UpsertBuildJobAsync(build with { Status = "RolledBack" }, ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "ROLLEDBACK", etag);
        await CommitIdempotencyAsync("build.rollback", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> PublishBuildResultAsync(PublishBuildResultRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("build.publish", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var result = await PublishBuildResultInternalAsync(request, context.IfMatch, ct);
        await CommitIdempotencyAsync("build.publish", context.IdempotencyKey, result, ct);
        return result;
    }

    public Task<ImageSnapshot?> GetImageAsync(string imageName, CancellationToken ct = default) => _readStore.GetImageAsync(imageName, ct);
    public Task<StackSnapshot?> GetStackAsync(string stackId, CancellationToken ct = default) => _readStore.GetStackAsync(stackId, ct);
    public Task<IReadOnlyList<ComposeServiceSnapshot>> GetComposeServicesAsync(string stackId, CancellationToken ct = default) => _readStore.GetComposeServicesAsync(stackId, ct);
    public Task<IReadOnlyList<ComposeEventSnapshot>> GetComposeEventsAsync(string stackId, CancellationToken ct = default) => _readStore.GetComposeEventsAsync(stackId, ct);
    public Task<ServiceDefinitionSnapshot?> GetServiceDefinitionAsync(string serviceId, CancellationToken ct = default) => _readStore.GetServiceDefinitionAsync(serviceId, ct);
    public Task<ContainerSnapshot?> GetContainerAsync(string containerId, CancellationToken ct = default) => _readStore.GetContainerAsync(containerId, ct);
    public Task<IReadOnlyList<RunSnapshot>> GetContainerRunsAsync(string containerId, CancellationToken ct = default) => _readStore.GetContainerRunsAsync(containerId, ct);
    public Task<RunSnapshot?> GetRunAsync(string runId, CancellationToken ct = default) => _readStore.GetRunAsync(runId, ct);
    public Task<BuildJobSnapshot?> GetBuildJobAsync(string buildJobId, CancellationToken ct = default) => _readStore.GetBuildJobAsync(buildJobId, ct);
    public Task<IReadOnlyList<BuildJobSnapshot>> GetBuildJobsAsync(CancellationToken ct = default) => _readStore.GetBuildJobsAsync(ct);

    public async Task<ImageTagSnapshot?> GetImageTagAsync(string imageName, string tag, CancellationToken ct = default)
    {
        var image = await _readStore.GetImageAsync(imageName, ct);
        if (image == null || !image.Tags.TryGetValue(tag, out var digest))
            return null;
        return new ImageTagSnapshot(imageName, tag, digest);
    }

    public async Task<ImageDigestSnapshot?> GetImageDigestAsync(string imageName, string digest, CancellationToken ct = default)
    {
        var image = await _readStore.GetImageAsync(imageName, ct);
        if (image == null)
            return null;
        return new ImageDigestSnapshot(imageName, digest, image.Digests.Contains(digest, StringComparer.Ordinal));
    }

    private async Task<DynamicCommandResult> SetServiceStatusAsync(string serviceId, DynamicServiceStatus status, DynamicCommandContext context, CancellationToken ct)
    {
        var actorId = $"dynamic:service:{serviceId}";
        var actor = await EnsureActorAsync<ScriptServiceDefinitionGAgent>(actorId, ct);
        var updatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (status == DynamicServiceStatus.Active)
        {
            await PublishActorEventAsync(
                actor,
                stackId: "_services",
                serviceName: serviceId,
                instanceSelector: "definition",
                new ScriptServiceActivatedEvent
                {
                    ServiceId = serviceId,
                    UpdatedAtUnixMs = updatedAtUnixMs,
                },
                ct);
        }
        else
        {
            await PublishActorEventAsync(
                actor,
                stackId: "_services",
                serviceName: serviceId,
                instanceSelector: "definition",
                new ScriptServiceDeactivatedEvent
                {
                    ServiceId = serviceId,
                    UpdatedAtUnixMs = updatedAtUnixMs,
                },
                ct);
        }

        var existing = await _serviceDefinitionStateStore.LoadAsync(actorId, ct)
            ?? throw new InvalidOperationException($"Service '{serviceId}' does not exist.");
        existing.Status = status.ToString();
        existing.UpdatedAtUnixMs = updatedAtUnixMs;
        await _serviceDefinitionStateStore.SaveAsync(actorId, existing, ct);

        var snapshot = ToServiceSnapshot(existing);
        await _readStore.UpsertServiceDefinitionAsync(snapshot, ct);
        if (status == DynamicServiceStatus.Active)
        {
            await EnsureServiceEnvelopeSubscriptionAsync(
                stackId: "_services",
                serviceName: serviceId,
                serviceMode: snapshot.ServiceMode,
                generation: updatedAtUnixMs,
                ct);
        }

        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        return new DynamicCommandResult(actorId, status == DynamicServiceStatus.Active ? "ACTIVE" : "INACTIVE", etag);
    }

    private async Task<ScriptServiceDefinitionState> GetServiceStateAsync(string serviceId, CancellationToken ct)
    {
        var actorId = $"dynamic:service:{serviceId}";
        var state = await _serviceDefinitionStateStore.LoadAsync(actorId, ct);
        if (state != null)
            return state;

        var snapshot = await _readStore.GetServiceDefinitionAsync(serviceId, ct)
            ?? throw new InvalidOperationException($"Service '{serviceId}' does not exist.");
        return new ScriptServiceDefinitionState
        {
            ServiceId = snapshot.ServiceId,
            Version = snapshot.Version,
            Status = snapshot.Status.ToString(),
            ScriptCode = snapshot.ScriptCode,
            EntrypointType = snapshot.EntrypointType,
            ServiceMode = snapshot.ServiceMode.ToString().ToLowerInvariant(),
            CapabilitiesHash = snapshot.CapabilitiesHash,
            UpdatedAtUnixMs = new DateTimeOffset(snapshot.UpdatedAtUtc).ToUnixTimeMilliseconds(),
        };
    }

    private async Task<ComposeServiceSnapshot> GetComposeServiceAsync(string stackId, string serviceName, CancellationToken ct)
    {
        var services = await _readStore.GetComposeServicesAsync(stackId, ct);
        var service = services.FirstOrDefault(item => string.Equals(item.ServiceName, serviceName, StringComparison.Ordinal));
        return service ?? throw new InvalidOperationException($"Service '{serviceName}' on stack '{stackId}' does not exist.");
    }

    private static ServiceDefinitionSnapshot ToServiceSnapshot(ScriptServiceDefinitionState state)
    {
        var status = System.Enum.TryParse<DynamicServiceStatus>(state.Status, true, out var parsedStatus)
            ? parsedStatus
            : DynamicServiceStatus.Inactive;
        var mode = ParseServiceMode(state.ServiceMode);
        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(state.UpdatedAtUnixMs == 0
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            : state.UpdatedAtUnixMs).UtcDateTime;
        return new ServiceDefinitionSnapshot(
            state.ServiceId,
            state.Version,
            status,
            state.ScriptCode,
            state.EntrypointType,
            mode,
            [.. state.PublicEndpoints],
            [.. state.EventSubscriptions],
            state.CapabilitiesHash,
            updatedAt);
    }

    private static DynamicServiceMode ParseServiceMode(string? value)
    {
        if (string.Equals(value, "daemon", StringComparison.OrdinalIgnoreCase))
            return DynamicServiceMode.Daemon;
        if (string.Equals(value, "event", StringComparison.OrdinalIgnoreCase))
            return DynamicServiceMode.Event;
        return DynamicServiceMode.Hybrid;
    }

    private async Task<IActor> EnsureActorAsync<TAgent>(string actorId, CancellationToken ct) where TAgent : IAgent
    {
        var existing = await _runtime.GetAsync(actorId);
        if (existing != null)
            return existing;

        return await _runtime.CreateAsync<TAgent>(actorId, ct);
    }

    private async Task<string> PublishImageInternalAsync(string imageName, string tag, string digest, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imageName))
            throw new InvalidOperationException("IMAGE_NOT_PUBLISHED");
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidOperationException("IMAGE_TAG_CONFLICT");
        if (string.IsNullOrWhiteSpace(digest))
            throw new InvalidOperationException("IMAGE_NOT_PUBLISHED");

        var actorId = $"dynamic:image:{imageName}";
        var actor = await EnsureActorAsync<ScriptImageCatalogGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            stackId: "_images",
            serviceName: imageName,
            instanceSelector: tag,
            new ScriptImagePublishedEvent
            {
                ImageName = imageName,
                Tag = tag,
                Digest = digest,
            },
            ct);

        var existing = await _readStore.GetImageAsync(imageName, ct);
        var tags = existing?.Tags is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(existing.Tags, StringComparer.Ordinal);
        tags[tag] = digest;

        var digests = existing?.Digests is null
            ? new List<string>()
            : [.. existing.Digests];
        if (!digests.Contains(digest, StringComparer.Ordinal))
            digests.Add(digest);

        await _readStore.UpsertImageAsync(new ImageSnapshot(imageName, tags, digests), ct);
        return actorId;
    }

    private async Task ValidateServiceModeAsync(string stackId, ComposeServiceSpec service, CancellationToken ct)
    {
        var decision = await _serviceModePolicyPort.ValidateAsync(new ServiceModePolicyRequest(
            stackId,
            service.ServiceName,
            service.ServiceMode,
            service.ReplicasDesired), ct);
        if (!decision.Allowed)
            throw new InvalidOperationException(decision.Reason ?? "SERVICE_MODE_CONFLICT");
    }

    private async Task<string> ResolveImageDigestAsync(string imageName, string imageRef, CancellationToken ct)
    {
        var resolved = await _imageReferenceResolver.ResolveAsync(imageName, imageRef, ct);
        if (!resolved.Found || string.IsNullOrWhiteSpace(resolved.Digest))
            throw new InvalidOperationException(resolved.ErrorCode ?? "IMAGE_NOT_PUBLISHED");
        if (!resolved.Digest.StartsWith("sha256:", StringComparison.Ordinal))
            throw new InvalidOperationException("IMAGE_NOT_PUBLISHED");
        return resolved.Digest;
    }

    private async Task EnsureServiceEnvelopeSubscriptionAsync(
        string stackId,
        string serviceName,
        DynamicServiceMode serviceMode,
        long generation,
        CancellationToken ct)
    {
        if (serviceMode == DynamicServiceMode.Daemon)
            return;

        var leaseId = $"{stackId}:{serviceName}:gen:{generation}";
        var result = await _eventEnvelopeSubscriberPort.SubscribeAsync(
            new EnvelopeSubscribeRequest(
                stackId,
                serviceName,
                $"dynamic-runtime.subscriber.{serviceName}",
                leaseId,
                MaxInFlight: 64),
            ct);
        if (!result.Success)
            throw new InvalidOperationException(result.ErrorCode ?? "ENVELOPE_LEASE_INVALID");
    }

    private async Task TryDeployBuildResultToComposeAsync(
        string stackId,
        string serviceName,
        string imageDigest,
        CancellationToken ct)
    {
        var services = await _readStore.GetComposeServicesAsync(stackId, ct);
        var target = services.FirstOrDefault(item => string.Equals(item.ServiceName, serviceName, StringComparison.Ordinal));
        if (target == null)
            return;

        var nextGeneration = target.Generation + 1;
        var actorId = $"dynamic:stack:{stackId}:service:{serviceName}";
        var actor = await EnsureActorAsync<ScriptComposeServiceGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            stackId,
            serviceName,
            "all",
            new ScriptComposeServiceRolledOutEvent
            {
                StackId = stackId,
                ServiceName = serviceName,
                ImageRef = imageDigest,
                Generation = nextGeneration,
            },
            ct);

        await _readStore.UpsertComposeServiceAsync(target with
        {
            ImageRef = imageDigest,
            Generation = nextGeneration,
            RolloutStatus = "RolledOut",
        }, ct);

        await _readStore.AppendComposeEventAsync(new ComposeEventSnapshot(
            stackId,
            nextGeneration,
            "ComposeServiceRolledOut",
            $"service={serviceName},image={imageDigest}",
            DateTime.UtcNow), ct);

        await EnsureServiceEnvelopeSubscriptionAsync(stackId, serviceName, target.ServiceMode, nextGeneration, ct);
    }

    private async Task<DynamicCommandResult> PublishBuildResultInternalAsync(PublishBuildResultRequest request, string? ifMatch, CancellationToken ct)
    {
        var actorId = $"dynamic:build:{request.BuildJobId}";
        var actor = await EnsureActorAsync<ScriptBuildJobGAgent>(actorId, ct);
        var existing = await _readStore.GetBuildJobAsync(request.BuildJobId, ct)
            ?? throw new InvalidOperationException($"Build job '{request.BuildJobId}' does not exist.");

        await PublishActorEventAsync(
            actor,
            existing.StackId,
            existing.ServiceName,
            existing.BuildJobId,
            new ScriptBuildPublishedEvent
            {
                BuildJobId = request.BuildJobId,
                ResultImageDigest = request.ResultImageDigest,
            },
            ct);

        await _readStore.UpsertBuildJobAsync(existing with
        {
            ResultImageDigest = request.ResultImageDigest,
            Status = "Published",
        }, ct);

        var imageName = $"{existing.StackId}/{existing.ServiceName}";
        await PublishImageInternalAsync(imageName, "latest", request.ResultImageDigest, ct);
        await PublishImageInternalAsync(imageName, request.BuildJobId, request.ResultImageDigest, ct);

        var etag = await AdvanceVersionAsync(actorId, ifMatch, ct);
        return new DynamicCommandResult(actorId, "PUBLISHED", etag);
    }

    private async Task<string> AdvanceVersionAsync(string aggregateId, string? expectedVersion, CancellationToken ct)
    {
        var check = await _concurrencyTokenPort.CheckAndAdvanceAsync(aggregateId, expectedVersion, ct);
        if (!check.Passed)
            throw new InvalidOperationException(check.ErrorCode ?? "VERSION_CONFLICT");
        return check.NextVersion;
    }

    private async Task PublishActorEventAsync(
        IActor actor,
        string stackId,
        string serviceName,
        string instanceSelector,
        Google.Protobuf.IMessage payload,
        CancellationToken ct)
    {
        var envelope = CreateEnvelope(stackId, serviceName, instanceSelector, payload);
        var dedup = await _eventEnvelopeDedupPort.CheckAndRecordAsync(
            scope: actor.Id,
            dedupKey: envelope.Metadata["dedup_key"],
            ttl: EnvelopeDedupTtl,
            ct);
        if (!dedup.Allowed)
            throw new InvalidOperationException(dedup.ErrorCode ?? "ENVELOPE_DUPLICATE");

        await actor.HandleEventAsync(envelope, ct);
        await _eventEnvelopePublisherPort.PublishAsync(
            new ScriptEventEnvelope(envelope.Id, stackId, serviceName, instanceSelector, envelope),
            ct);
    }

    private async Task PublishActorEventAsync(
        IActor actor,
        string stackId,
        string serviceName,
        string instanceSelector,
        ScriptServiceRegisteredEvent payload,
        IEnumerable<string> publicEndpoints,
        IEnumerable<string> eventSubscriptions,
        CancellationToken ct)
    {
        payload.PublicEndpoints.AddRange(publicEndpoints);
        payload.EventSubscriptions.AddRange(eventSubscriptions);
        await PublishActorEventAsync(actor, stackId, serviceName, instanceSelector, (Google.Protobuf.IMessage)payload, ct);
    }

    private async Task PublishActorEventAsync(
        IActor actor,
        string stackId,
        string serviceName,
        string instanceSelector,
        ScriptServiceUpdatedEvent payload,
        IEnumerable<string> publicEndpoints,
        IEnumerable<string> eventSubscriptions,
        CancellationToken ct)
    {
        payload.PublicEndpoints.AddRange(publicEndpoints);
        payload.EventSubscriptions.AddRange(eventSubscriptions);
        await PublishActorEventAsync(actor, stackId, serviceName, instanceSelector, (Google.Protobuf.IMessage)payload, ct);
    }

    private static EventEnvelope CreateEnvelope(string stackId, string serviceName, string instanceSelector, Google.Protobuf.IMessage payload)
    {
        var eventId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var traceId = Guid.NewGuid().ToString("N");
        var correlationId = Guid.NewGuid().ToString("N");
        var packedPayload = Any.Pack(payload);
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTime(now),
            Payload = packedPayload,
            PublisherId = PublisherId,
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
            Metadata =
            {
                ["trace_id"] = traceId,
                ["correlation_id"] = correlationId,
                ["causation_id"] = eventId,
                ["dedup_key"] = $"{packedPayload.TypeUrl}:{eventId}",
                ["type_url"] = packedPayload.TypeUrl,
                ["stack_id"] = stackId,
                ["service_name"] = serviceName,
                ["instance_selector"] = instanceSelector,
                ["occurred_at"] = now.ToString("O"),
            },
        };
    }

    private async Task<DynamicCommandResult?> EnsureIdempotentAsync(string scope, string key, object requestPayload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Idempotency key is required.");

        var requestHash = ComputeHashBytes(requestPayload);
        var acquire = await _idempotencyPort.AcquireAsync(scope, key, requestHash, ct);
        if (acquire.Acquired)
            return null;

        if (string.Equals(acquire.ErrorCode, "IDEMPOTENCY_PAYLOAD_MISMATCH", StringComparison.Ordinal))
            throw new InvalidOperationException("IDEMPOTENCY_PAYLOAD_MISMATCH");

        if (acquire.IsReplay)
        {
            var responsePayload = await _idempotencyPort.GetCommittedResponseAsync(scope, key, ct);
            if (!string.IsNullOrWhiteSpace(responsePayload))
            {
                var replay = System.Text.Json.JsonSerializer.Deserialize<DynamicCommandResult>(responsePayload);
                if (replay != null)
                    return replay;
            }

            throw new InvalidOperationException($"Duplicate command detected for scope '{scope}'.");
        }

        throw new InvalidOperationException(acquire.ErrorCode ?? "IDEMPOTENCY_CONFLICT");
    }

    private async Task CommitIdempotencyAsync(string scope, string key, DynamicCommandResult result, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        var payload = System.Text.Json.JsonSerializer.Serialize(result);
        await _idempotencyPort.CommitAsync(scope, key, ComputeHashBytes(result), payload, ct);
    }

    private static void RequireIfMatch(string? ifMatch)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
            throw new InvalidOperationException("VERSION_CONFLICT");
    }

    private static void ValidateServiceRequest(string serviceId, string version, string scriptCode, string entrypointType)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
            throw new InvalidOperationException("SERVICE_DEFINITION_INVALID: service_id is required.");
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("SERVICE_DEFINITION_INVALID: version is required.");
        if (string.IsNullOrWhiteSpace(scriptCode))
            throw new InvalidOperationException("SERVICE_DEFINITION_INVALID: script_code is required.");
        if (string.IsNullOrWhiteSpace(entrypointType))
            throw new InvalidOperationException("SERVICE_DEFINITION_INVALID: entrypoint_type is required.");
    }

    private static bool IsRunTerminal(string status) =>
        string.Equals(status, "Succeeded", StringComparison.Ordinal) ||
        string.Equals(status, "Failed", StringComparison.Ordinal) ||
        string.Equals(status, "Canceled", StringComparison.Ordinal) ||
        string.Equals(status, "TimedOut", StringComparison.Ordinal);

    private static ScriptRoleRequest BuildScriptInput(
        ScriptRoleRequest input,
        string runId,
        string containerId,
        string serviceId,
        string stackId,
        string serviceName)
    {
        var metadata = input.Metadata == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(input.Metadata, StringComparer.Ordinal);

        metadata["run_id"] = runId;
        metadata["container_id"] = containerId;
        metadata["service_id"] = serviceId;
        metadata["stack_id"] = stackId;
        metadata["service_name"] = serviceName;

        return input with
        {
            Metadata = metadata,
            CorrelationId = string.IsNullOrWhiteSpace(input.CorrelationId) ? runId : input.CorrelationId,
            MessageType = string.IsNullOrWhiteSpace(input.MessageType) ? "container.exec" : input.MessageType,
        };
    }

    private static string BuildDigest(string imageName, string sourceBundleDigest)
    {
        var normalized = $"{imageName}:{sourceBundleDigest}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"sha256:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static byte[] ComputeHashBytes(object payload)
    {
        var serialized = System.Text.Json.JsonSerializer.Serialize(payload);
        return SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
    }
}
