using System.Text;
using System.Text.RegularExpressions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Models;

using Aevatar.Studio.Application.Scripts.Contracts;
namespace Aevatar.Studio.Hosting.Endpoints;

internal sealed record WorkflowGenerateRequest(
    string Prompt,
    string? CurrentYaml,
    IReadOnlyCollection<string>? AvailableWorkflowNames,
    IReadOnlyDictionary<string, string>? Metadata);

internal sealed record WorkflowGenerateResult(
    string Yaml,
    int Attempts,
    IReadOnlyList<ValidationFinding> Findings);

internal enum WorkflowGenerateProgressStage
{
    Starting,
    Queued,
    GeneratingDraft,
    ValidatingDraft,
    RepairingDraft,
    Completed,
}

internal sealed record WorkflowGenerateProgress(
    WorkflowGenerateProgressStage Stage,
    int Attempt,
    string Message);

internal sealed class WorkflowGenerateOrchestrator
{
    private const int MaxAttempts = 3;
    private static readonly Regex YamlFenceRegex = new(
        @"```(?:ya?ml)?\s*\n([\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] AuthoringSchemaRules =
    [
        "Use only these step-level fields: id, type, target_role (or role), parameters, next, branches, children, retry, on_error, timeout_ms.",
        "Do not put model, provider, temperature, max_tokens, max_history_messages, connectors, or system_prompt on steps. Those belong under roles[*].",
        "Do not use steps[*].messages.",
        "Do not use steps[*].params. Put step options under steps[*].parameters.",
        "If you need to shape LLM input, use steps[*].parameters.prompt_prefix.",
    ];

    private readonly WorkflowEditorService _editorService;

    public WorkflowGenerateOrchestrator(WorkflowEditorService editorService)
    {
        _editorService = editorService;
    }

    public async Task<WorkflowGenerateResult> GenerateAsync(
        WorkflowGenerateRequest request,
        Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<string?>> generateTurnAsync,
        Func<WorkflowGenerateProgress, CancellationToken, Task>? onProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(generateTurnAsync);

        var normalizedPrompt = (request.Prompt ?? string.Empty).Trim();
        if (normalizedPrompt.Length == 0)
            throw new InvalidOperationException("Workflow authoring prompt is required.");

        var currentYaml = (request.CurrentYaml ?? string.Empty).Trim();
        var lastCandidate = currentYaml;
        IReadOnlyList<ValidationFinding> lastFindings = [];

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (onProgress != null)
            {
                await onProgress(
                    new WorkflowGenerateProgress(
                        WorkflowGenerateProgressStage.GeneratingDraft,
                        attempt,
                        $"Generating workflow draft (attempt {attempt}/{MaxAttempts})..."),
                    ct);
            }

            var prompt = attempt == 1
                ? BuildInitialPrompt(normalizedPrompt, currentYaml)
                : BuildRepairPrompt(normalizedPrompt, currentYaml, lastCandidate, lastFindings, attempt);
            var completion = (await generateTurnAsync(prompt, request.Metadata, ct) ?? string.Empty).Trim();
            if (!TryExtractYamlCandidate(completion, out var candidateYaml))
            {
                lastCandidate = completion;
                lastFindings =
                [
                    ValidationFinding.Error(
                        "/",
                        "AI did not return workflow YAML.",
                        "Return workflow YAML only without extra commentary.",
                        "missing_yaml")
                ];
                continue;
            }

            lastCandidate = candidateYaml;
            if (onProgress != null)
            {
                await onProgress(
                    new WorkflowGenerateProgress(
                        WorkflowGenerateProgressStage.ValidatingDraft,
                        attempt,
                        $"Validating generated YAML (attempt {attempt}/{MaxAttempts})..."),
                    ct);
            }
            var parse = _editorService.ParseYaml(new ParseYamlRequest(candidateYaml, request.AvailableWorkflowNames));
            if (parse.Document == null)
            {
                lastFindings = parse.Findings.Count > 0
                    ? parse.Findings
                    :
                    [
                        ValidationFinding.Error(
                            "/",
                            "Workflow YAML could not be parsed.",
                            "Return a complete workflow YAML document.",
                            "parse_failed")
                    ];
                if (onProgress != null && attempt < MaxAttempts)
                {
                    await onProgress(
                        new WorkflowGenerateProgress(
                            WorkflowGenerateProgressStage.RepairingDraft,
                            attempt,
                            BuildRepairStatusMessage(lastFindings, attempt)),
                        ct);
                }
                continue;
            }

            var normalized = _editorService.Normalize(new NormalizeWorkflowRequest(
                parse.Document,
                request.AvailableWorkflowNames));
            var blockingParseFindings = GetBlockingParseFindings(parse.Findings);
            if (blockingParseFindings.Count > 0)
            {
                lastCandidate = string.IsNullOrWhiteSpace(normalized.Yaml)
                    ? candidateYaml
                    : normalized.Yaml;
                lastFindings = blockingParseFindings;
                if (onProgress != null && attempt < MaxAttempts)
                {
                    await onProgress(
                        new WorkflowGenerateProgress(
                            WorkflowGenerateProgressStage.RepairingDraft,
                            attempt,
                            BuildRepairStatusMessage(lastFindings, attempt)),
                        ct);
                }
                continue;
            }

            if (HasErrors(normalized.Findings))
            {
                lastCandidate = normalized.Yaml;
                lastFindings = normalized.Findings;
                if (onProgress != null && attempt < MaxAttempts)
                {
                    await onProgress(
                        new WorkflowGenerateProgress(
                            WorkflowGenerateProgressStage.RepairingDraft,
                            attempt,
                            BuildRepairStatusMessage(lastFindings, attempt)),
                        ct);
                }
                continue;
            }

            if (onProgress != null)
            {
                await onProgress(
                    new WorkflowGenerateProgress(
                        WorkflowGenerateProgressStage.Completed,
                        attempt,
                        "Workflow YAML validated and ready."),
                    ct);
            }

            return new WorkflowGenerateResult(normalized.Yaml, attempt, normalized.Findings);
        }

        throw new InvalidOperationException(BuildFailureMessage(lastFindings));
    }

