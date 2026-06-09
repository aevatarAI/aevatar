using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.Abstractions.Runs;

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

    private readonly IWorkflowRunSeedQueryPort _seedQueryPort;
    private readonly IWorkflowRunProvisioningPort _runProvisioningPort;
    private readonly IWorkflowDefinitionParser _definitionParser;

    public WorkflowForkRunCommandTargetResolver(
        IWorkflowRunSeedQueryPort seedQueryPort,
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
        var seedView = await _seedQueryPort.GetResumeSeedAsync(sourceRunId, ct).ConfigureAwait(false);
        if (seedView == null)
            return Failure(WorkflowForkRunStartError.SourceRunNotFound(sourceRunId));

        if (!IsTerminal(seedView.Status))
            return Failure(WorkflowForkRunStartError.SourceRunNotTerminal(sourceRunId, seedView.Status));

        var workflowYaml = string.IsNullOrWhiteSpace(command.InlineYaml)
            ? seedView.WorkflowYaml
            : command.InlineYaml!;
        var inlineWorkflowYamls = CopyDictionary(command.InlineSubYamls ?? seedView.InlineWorkflowYamls);
        var variables = MergeVariables(seedView.Variables, command.VariableOverrides);
        var validation = await ValidateWorkflowAsync(sourceRunId, startAtStepId, workflowYaml, ct)
            .ConfigureAwait(false);
        if (!validation.Succeeded)
            return Failure(validation.Error!);

        WorkflowRunCreationReceipt creationReceipt;
        try
        {
            creationReceipt = await _runProvisioningPort.CreateRunAsync(
                new WorkflowDefinitionBinding(
                    DefinitionActorId: string.Empty,
                    WorkflowName: validation.WorkflowName,
                    WorkflowYaml: workflowYaml,
                    InlineWorkflowYamls: inlineWorkflowYamls,
                    ScopeId: ResolveScopeId(command.ScopeId, seedView.ScopeId)),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(WorkflowForkRunStartError.RunCreationFailed(sourceRunId, startAtStepId, ex.Message));
        }

        return CommandTargetResolution<WorkflowForkRunCommandTarget, WorkflowForkRunStartError>.Success(
                new WorkflowForkRunCommandTarget(
                    sourceRunId,
                    creationReceipt.ActorId,
                    validation.WorkflowName,
                    BuildChatRequest(command, seedView, creationReceipt.ActorId, validation.WorkflowName, variables),
                    creationReceipt.CreatedActorIds,
                    _runProvisioningPort));
    }

    private static WorkflowChatRunRequest BuildChatRequest(
        WorkflowForkRunCommand command,
        WorkflowRunResumeSeedView seedView,
        string actorId,
        string workflowName,
        IReadOnlyDictionary<string, string> variables)
    {
        var sourceRunId = Normalize(command.SourceRunId);
        var normalizedWorkflowName = Normalize(workflowName);
        var resumeInput = ResolveResumeInput(variables, command.Input);
        return new WorkflowChatRunRequest(
            Prompt: resumeInput,
            Source: WorkflowChatSource.DefinitionActor(actorId, normalizedWorkflowName),
            ScopeId: ResolveScopeId(command.ScopeId, seedView.ScopeId),
            CallerCredential: command.CallerCredential,
            Headers: command.Headers,
            CommandIdSeed: command.CommandId,
            CorrelationIdSeed: command.CorrelationId,
            ResumeSeed: new WorkflowChatRunResumeSeed(
                sourceRunId,
                Normalize(command.StartAtStepId),
                variables,
                Math.Max(0, command.Attempt)),
            TargetSeed: new WorkflowRunTargetSeed(
                actorId,
                normalizedWorkflowName,
                [actorId],
                WorkflowChatSource.DefinitionActor(actorId, normalizedWorkflowName)));
    }

    private async Task<WorkflowForkRunValidationResult> ValidateWorkflowAsync(
        string sourceRunId,
        string startAtStepId,
        string workflowYaml,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workflowYaml))
            return WorkflowForkRunValidationResult.Failure(
                WorkflowForkRunStartError.InvalidWorkflowYaml(sourceRunId, startAtStepId, "Workflow YAML is required."));

        var parseResult = await _definitionParser.ParseWorkflowYamlAsync(workflowYaml, ct).ConfigureAwait(false);
        if (!parseResult.Succeeded)
            return WorkflowForkRunValidationResult.Failure(
                WorkflowForkRunStartError.InvalidWorkflowYaml(sourceRunId, startAtStepId, parseResult.Error));

        return WorkflowForkRunValidationResult.Success(parseResult.WorkflowName);
    }

    private static CommandTargetResolution<WorkflowForkRunCommandTarget, WorkflowForkRunStartError> Failure(
        WorkflowForkRunStartError error) =>
        CommandTargetResolution<WorkflowForkRunCommandTarget, WorkflowForkRunStartError>.Failure(error);

    private static Dictionary<string, string> CopyDictionary(IReadOnlyDictionary<string, string>? source)
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

    private static string ResolveResumeInput(
        IReadOnlyDictionary<string, string> variables,
        string? commandInput) =>
        variables.TryGetValue("input", out var seedInput)
            ? seedInput ?? string.Empty
            : commandInput ?? string.Empty;

    private static bool IsTerminal(string status) =>
        !string.IsNullOrWhiteSpace(status) && TerminalStatuses.Contains(status.Trim());

    private static string ResolveScopeId(string? commandScopeId, string? sourceScopeId) =>
        !string.IsNullOrWhiteSpace(commandScopeId)
            ? commandScopeId.Trim()
            : sourceScopeId?.Trim() ?? string.Empty;

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
