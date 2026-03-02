using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Workflows;

internal interface IFileBackedWorkflowNameCatalog
{
    bool Contains(string workflowName);
}

internal sealed class FileBackedWorkflowNameCatalog : IFileBackedWorkflowNameCatalog
{
    private readonly ISet<string> _workflowNames;

    public FileBackedWorkflowNameCatalog(IOptions<WorkflowDefinitionFileSourceOptions> options)
    {
        var configuredDirectories = options.Value.WorkflowDirectories;
        _workflowNames = BuildWorkflowNameSet(configuredDirectories);
    }

    public bool Contains(string workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            return false;

        return _workflowNames.Contains(workflowName.Trim());
    }

    private static ISet<string> BuildWorkflowNameSet(IEnumerable<string> configuredDirectories)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedDirectories = configuredDirectories
            .Where(static directory => !string.IsNullOrWhiteSpace(directory))
            .Select(static directory => Path.GetFullPath(directory))
            .Select(static directory => Path.TrimEndingDirectorySeparator(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists);

        foreach (var directory in normalizedDirectories)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.*")
                         .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name.Trim());
            }
        }

        return names;
    }
}
