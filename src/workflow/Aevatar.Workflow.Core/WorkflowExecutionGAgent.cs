using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Composition;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

/// <summary>
/// Per-run workflow execution actor.
/// AgentId is bound to runId and owner workflow agent is explicitly injected.
/// </summary>
public sealed class WorkflowExecutionGAgent : WorkflowGAgent
{
    private string _workflowAgentId = string.Empty;

    public WorkflowExecutionGAgent(
        IActorRuntime runtime,
        IRoleAgentTypeResolver roleAgentTypeResolver,
        IEnumerable<IEventModuleFactory> eventModuleFactories,
        IEnumerable<IWorkflowModuleDependencyExpander> moduleDependencyExpanders,
        IEnumerable<IWorkflowModuleConfigurator> moduleConfigurators)
        : base(
            runtime,
            roleAgentTypeResolver,
            eventModuleFactories,
            moduleDependencyExpanders,
            moduleConfigurators)
    {
    }

    /// <summary>
    /// Long-lived workflow owner actor ID.
    /// </summary>
    public string WorkflowAgentId => _workflowAgentId;

    /// <summary>
    /// Binds owner workflow agent. Can only be set once per execution actor.
    /// </summary>
    public void BindWorkflowAgentId(string workflowAgentId)
    {
        if (string.IsNullOrWhiteSpace(workflowAgentId))
            throw new ArgumentException("WorkflowAgentId is required.", nameof(workflowAgentId));

        if (!string.IsNullOrWhiteSpace(_workflowAgentId) &&
            !string.Equals(_workflowAgentId, workflowAgentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"WorkflowExecutionGAgent owner already bound: '{_workflowAgentId}'.");
        }

        _workflowAgentId = workflowAgentId;
    }

    /// <inheritdoc />
    public override Task<string> GetDescriptionAsync() =>
        Task.FromResult($"WorkflowExecutionGAgent[owner={_workflowAgentId}, run={Id}]");

    /// <inheritdoc />
    protected override string ResolveRoleActorId(string roleId) => $"{Id}:{roleId}";

    /// <inheritdoc />
    protected override string ResolveRunId(ChatRequestEvent request) => Id;

    /// <inheritdoc />
    protected override bool ValidateBeforeRun(ChatRequestEvent request, out string? validationError)
    {
        if (string.IsNullOrWhiteSpace(_workflowAgentId))
        {
            validationError = "执行实例未绑定长期工作流 Actor。";
            return false;
        }

        if (request.Metadata.TryGetValue(ChatRequestMetadataKeys.RunId, out var runIdFromMetadata) &&
            !string.IsNullOrWhiteSpace(runIdFromMetadata) &&
            !string.Equals(runIdFromMetadata, Id, StringComparison.Ordinal))
        {
            validationError = $"执行实例 run_id 与 actorId 不一致: run_id={runIdFromMetadata}, actorId={Id}";
            return false;
        }

        return base.ValidateBeforeRun(request, out validationError);
    }

    /// <inheritdoc />
    [EventHandler]
    public override async Task HandleWorkflowCompleted(WorkflowCompletedEvent evt)
    {
        Logger.LogInformation("执行实例 {RunId} 完成: {Success}", evt.RunId, evt.Success);
        await PublishAsync(new TextMessageEndEvent
        {
            Content = evt.Success ? evt.Output : $"工作流执行失败: {evt.Error}",
            MessageId = evt.RunId,
        }, EventDirection.Up);
    }
}
