using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Workflows;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.Workflows;

public sealed class WorkflowDefinitionFileLoader
{
    public async Task<int> LoadIntoAsync(
        IWorkflowDefinitionCatalog catalog,
        IEnumerable<string> directories,
        ILogger logger,
        IEnumerable<IWorkflowDefinitionSeedSource>? seedSources = null,
        WorkflowDefinitionDuplicatePolicy duplicatePolicy = WorkflowDefinitionDuplicatePolicy.Throw)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(logger);

        var loaded = 0;
        var registeredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seedSource in seedSources ?? [])
        {
            foreach (var (name, yaml) in seedSource.GetSeedDefinitions())
            {
                if (!registeredNames.Add(name))
                {
                    if (duplicatePolicy == WorkflowDefinitionDuplicatePolicy.Throw)
                    {
                        throw new InvalidOperationException(
                            $"Duplicate workflow definition name '{name}' detected in seed sources.");
                    }

                    if (duplicatePolicy == WorkflowDefinitionDuplicatePolicy.Skip)
                    {
                        logger.LogWarning(
                            "Skipping duplicate seeded workflow definition '{Name}'.",
                            name);
                        continue;
                    }

                    logger.LogWarning(
                        "Overriding existing seeded workflow definition '{Name}'.",
                        name);
                }

                await catalog.UpsertAsync(name, yaml, CancellationToken.None);
            }
        }

        var normalizedDirectories = directories
            .Where(static directory => !string.IsNullOrWhiteSpace(directory))
            .Select(static directory => Path.GetFullPath(directory))
            .Select(static directory => Path.TrimEndingDirectorySeparator(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists);

        foreach (var directory in normalizedDirectories)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.*")
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                         .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!registeredNames.Add(name))
                {
                    if (duplicatePolicy == WorkflowDefinitionDuplicatePolicy.Throw)
                    {
                        throw new InvalidOperationException(
                            $"Duplicate workflow definition name '{name}' detected in '{file}'.");
                    }

                    if (duplicatePolicy == WorkflowDefinitionDuplicatePolicy.Skip)
                    {
                        logger.LogWarning(
                            "Skipping duplicate workflow definition '{Name}' from '{File}'.",
                            name,
                            file);
                        continue;
                    }

                    logger.LogWarning(
                        "Overriding existing workflow definition '{Name}' with file '{File}'.",
                        name,
                        file);
                }

                var yaml = File.ReadAllText(file);
                await catalog.UpsertAsync(name, yaml, CancellationToken.None);
                loaded++;
            }
        }

        logger.LogInformation("Loaded {Count} workflow definition(s) from file sources.", loaded);
        return loaded;
    }
}
