using System.Collections.Concurrent;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Workflows;

/// <summary>
/// Explicit development/test workflow definition catalog backed by in-memory storage.
/// This type must not be treated as the production system-of-record.
/// </summary>
public sealed class InMemoryWorkflowDefinitionCatalog : IWorkflowDefinitionCatalog
{
    private readonly ConcurrentDictionary<string, string> _workflows = new(StringComparer.OrdinalIgnoreCase);

    public void Upsert(string name, string yaml) => _workflows[name] = yaml;

    public string? GetYaml(string name) =>
        _workflows.GetValueOrDefault(name);

    public IReadOnlyList<string> GetNames() => _workflows.Keys.ToList();
}
