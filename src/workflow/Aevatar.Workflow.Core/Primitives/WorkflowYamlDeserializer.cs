using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aevatar.Workflow.Core.Primitives;

internal sealed class WorkflowYamlDeserializer
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public WorkflowRawDefinition Deserialize(string yaml) =>
        Deserializer.Deserialize<WorkflowRawDefinition>(yaml)
        ?? throw new InvalidOperationException("YAML 为空");
}
