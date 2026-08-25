using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Application.RunForks;

internal sealed class WorkflowForkRunCommandTargetResolver
    : ICommandTargetResolver<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunStartError>
{
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "failed",
        "completed",
        "stopped",
    };

    private readonly IWorkflowRunForkSeedQueryPort _seedQueryPort;
    private readonly IWorkflowRunProvisioningPort _runProvisioningPort;
    private readonly IWorkflowDefinitionParser _definitionParser;
    private readonly WorkflowParser _workflowParser = new();

    public WorkflowForkRunCommandTargetResolver(
        IWorkflowRunForkSeedQueryPort seedQueryPort,
        IWorkflowRunProvisioningPort runProvisioningPort,
        IWorkflowDefinitionParser definitionParser)
    {
        _seedQueryPort = seedQueryPort ?? throw new ArgumentNullException(nameof(seedQueryPort));
        _runProvisioningPort = runProvisioningPort ?? throw new ArgumentNullException(nameof(runProvisioningPort));
        _definitionParser = definitionParser ?? throw new ArgumentNullException(nameof(definitionParser));
    }

    public async Task<CommandTargetResolution<WorkflowForkRunCommandTarget, WorkflowForkRunStartError>> ResolveAsync(
        WorkflowForkRunCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sourceRunId = Normalize(command.SourceRunId);
        var startAtStepId = Normalize(command.StartAtStepId);
        var scopeId = Normalize(command.ScopeId);
        if (WorkflowCallerCredentialTokens.IsInvalidCredentialSet(
                command.CallerCredential?.BearerToken,
                command.CallerCredential?.Kind ?? NyxIdCallerCredentialKind.Unspecified,
                command.CallerCredential?.SourceReadableUserBearerToken))
        {
            return CommandTargetResolution<WorkflowForkRunCommandTarget, WorkflowForkRunStartError>.Failure(
                WorkflowForkRunStartError.InvalidCallerCredential(sourceRunId, startAtStepId));
        }

        var seedView = await _seedQueryPort.GetForkSeedAsync(scopeId, sourceRunId, ct).ConfigureAwait(false);
        if (seedView == null)
        {
            return CommandTargetResolution<WorkflowForkRunCommandTarget, WorkflowForkRunStartError>.Failure(
                WorkflowForkRunStartError.SourceRunNotFound(sourceRunId));
        }

        if (!string.IsNullOrWhiteSpace(scopeId) &&
            !string.Equals(scopeId, Normalize(seedView.ScopeId), StringComparison.Ordinal))
        {
            return CommandTargetResolution<WorkflowForkRunCommandTarget, WorkflowForkRunStartError>.Failure(
                WorkflowForkRunStartError.SourceRunNotFound(sourceRunId));
        }

        if (!IsTerminal(seedView.Status))
        {
            return CommandTargetResolution<WorkflowForkRunCommandTarget, WorkflowForkRunStartError>.Failure(
                WorkflowForkRunStartError.SourceRunNotTerminal(sourceRunId, seedView.Status));
        }

        var workflowYaml = ResolveWorkflowYaml(command, seedView);
        var inlineWorkflowYamls = CopyDictionary(command.InlineSubYamls ?? seedView.InlineWorkflowYamls);
        var preservesSourceArtifacts = PreservesSourceArtifacts(workflowYaml, inlineWorkflowYamls, seedView);
        var variables = MergeVariables(seedView.Variables, command.VariableOverrides);
        var validation = await ValidateWorkflowAsync(sourceRunId, startAtStepId, workflowYaml, ct)
            .ConfigureAwait(false);
        if (!validation.Succeeded)
        {
            return CommandTargetResolution<WorkflowForkRunCommandTarget, WorkflowForkRunStartError>.Failure(
                validation.Error!);
        }

        WorkflowRunCreationReceipt creationReceipt;
        try
        {
            creationReceipt = await _runProvisioningPort.CreateRunAsync(
                new WorkflowDefinitionBinding(
                    DefinitionActorId: string.Empty,
                    WorkflowName: validation.WorkflowName,
                    WorkflowYaml: workflowYaml,
                    InlineWorkflowYamls: inlineWorkflowYamls,
                    ExpectedExecutionMode: seedView.ExpectedExecutionMode,
                    ScopeId: scopeId,
                    CapabilityAdmissionPlan: preservesSourceArtifacts
                        ? seedView.CapabilityAdmissionPlan?.Clone()
                        : null,
                    WorkflowId: preservesSourceArtifacts ? seedView.WorkflowId : string.Empty,
                    RevisionId: preservesSourceArtifacts ? seedView.RevisionId : string.Empty,
                    DefinitionVersion: preservesSourceArtifacts ? Math.Max(0, seedView.DefinitionVersion) : 0,
                    ToolCatalogPolicyVersion: WorkflowToolCatalogPolicies.CurrentVersion),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CommandTargetResolution<WorkflowForkRunCommandTarget, WorkflowForkRunStartError>.Failure(
                WorkflowForkRunStartError.RunCreationFailed(sourceRunId, startAtStepId, ex.Message));
        }

        var source = WorkflowChatSource.DefinitionActor(creationReceipt.ActorId, validation.WorkflowName);
        var target = new WorkflowForkRunCommandTarget(
            sourceRunId,
            ResolveOriginalRunId(seedView, sourceRunId),
            startAtStepId,
            creationReceipt.ActorId,
            ResolveReceiptRunId(creationReceipt),
            validation.WorkflowName,
            BuildChatRunRequest(
                command,
                sourceRunId,
                startAtStepId,
                creationReceipt.ActorId,
                validation.WorkflowName,
                creationReceipt.CreatedActorIds,
                source,
                seedView,
                variables,
                scopeId),
            creationReceipt.CreatedActorIds,
            _runProvisioningPort);

        return CommandTargetResolution<WorkflowForkRunCommandTarget, WorkflowForkRunStartError>.Success(target);
    }

    private static string ResolveReceiptRunId(WorkflowRunCreationReceipt creationReceipt) =>
        string.IsNullOrWhiteSpace(creationReceipt.RunId)
            ? creationReceipt.ActorId
            : creationReceipt.RunId.Trim();

    private async Task<WorkflowForkRunValidationResult> ValidateWorkflowAsync(
        string sourceRunId,
        string startAtStepId,
        string workflowYaml,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workflowYaml))
        {
            return WorkflowForkRunValidationResult.Failure(
                WorkflowForkRunStartError.InvalidWorkflowYaml(
                    sourceRunId,
                    startAtStepId,
                    "Workflow YAML is required."));
        }

        var parseResult = await _definitionParser
            .ParseWorkflowYamlForPublicationAsync(workflowYaml, ct)
            .ConfigureAwait(false);
        if (!parseResult.Succeeded)
        {
            return WorkflowForkRunValidationResult.Failure(
                WorkflowForkRunStartError.InvalidWorkflowYaml(
                    sourceRunId,
                    startAtStepId,
                    parseResult.Error));
        }

        WorkflowDefinition workflow;
        try
        {
            workflow = _workflowParser.Parse(workflowYaml);
        }
        catch (Exception ex)
        {
            return WorkflowForkRunValidationResult.Failure(
                WorkflowForkRunStartError.InvalidWorkflowYaml(sourceRunId, startAtStepId, ex.Message));
        }

        if (workflow.GetStep(startAtStepId) == null)
        {
            return WorkflowForkRunValidationResult.Failure(
                WorkflowForkRunStartError.StartStepNotFound(sourceRunId, startAtStepId));
        }

        return WorkflowForkRunValidationResult.Success(parseResult.WorkflowName);
    }

    private static WorkflowChatRunRequest BuildChatRunRequest(
        WorkflowForkRunCommand command,
        string sourceRunId,
        string startAtStepId,
        string actorId,
        string workflowName,
        IReadOnlyList<string>? createdActorIds,
        WorkflowChatSource source,
        WorkflowRunForkSeedView seedView,
        IReadOnlyDictionary<string, string> variables,
        string scopeId)
    {
        return new WorkflowChatRunRequest(
            Prompt: ResolveResumeInput(variables, command.Input),
            Source: source,
            ExpectedExecutionMode: seedView.ExpectedExecutionMode,
            ScopeId: scopeId,
            CallerCredential: command.CallerCredential,
            CommandIdSeed: command.CommandId,
            CorrelationIdSeed: command.CorrelationId,
            ForkSeed: new WorkflowChatRunForkSeed(
                sourceRunId,
                startAtStepId,
                seedView.NormalizedValues == null
                    ? variables
                    : new Dictionary<string, string>(StringComparer.Ordinal),
                Math.Max(0, command.Attempt),
                ResolveStartStepIdempotency(seedView, startAtStepId),
                ResolveOriginalRunId(seedView, sourceRunId),
                seedView.NormalizedValues?.Clone(),
                seedView.NormalizedValues == null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : CopyDictionary(command.VariableOverrides)),
            TargetSeed: new WorkflowRunTargetSeed(
                actorId,
                workflowName,
                createdActorIds,
                source));
    }

    private static IReadOnlyDictionary<string, string> MergeVariables(
        IReadOnlyDictionary<string, string> seedVariables,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var merged = CopyDictionary(seedVariables);
        if (overrides == null || overrides.Count == 0)
            return merged;

        foreach (var (key, value) in overrides)
        {
            var normalizedKey = Normalize(key);
            if (normalizedKey.Length == 0)
                continue;

            merged[normalizedKey] = value ?? string.Empty;
        }

        return merged;
    }

    private static Dictionary<string, string> CopyDictionary(
        IReadOnlyDictionary<string, string>? source)
    {
        var destination = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source == null || source.Count == 0)
            return destination;

        foreach (var (key, value) in source)
        {
            var normalizedKey = Normalize(key);
            if (normalizedKey.Length == 0)
                continue;

            destination[normalizedKey] = value ?? string.Empty;
        }

        return destination;
    }

    private static string ResolveWorkflowYaml(
        WorkflowForkRunCommand command,
        WorkflowRunForkSeedView seedView) =>
        string.IsNullOrWhiteSpace(command.InlineYaml)
            ? seedView.WorkflowYaml
            : command.InlineYaml!;

    private static bool PreservesSourceArtifacts(
        string workflowYaml,
        IReadOnlyDictionary<string, string> inlineWorkflowYamls,
        WorkflowRunForkSeedView seedView)
    {
        if (!string.Equals(workflowYaml, seedView.WorkflowYaml, StringComparison.Ordinal))
            return false;

        return DictionaryEquals(inlineWorkflowYamls, CopyDictionary(seedView.InlineWorkflowYamls));
    }

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
            return false;

        return left.All(entry =>
            right.TryGetValue(entry.Key, out var value) &&
            string.Equals(entry.Value, value, StringComparison.Ordinal));
    }

    private static string ResolveResumeInput(
        IReadOnlyDictionary<string, string> variables,
        string? commandInput) =>
        variables.TryGetValue("input", out var seedInput)
            ? seedInput ?? string.Empty
            : commandInput ?? string.Empty;

    private static WorkflowStepIdempotencyView? ResolveStartStepIdempotency(
        WorkflowRunForkSeedView seedView,
        string startAtStepId)
    {
        if (seedView.IdempotencyByStepId == null ||
            string.IsNullOrWhiteSpace(startAtStepId))
        {
            return null;
        }

        return seedView.IdempotencyByStepId.TryGetValue(startAtStepId, out var idempotency)
            ? idempotency
            : null;
    }

    private static string ResolveOriginalRunId(
        WorkflowRunForkSeedView seedView,
        string sourceRunId)
    {
        var originalRunId = Normalize(seedView.OriginalRunId);
        return string.IsNullOrWhiteSpace(originalRunId) ? sourceRunId : originalRunId;
    }

    private static bool IsTerminal(string status) =>
        !string.IsNullOrWhiteSpace(status) && TerminalStatuses.Contains(status.Trim());

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private sealed record WorkflowForkRunValidationResult(
        bool Succeeded,
        string WorkflowName,
        WorkflowForkRunStartError? Error)
    {
        public static WorkflowForkRunValidationResult Success(string workflowName) =>
            new(true, Normalize(workflowName), null);

        public static WorkflowForkRunValidationResult Failure(WorkflowForkRunStartError error)
        {
            ArgumentNullException.ThrowIfNull(error);
            return new WorkflowForkRunValidationResult(false, string.Empty, error);
        }
    }
}
