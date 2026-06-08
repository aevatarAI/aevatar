using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Application.RunForks;

public sealed class WorkflowForkRunService : IWorkflowForkRunService
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
    private readonly ICommandContextPolicy _contextPolicy;
    private readonly ICommandEnvelopeFactory<WorkflowChatRunRequest> _envelopeFactory;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly WorkflowParser _workflowParser = new();

    public WorkflowForkRunService(
        IWorkflowRunSeedQueryPort seedQueryPort,
        IWorkflowRunProvisioningPort runProvisioningPort,
        IWorkflowDefinitionParser definitionParser,
        ICommandContextPolicy contextPolicy,
        ICommandEnvelopeFactory<WorkflowChatRunRequest> envelopeFactory,
        IActorDispatchPort dispatchPort)
    {
        _seedQueryPort = seedQueryPort ?? throw new ArgumentNullException(nameof(seedQueryPort));
        _runProvisioningPort = runProvisioningPort ?? throw new ArgumentNullException(nameof(runProvisioningPort));
        _definitionParser = definitionParser ?? throw new ArgumentNullException(nameof(definitionParser));
        _contextPolicy = contextPolicy ?? throw new ArgumentNullException(nameof(contextPolicy));
        _envelopeFactory = envelopeFactory ?? throw new ArgumentNullException(nameof(envelopeFactory));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<WorkflowForkRunResult> ForkAsync(
        WorkflowForkRunCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sourceRunId = Normalize(command.SourceRunId);
        var startAtStepId = Normalize(command.StartAtStepId);
        var seedView = await _seedQueryPort.GetResumeSeedAsync(sourceRunId, ct).ConfigureAwait(false);
        if (seedView == null)
            return WorkflowForkRunResult.Failure(WorkflowForkRunStartError.SourceRunNotFound(sourceRunId));

        if (!IsTerminal(seedView.Status))
        {
            return WorkflowForkRunResult.Failure(
                WorkflowForkRunStartError.SourceRunNotTerminal(sourceRunId, seedView.Status));
        }

        var workflowYaml = string.IsNullOrWhiteSpace(command.InlineYaml)
            ? seedView.WorkflowYaml
            : command.InlineYaml!;
        var inlineWorkflowYamls = CopyDictionary(command.InlineSubYamls ?? seedView.InlineWorkflowYamls);
        var variables = MergeVariables(seedView.Variables, command.VariableOverrides);
        var validation = await ValidateWorkflowAsync(sourceRunId, startAtStepId, workflowYaml, ct)
            .ConfigureAwait(false);
        if (!validation.Succeeded)
            return WorkflowForkRunResult.Failure(validation.Error!);

        WorkflowRunCreationReceipt creationReceipt;
        try
        {
            creationReceipt = await _runProvisioningPort.CreateRunAsync(
                new WorkflowDefinitionBinding(
                    DefinitionActorId: string.Empty,
                    WorkflowName: validation.WorkflowName,
                    WorkflowYaml: workflowYaml,
                    InlineWorkflowYamls: inlineWorkflowYamls),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return WorkflowForkRunResult.Failure(
                WorkflowForkRunStartError.RunCreationFailed(sourceRunId, startAtStepId, ex.Message));
        }

        var resumeInput = ResolveResumeInput(variables, command.Input);
        var request = new WorkflowChatRunRequest(
            Prompt: resumeInput,
            Source: WorkflowChatSource.DefinitionActor(creationReceipt.ActorId, validation.WorkflowName),
            CommandIdSeed: command.CommandId,
            CorrelationIdSeed: command.CorrelationId,
            ResumeSeed: new WorkflowChatRunResumeSeed(
                sourceRunId,
                startAtStepId,
                variables,
                Math.Max(0, command.Attempt)),
            TargetSeed: new WorkflowRunTargetSeed(
                creationReceipt.ActorId,
                validation.WorkflowName,
                creationReceipt.CreatedActorIds,
                WorkflowChatSource.DefinitionActor(creationReceipt.ActorId, validation.WorkflowName)));
        var context = _contextPolicy.Create(
            creationReceipt.ActorId,
            commandId: command.CommandId,
            correlationId: command.CorrelationId);
        var envelope = _envelopeFactory.CreateEnvelope(request, context);

        DispatchAdmission admission;
        try
        {
            admission = await _dispatchPort.DispatchAsync(creationReceipt.ActorId, envelope, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TryDestroyCreatedActorsAsync(creationReceipt.CreatedActorIds).ConfigureAwait(false);
            return WorkflowForkRunResult.Failure(
                WorkflowForkRunStartError.DispatchFailed(sourceRunId, startAtStepId, ex.Message));
        }

        return WorkflowForkRunResult.Accepted(new WorkflowForkRunAcceptedReceipt(
            sourceRunId,
            creationReceipt.ActorId,
            validation.WorkflowName,
            admission.Accepted,
            admission.CommandId,
            admission.CorrelationId,
            admission.AckedAt));
    }

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

        var parseResult = await _definitionParser.ParseWorkflowYamlAsync(workflowYaml, ct).ConfigureAwait(false);
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

    private async Task TryDestroyCreatedActorsAsync(IReadOnlyList<string> actorIds)
    {
        foreach (var actorId in actorIds
                     .Where(static x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal)
                     .Reverse())
        {
            try
            {
                await _runProvisioningPort.DestroyAsync(actorId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
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

    private static string ResolveResumeInput(
        IReadOnlyDictionary<string, string> variables,
        string? commandInput)
    {
        if (variables.TryGetValue("input", out var seedInput))
            return seedInput ?? string.Empty;

        return commandInput ?? string.Empty;
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
