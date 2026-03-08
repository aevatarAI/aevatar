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
        IReadOnlyDictionary<string, string> inlineWorkflowYamlMapForRun =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
            return await ResolveFromSourceActorAsync(
                request.ActorId,
                hasInlineWorkflowYamls,
                hasRequestedWorkflowName,
                requestedWorkflowName,
                workflowNameForRun,
                workflowYamlForRun,
                inlineWorkflowYamlMapForRun,
                ct);
        }

        if (!hasInlineWorkflowYamls)
        {
            var yaml = _workflowRegistry.GetYaml(workflowNameForRun);
            if (yaml == null)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.WorkflowNotFound);

            workflowYamlForRun = yaml;
        }

        var actor = await CreateRunActorAsync(
            new WorkflowDefinitionBinding(
                string.Empty,
                workflowNameForRun,
                workflowYamlForRun,
                inlineWorkflowYamlMapForRun),
            wrapAsFallbackTrigger: !hasInlineWorkflowYamls,
            ct);

        return new WorkflowActorResolutionResult(actor, workflowNameForRun, WorkflowChatRunStartError.None);
    }

    private async Task<WorkflowActorResolutionResult> ResolveFromSourceActorAsync(
        string actorId,
        bool hasInlineWorkflowYamls,
        bool hasRequestedWorkflowName,
        string requestedWorkflowName,
        string workflowNameForRun,
        string workflowYamlForRun,
        IReadOnlyDictionary<string, string> inlineWorkflowYamlMapForRun,
        CancellationToken ct)
    {
        var sourceActor = await _actorPort.GetAsync(actorId, ct);
        if (sourceActor == null)
            return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentNotFound);

        var sourceBinding = await _actorPort.DescribeAsync(sourceActor, ct);
        if (!sourceBinding.IsWorkflowCapable)
            return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentTypeNotSupported);

        var boundWorkflowName = sourceBinding.HasWorkflowName
            ? WorkflowRunNameNormalizer.NormalizeWorkflowName(sourceBinding.WorkflowName)
            : WorkflowRunNameNormalizer.NormalizeWorkflowName(await _actorPort.GetBoundWorkflowNameAsync(sourceActor, ct));

        if (hasInlineWorkflowYamls)
        {
            if (!string.IsNullOrWhiteSpace(boundWorkflowName) &&
                !string.Equals(workflowNameForRun, boundWorkflowName, StringComparison.OrdinalIgnoreCase))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    boundWorkflowName,
                    WorkflowChatRunStartError.WorkflowBindingMismatch);
            }

            var inlineRunActor = await CreateRunActorAsync(
                new WorkflowDefinitionBinding(
                    sourceBinding.EffectiveDefinitionActorId,
                    workflowNameForRun,
                    workflowYamlForRun,
                    inlineWorkflowYamlMapForRun),
                wrapAsFallbackTrigger: false,
                ct);
            return new WorkflowActorResolutionResult(inlineRunActor, workflowNameForRun, WorkflowChatRunStartError.None);
        }

        if (string.IsNullOrWhiteSpace(boundWorkflowName))
        {
            return new WorkflowActorResolutionResult(
                null,
                workflowNameForRun,
                WorkflowChatRunStartError.AgentWorkflowNotConfigured);
        }

        if (hasRequestedWorkflowName &&
            !string.Equals(requestedWorkflowName, boundWorkflowName, StringComparison.OrdinalIgnoreCase))
        {
            return new WorkflowActorResolutionResult(
                null,
                boundWorkflowName,
                WorkflowChatRunStartError.WorkflowBindingMismatch);
        }

        var workflowYamlFromSource = ResolveWorkflowYamlForExecution(boundWorkflowName, sourceBinding);
        if (string.IsNullOrWhiteSpace(workflowYamlFromSource))
        {
            return new WorkflowActorResolutionResult(
                null,
                boundWorkflowName,
                WorkflowChatRunStartError.AgentWorkflowNotConfigured);
        }

        var runActor = await CreateRunActorAsync(
            new WorkflowDefinitionBinding(
                sourceBinding.EffectiveDefinitionActorId,
                boundWorkflowName,
                workflowYamlFromSource,
                sourceBinding.InlineWorkflowYamls),
            wrapAsFallbackTrigger: true,
            ct);

        return new WorkflowActorResolutionResult(
            runActor,
            boundWorkflowName,
            WorkflowChatRunStartError.None);
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

    private async Task<IActor> CreateRunActorAsync(
        WorkflowDefinitionBinding definitionBinding,
        bool wrapAsFallbackTrigger,
        CancellationToken ct)
    {
        try
        {
            return await _actorPort.CreateRunAsync(definitionBinding, ct);
        }
        catch (Exception ex) when (wrapAsFallbackTrigger && ex is InvalidOperationException or NotSupportedException)
        {
            throw new WorkflowDirectFallbackTriggerException(
                $"Failed to create workflow run actor for workflow '{definitionBinding.WorkflowName}'.",
                ex);
        }
    }

    private string ResolveWorkflowYamlForExecution(
        string workflowName,
        WorkflowActorBinding sourceBinding)
    {
        if (!string.IsNullOrWhiteSpace(sourceBinding.WorkflowYaml))
            return sourceBinding.WorkflowYaml;

        return _workflowRegistry.GetYaml(workflowName) ?? string.Empty;
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
