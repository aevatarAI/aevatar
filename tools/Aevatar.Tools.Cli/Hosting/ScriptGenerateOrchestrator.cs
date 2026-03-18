using System.Text;
using System.Text.RegularExpressions;
using Aevatar.Scripting.Core.Compilation;

namespace Aevatar.Tools.Cli.Hosting;

internal sealed record ScriptGenerateRequest(
    string Prompt,
    string? CurrentSource,
    IReadOnlyDictionary<string, string>? Metadata);

internal sealed record ScriptGenerateResult(
    string Source,
    int Attempts,
    IReadOnlyList<string> Diagnostics);

internal enum ScriptGenerateProgressStage
{
    Starting,
    Queued,
    GeneratingDraft,
    ValidatingDraft,
    RepairingDraft,
    Completed,
}

internal sealed record ScriptGenerateProgress(
    ScriptGenerateProgressStage Stage,
    int Attempt,
    string Message);

internal sealed class ScriptGenerateOrchestrator
{
    private const int MaxAttempts = 3;
    private static readonly Regex CodeFenceRegex = new(
        @"```(?:csharp|cs)?\s*\n([\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IScriptBehaviorCompiler _compiler;

    public ScriptGenerateOrchestrator(IScriptBehaviorCompiler compiler)
    {
        _compiler = compiler;
    }

    public async Task<ScriptGenerateResult> GenerateAsync(
        ScriptGenerateRequest request,
        Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<string?>> generateTurnAsync,
        Func<ScriptGenerateProgress, CancellationToken, Task>? onProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(generateTurnAsync);

        var normalizedPrompt = (request.Prompt ?? string.Empty).Trim();
        if (normalizedPrompt.Length == 0)
            throw new InvalidOperationException("Script authoring prompt is required.");

        var currentSource = (request.CurrentSource ?? string.Empty).Trim();
        var lastCandidate = currentSource;
        IReadOnlyList<string> lastDiagnostics = Array.Empty<string>();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (onProgress != null)
            {
                await onProgress(
                    new ScriptGenerateProgress(
                        ScriptGenerateProgressStage.GeneratingDraft,
                        attempt,
                        $"Generating script draft (attempt {attempt}/{MaxAttempts})..."),
                    ct);
            }

            var prompt = attempt == 1
                ? BuildInitialPrompt(normalizedPrompt, currentSource)
                : BuildRepairPrompt(normalizedPrompt, currentSource, lastCandidate, lastDiagnostics, attempt);
            var completion = (await generateTurnAsync(prompt, request.Metadata, ct) ?? string.Empty).Trim();
            if (!TryExtractSourceCandidate(completion, out var candidateSource))
            {
                lastCandidate = completion;
                lastDiagnostics =
                [
                    "AI did not return compilable C# source.",
                    "Return a full ScriptBehavior C# file without markdown fences or commentary.",
                ];
                continue;
            }

            lastCandidate = NormalizeCandidate(candidateSource);
            if (onProgress != null)
            {
                await onProgress(
                    new ScriptGenerateProgress(
                        ScriptGenerateProgressStage.ValidatingDraft,
                        attempt,
                        $"Compiling generated source (attempt {attempt}/{MaxAttempts})..."),
                    ct);
            }

            var compilation = _compiler.Compile(new ScriptBehaviorCompilationRequest(
                ScriptId: "app-script-preview",
                Revision: $"draft-{attempt}",
                Source: lastCandidate));
            if (!compilation.IsSuccess)
            {
                lastDiagnostics = compilation.Diagnostics;
                if (onProgress != null && attempt < MaxAttempts)
                {
                    await onProgress(
                        new ScriptGenerateProgress(
                            ScriptGenerateProgressStage.RepairingDraft,
                            attempt,
                            BuildRepairStatusMessage(lastDiagnostics, attempt)),
                        ct);
                }

                continue;
            }

            if (onProgress != null)
            {
                await onProgress(
                    new ScriptGenerateProgress(
                        ScriptGenerateProgressStage.Completed,
                        attempt,
                        "Script source compiled and is ready."),
                    ct);
            }

            return new ScriptGenerateResult(lastCandidate, attempt, compilation.Diagnostics);
        }

        throw new InvalidOperationException(BuildFailureMessage(lastDiagnostics));
    }

    internal static bool TryExtractSourceCandidate(string content, out string source)
    {
        var normalized = (content ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            source = string.Empty;
            return false;
        }

        var lastMatch = string.Empty;
        foreach (Match match in CodeFenceRegex.Matches(normalized))
        {
            if (match.Groups.Count > 1)
                lastMatch = match.Groups[1].Value.Trim();
        }

        source = string.IsNullOrWhiteSpace(lastMatch) ? normalized : lastMatch;
        return source.Length > 0;
    }

    private static string NormalizeCandidate(string candidate) =>
        (candidate ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();

    private static string BuildInitialPrompt(string request, string currentSource)
    {
        var parts = new List<string>
        {
            "Author an Aevatar ScriptBehavior C# source file that satisfies the user request.",
        };

        if (!string.IsNullOrWhiteSpace(currentSource))
        {
            parts.AddRange(
            [
                "Current source:",
                currentSource,
                "Treat the task as an edit. Preserve unrelated working sections unless the user asks to replace them."
            ]);
        }
        else
        {
            parts.Add("Create the script from scratch.");
        }

        parts.Add($"User request:\n{request}");
        return string.Join("\n\n", parts);
    }

    private static string BuildRepairPrompt(
        string request,
        string currentSource,
        string previousCandidate,
        IReadOnlyList<string> diagnostics,
        int attempt)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"The previous script draft does not compile. Rewrite it completely and fix every issue. Retry #{attempt}.");
        builder.AppendLine();
        builder.AppendLine("Original user request:");
        builder.AppendLine(request);

        if (!string.IsNullOrWhiteSpace(currentSource))
        {
            builder.AppendLine();
            builder.AppendLine("Current source that the user was editing from:");
            builder.AppendLine(currentSource);
        }

        if (!string.IsNullOrWhiteSpace(previousCandidate))
        {
            builder.AppendLine();
            builder.AppendLine("Previous invalid draft:");
            builder.AppendLine(previousCandidate);
        }

        builder.AppendLine();
        builder.AppendLine("Compilation diagnostics:");
        foreach (var diagnostic in diagnostics)
        {
            builder.Append("- ");
            builder.AppendLine(diagnostic);
        }

        builder.AppendLine();
        builder.Append("Return C# source only.");
        return builder.ToString();
    }

    private static string BuildRepairStatusMessage(IReadOnlyList<string> diagnostics, int attempt)
    {
        var first = diagnostics.FirstOrDefault() ?? "Fixing compilation issues.";
        return $"Repairing script draft after compile failure #{attempt}: {first}";
    }

    private static string BuildFailureMessage(IReadOnlyList<string> diagnostics)
    {
        if (diagnostics.Count == 0)
            return "Ask AI could not produce compilable script source.";

        return $"Ask AI could not produce compilable script source. {string.Join(" | ", diagnostics.Take(3))}";
    }
}
