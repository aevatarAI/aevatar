using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunActorResolver : IWorkflowRunActorResolver
{
    private readonly IWorkflowRunActorPort _actorPort;
    private readonly IWorkflowDefinitionLookupService _workflowLookup;
    private readonly WorkflowRunBehaviorOptions _behaviorOptions;

    public WorkflowRunActorResolver(
        IWorkflowRunActorPort actorPort,
        IWorkflowDefinitionLookupService workflowLookup,
        WorkflowRunBehaviorOptions? behaviorOptions = null)
    {
        _actorPort = actorPort;
        _workflowLookup = workflowLookup;
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
        var hasDefinitionActorId = !string.IsNullOrWhiteSpace(request.DefinitionActorId);
        if (hasDefinitionActorId && hasInlineWorkflowYamls)
        {
            return new WorkflowActorResolutionResult(
                null,
                string.Empty,
                null,
                WorkflowChatRunStartError.DefinitionSourceConflict);
        }

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
            {
                return new WorkflowActorResolutionResult(
                    null,
                    workflowNameForRun,
                    null,
                    WorkflowChatRunStartError.InvalidWorkflowYaml);
            }

            workflowNameForRun = inlineBundle.EntryWorkflowName;
            workflowYamlForRun = inlineBundle.EntryWorkflowYaml;
            inlineWorkflowYamlMapForRun = inlineBundle.WorkflowYamlsByName;
        }

        if (hasDefinitionActorId)
        {
            var definitionActorId = request.DefinitionActorId!;
            var existing = await _actorPort.GetDefinitionActorAsync(definitionActorId, ct);
            if (existing == null)
            {
                return new WorkflowActorResolutionResult(
                    null,
                    workflowNameForRun,
                    definitionActorId,
                    WorkflowChatRunStartError.DefinitionActorNotFound);
            }

            if (!await _actorPort.IsWorkflowDefinitionActorAsync(existing, ct))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    workflowNameForRun,
                    definitionActorId,
                    WorkflowChatRunStartError.DefinitionActorTypeNotSupported);
            }

            var bindingSnapshot = await _actorPort.GetDefinitionBindingSnapshotAsync(existing, ct);
            var boundWorkflowName = WorkflowRunNameNormalizer.NormalizeWorkflowName(bindingSnapshot?.WorkflowName);
            if (string.IsNullOrWhiteSpace(boundWorkflowName) || bindingSnapshot == null || !bindingSnapshot.IsBound)
            {
                return new WorkflowActorResolutionResult(
                    null,
                    workflowNameForRun,
                    definitionActorId,
                    WorkflowChatRunStartError.DefinitionActorWorkflowNotConfigured);
            }

            if (hasRequestedWorkflowName &&
                !string.Equals(requestedWorkflowName, boundWorkflowName, StringComparison.OrdinalIgnoreCase))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    boundWorkflowName,
                    definitionActorId,
                    WorkflowChatRunStartError.DefinitionBindingMismatch);
            }

            var isolatedRunActor = await _actorPort.CreateRunActorAsync(ct);
            await BindWorkflowDefinitionForRunAsync(
                isolatedRunActor,
                bindingSnapshot.WorkflowYaml,
                boundWorkflowName,
                bindingSnapshot.InlineWorkflowYamls,
                ct);
            return new WorkflowActorResolutionResult(
                isolatedRunActor,
                boundWorkflowName,
                definitionActorId,
                WorkflowChatRunStartError.None);
        }

        if (!hasInlineWorkflowYamls)
        {
            var yaml = _workflowLookup.GetYaml(workflowNameForRun);
            if (yaml == null)
            {
                return new WorkflowActorResolutionResult(
                    null,
                    workflowNameForRun,
                    null,
                    WorkflowChatRunStartError.WorkflowNotFound);
            }

            workflowYamlForRun = yaml;
        }

        var runActor = await _actorPort.CreateRunActorAsync(ct);
        if (hasInlineWorkflowYamls)
        {
            await BindWorkflowDefinitionForRunAsync(
                runActor,
                workflowYamlForRun,
                workflowNameForRun,
                inlineWorkflowYamlMapForRun,
                ct);
        }
        else
        {
            await BindWorkflowDefinitionForRunAsync(
                runActor,
                workflowYamlForRun,
                workflowNameForRun,
                null,
                ct);
        }

        return new WorkflowActorResolutionResult(
            runActor,
            workflowNameForRun,
            null,
            WorkflowChatRunStartError.None);
    }

    private Task BindWorkflowDefinitionForRunAsync(
        IActor actor,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        CancellationToken ct) =>
        _actorPort.BindWorkflowDefinitionAsync(actor, workflowYaml, workflowName, inlineWorkflowYamls, ct);

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
