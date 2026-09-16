using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Studio.Projection.Metadata;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using ProtobufValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowDeliveryCurrentStateDocumentMetadataProviderTests
{
    [Fact]
    public void Metadata_ShouldDisableExactAcceptanceStructPaths()
    {
        var mappings = new WorkflowDeliveryCurrentStateDocumentMetadataProvider().Metadata.Mappings;

        mappings.Should().ContainKey("dynamic").WhoseValue.Should().Be(true);
        MappingAt(
                mappings,
                "package",
                "acceptance_policy",
                "input",
                "literals")
            .Should().BeEquivalentTo(DisabledObjectMapping());
        MappingAt(mappings, "installation", "acceptance_input")
            .Should().BeEquivalentTo(DisabledObjectMapping());
    }

    [Fact]
    public void Metadata_ShouldNotExpandHeterogeneousAcceptanceStructFields()
    {
        const string conflictingField = "tenant_defined_value";
        const string nestedField = "tenant_defined_nested";
        var documents = new[]
        {
            DocumentWithAcceptanceStructs(
                ProtobufValue.ForString("alpha"),
                ProtobufValue.ForStruct(new Struct
                {
                    Fields = { ["flag"] = ProtobufValue.ForBool(true) },
                })),
            DocumentWithAcceptanceStructs(
                ProtobufValue.ForNumber(42),
                ProtobufValue.ForList(ProtobufValue.ForString("beta"))),
        };
        var mappings = new WorkflowDeliveryCurrentStateDocumentMetadataProvider().Metadata.Mappings;

        documents.Select(document =>
                document.Package.AcceptancePolicy.Input.Literals.Fields[conflictingField].KindCase)
            .Should().Equal(
            [
                ProtobufValue.KindOneofCase.StringValue,
                ProtobufValue.KindOneofCase.NumberValue,
            ]);
        documents.Select(document =>
                document.Installation.AcceptanceInput.Fields[nestedField].KindCase)
            .Should().Equal(
            [
                ProtobufValue.KindOneofCase.StructValue,
                ProtobufValue.KindOneofCase.ListValue,
            ]);
        FlattenMappingKeys(mappings).Should().NotContain(new[] { conflictingField, nestedField, "flag" });
        MappingAt(mappings, "package", "acceptance_policy", "input", "literals")
            .Keys.Should().BeEquivalentTo("type", "enabled");
        MappingAt(mappings, "installation", "acceptance_input")
            .Keys.Should().BeEquivalentTo("type", "enabled");
    }

    private static WorkflowDeliveryCurrentStateDocument DocumentWithAcceptanceStructs(
        ProtobufValue literal,
        ProtobufValue acceptanceInput) =>
        new()
        {
            Package = new WorkflowPackageVersionSnapshot
            {
                AcceptancePolicy = new WorkflowDeliveryAcceptancePolicy
                {
                    Input = new WorkflowDeliveryAcceptanceInputRecipe
                    {
                        Literals = new Struct
                        {
                            Fields = { ["tenant_defined_value"] = literal },
                        },
                    },
                },
            },
            Installation = new WorkflowInstallationState
            {
                AcceptanceInput = new Struct
                {
                    Fields = { ["tenant_defined_nested"] = acceptanceInput },
                },
            },
        };

    private static IReadOnlyDictionary<string, object?> MappingAt(
        IReadOnlyDictionary<string, object?> mappings,
        params string[] path)
    {
        var current = mappings;
        foreach (var segment in path)
        {
            current = DictionaryAt(current, "properties");
            current = DictionaryAt(current, segment);
        }

        return current;
    }

    private static IReadOnlyDictionary<string, object?> DictionaryAt(
        IReadOnlyDictionary<string, object?> dictionary,
        string key) =>
        dictionary[key].Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;

    private static Dictionary<string, object?> DisabledObjectMapping() => new(StringComparer.Ordinal)
    {
        ["type"] = "object",
        ["enabled"] = false,
    };

    private static IEnumerable<string> FlattenMappingKeys(
        IReadOnlyDictionary<string, object?> dictionary)
    {
        foreach (var (key, value) in dictionary)
        {
            yield return key;
            if (value is not IReadOnlyDictionary<string, object?> child)
            {
                continue;
            }

            foreach (var childKey in FlattenMappingKeys(child))
            {
                yield return childKey;
            }
        }
    }
}
