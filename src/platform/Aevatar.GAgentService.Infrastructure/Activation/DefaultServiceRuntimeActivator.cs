using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgentService.Infrastructure.Activation;

public sealed class DefaultServiceRuntimeActivator : IServiceRuntimeActivator
{
    private readonly IActorRuntime _runtime;
    // Nullable by design: the scripting capability is optional. Hosts composed without it
    // resolve these ports to null, and scripting deployment plans are rejected on activation.
    private readonly IScriptDefinitionSnapshotPort? _scriptDefinitionSnapshotPort;
    private readonly IScriptRuntimeProvisioningPort? _scriptRuntimeProvisioningPort;
    private readonly IWorkflowDefinitionProvisioningPort _workflowDefinitionProvisioningPort;

    public DefaultServiceRuntimeActivator(
        IActorRuntime runtime,
        IScriptDefinitionSnapshotPort? scriptDefinitionSnapshotPort,
        IScriptRuntimeProvisioningPort? scriptRuntimeProvisioningPort,
        IWorkflowDefinitionProvisioningPort workflowDefinitionProvisioningPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _scriptDefinitionSnapshotPort = scriptDefinitionSnapshotPort;
        _scriptRuntimeProvisioningPort = scriptRuntimeProvisioningPort;
        _workflowDefinitionProvisioningPort = workflowDefinitionProvisioningPort ?? throw new ArgumentNullException(nameof(workflowDefinitionProvisioningPort));
    }

    public async Task<ServiceRuntimeActivationResult> ActivateAsync(
        ServiceRuntimeActivationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deploymentId = $"{request.DeploymentActorId}:{request.RevisionId}";

        return request.Artifact.DeploymentPlan.PlanSpecCase switch
        {
            ServiceDeploymentPlan.PlanSpecOneofCase.StaticPlan =>
                await ActivateStaticAsync(request.Artifact.DeploymentPlan.StaticPlan, deploymentId, ct),
            ServiceDeploymentPlan.PlanSpecOneofCase.ScriptingPlan =>
                await ActivateScriptingAsync(
                    request.Artifact.DeploymentPlan.ScriptingPlan,
                    deploymentId,
                    request.Identity?.TenantId,
                    ct),
            ServiceDeploymentPlan.PlanSpecOneofCase.WorkflowPlan =>
                await ActivateWorkflowAsync(
                    request.Artifact,
                    request.RevisionId,
                    deploymentId,
                    request.Identity?.TenantId,
                    ct),
            _ => throw new InvalidOperationException("Unsupported deployment plan."),
        };
    }

    public async Task DeactivateAsync(
        ServiceRuntimeDeactivationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PrimaryActorId))
            return;
        if (!await _runtime.ExistsAsync(request.PrimaryActorId))
            return;

        await _runtime.DestroyAsync(request.PrimaryActorId, ct);
    }

    private async Task<ServiceRuntimeActivationResult> ActivateStaticAsync(
        StaticServiceDeploymentPlan plan,
        string deploymentId,
        CancellationToken ct)
    {
        var agentKind = plan.AgentKind?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(agentKind))
            throw new InvalidOperationException("Static agent_kind is required.");

        var actorId = string.IsNullOrWhiteSpace(plan.PreferredActorId)
            ? $"gagent-service:static-runtime:{deploymentId}"
            : $"{plan.PreferredActorId}:{deploymentId}";
        if (!await _runtime.ExistsAsync(actorId))
        {
            // Refactor (issue1044/static-service-agent-kind):
            //   Old pattern: static service activation reconstructed CLR Type from actor_type_name.
            //   New principle: runtime owns kind-based creation through IAgentKindRegistry + CreateByKindAsync.
            _ = await _runtime.CreateByKindAsync(agentKind, actorId, ct);
        }

        return new ServiceRuntimeActivationResult(deploymentId, actorId, "active");
    }

    private async Task<ServiceRuntimeActivationResult> ActivateScriptingAsync(
        ScriptingServiceDeploymentPlan plan,
        string deploymentId,
        string? scopeId,
        CancellationToken ct)
    {
        if (_scriptDefinitionSnapshotPort is not { } scriptDefinitionSnapshotPort ||
            _scriptRuntimeProvisioningPort is not { } scriptRuntimeProvisioningPort)
        {
            throw new InvalidOperationException(
                "Scripting capability is not enabled on this host; scripting deployment plans cannot be activated.");
        }

        var definitionSnapshot = await scriptDefinitionSnapshotPort.GetRequiredAsync(
            plan.DefinitionActorId,
            plan.Revision,
            ct);
        var runtimeActorId = $"gagent-service:script-runtime:{deploymentId}";
        var actorId = await scriptRuntimeProvisioningPort.EnsureRuntimeAsync(
            plan.DefinitionActorId,
            plan.Revision,
            runtimeActorId,
            definitionSnapshot,
            scopeId,
            ct);
        return new ServiceRuntimeActivationResult(deploymentId, actorId, "active");
    }

    private async Task<ServiceRuntimeActivationResult> ActivateWorkflowAsync(
        PreparedServiceRevisionArtifact artifact,
        string resolvedRevisionId,
        string deploymentId,
        string? scopeId,
        CancellationToken ct)
    {
        var plan = artifact.DeploymentPlan.WorkflowPlan;
        if (plan.ExecutionMode == ExternalCapabilityExecutionMode.Unspecified ||
            !Enum.IsDefined(plan.ExecutionMode))
        {
            throw new InvalidOperationException("Workflow service deployment execution mode is required.");
        }
        var bindingIdentity = WorkflowServiceDeploymentPlanIntegrity.ResolveBindingIdentity(
            artifact,
            resolvedRevisionId);
        var preferredActorId = string.IsNullOrWhiteSpace(plan.DefinitionActorId)
            ? $"gagent-service:workflow-definition:{deploymentId}"
            : $"{plan.DefinitionActorId}:{deploymentId}";
        var receipt = await _workflowDefinitionProvisioningPort.EnsureDefinitionAsync(
            new WorkflowDefinitionBinding(
                preferredActorId,
                plan.WorkflowName,
                plan.WorkflowYaml,
                plan.InlineWorkflowYamls,
                plan.ExecutionMode,
                ScopeId: scopeId?.Trim() ?? string.Empty,
                SourceKind: "service_revision",
                CapabilityAdmissionPlan: plan.CapabilityAdmissionPlan?.Clone(),
                WorkflowId: bindingIdentity.WorkflowId,
                RevisionId: bindingIdentity.RevisionId),
            preferredActorId,
            ct);

        return new ServiceRuntimeActivationResult(deploymentId, receipt.ActorId, "active");
    }
}
