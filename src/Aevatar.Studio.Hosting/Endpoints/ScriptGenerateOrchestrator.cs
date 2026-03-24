using System.Text;
using System.Text.RegularExpressions;
using Aevatar.Scripting.Core.Compilation;
using System.Text.Json;

using Aevatar.Studio.Application.Scripts.Contracts;
namespace Aevatar.Studio.Hosting.Endpoints;

internal sealed record ScriptGenerateRequest(
    string Prompt,
    string? CurrentSource,
    IReadOnlyDictionary<string, string>? Metadata,
    AppScriptPackage? CurrentPackage = null,
    string? CurrentFilePath = null);

internal sealed record ScriptGenerateResult(
    string Source,
    int Attempts,
    IReadOnlyList<string> Diagnostics,
    AppScriptPackage? Package = null,
    string? CurrentFilePath = null);

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
    private static readonly Regex JsonFenceRegex = new(
        @"```(?:json)?\s*\n([\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IScriptBehaviorCompiler _compiler;
    private const string FixedCommandContractInstruction =
        "The studio draft-run UI always sends AppScriptCommand. " +
        "Keep AppScriptCommand as the only inbound command contract. " +
        "Do not introduce or replace it with richer command message types. " +
        "If structured input is needed, parse it from AppScriptCommand.Input.";

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
        var currentPackage = request.CurrentPackage;
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
                ? BuildInitialPrompt(normalizedPrompt, currentSource, request.CurrentPackage, request.CurrentFilePath)
                : BuildRepairPrompt(normalizedPrompt, currentSource, request.CurrentPackage, request.CurrentFilePath, lastCandidate, lastDiagnostics, attempt);
            var completion = (await generateTurnAsync(prompt, request.Metadata, ct) ?? string.Empty).Trim();
            var hasPackageCandidate = TryExtractPackageCandidate(completion, out var candidatePackage, out var candidateFilePath);
            var hasSourceCandidate = TryExtractSourceCandidate(completion, out var candidateSource);
            if (!hasPackageCandidate && !hasSourceCandidate)
            {
                lastCandidate = completion;
                lastDiagnostics =
                [
                    "AI did not return a compilable script package payload.",
                    "Return a full script package JSON object without markdown fences or commentary.",
                ];
                continue;
            }

            var resolvedPackage = hasPackageCandidate
                ? candidatePackage
                : MergeSourceIntoPackage(currentPackage, request.CurrentFilePath, NormalizeCandidate(candidateSource));
            var resolvedFilePath = ResolvePreviewFilePath(
                resolvedPackage,
                request.CurrentFilePath,
                candidateFilePath);
            lastCandidate = hasPackageCandidate
                ? JsonSerializer.Serialize(new
                {
                    currentFilePath = resolvedFilePath,
                    scriptPackage = resolvedPackage,
                })
                : NormalizeCandidate(candidateSource);
            if (onProgress != null)
            {
                await onProgress(
                    new ScriptGenerateProgress(
                        ScriptGenerateProgressStage.ValidatingDraft,
                        attempt,
                        $"Compiling generated script package (attempt {attempt}/{MaxAttempts})..."),
                    ct);
            }

            var compilation = _compiler.Compile(new ScriptBehaviorCompilationRequest(
                ScriptId: "app-script-preview",
                Revision: $"draft-{attempt}",
                Package: AppScriptPackagePayloads.ResolvePackage(resolvedPackage, sourceText: null)));
            try
            {
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
                            "Script package compiled and is ready."),
                        ct);
                }

                currentPackage = resolvedPackage;
                return new ScriptGenerateResult(
                    GetPreviewSource(resolvedPackage, resolvedFilePath),
                    attempt,
                    compilation.Diagnostics,
                    resolvedPackage,
                    resolvedFilePath);
            }
            finally
            {
                if (compilation.Artifact != null)
                    await compilation.Artifact.DisposeAsync();
            }
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

    private static string BuildInitialPrompt(
        string request,
        string currentSource,
        AppScriptPackage? currentPackage,
        string? currentFilePath)
    {
        var parts = new List<string>
        {
            "Author an Aevatar script package that satisfies the user request.",
            FixedCommandContractInstruction,
            BuildPackageReturnContract(),
        };

        var normalizedFilePath = string.IsNullOrWhiteSpace(currentFilePath)
            ? "Behavior.cs"
            : currentFilePath.Trim();
        var packageContext = BuildPackageContext(currentPackage);
        if (!string.IsNullOrWhiteSpace(packageContext))
        {
            parts.AddRange(
            [
                $"Current script package (edit only `{normalizedFilePath}` unless the prompt explicitly requires otherwise):",
                packageContext,
            ]);
        }

        if (!string.IsNullOrWhiteSpace(currentSource))
        {
            parts.AddRange(
            [
                $"Current source for `{normalizedFilePath}`:",
                currentSource,
                "Treat the task as an edit. Preserve unrelated working sections unless the user asks to replace them."
            ]);
        }
        else
        {
            parts.Add("Create the script package from scratch.");
        }

        parts.Add($"User request:\n{request}");
        return string.Join("\n\n", parts);
    }

    private static string BuildRepairPrompt(
        string request,
        string currentSource,
        AppScriptPackage? currentPackage,
        string? currentFilePath,
        string previousCandidate,
        IReadOnlyList<string> diagnostics,
        int attempt)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"The previous script draft does not compile. Rewrite it completely and fix every issue. Retry #{attempt}.");
        builder.AppendLine();
        builder.AppendLine("Original user request:");
        builder.AppendLine(request);
        builder.AppendLine();
        builder.AppendLine(FixedCommandContractInstruction);
        builder.AppendLine();
        builder.AppendLine(BuildPackageReturnContract());

        var normalizedFilePath = string.IsNullOrWhiteSpace(currentFilePath)
            ? "Behavior.cs"
            : currentFilePath.Trim();
        var packageContext = BuildPackageContext(currentPackage);
        if (!string.IsNullOrWhiteSpace(packageContext))
        {
            builder.AppendLine();
            builder.AppendLine($"Current script package (rewrite only `{normalizedFilePath}` unless the request explicitly requires other files):");
            builder.AppendLine(packageContext);
        }

        if (!string.IsNullOrWhiteSpace(currentSource))
        {
            builder.AppendLine();
            builder.AppendLine($"Current source for `{normalizedFilePath}` that the user was editing from:");
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
        builder.Append("Return the full script package JSON object only.");
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
            return "Ask AI could not produce a compilable script package.";

        return $"Ask AI could not produce a compilable script package. {string.Join(" | ", diagnostics.Take(3))}";
    }

    private static string BuildPackageContext(AppScriptPackage? package)
    {
        if (!AppScriptPackagePayloads.HasFiles(package))
            return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine($"entryBehaviorTypeName: {(package?.EntryBehaviorTypeName ?? string.Empty)}");
        builder.AppendLine($"entrySourcePath: {(package?.EntrySourcePath ?? string.Empty)}");

        foreach (var file in package?.CsharpSources ?? Array.Empty<AppScriptPackageFile>())
        {
            builder.AppendLine($"[csharp] {file.Path}");
            builder.AppendLine(file.Content ?? string.Empty);
        }

        foreach (var file in package?.ProtoFiles ?? Array.Empty<AppScriptPackageFile>())
        {
            builder.AppendLine($"[proto] {file.Path}");
            builder.AppendLine(file.Content ?? string.Empty);
        }

        return builder.ToString().Trim();
    }

    private static string BuildPackageReturnContract() =>
        """
        Return a JSON object with this shape and no markdown fences:
        {
          "currentFilePath": "Behavior.cs",
          "scriptPackage": {
            "csharpSources": [{ "path": "Behavior.cs", "content": "..." }],
            "protoFiles": [],
            "entryBehaviorTypeName": "DraftBehavior",
            "entrySourcePath": "Behavior.cs"
          }
        }
        Preserve unrelated files unless the user explicitly asks to remove or rewrite them.
        """;

    private static bool TryExtractPackageCandidate(
        string content,
        out AppScriptPackage package,
        out string currentFilePath)
    {
        package = null!;
        currentFilePath = string.Empty;
        foreach (var candidate in EnumerateJsonCandidates(content))
        {
            if (!TryDeserializePackageCandidate(candidate, out package, out currentFilePath))
                continue;

            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateJsonCandidates(string content)
    {
        var normalized = (content ?? string.Empty).Trim();
        if (normalized.Length == 0)
            yield break;

        yield return normalized;
        foreach (Match match in JsonFenceRegex.Matches(normalized))
        {
            if (match.Groups.Count > 1)
                yield return match.Groups[1].Value.Trim();
        }
    }

    private static bool TryDeserializePackageCandidate(
        string candidate,
        out AppScriptPackage package,
        out string currentFilePath)
    {
        package = null!;
        currentFilePath = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(candidate);
            var root = document.RootElement;
            var packageElement = root;
            if (TryGetProperty(root, "scriptPackage", out var nestedPackage))
                packageElement = nestedPackage;

            var csharpSources = ReadFiles(packageElement, "csharpSources", "CsharpSources");
            var protoFiles = ReadFiles(packageElement, "protoFiles", "ProtoFiles");
            if (csharpSources.Count == 0 && protoFiles.Count == 0)
                return false;

            package = new AppScriptPackage(
                csharpSources,
                protoFiles,
                ReadString(packageElement, "entryBehaviorTypeName", "EntryBehaviorTypeName"),
                ReadString(packageElement, "entrySourcePath", "EntrySourcePath"));
            currentFilePath = ReadString(root, "currentFilePath", "CurrentFilePath") ?? string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<AppScriptPackageFile> ReadFiles(
        JsonElement element,
        string camelCaseName,
        string pascalCaseName)
    {
        if (!TryGetProperty(element, camelCaseName, out var files) &&
            !TryGetProperty(element, pascalCaseName, out files) ||
            files.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<AppScriptPackageFile>();
        foreach (var file in files.EnumerateArray())
        {
            result.Add(new AppScriptPackageFile(
                ReadString(file, "path", "Path"),
                ReadString(file, "content", "Content")));
        }

        return result;
    }

    private static string? ReadString(JsonElement element, string camelCaseName, string pascalCaseName)
    {
        if (!TryGetProperty(element, camelCaseName, out var value) &&
            !TryGetProperty(element, pascalCaseName, out value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value))
            return true;

        value = default;
        return false;
    }

    private static AppScriptPackage MergeSourceIntoPackage(
        AppScriptPackage? currentPackage,
        string? currentFilePath,
        string source)
    {
        var normalizedFilePath = string.IsNullOrWhiteSpace(currentFilePath)
            ? "Behavior.cs"
            : currentFilePath.Trim();
        if (!AppScriptPackagePayloads.HasFiles(currentPackage))
        {
            return new AppScriptPackage(
                [new AppScriptPackageFile(normalizedFilePath, source)],
                [],
                string.Empty,
                normalizedFilePath);
        }

        var nextCsharpSources = new List<AppScriptPackageFile>();
        var replaced = false;
        foreach (var file in currentPackage!.CsharpSources ?? Array.Empty<AppScriptPackageFile>())
        {
            if (string.Equals(file.Path, normalizedFilePath, StringComparison.Ordinal))
            {
                nextCsharpSources.Add(new AppScriptPackageFile(normalizedFilePath, source));
                replaced = true;
            }
            else
            {
                nextCsharpSources.Add(new AppScriptPackageFile(file.Path, file.Content));
            }
        }

        if (!replaced)
            nextCsharpSources.Add(new AppScriptPackageFile(normalizedFilePath, source));

        return new AppScriptPackage(
            nextCsharpSources,
            currentPackage.ProtoFiles?.Select(file => new AppScriptPackageFile(file.Path, file.Content)).ToArray() ?? [],
            currentPackage.EntryBehaviorTypeName,
            string.IsNullOrWhiteSpace(currentPackage.EntrySourcePath)
                ? normalizedFilePath
                : currentPackage.EntrySourcePath);
    }

    private static string ResolvePreviewFilePath(
        AppScriptPackage package,
        string? requestedFilePath,
        string? returnedFilePath)
    {
        var candidates = new[]
        {
            requestedFilePath?.Trim(),
            returnedFilePath?.Trim(),
            package.EntrySourcePath?.Trim(),
            package.CsharpSources?.FirstOrDefault()?.Path?.Trim(),
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if (package.CsharpSources?.Any(file => string.Equals(file.Path, candidate, StringComparison.Ordinal)) == true)
                return candidate;
        }

        return "Behavior.cs";
    }

    private static string GetPreviewSource(AppScriptPackage package, string currentFilePath)
    {
        var selected = package.CsharpSources?
            .FirstOrDefault(file => string.Equals(file.Path, currentFilePath, StringComparison.Ordinal));
        return selected?.Content ?? package.CsharpSources?.FirstOrDefault()?.Content ?? string.Empty;
    }
}
