using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunActorResolver : IWorkflowRunActorResolver
{
    private readonly IWorkflowRunActorPort _actorPort;
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;
    private readonly WorkflowRunBehaviorOptions _behaviorOptions;

    public WorkflowRunActorResolver(
        IWorkflowRunActorPort actorPort,
        IWorkflowDefinitionRegistry workflowRegistry,
        WorkflowRunBehaviorOptions? behaviorOptions = null)
    {
        _actorPort = actorPort;
        _workflowRegistry = workflowRegistry;
        _behaviorOptions = behaviorOptions ?? new WorkflowRunBehaviorOptions();
    }

    public async Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct = default)
    {
        var requestedWorkflowName = WorkflowRunNameNormalizer.NormalizeWorkflowName(request.WorkflowName);
        var hasInlineWorkflowYaml = !string.IsNullOrWhiteSpace(request.WorkflowYaml);
        var hasRequestedWorkflowName = !string.IsNullOrWhiteSpace(requestedWorkflowName);
        var workflowNameForRun = hasRequestedWorkflowName
            ? requestedWorkflowName
            : hasInlineWorkflowYaml
                ? string.Empty
                : ResolveDefaultWorkflowName();
        var workflowYamlForRun = string.Empty;

        if (hasInlineWorkflowYaml)
        {
            var parseResult = await _actorPort.ParseWorkflowYamlAsync(request.WorkflowYaml!, ct);
            if (!parseResult.Succeeded)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.InvalidWorkflowYaml);

            var inlineWorkflowName = WorkflowRunNameNormalizer.NormalizeWorkflowName(parseResult.WorkflowName);
            if (string.IsNullOrWhiteSpace(inlineWorkflowName))
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.InvalidWorkflowYaml);

            if (hasRequestedWorkflowName &&
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

            var boundWorkflowName = WorkflowRunNameNormalizer.NormalizeWorkflowName(await _actorPort.GetBoundWorkflowNameAsync(existing, ct));
            if (string.IsNullOrWhiteSpace(boundWorkflowName))
            {
                if (!hasInlineWorkflowYaml)
                {
                    return new WorkflowActorResolutionResult(
                        null,
                        workflowNameForRun,
                        WorkflowChatRunStartError.AgentWorkflowNotConfigured);
                }

                await ConfigureWorkflowForRunAsync(
                    existing,
                    workflowYamlForRun,
                    workflowNameForRun,
                    ct);
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

            if (!hasInlineWorkflowYaml &&
                hasRequestedWorkflowName &&
                !string.Equals(requestedWorkflowName, boundWorkflowName, StringComparison.OrdinalIgnoreCase))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    boundWorkflowName,
                    WorkflowChatRunStartError.WorkflowBindingMismatch);
            }

            if (hasInlineWorkflowYaml)
            {
                await ConfigureWorkflowForRunAsync(
                    existing,
                    workflowYamlForRun,
                    boundWorkflowName,
                    ct);
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
        if (hasInlineWorkflowYaml)
        {
            await ConfigureWorkflowForRunAsync(
                actor,
                workflowYamlForRun,
                workflowNameForRun,
                ct);
        }
        else
        {
            await ConfigureWorkflowForRunWithFallbackWrapAsync(
                actor,
                workflowYamlForRun,
                workflowNameForRun,
                ct);
        }

        return new WorkflowActorResolutionResult(actor, workflowNameForRun, WorkflowChatRunStartError.None);
    }

    private async Task ConfigureWorkflowForRunAsync(
        IActor actor,
        string workflowYaml,
        string workflowName,
        CancellationToken ct)
    {
        await _actorPort.ConfigureWorkflowAsync(actor, workflowYaml, workflowName, ct);
    }

    private async Task ConfigureWorkflowForRunWithFallbackWrapAsync(
        IActor actor,
        string workflowYaml,
        string workflowName,
        CancellationToken ct)
    {
        try
        {
            await _actorPort.ConfigureWorkflowAsync(actor, workflowYaml, workflowName, ct);
        }
        catch (Exception ex) when (ex is not WorkflowDirectFallbackTriggerException)
        {
            throw new WorkflowDirectFallbackTriggerException(
                $"Failed to configure workflow '{workflowName}' for actor '{actor.Id}'.",
                ex);
        }
    }

    private string ResolveDefaultWorkflowName()
    {
        if (_behaviorOptions.UseAutoAsDefaultWhenWorkflowUnspecified)
            return WorkflowRunBehaviorOptions.AutoWorkflowName;

        var configuredDefault = WorkflowRunNameNormalizer.NormalizeWorkflowName(_behaviorOptions.DefaultWorkflowName);
        return string.IsNullOrWhiteSpace(configuredDefault)
            ? WorkflowRunBehaviorOptions.DirectWorkflowName
            : configuredDefault;
    }
}
