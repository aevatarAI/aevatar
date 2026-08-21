using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunActorResolver : IWorkflowRunActorResolver
{
    private readonly IWorkflowActorBindingReader _bindingReader;
    private readonly IWorkflowRunProvisioningPort _runProvisioningPort;
    private readonly IWorkflowDefinitionParser _definitionParser;
    private readonly IWorkflowDefinitionCatalog _workflowRegistry;
    private readonly IWorkflowDraftRunCapabilityAdmissionService? _draftRunCapabilityAdmissionService;
    private readonly WorkflowRunBehaviorOptions _behaviorOptions;

    public WorkflowRunActorResolver(
        IWorkflowActorBindingReader bindingReader,
        IWorkflowRunProvisioningPort runProvisioningPort,
        IWorkflowDefinitionParser definitionParser,
        IWorkflowDefinitionCatalog workflowRegistry,
        WorkflowRunBehaviorOptions? behaviorOptions = null)
    {
        _bindingReader = bindingReader ?? throw new ArgumentNullException(nameof(bindingReader));
        _runProvisioningPort = runProvisioningPort ?? throw new ArgumentNullException(nameof(runProvisioningPort));
        _definitionParser = definitionParser ?? throw new ArgumentNullException(nameof(definitionParser));
        _workflowRegistry = workflowRegistry ?? throw new ArgumentNullException(nameof(workflowRegistry));
        _draftRunCapabilityAdmissionService = null;
        _behaviorOptions = behaviorOptions ?? new WorkflowRunBehaviorOptions();
    }

    public WorkflowRunActorResolver(
        IWorkflowActorBindingReader bindingReader,
        IWorkflowRunProvisioningPort runProvisioningPort,
        IWorkflowDefinitionParser definitionParser,
        IWorkflowDefinitionCatalog workflowRegistry,
        IWorkflowDraftRunCapabilityAdmissionService draftRunCapabilityAdmissionService,
        WorkflowRunBehaviorOptions? behaviorOptions = null)
        : this(
            bindingReader,
            runProvisioningPort,
            definitionParser,
            workflowRegistry,
            behaviorOptions)
    {
        _draftRunCapabilityAdmissionService = draftRunCapabilityAdmissionService
            ?? throw new ArgumentNullException(nameof(draftRunCapabilityAdmissionService));
    }

    public async Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct = default)
    {
        if (request.ExpectedExecutionMode == ExternalCapabilityExecutionMode.Unspecified ||
            !Enum.IsDefined(request.ExpectedExecutionMode))
        {
            throw new InvalidOperationException("Workflow expected execution mode is required.");
        }

        // Refactor (iter112/cluster-3): Old pattern: resolver rebuilt source from legacy request mirrors. New principle: Application consumes the typed WorkflowChatSource as the single command source.
        var source = request.Source;
        var requestedWorkflowName = ResolveRequestedWorkflowName(source);
        var sourceActorId = ResolveSourceActorId(source);
        var inlineBundleSource = source.InlineBundle;
        var inlineWorkflowDocuments = inlineBundleSource?.YamlDocuments ?? [];
        var hasInlineWorkflowYamls = inlineWorkflowDocuments.Count > 0;
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

        if (request.ResolvedDefinitionBinding is { } resolvedDefinitionBinding)
        {
            return await ResolveFromResolvedDefinitionBindingAsync(
                resolvedDefinitionBinding,
                sourceActorId,
                hasInlineWorkflowYamls,
                hasRequestedWorkflowName,
                requestedWorkflowName,
                workflowNameForRun,
                scopeIdForRun,
                ct);
        }

        if (hasInlineWorkflowYamls)
        {
            var inlineBundle = await _definitionParser
                .ParseInlineWorkflowBundleForPublicationAsync(inlineWorkflowDocuments, ct);
            if (!inlineBundle.Succeeded)
                return new WorkflowActorResolutionResult(
                    null,
                    workflowNameForRun,
                    WorkflowChatRunStartError.InvalidWorkflowYaml,
                    WorkflowChatRunStartFailureDetail.Create(
                        WorkflowChatRunStartError.InvalidWorkflowYaml,
                        inlineBundle.Error,
                        inlineBundle.ExternalCapabilityReadiness));

            workflowNameForRun = inlineBundle.EntryWorkflowName;
            workflowYamlForRun = inlineBundle.EntryWorkflowYaml;
            inlineWorkflowYamlMapForRun = ResolveInlineWorkflowYamlsForRun(inlineBundle);

            if (hasRequestedWorkflowName &&
                !string.Equals(requestedWorkflowName, workflowNameForRun, StringComparison.OrdinalIgnoreCase))
            {
                return new WorkflowActorResolutionResult(
                    null,
                    workflowNameForRun,
                    WorkflowChatRunStartError.WorkflowNameMismatch);
            }
        }

        if (!string.IsNullOrWhiteSpace(sourceActorId))
        {
            return await ResolveFromSourceActorAsync(
                sourceActorId,
                hasInlineWorkflowYamls,
                hasRequestedWorkflowName,
                requestedWorkflowName,
                workflowNameForRun,
                workflowYamlForRun,
                inlineWorkflowYamlMapForRun,
                scopeIdForRun,
                request,
                ct);
        }

        if (!hasInlineWorkflowYamls)
        {
            registryDefinitionForRun = _workflowRegistry.GetDefinition(workflowNameForRun);
            if (registryDefinitionForRun == null)
                return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.WorkflowNotFound);

            workflowYamlForRun = registryDefinitionForRun.WorkflowYaml;
            var publicationParse = await _definitionParser
                .ParseWorkflowYamlForPublicationAsync(workflowYamlForRun, ct);
            if (!publicationParse.Succeeded)
            {
                return new WorkflowActorResolutionResult(
                    null,
                    workflowNameForRun,
                    WorkflowChatRunStartError.InvalidWorkflowYaml,
                    WorkflowChatRunStartFailureDetail.Create(
                        WorkflowChatRunStartError.InvalidWorkflowYaml,
                        publicationParse.Error,
                        publicationParse.ExternalCapabilityReadiness));
            }
        }

        var draftAdmission = hasInlineWorkflowYamls
            ? await PrepareDraftRunCapabilityAdmissionAsync(
                request,
                workflowYamlForRun,
                inlineWorkflowYamlMapForRun,
                scopeIdForRun,
                ct)
            : DraftRunCapabilityAdmissionPreparation.NotApplicable;
        if (draftAdmission.FailureDetail is not null)
        {
            return new WorkflowActorResolutionResult(
                null,
                workflowNameForRun,
                draftAdmission.FailureDetail.Error,
                draftAdmission.FailureDetail);
        }

        var createdRun = await CreateRunActorAsync(
            new WorkflowDefinitionBinding(
                registryDefinitionForRun?.DefinitionActorId ?? string.Empty,
                workflowNameForRun,
                workflowYamlForRun,
                inlineWorkflowYamlMapForRun,
                request.ExpectedExecutionMode,
                scopeIdForRun,
                hasInlineWorkflowYamls ? WorkflowRunOrigins.Draft : WorkflowRunOrigins.AdHocChat,
                SourceKind: draftAdmission.Admission?.SourceKind ?? string.Empty,
                CapabilityAdmissionPlan: draftAdmission.Admission?.CapabilityAdmissionPlan.Clone(),
                WorkflowId: draftAdmission.Admission?.WorkflowId ?? string.Empty,
                RevisionId: draftAdmission.Admission?.RevisionId ?? string.Empty,
                ToolCatalogPolicyVersion: WorkflowToolCatalogPolicies.CurrentVersion),
            wrapAsFallbackTrigger: !hasInlineWorkflowYamls,
            ct);

        return new WorkflowActorResolutionResult(
            createdRun,
            workflowNameForRun,
            WorkflowChatRunStartError.None);
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
        WorkflowChatRunRequest request,
        CancellationToken ct)
    {
        var expectedExecutionMode = request.ExpectedExecutionMode;
        var sourceBinding = await _bindingReader.GetAsync(actorId, ct);
        if (sourceBinding == null)
            return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentNotFound);

        if (!sourceBinding.IsWorkflowCapable)
            return new WorkflowActorResolutionResult(null, workflowNameForRun, WorkflowChatRunStartError.AgentTypeNotSupported);

        if (sourceBinding.ExpectedExecutionMode != expectedExecutionMode)
        {
            return new WorkflowActorResolutionResult(
                null,
                workflowNameForRun,
                WorkflowChatRunStartError.WorkflowBindingMismatch);
        }

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

            var scopeIdForRun = ResolveScopeId(sourceBinding.ScopeId, scopeIdHint);
            var draftAdmission = await PrepareDraftRunCapabilityAdmissionAsync(
                request,
                workflowYamlForRun,
                inlineWorkflowYamlMapForRun,
                scopeIdForRun,
                ct);
            if (draftAdmission.FailureDetail is not null)
            {
                return new WorkflowActorResolutionResult(
                    null,
                    workflowNameForRun,
                    draftAdmission.FailureDetail.Error,
                    draftAdmission.FailureDetail);
            }

            var inlineRunActor = await CreateRunActorAsync(
                new WorkflowDefinitionBinding(
                    string.Empty,
                    workflowNameForRun,
                    workflowYamlForRun,
                    inlineWorkflowYamlMapForRun,
                    expectedExecutionMode,
                    scopeIdForRun,
                    WorkflowRunOrigins.Draft,
                    SourceKind: draftAdmission.Admission?.SourceKind ?? string.Empty,
                    CapabilityAdmissionPlan: draftAdmission.Admission?.CapabilityAdmissionPlan.Clone(),
                    WorkflowId: draftAdmission.Admission?.WorkflowId ?? string.Empty,
                    RevisionId: draftAdmission.Admission?.RevisionId ?? string.Empty,
                    ToolCatalogPolicyVersion: WorkflowToolCatalogPolicies.CurrentVersion),
                wrapAsFallbackTrigger: false,
                ct);
            return new WorkflowActorResolutionResult(
                inlineRunActor,
                workflowNameForRun,
                WorkflowChatRunStartError.None);
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
                expectedExecutionMode,
                ResolveScopeId(sourceBinding.ScopeId, scopeIdHint),
                WorkflowRunOrigins.AdHocChat,
                SourceKind: sourceBinding.SourceKind,
                CapabilityAdmissionPlan: sourceBinding.CapabilityAdmissionPlan?.Clone(),
                WorkflowId: sourceBinding.WorkflowId,
                RevisionId: sourceBinding.RevisionId,
                DefinitionVersion: ResolveDefinitionVersionForExecution(sourceBinding),
                ToolCatalogPolicyVersion: sourceBinding.ToolCatalogPolicyVersion),
            wrapAsFallbackTrigger: true,
            ct);

        return new WorkflowActorResolutionResult(
            runActor,
            boundWorkflowName,
            WorkflowChatRunStartError.None);
    }

    private async Task<WorkflowActorResolutionResult> ResolveFromResolvedDefinitionBindingAsync(
        WorkflowDefinitionBinding resolvedDefinitionBinding,
        string? sourceActorId,
        bool hasInlineWorkflowYamls,
        bool hasRequestedWorkflowName,
        string requestedWorkflowName,
        string workflowNameForRun,
        string scopeIdHint,
        CancellationToken ct)
    {
        if (hasInlineWorkflowYamls)
            throw new InvalidOperationException(
                "Resolved workflow definition binding cannot be combined with inline workflow YAML.");

        var definitionActorId = resolvedDefinitionBinding.DefinitionActorId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(definitionActorId))
        {
            return new WorkflowActorResolutionResult(
                null,
                workflowNameForRun,
                WorkflowChatRunStartError.AgentNotFound);
        }

        var requestedSourceActorId = sourceActorId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(requestedSourceActorId) &&
            !string.Equals(requestedSourceActorId, definitionActorId, StringComparison.Ordinal))
        {
            return new WorkflowActorResolutionResult(
                null,
                workflowNameForRun,
                WorkflowChatRunStartError.WorkflowBindingMismatch);
        }

        var boundWorkflowName = WorkflowRunNameNormalizer.NormalizeWorkflowName(resolvedDefinitionBinding.WorkflowName);
        if (string.IsNullOrWhiteSpace(boundWorkflowName) || !HasDefinitionPayload(resolvedDefinitionBinding))
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

        var runActor = await CreateRunActorAsync(
            new WorkflowDefinitionBinding(
                definitionActorId,
                boundWorkflowName,
                resolvedDefinitionBinding.WorkflowYaml ?? string.Empty,
                resolvedDefinitionBinding.InlineWorkflowYamls
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                resolvedDefinitionBinding.ExpectedExecutionMode,
                ResolveScopeId(resolvedDefinitionBinding.ScopeId, scopeIdHint),
                string.IsNullOrWhiteSpace(resolvedDefinitionBinding.RunOrigin)
                    ? WorkflowRunOrigins.AdHocChat
                    : resolvedDefinitionBinding.RunOrigin.Trim(),
                resolvedDefinitionBinding.ScheduleId?.Trim() ?? string.Empty,
                SourceKind: resolvedDefinitionBinding.SourceKind?.Trim() ?? string.Empty,
                CapabilityAdmissionPlan: resolvedDefinitionBinding.CapabilityAdmissionPlan?.Clone(),
                WorkflowId: resolvedDefinitionBinding.WorkflowId?.Trim() ?? string.Empty,
                RevisionId: resolvedDefinitionBinding.RevisionId?.Trim() ?? string.Empty,
                DefinitionVersion: Math.Max(0, resolvedDefinitionBinding.DefinitionVersion),
                ToolCatalogPolicyVersion: resolvedDefinitionBinding.ToolCatalogPolicyVersion),
            wrapAsFallbackTrigger: false,
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

    private static string ResolveRequestedWorkflowName(WorkflowChatSource source)
    {
        return source.Kind switch
        {
            WorkflowChatSourceKind.CatalogWorkflow =>
                WorkflowRunNameNormalizer.NormalizeWorkflowName(source.CatalogName?.WorkflowName),
            WorkflowChatSourceKind.DefinitionActor =>
                WorkflowRunNameNormalizer.NormalizeWorkflowName(source.DefinitionActorSource?.WorkflowName),
            WorkflowChatSourceKind.InlineYamlBundle =>
                WorkflowRunNameNormalizer.NormalizeWorkflowName(source.InlineBundle?.EntryName),
            _ => string.Empty,
        };
    }

    private static string? ResolveSourceActorId(WorkflowChatSource source)
    {
        // Refactor (phase9/cluster-349):
        //   Old pattern: Direct source could resolve a source actor binding through DefinitionActorSource.
        //   New principle: only actor-addressed source variants participate in binding lookup.
        return source.Kind switch
        {
            WorkflowChatSourceKind.DefinitionActor => source.DefinitionActorSource?.ActorId,
            WorkflowChatSourceKind.InlineYamlBundle => source.InlineBundle?.ActorId,
            _ => null,
        };
    }

    private async Task<WorkflowRunCreationReceipt> CreateRunActorAsync(
        WorkflowDefinitionBinding definitionBinding,
        bool wrapAsFallbackTrigger,
        CancellationToken ct)
    {
        try
        {
            return await _runProvisioningPort.CreateRunAsync(definitionBinding, ct);
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

    private static long ResolveDefinitionVersionForExecution(WorkflowActorBinding sourceBinding) =>
        sourceBinding.ActorKind == WorkflowActorKind.Definition
            ? Math.Max(0, sourceBinding.SourceVersion)
            : 0;

    private static string ResolveScopeId(string? scopeId) =>
        string.IsNullOrWhiteSpace(scopeId) ? string.Empty : scopeId.Trim();

    private static string ResolveScopeId(string? sourceScopeId, string? scopeIdHint) =>
        !string.IsNullOrWhiteSpace(sourceScopeId)
            ? sourceScopeId.Trim()
            : scopeIdHint?.Trim() ?? string.Empty;

    private async Task<DraftRunCapabilityAdmissionPreparation> PrepareDraftRunCapabilityAdmissionAsync(
        WorkflowChatRunRequest request,
        string workflowYaml,
        IReadOnlyDictionary<string, string> inlineWorkflowYamls,
        string scopeId,
        CancellationToken ct)
    {
        if (_draftRunCapabilityAdmissionService is null)
            return DraftRunCapabilityAdmissionPreparation.NotApplicable;

        try
        {
            var admission = await _draftRunCapabilityAdmissionService.PrepareAsync(
                new WorkflowDraftRunCapabilityAdmissionRequest(
                    scopeId,
                    request.CommandIdSeed ?? string.Empty,
                    request.CallerCredential,
                    workflowYaml,
                    inlineWorkflowYamls,
                    request.CallerNyxIdCredentialSelection),
                ct);
            return new DraftRunCapabilityAdmissionPreparation(admission, null);
        }
        catch (WorkflowExternalCapabilityAdmissionException ex)
        {
            return new DraftRunCapabilityAdmissionPreparation(
                null,
                WorkflowChatRunStartFailureDetail.Create(
                    WorkflowChatRunStartError.ExternalCapabilityNotReady,
                    ex.Message,
                    ex.Readiness));
        }
    }

    private static IReadOnlyDictionary<string, string> ResolveInlineWorkflowYamlsForRun(
        WorkflowInlineYamlBundleParseResult inlineBundle) =>
        inlineBundle.WorkflowYamlsByName
            .Where(entry => !string.Equals(
                entry.Key,
                inlineBundle.EntryWorkflowName,
                StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                static entry => entry.Key,
                static entry => entry.Value,
                StringComparer.OrdinalIgnoreCase);

    private static bool HasDefinitionPayload(WorkflowDefinitionBinding binding) =>
        !string.IsNullOrWhiteSpace(binding.WorkflowYaml) ||
        binding.InlineWorkflowYamls is { Count: > 0 };

    private sealed record DraftRunCapabilityAdmissionPreparation(
        WorkflowDraftRunCapabilityAdmissionResult? Admission,
        WorkflowChatRunStartFailureDetail? FailureDetail)
    {
        public static DraftRunCapabilityAdmissionPreparation NotApplicable { get; } = new(null, null);
    }
}
