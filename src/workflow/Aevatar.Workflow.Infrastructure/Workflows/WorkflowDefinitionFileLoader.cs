using Aevatar.Workflow.Application.Abstractions.Workflows;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.Workflows;

public sealed class WorkflowDefinitionFileLoader
{
    public int LoadInto(
        IWorkflowDefinitionRegistry registry,
        IEnumerable<string> directories,
        ILogger logger,
        WorkflowDefinitionDuplicatePolicy duplicatePolicy = WorkflowDefinitionDuplicatePolicy.Throw)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(logger);

        var loaded = 0;
        var registeredNames = new HashSet<string>(registry.GetNames(), StringComparer.OrdinalIgnoreCase);
        var normalizedDirectories = WorkflowDefinitionFileSourceResolver.ResolveNormalizedExistingDirectories(
            directories,
            logger);

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
                        logger.LogInformation(
                            "Skipping duplicate workflow definition '{Name}' from '{File}'.",
                            name,
                            file);
                        continue;
                    }

                    logger.LogInformation(
                        "Overriding existing workflow definition '{Name}' with file '{File}'.",
                        name,
                        file);
                }

                var yaml = File.ReadAllText(file);
                registry.Register(name, yaml);
                loaded++;
            }
        }

        logger.LogInformation("Loaded {Count} workflow definition(s) from file sources.", loaded);
        return loaded;
    }
}
