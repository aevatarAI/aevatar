using Google.Protobuf.WellKnownTypes;

namespace Aevatar.DynamicRuntime.Abstractions.Contracts;

public interface IDynamicRuntimeCommandService
{
    Task<DynamicCommandResult> BuildImageAsync(BuildImageRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> PublishImageAsync(PublishImageRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> ApplyComposeAsync(ComposeApplyYamlRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> ComposeUpAsync(string stackId, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> ComposeDownAsync(string stackId, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> ScaleComposeServiceAsync(ComposeServiceScaleRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> RolloutComposeServiceAsync(ComposeServiceRolloutRequest request, DynamicCommandContext context, CancellationToken ct = default);

    Task<DynamicCommandResult> RegisterServiceAsync(RegisterServiceDefinitionRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> UpdateServiceAsync(UpdateServiceDefinitionRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> ActivateServiceAsync(string serviceId, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> DeactivateServiceAsync(string serviceId, DynamicCommandContext context, CancellationToken ct = default);

    Task<DynamicCommandResult> CreateContainerAsync(CreateContainerRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> StartContainerAsync(string containerId, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> StopContainerAsync(string containerId, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> DestroyContainerAsync(string containerId, DynamicCommandContext context, CancellationToken ct = default);

    Task<DynamicCommandResult> ExecuteContainerAsync(ExecuteContainerRequest request, DynamicCommandContext context, CancellationToken ct = default);
    Task<DynamicCommandResult> CancelRunAsync(string runId, string reason, DynamicCommandContext context, CancellationToken ct = default);

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
    Task<ImageTagSnapshot?> GetImageTagAsync(string imageName, string tag, CancellationToken ct = default);
    Task<ImageDigestSnapshot?> GetImageDigestAsync(string imageName, string digest, CancellationToken ct = default);
    Task<StackSnapshot?> GetStackAsync(string stackId, CancellationToken ct = default);
    Task<IReadOnlyList<ComposeServiceSnapshot>> GetComposeServicesAsync(string stackId, CancellationToken ct = default);
    Task<IReadOnlyList<ComposeEventSnapshot>> GetComposeEventsAsync(string stackId, CancellationToken ct = default);
    Task<ServiceDefinitionSnapshot?> GetServiceDefinitionAsync(string serviceId, CancellationToken ct = default);
    Task<ContainerSnapshot?> GetContainerAsync(string containerId, CancellationToken ct = default);
    Task<IReadOnlyList<RunSnapshot>> GetContainerRunsAsync(string containerId, CancellationToken ct = default);
    Task<RunSnapshot?> GetRunAsync(string runId, CancellationToken ct = default);
    Task<BuildJobSnapshot?> GetBuildJobAsync(string buildJobId, CancellationToken ct = default);
    Task<IReadOnlyList<BuildJobSnapshot>> GetBuildJobsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ScriptReadModelDefinitionSnapshot>> GetScriptReadModelDefinitionsAsync(string serviceId, CancellationToken ct = default);
    Task<IReadOnlyList<ScriptReadModelRelationSnapshot>> GetScriptReadModelRelationsAsync(string serviceId, CancellationToken ct = default);
    Task<IReadOnlyList<ScriptReadModelDocumentSnapshot>> GetScriptReadModelDocumentsAsync(string serviceId, string readModelName, CancellationToken ct = default);
    Task<ScriptReadModelDocumentSnapshot?> GetScriptReadModelDocumentAsync(string serviceId, string readModelName, string documentId, CancellationToken ct = default);
}

public interface IScriptRoleEntrypoint
{
    Task<ScriptRoleExecutionResult> HandleEventAsync(Aevatar.Foundation.Abstractions.EventEnvelope envelope, CancellationToken ct = default);
}

public sealed record ScriptRoleExecutionResult(string Output)
{
    public static readonly ScriptRoleExecutionResult Empty = new(string.Empty);
}

public sealed record DynamicScriptExecutionRequest(
    string ScriptCode,
    Aevatar.Foundation.Abstractions.EventEnvelope Envelope,
    string EntrypointType = "ScriptEntrypoint",
    Any? CustomState = null);

public sealed record DynamicScriptExecutionResult(
    bool Success,
    string Output,
    IReadOnlyList<Aevatar.Foundation.Abstractions.EventEnvelope>? PublishedEvents = null,
    Any? CustomState = null,
    string? Error = null);

public interface IDynamicScriptExecutionService
{
    Task<DynamicScriptExecutionResult> ExecuteAsync(DynamicScriptExecutionRequest request, CancellationToken ct = default);
}

public interface IScriptRoleCapabilityAdapter : Aevatar.AI.Abstractions.Agents.IRoleAgent
{
    ScriptRoleCapabilitySnapshot Snapshot { get; }
    Task<string> ExecuteAsync(Aevatar.Foundation.Abstractions.EventEnvelope envelope, CancellationToken ct = default);
}

public interface IIdempotencyPort
{
    Task<IdempotencyAcquireResult> AcquireAsync(string scope, string key, byte[] requestHash, CancellationToken ct = default);
    Task CommitAsync(string scope, string key, byte[] responseHash, string responsePayload, CancellationToken ct = default);
    Task<string?> GetCommittedResponseAsync(string scope, string key, CancellationToken ct = default);
}

public interface IConcurrencyTokenPort
{
    Task<ConcurrencyCheckResult> CheckAndAdvanceAsync(string aggregateId, string? expectedVersion, CancellationToken ct = default);
}

public interface IImageReferenceResolver
{
    Task<ImageDigestResolveResult> ResolveAsync(string imageName, string tagOrDigest, CancellationToken ct = default);
}

public interface IScriptComposeSpecValidator
{
    Task<ComposeSpecValidationResult> ValidateAsync(ComposeApplyYamlRequest request, CancellationToken ct = default);
}

public interface IScriptComposeReconcilePort
{
    Task<ComposeReconcileResult> ReconcileAsync(string stackId, long desiredGeneration, CancellationToken ct = default);
}

public interface IAgentBuildPlanPort
{
    Task<BuildPlanResult> PlanAsync(BuildPlanRequest request, CancellationToken ct = default);
}

public interface IAgentBuildPolicyPort
{
    Task<BuildPolicyDecision> ValidateAsync(BuildPolicyRequest request, CancellationToken ct = default);
}

public interface IAgentBuildExecutionPort
{
    Task<BuildExecutionResult> ExecuteAsync(BuildExecutionRequest request, CancellationToken ct = default);
}

public interface IServiceModePolicyPort
{
    Task<ServiceModeDecision> ValidateAsync(ServiceModePolicyRequest request, CancellationToken ct = default);
}

public interface IBuildApprovalPort
{
    Task<BuildApprovalDecision> DecideAsync(BuildApprovalRequest request, CancellationToken ct = default);
}

public interface IScriptCompilationPolicy
{
    IReadOnlySet<string> AllowedReferences { get; }
    IReadOnlySet<string> BlockedNamespacePrefixes { get; }
    Task<PolicyValidationResult> ValidateAsync(ScriptSourceBundle bundle, CancellationToken ct = default);
}

public interface IScriptAssemblyLoadPolicy
{
    Task<ScriptAssemblyHandle> LoadAsync(CompiledScriptArtifact artifact, CancellationToken ct = default);
    Task<UnloadResult> UnloadAsync(ScriptAssemblyHandle handle, TimeSpan timeout, CancellationToken ct = default);
}

public interface IScriptSandboxPolicy
{
    Task<SandboxPrepareResult> PrepareAsync(ScriptExecutionContext context, CancellationToken ct = default);
}

public interface IScriptResourceQuotaPolicy
{
    Task<ResourceQuotaDecision> EvaluateAsync(ScriptExecutionContext context, CancellationToken ct = default);
}

public interface IScriptNetworkPolicy
{
    Task<NetworkAccessDecision> AuthorizeAsync(ScriptNetworkRequest request, CancellationToken ct = default);
}

public interface IEventEnvelopePublisherPort
{
    Task PublishAsync(ScriptEventEnvelope envelope, CancellationToken ct = default);
}

public interface IEventEnvelopeSubscriberPort
{
    Task<EnvelopeLeaseResult> SubscribeAsync(EnvelopeSubscribeRequest request, CancellationToken ct = default);
}

public interface IEventEnvelopeDedupPort
{
    Task<EnvelopeDedupResult> CheckAndRecordAsync(string scope, string dedupKey, TimeSpan ttl, CancellationToken ct = default);
}

public interface IDynamicRuntimeReadStore
{
    Task UpsertImageAsync(ImageSnapshot snapshot, CancellationToken ct = default);
    Task UpsertStackAsync(StackSnapshot snapshot, CancellationToken ct = default);
    Task UpsertComposeServiceAsync(ComposeServiceSnapshot snapshot, CancellationToken ct = default);
    Task AppendComposeEventAsync(ComposeEventSnapshot snapshot, CancellationToken ct = default);
    Task UpsertServiceDefinitionAsync(ServiceDefinitionSnapshot snapshot, CancellationToken ct = default);
    Task UpsertContainerAsync(ContainerSnapshot snapshot, CancellationToken ct = default);
    Task UpsertRunAsync(RunSnapshot snapshot, CancellationToken ct = default);
    Task UpsertBuildJobAsync(BuildJobSnapshot snapshot, CancellationToken ct = default);
    Task UpsertScriptReadModelDefinitionAsync(ScriptReadModelDefinitionSnapshot snapshot, CancellationToken ct = default);
    Task UpsertScriptReadModelRelationAsync(ScriptReadModelRelationSnapshot snapshot, CancellationToken ct = default);
    Task UpsertScriptReadModelDocumentAsync(ScriptReadModelDocumentSnapshot snapshot, CancellationToken ct = default);

    Task<ImageSnapshot?> GetImageAsync(string imageName, CancellationToken ct = default);
    Task<StackSnapshot?> GetStackAsync(string stackId, CancellationToken ct = default);
    Task<IReadOnlyList<ComposeServiceSnapshot>> GetComposeServicesAsync(string stackId, CancellationToken ct = default);
    Task<IReadOnlyList<ComposeEventSnapshot>> GetComposeEventsAsync(string stackId, CancellationToken ct = default);
    Task<ServiceDefinitionSnapshot?> GetServiceDefinitionAsync(string serviceId, CancellationToken ct = default);
    Task<ContainerSnapshot?> GetContainerAsync(string containerId, CancellationToken ct = default);
    Task<IReadOnlyList<RunSnapshot>> GetContainerRunsAsync(string containerId, CancellationToken ct = default);
    Task<RunSnapshot?> GetRunAsync(string runId, CancellationToken ct = default);
    Task<BuildJobSnapshot?> GetBuildJobAsync(string buildJobId, CancellationToken ct = default);
    Task<IReadOnlyList<BuildJobSnapshot>> GetBuildJobsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ScriptReadModelDefinitionSnapshot>> GetScriptReadModelDefinitionsAsync(string serviceId, CancellationToken ct = default);
    Task<IReadOnlyList<ScriptReadModelRelationSnapshot>> GetScriptReadModelRelationsAsync(string serviceId, CancellationToken ct = default);
    Task<IReadOnlyList<ScriptReadModelDocumentSnapshot>> GetScriptReadModelDocumentsAsync(string serviceId, string readModelName, CancellationToken ct = default);
    Task<ScriptReadModelDocumentSnapshot?> GetScriptReadModelDocumentAsync(string serviceId, string readModelName, string documentId, CancellationToken ct = default);
}
