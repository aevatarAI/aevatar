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
        var inlineWorkflowYamls = request.WorkflowYamls?.ToList() ?? [];
        var hasInlineWorkflowYamls = inlineWorkflowYamls.Count > 0;
        var hasRequestedWorkflowName = !string.IsNullOrWhiteSpace(requestedWorkflowName);
        var workflowNameForRun = hasInlineWorkflowYamls
            ? string.Empty
            : hasRequestedWorkflowName
            ? requestedWorkflowName
            : ResolveDefaultWorkflowName();
        var workflowYamlForRun = string.Empty;
        IReadOnlyDictionary<string, string>? inlineWorkflowYamlMapForRun = null;

        if (hasInlineWorkflowYamls)
        {
            var inlineBundle = await BuildInlineWorkflowBundleAsync(inlineWorkflowYamls, ct);
            if (!inlineBundle.Succeeded)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.InvalidWorkflowYaml);

            workflowNameForRun = inlineBundle.EntryWorkflowName;
            workflowYamlForRun = inlineBundle.EntryWorkflowYaml;
            inlineWorkflowYamlMapForRun = inlineBundle.WorkflowYamlsByName;
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
                if (!hasInlineWorkflowYamls)
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
                    inlineWorkflowYamlMapForRun,
                    ct);
                return new WorkflowActorResolutionResult(existing, workflowNameForRun, WorkflowChatRunStartError.None);
            }

            if (hasInlineWorkflowYamls &&
                !string.Equals(workflowNameForRun, boundWorkflowName, StringComparison.OrdinalIgnoreCase))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    boundWorkflowName,
                    WorkflowChatRunStartError.WorkflowBindingMismatch);
            }

            if (hasInlineWorkflowYamls)
            {
                // Never mutate an already bound actor in-place with inline yaml.
                // Inline workflow execution must run on a fresh actor instance.
                var isolatedActor = await _actorPort.CreateAsync(ct);
                await ConfigureWorkflowForRunAsync(
                    isolatedActor,
                    workflowYamlForRun,
                    workflowNameForRun,
                    inlineWorkflowYamlMapForRun,
                    ct);
                return new WorkflowActorResolutionResult(isolatedActor, workflowNameForRun, WorkflowChatRunStartError.None);
            }

            if (!hasInlineWorkflowYamls &&
                hasRequestedWorkflowName &&
                !string.Equals(requestedWorkflowName, boundWorkflowName, StringComparison.OrdinalIgnoreCase))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    boundWorkflowName,
                    WorkflowChatRunStartError.WorkflowBindingMismatch);
            }
            return new WorkflowActorResolutionResult(existing, boundWorkflowName, WorkflowChatRunStartError.None);
        }

        if (!hasInlineWorkflowYamls)
        {
            var yaml = _workflowRegistry.GetYaml(workflowNameForRun);
            if (yaml == null)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.WorkflowNotFound);
            workflowYamlForRun = yaml;
        }

        var actor = await _actorPort.CreateAsync(ct);
        if (hasInlineWorkflowYamls)
        {
            await ConfigureWorkflowForRunAsync(
                actor,
                workflowYamlForRun,
                workflowNameForRun,
                inlineWorkflowYamlMapForRun,
                ct);
        }
        else
        {
            await ConfigureWorkflowForRunWithFallbackWrapAsync(
                actor,
                workflowYamlForRun,
                workflowNameForRun,
                null,
                ct);
        }

        return new WorkflowActorResolutionResult(actor, workflowNameForRun, WorkflowChatRunStartError.None);
    }

    private async Task ConfigureWorkflowForRunAsync(
        IActor actor,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        CancellationToken ct)
    {
        await _actorPort.ConfigureWorkflowAsync(actor, workflowYaml, workflowName, inlineWorkflowYamls, ct);
    }

    private async Task ConfigureWorkflowForRunWithFallbackWrapAsync(
        IActor actor,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        CancellationToken ct)
    {
        try
        {
            await _actorPort.ConfigureWorkflowAsync(actor, workflowYaml, workflowName, inlineWorkflowYamls, ct);
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

    private async Task<InlineWorkflowBundle> BuildInlineWorkflowBundleAsync(
        IReadOnlyList<string> inlineWorkflowYamls,
        CancellationToken ct)
    {
        if (inlineWorkflowYamls.Count == 0)
            return InlineWorkflowBundle.Invalid;

        var workflowByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string entryWorkflowName = string.Empty;
        string entryWorkflowYaml = string.Empty;

        for (var i = 0; i < inlineWorkflowYamls.Count; i++)
        {
            var yaml = inlineWorkflowYamls[i];
            if (string.IsNullOrWhiteSpace(yaml))
                return InlineWorkflowBundle.Invalid;

            var parseResult = await _actorPort.ParseWorkflowYamlAsync(yaml, ct);
            if (!parseResult.Succeeded)
                return InlineWorkflowBundle.Invalid;

            var workflowName = WorkflowRunNameNormalizer.NormalizeWorkflowName(parseResult.WorkflowName);
            if (string.IsNullOrWhiteSpace(workflowName))
                return InlineWorkflowBundle.Invalid;
            if (!workflowByName.TryAdd(workflowName, yaml))
                return InlineWorkflowBundle.Invalid;

            if (i == 0)
            {
                entryWorkflowName = workflowName;
                entryWorkflowYaml = yaml;
            }
        }

        if (string.IsNullOrWhiteSpace(entryWorkflowName) || string.IsNullOrWhiteSpace(entryWorkflowYaml))
            return InlineWorkflowBundle.Invalid;

        return new InlineWorkflowBundle(
            true,
            entryWorkflowName,
            entryWorkflowYaml,
            workflowByName);
    }

    private readonly record struct InlineWorkflowBundle(
        bool Succeeded,
        string EntryWorkflowName,
        string EntryWorkflowYaml,
        IReadOnlyDictionary<string, string> WorkflowYamlsByName)
    {
        public static InlineWorkflowBundle Invalid { get; } = new(
            false,
            string.Empty,
            string.Empty,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }
}
