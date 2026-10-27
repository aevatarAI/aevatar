namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// A workflow YAML shape error found before mapping or normalization.
/// </summary>
public sealed class WorkflowYamlStructureException : InvalidOperationException
{
    public WorkflowYamlStructureException(string path, string message, string code = "yaml_structure")
        : base($"{message} Path: {path}.")
    {
        Path = path;
        Code = code;
    }

    public string Path { get; }

    public string Code { get; }
}
