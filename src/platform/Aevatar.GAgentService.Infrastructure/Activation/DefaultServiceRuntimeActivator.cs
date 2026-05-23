using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgentService.Infrastructure.Activation;

public sealed class DefaultServiceRuntimeActivator : IServiceRuntimeActivator
{
    private readonly IActorRuntime _runtime;
    private readonly IScriptDefinitionSnapshotPort _scriptDefinitionSnapshotPort;
    private readonly IScriptRuntimeProvisioningPort _scriptRuntimeProvisioningPort;
    private readonly IWorkflowDefinitionProvisioningPort _workflowDefinitionProvisioningPort;

    public DefaultServiceRuntimeActivator(
        IActorRuntime runtime,
        IScriptDefinitionSnapshotPort scriptDefinitionSnapshotPort,
        IScriptRuntimeProvisioningPort scriptRuntimeProvisioningPort,
        IWorkflowDefinitionProvisioningPort workflowDefinitionProvisioningPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _scriptDefinitionSnapshotPort = scriptDefinitionSnapshotPort ?? throw new ArgumentNullException(nameof(scriptDefinitionSnapshotPort));
        _scriptRuntimeProvisioningPort = scriptRuntimeProvisioningPort ?? throw new ArgumentNullException(nameof(scriptRuntimeProvisioningPort));
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
                await ActivateWorkflowAsync(request.Artifact.DeploymentPlan.WorkflowPlan, deploymentId, ct),
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
        var actorType = ResolveActorType(plan.ActorTypeName)
            ?? throw new InvalidOperationException($"Static actor type '{plan.ActorTypeName}' was not found.");
        var actorId = string.IsNullOrWhiteSpace(plan.PreferredActorId)
            ? $"gagent-service:static-runtime:{deploymentId}"
            : $"{plan.PreferredActorId}:{deploymentId}";
        if (!await _runtime.ExistsAsync(actorId))
            _ = await _runtime.CreateAsync(actorType, actorId, ct);

        return new ServiceRuntimeActivationResult(deploymentId, actorId, "active");
    }

    private async Task<ServiceRuntimeActivationResult> ActivateScriptingAsync(
        ScriptingServiceDeploymentPlan plan,
        string deploymentId,
        string? scopeId,
        CancellationToken ct)
    {
        var definitionSnapshot = await _scriptDefinitionSnapshotPort.GetRequiredAsync(
            plan.DefinitionActorId,
            plan.Revision,
            ct);
        var runtimeActorId = $"gagent-service:script-runtime:{deploymentId}";
        var actorId = await _scriptRuntimeProvisioningPort.EnsureRuntimeAsync(
            plan.DefinitionActorId,
            plan.Revision,
            runtimeActorId,
            definitionSnapshot,
            scopeId,
            ct);
        return new ServiceRuntimeActivationResult(deploymentId, actorId, "active");
    }

    private async Task<ServiceRuntimeActivationResult> ActivateWorkflowAsync(
        WorkflowServiceDeploymentPlan plan,
        string deploymentId,
        CancellationToken ct)
    {
        var preferredActorId = string.IsNullOrWhiteSpace(plan.DefinitionActorId)
            ? $"gagent-service:workflow-definition:{deploymentId}"
            : $"{plan.DefinitionActorId}:{deploymentId}";
        var receipt = await _workflowDefinitionProvisioningPort.EnsureDefinitionAsync(
            new WorkflowDefinitionBinding(
                preferredActorId,
                plan.WorkflowName,
                plan.WorkflowYaml,
                plan.InlineWorkflowYamls),
            preferredActorId,
            ct);

        return new ServiceRuntimeActivationResult(deploymentId, receipt.ActorId, "active");
    }

    private static Type? ResolveActorType(string typeName)
    {
        // Try direct resolution first (works for assembly-qualified names).
        var type = Type.GetType(typeName, throwOnError: false);
        if (type is not null)
            return type;

        // Fallback: scan all loaded assemblies by full type name.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            try
            {
                type = assembly.GetType(typeName);
                if (type is not null)
                    return type;
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }
}
