using Aevatar.Configuration;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.Workflows;

internal static class WorkflowDefinitionFileSourceResolver
{
    public static IReadOnlyList<string> ResolveNormalizedExistingDirectories(
        IEnumerable<string> workflowDirectories,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(workflowDirectories);
        ArgumentNullException.ThrowIfNull(logger);

        var normalizedDirectories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawDirectory in workflowDirectories)
        {
            if (string.IsNullOrWhiteSpace(rawDirectory))
                continue;

            try
            {
                var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawDirectory));
                if (!Directory.Exists(normalized) || !seen.Add(normalized))
                    continue;

                normalizedDirectories.Add(normalized);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to normalize workflow directory '{WorkflowDirectory}'.", rawDirectory);
            }
        }

        return normalizedDirectories;
    }

    public static Dictionary<string, WorkflowDefinitionFileEntry> DiscoverWorkflowFiles(
        IEnumerable<string> workflowDirectories,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(workflowDirectories);
        ArgumentNullException.ThrowIfNull(logger);

        var entries = new Dictionary<string, WorkflowDefinitionFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in ResolveNormalizedExistingDirectories(workflowDirectories, logger))
        {
            var sourceKind = ResolveSourceKind(directory);
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.*")
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                             .Where(path => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                                            path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
                {
                    var name = Path.GetFileNameWithoutExtension(file)?.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    entries[name] = new WorkflowDefinitionFileEntry(name, file, sourceKind);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to enumerate workflow files from directory '{WorkflowDirectory}'.", directory);
            }
        }

        return entries;
    }

    public static string ResolveSourceKind(string directory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(normalized, Normalize(AevatarPaths.Workflows), StringComparison.OrdinalIgnoreCase))
            return "home";

        if (string.Equals(normalized, Normalize(AevatarPaths.RepoRootWorkflows), StringComparison.OrdinalIgnoreCase))
            return "repo";

        if (string.Equals(normalized, Normalize(Path.Combine(Directory.GetCurrentDirectory(), "workflows")), StringComparison.OrdinalIgnoreCase))
            return "cwd";

        if (string.Equals(normalized, Normalize(Path.Combine(AppContext.BaseDirectory, "workflows")), StringComparison.OrdinalIgnoreCase))
            return "app";

        if (normalized.EndsWith($"{Path.DirectorySeparatorChar}turing-completeness", StringComparison.OrdinalIgnoreCase))
            return "turing";

        return "file";
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

internal sealed record WorkflowDefinitionFileEntry(
    string Name,
    string FilePath,
    string SourceKind);
