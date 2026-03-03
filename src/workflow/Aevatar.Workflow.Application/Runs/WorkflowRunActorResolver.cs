using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunActorResolver : IWorkflowRunActorResolver
{
    private readonly IWorkflowRunActorPort _actorPort;
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;

    public WorkflowRunActorResolver(
        IWorkflowRunActorPort actorPort,
        IWorkflowDefinitionRegistry workflowRegistry)
    {
        _actorPort = actorPort;
        _workflowRegistry = workflowRegistry;
    }

    public async Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct = default)
    {
        var requestedWorkflowName = NormalizeWorkflowName(request.WorkflowName);
        var hasInlineWorkflowYaml = !string.IsNullOrWhiteSpace(request.WorkflowYaml);
        var workflowNameForRun = string.IsNullOrWhiteSpace(requestedWorkflowName) ? "direct" : requestedWorkflowName;
        var workflowYamlForRun = string.Empty;

        if (hasInlineWorkflowYaml)
        {
            var parseResult = await _actorPort.ParseWorkflowYamlAsync(request.WorkflowYaml!, ct);
            if (!parseResult.Succeeded)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.InvalidWorkflowYaml);

            var inlineWorkflowName = NormalizeWorkflowName(parseResult.WorkflowName);
            if (string.IsNullOrWhiteSpace(inlineWorkflowName))
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.InvalidWorkflowYaml);

            if (!string.IsNullOrWhiteSpace(requestedWorkflowName) &&
                !string.Equals(requestedWorkflowName, inlineWorkflowName, StringComparison.OrdinalIgnoreCase))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    inlineWorkflowName,
                    WorkflowChatRunStartError.WorkflowNameMismatch);
            }

            workflowNameForRun = inlineWorkflowName;
            workflowYamlForRun = request.WorkflowYaml!;
        }

        if (!string.IsNullOrWhiteSpace(request.ActorId))
        {
            var existing = await _actorPort.GetAsync(request.ActorId, ct);
            if (existing == null)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentNotFound);

            if (!await _actorPort.IsWorkflowActorAsync(existing, ct))
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentTypeNotSupported);

            var boundWorkflowName = NormalizeWorkflowName(await _actorPort.GetBoundWorkflowNameAsync(existing, ct));
            if (string.IsNullOrWhiteSpace(boundWorkflowName))
            {
                if (!hasInlineWorkflowYaml)
                {
                    return new WorkflowActorResolutionResult(
                        null,
                        workflowNameForRun,
                        WorkflowChatRunStartError.AgentWorkflowNotConfigured);
                }

                await _actorPort.ConfigureWorkflowAsync(existing, workflowYamlForRun, workflowNameForRun, ct);
                return new WorkflowActorResolutionResult(existing, workflowNameForRun, WorkflowChatRunStartError.None);
            }

            if (hasInlineWorkflowYaml &&
                !string.Equals(workflowNameForRun, boundWorkflowName, StringComparison.OrdinalIgnoreCase))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    boundWorkflowName,
                    WorkflowChatRunStartError.WorkflowBindingMismatch);
            }

            if (hasInlineWorkflowYaml)
            {
                // Never mutate an already bound actor in-place with inline yaml.
                // Inline workflow execution must run on a fresh actor instance.
                var isolatedActor = await _actorPort.CreateAsync(ct);
                await _actorPort.ConfigureWorkflowAsync(isolatedActor, workflowYamlForRun, workflowNameForRun, ct);
                return new WorkflowActorResolutionResult(isolatedActor, workflowNameForRun, WorkflowChatRunStartError.None);
            }

            if (!string.IsNullOrWhiteSpace(requestedWorkflowName) &&
                !string.Equals(requestedWorkflowName, boundWorkflowName, StringComparison.OrdinalIgnoreCase))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    boundWorkflowName,
                    WorkflowChatRunStartError.WorkflowBindingMismatch);
            }

            return new WorkflowActorResolutionResult(existing, boundWorkflowName, WorkflowChatRunStartError.None);
        }

        if (!hasInlineWorkflowYaml)
        {
            var yaml = _workflowRegistry.GetYaml(workflowNameForRun);
            if (yaml == null)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.WorkflowNotFound);
            workflowYamlForRun = yaml;
        }

        var actor = await _actorPort.CreateAsync(ct);
        await _actorPort.ConfigureWorkflowAsync(actor, workflowYamlForRun, workflowNameForRun, ct);

        return new WorkflowActorResolutionResult(actor, workflowNameForRun, WorkflowChatRunStartError.None);
    }

    private static string NormalizeWorkflowName(string? workflowName) =>
        string.IsNullOrWhiteSpace(workflowName) ? string.Empty : workflowName.Trim();
}
