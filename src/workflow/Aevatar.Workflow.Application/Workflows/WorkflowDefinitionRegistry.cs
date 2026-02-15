using System.Collections.Concurrent;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Application.Workflows;

/// <summary>
/// Workflow YAML definition registry keyed by workflow name.
/// </summary>
public sealed class WorkflowDefinitionRegistry : IWorkflowDefinitionRegistry
{
    private readonly ConcurrentDictionary<string, string> _workflows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Built-in default workflow used when request does not provide a workflow name.
    /// </summary>
    public static string BuiltInDirectYaml { get; } = """
        name: direct
        description: Direct chat; default when no workflow name is provided.
        roles:
          - id: assistant
            name: Assistant
            system_prompt: |
              You are a helpful assistant. Answer the user clearly and concisely.
        steps:
          - id: reply
            type: llm_call
            role: assistant
            parameters: {}
        """;

    public void Register(string name, string yaml) => _workflows[name] = yaml;

    public string? GetYaml(string name) =>
        _workflows.GetValueOrDefault(name);

    public IReadOnlyList<string> GetNames() => _workflows.Keys.ToList();
}
