using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunActorResolver : IWorkflowRunActorResolver
{
    private readonly IWorkflowActorBindingReader _bindingReader;
    private readonly IWorkflowRunActorPort _actorPort;
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;
    private readonly WorkflowRunBehaviorOptions _behaviorOptions;

    public WorkflowRunActorResolver(
        IWorkflowActorBindingReader bindingReader,
        IWorkflowRunActorPort actorPort,
        IWorkflowDefinitionRegistry workflowRegistry,
        WorkflowRunBehaviorOptions? behaviorOptions = null)
    {
        _bindingReader = bindingReader ?? throw new ArgumentNullException(nameof(bindingReader));
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
        _workflowRegistry = workflowRegistry ?? throw new ArgumentNullException(nameof(workflowRegistry));
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
        var scopeIdForRun = ResolveScopeId(request.ScopeId);
        var workflowYamlForRun = string.Empty;
        WorkflowDefinitionRegistration? registryDefinitionForRun = null;
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

            if (hasRequestedWorkflowName &&
                !string.Equals(requestedWorkflowName, workflowNameForRun, StringComparison.OrdinalIgnoreCase))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    workflowNameForRun,
                    WorkflowChatRunStartError.WorkflowNameMismatch);
            }
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
                scopeIdForRun,
                ct);
        }

        if (!hasInlineWorkflowYamls)
        {
            registryDefinitionForRun = _workflowRegistry.GetDefinition(workflowNameForRun);
            if (registryDefinitionForRun == null)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.WorkflowNotFound);

            workflowYamlForRun = registryDefinitionForRun.WorkflowYaml;
        }

        var createdRun = await CreateRunActorAsync(
            new WorkflowDefinitionBinding(
                registryDefinitionForRun?.DefinitionActorId ?? string.Empty,
                workflowNameForRun,
                workflowYamlForRun,
                inlineWorkflowYamlMapForRun,
                scopeIdForRun),
            wrapAsFallbackTrigger: !hasInlineWorkflowYamls,
            ct);

        return new WorkflowActorResolutionResult(
            createdRun.Actor,
            workflowNameForRun,
            WorkflowChatRunStartError.None,
            createdRun.CreatedActorIds);
    }

    private async Task<WorkflowActorResolutionResult> ResolveFromSourceActorAsync(
        string actorId,
        bool hasInlineWorkflowYamls,
        bool hasRequestedWorkflowName,
        string requestedWorkflowName,
        string workflowNameForRun,
        string workflowYamlForRun,
        IReadOnlyDictionary<string, string> inlineWorkflowYamlMapForRun,
        string scopeIdHint,
        CancellationToken ct)
    {
        var sourceBinding = await _bindingReader.GetAsync(actorId, ct);
        if (sourceBinding == null)
            return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentNotFound);

        if (!sourceBinding.IsWorkflowCapable)
            return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentTypeNotSupported);

        var boundWorkflowName = WorkflowRunNameNormalizer.NormalizeWorkflowName(sourceBinding.WorkflowName);

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
                    string.Empty,
                    workflowNameForRun,
                    workflowYamlForRun,
                    inlineWorkflowYamlMapForRun,
                    ResolveScopeId(sourceBinding.ScopeId, scopeIdHint)),
                wrapAsFallbackTrigger: false,
                ct);
            return new WorkflowActorResolutionResult(
                inlineRunActor.Actor,
                workflowNameForRun,
                WorkflowChatRunStartError.None,
                inlineRunActor.CreatedActorIds);
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

        var registryDefinition = _workflowRegistry.GetDefinition(boundWorkflowName);
        var workflowYamlFromSource = ResolveWorkflowYamlForExecution(boundWorkflowName, sourceBinding, registryDefinition);
        if (string.IsNullOrWhiteSpace(workflowYamlFromSource))
        {
            return new WorkflowActorResolutionResult(
                null,
                boundWorkflowName,
                WorkflowChatRunStartError.AgentWorkflowNotConfigured);
        }

        var runActor = await CreateRunActorAsync(
            new WorkflowDefinitionBinding(
                ResolveDefinitionActorIdForExecution(sourceBinding, registryDefinition),
                boundWorkflowName,
                workflowYamlFromSource,
                sourceBinding.InlineWorkflowYamls,
                ResolveScopeId(sourceBinding.ScopeId, scopeIdHint)),
            wrapAsFallbackTrigger: true,
            ct);

        return new WorkflowActorResolutionResult(
            runActor.Actor,
            boundWorkflowName,
            WorkflowChatRunStartError.None,
            runActor.CreatedActorIds);
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

    private async Task<WorkflowRunCreationResult> CreateRunActorAsync(
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
        WorkflowActorBinding sourceBinding,
        WorkflowDefinitionRegistration? registryDefinition)
    {
        if (!string.IsNullOrWhiteSpace(sourceBinding.WorkflowYaml))
            return sourceBinding.WorkflowYaml;

        return registryDefinition?.WorkflowYaml
               ?? _workflowRegistry.GetYaml(workflowName)
               ?? string.Empty;
    }

    private static string ResolveDefinitionActorIdForExecution(
        WorkflowActorBinding sourceBinding,
        WorkflowDefinitionRegistration? registryDefinition)
    {
        if (!string.IsNullOrWhiteSpace(sourceBinding.EffectiveDefinitionActorId))
            return sourceBinding.EffectiveDefinitionActorId;

        return registryDefinition?.DefinitionActorId ?? string.Empty;
    }

    private static string ResolveScopeId(string? scopeId) =>
        string.IsNullOrWhiteSpace(scopeId) ? string.Empty : scopeId.Trim();

    private static string ResolveScopeId(string? sourceScopeId, string? scopeIdHint) =>
        !string.IsNullOrWhiteSpace(sourceScopeId)
            ? sourceScopeId.Trim()
            : scopeIdHint?.Trim() ?? string.Empty;

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
