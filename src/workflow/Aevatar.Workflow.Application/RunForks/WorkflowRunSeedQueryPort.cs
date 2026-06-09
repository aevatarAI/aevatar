using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.RunForks;

internal sealed class WorkflowRunSeedQueryPort : IWorkflowRunSeedQueryPort
{
    private readonly IWorkflowRunBindingReader _bindingReader;
    private readonly IWorkflowExecutionArtifactQueryPort _artifactQueryPort;

    public WorkflowRunSeedQueryPort(
        IWorkflowRunBindingReader bindingReader,
        IWorkflowExecutionArtifactQueryPort artifactQueryPort)
    {
        _bindingReader = bindingReader ?? throw new ArgumentNullException(nameof(bindingReader));
        _artifactQueryPort = artifactQueryPort ?? throw new ArgumentNullException(nameof(artifactQueryPort));
    }

    public async Task<WorkflowRunResumeSeedView?> GetResumeSeedAsync(
        string runId,
        CancellationToken ct = default)
    {
        var normalizedRunId = Normalize(runId);
        if (normalizedRunId.Length == 0)
            return null;

        var bindings = await _bindingReader.ListByRunIdAsync(normalizedRunId, take: 20, ct).ConfigureAwait(false);
        var binding = SelectRunBinding(bindings);
        if (binding == null)
            return null;

        var report = await _artifactQueryPort.GetWorkflowRunReportArtifactAsync(binding.ActorId, ct).ConfigureAwait(false);
        if (report == null)
            return null;

        return new WorkflowRunResumeSeedView(
            normalizedRunId,
            ResolveWorkflowName(binding, report),
            binding.WorkflowYaml,
            CopyDictionary(binding.InlineWorkflowYamls),
            BuildVariables(report),
            CompletedStepIds(report),
            LastFailedStepId(report),
            ToStatus(report.CompletionStatus),
            report.FinalError ?? string.Empty,
            binding.ScopeId);
    }

    private static WorkflowActorBinding? SelectRunBinding(IReadOnlyList<WorkflowActorBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            if (binding.ActorKind == WorkflowActorKind.Run && binding.IsWorkflowCapable)
                return binding;
        }

        return null;
    }

    private static string ResolveWorkflowName(
        WorkflowActorBinding binding,
        WorkflowRunReport report)
    {
        var bindingWorkflowName = Normalize(binding.WorkflowName);
        return bindingWorkflowName.Length > 0
            ? bindingWorkflowName
            : Normalize(report.WorkflowName);
    }

    private static IReadOnlyDictionary<string, string> BuildVariables(WorkflowRunReport report)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(report.Input))
            variables["input"] = report.Input;

        foreach (var step in report.Steps)
        {
            if (step.Success != true)
                continue;

            var variableName = Normalize(step.AssignedVariable);
            if (variableName.Length == 0)
                continue;

            variables[variableName] = step.AssignedValue ?? string.Empty;
        }

        return variables;
    }

    private static IReadOnlyList<string> CompletedStepIds(WorkflowRunReport report) =>
        report.Steps
            .Where(static x => x.CompletedAt.HasValue)
            .Select(static x => Normalize(x.StepId))
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string LastFailedStepId(WorkflowRunReport report) =>
        report.Steps
            .Where(static x => x.Success == false)
            .OrderByDescending(static x => x.CompletedAt ?? x.RequestedAt ?? DateTimeOffset.MinValue)
            .Select(static x => Normalize(x.StepId))
            .FirstOrDefault(static x => x.Length > 0) ?? string.Empty;

    private static string ToStatus(WorkflowRunCompletionStatus status) =>
        status switch
        {
            WorkflowRunCompletionStatus.Completed => "completed",
            WorkflowRunCompletionStatus.Failed => "failed",
            WorkflowRunCompletionStatus.Stopped => "stopped",
            WorkflowRunCompletionStatus.TimedOut => "failed",
            WorkflowRunCompletionStatus.Running => "running",
            WorkflowRunCompletionStatus.Disabled => "disabled",
            WorkflowRunCompletionStatus.NotFound => "not_found",
            _ => "unknown",
        };

    private static IReadOnlyDictionary<string, string> CopyDictionary(IReadOnlyDictionary<string, string>? source)
    {
        if (source == null || source.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        return new Dictionary<string, string>(source, StringComparer.Ordinal);
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
