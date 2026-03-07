namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// Thin facade that keeps the historical entrypoint while delegating to
/// explicit deserialize + normalize stages.
/// </summary>
public sealed class WorkflowParser
{
    private readonly WorkflowYamlDeserializer _deserializer = new();
    private readonly WorkflowDefinitionNormalizer _normalizer = new();

    public WorkflowDefinition Parse(string yaml) =>
        _normalizer.Normalize(_deserializer.Deserialize(yaml));
}