    internal static bool TryExtractYamlCandidate(string content, out string yaml)
    {
        var source = (content ?? string.Empty).Trim();
        if (source.Length == 0)
        {
            yaml = string.Empty;
            return false;
        }

        var lastMatch = string.Empty;
        foreach (Match match in YamlFenceRegex.Matches(source))
        {
            if (match.Groups.Count > 1)
                lastMatch = match.Groups[1].Value.Trim();
        }

        yaml = string.IsNullOrWhiteSpace(lastMatch) ? source : lastMatch;
        return yaml.Length > 0;
    }

    private static bool HasErrors(IReadOnlyList<ValidationFinding> findings) =>
        findings.Any(static finding => finding.Level == ValidationLevel.Error);

    private static string BuildInitialPrompt(string request, string currentYaml)
    {
        var parts = new List<string>
        {
            "Author an Aevatar workflow YAML that satisfies the user request.",
        };

        if (!string.IsNullOrWhiteSpace(currentYaml))
        {
            parts.AddRange(
            [
                "Current workflow YAML:",
                currentYaml,
                "Treat the task as an edit. Preserve unrelated sections unless the user asks to remove them."
            ]);
        }
        else
        {
            parts.Add("Create the workflow from scratch.");
        }

        parts.Add($"User request:\n{request}");
        parts.Add(BuildAuthoringSchemaHintBlock());
        return string.Join("\n\n", parts);
    }

    private static string BuildRepairPrompt(
        string request,
        string currentYaml,
        string previousCandidate,
        IReadOnlyList<ValidationFinding> findings,
        int attempt)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"The previous workflow YAML draft is invalid. Rewrite it completely and fix every issue. Retry #{attempt}.");
        builder.AppendLine();
        builder.AppendLine("Original user request:");
        builder.AppendLine(request);

        if (!string.IsNullOrWhiteSpace(currentYaml))
        {
            builder.AppendLine();
            builder.AppendLine("Current workflow YAML that the user was editing from:");
            builder.AppendLine(currentYaml);
        }

        if (!string.IsNullOrWhiteSpace(previousCandidate))
        {
            builder.AppendLine();
            builder.AppendLine("Previous invalid draft:");
            builder.AppendLine(previousCandidate);
        }

        builder.AppendLine();
        builder.AppendLine("Validation findings:");
        foreach (var finding in findings)
        {
            builder.Append("- ");
            builder.Append(finding.Path);
            builder.Append(": ");
            builder.Append(finding.Message);
            if (!string.IsNullOrWhiteSpace(finding.Hint))
            {
                builder.Append(" Hint: ");
                builder.Append(finding.Hint);
            }

            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("Workflow authoring constraints:");
        foreach (var rule in AuthoringSchemaRules)
        {
            builder.Append("- ");
            builder.AppendLine(rule);
        }

        builder.AppendLine();
        builder.Append("Return workflow YAML only.");
        return builder.ToString().Trim();
    }

    private static string BuildAuthoringSchemaHintBlock()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Workflow authoring constraints:");
        foreach (var rule in AuthoringSchemaRules)
        {
            builder.Append("- ");
            builder.AppendLine(rule);
        }

        return builder.ToString().Trim();
    }

    private static string BuildFailureMessage(IReadOnlyList<ValidationFinding> findings)
    {
        if (findings.Count == 0)
            return "AI could not produce a valid workflow YAML draft.";

        var details = string.Join("; ", findings.Select(static finding => $"{finding.Path}: {finding.Message}"));
        return $"AI could not produce a valid workflow YAML draft. {details}";
    }

    private static string BuildRepairStatusMessage(IReadOnlyList<ValidationFinding> findings, int attempt)
    {
        var headline = $"Draft {attempt} was invalid. Repairing and retrying...";
        if (findings.Count == 0)
            return headline;

        var firstFinding = findings[0];
        return $"{headline} {firstFinding.Path}: {firstFinding.Message}";
    }

    private static IReadOnlyList<ValidationFinding> GetBlockingParseFindings(
        IReadOnlyList<ValidationFinding> findings) =>
        findings
            .Where(static finding => finding.Level == ValidationLevel.Error)
            .Where(static finding => !IsSanitizableParseFinding(finding))
            .ToList();

    private static bool IsSanitizableParseFinding(ValidationFinding finding)
    {
        if (string.Equals(finding.Code, "unknown_field", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(finding.Code, "runtime_validation", StringComparison.OrdinalIgnoreCase))
            return false;

        var message = finding.Message ?? string.Empty;
        return message.Contains("Unknown field '", StringComparison.OrdinalIgnoreCase) ||
               (message.Contains("Property '", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("not found on type", StringComparison.OrdinalIgnoreCase));
    }
}
