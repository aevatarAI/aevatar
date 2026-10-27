using System.Text.Json;
using Aevatar.Foundation.Abstractions.Interactions;
using Google.Protobuf.Reflection;
using YamlDotNet.RepresentationModel;

namespace Aevatar.Workflow.Core.Primitives;

public sealed partial class WorkflowParser
{
    private static bool ValidateSpecialField(Type contract, string key, YamlNode node, string path)
    {
        if (contract != typeof(RawRole) && contract != typeof(RawStep) && contract != typeof(RawStepPresentation))
            return false;

        return ValidatePresentationField(key, node, path, inlinePresentation: contract == typeof(RawStepPresentation));
    }

    private static void ValidateReservedParameters(YamlMappingNode parameters, string path)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in parameters.Children)
        {
            var key = ReadKey(entry.Key, path);
            var fieldPath = AppendPath(path, key);
            if (ValidatePresentationField(key, entry.Value, fieldPath))
            {
                var canonical = key switch
                {
                    "allowedTools" => "allowed_tools",
                    "toolSets" => "tool_sets",
                    "interactionSpec" => "interaction_spec",
                    "interactionTemplateSpec" => "interaction_template_spec",
                    _ => key,
                };
                if (!fields.Add(canonical))
                    throw StructureError(fieldPath, $"Duplicate parameter aliases for '{canonical}'.");
            }
        }
    }

    private static bool ValidatePresentationField(string key, YamlNode node, string path, bool inlinePresentation = false)
    {
        switch (key)
        {
            case "allowed_tools":
            case "allowedTools":
            case "tool_sets":
            case "toolSets":
                ValidateToolScope(node, path);
                return true;
            case "interaction_spec":
            case "interactionSpec":
                ValidateInteraction(node, InteractionSpec.Descriptor, path);
                return true;
            case "interaction_template_spec":
            case "interactionTemplateSpec":
                ValidateInteraction(node, InteractionTemplateSpec.Descriptor, path);
                return true;
            case "actions" when inlinePresentation:
            case "fields" when inlinePresentation:
            case "cards" when inlinePresentation:
                ValidateInteractionField(node, InteractionSpec.Descriptor.FindFieldByName(key), path);
                return true;
            default:
                return false;
        }
    }

    private static void ValidateToolScope(YamlNode node, string path)
    {
        if (IsNullNode(node))
            return;
        if (node is YamlScalarNode scalar)
        {
            var text = scalar.Value?.Trim();
            if (text?.StartsWith("[", StringComparison.Ordinal) == true || text?.StartsWith("{", StringComparison.Ordinal) == true)
                ValidateNode(DecodeJsonNode(node, path), typeof(List<string>), path, allowNull: false);
            return;
        }
        ValidateNode(node, typeof(List<string>), path, allowNull: false);
    }

    private static void ValidateInteraction(YamlNode node, MessageDescriptor contract, string path)
    {
        if (IsNullNode(node))
            return;
        node = DecodeJsonNode(node, path);
        if (node is not YamlMappingNode mapping)
            throw StructureError(path, "Expected an interaction mapping or a JSON object string.");

        var fields = new HashSet<int>();
        foreach (var entry in mapping.Children)
        {
            var key = ReadKey(entry.Key, path);
            var field = contract.Fields.InDeclarationOrder().FirstOrDefault(candidate =>
                KeyCandidates(candidate.Name).Contains(key, StringComparer.OrdinalIgnoreCase) ||
                (candidate.Name == "disabled" && KeyCandidates("is_disabled").Contains(key, StringComparer.OrdinalIgnoreCase)));
            var fieldPath = AppendPath(path, key);
            if (field is null)
                throw new WorkflowYamlStructureException(fieldPath, $"Unknown interaction field '{key}'.", code: "unknown_field");
            if (!fields.Add(field.FieldNumber))
                throw StructureError(fieldPath, $"Duplicate interaction aliases for '{field.Name}'.");
            ValidateInteractionField(entry.Value, field, fieldPath);
        }
    }

    private static void ValidateInteractionField(YamlNode node, FieldDescriptor field, string path)
    {
        if (IsNullNode(node))
            return;
        if (field.IsMap)
        {
            node = DecodeJsonNode(node, path);
            ValidateNode(node, typeof(Dictionary<string, object?>), path, allowNull: false);
        }
        else if (field.IsRepeated)
        {
            node = DecodeJsonNode(node, path);
            if (node is not YamlSequenceNode sequence)
                throw StructureError(path, "Expected a list of interaction objects.");
            for (var index = 0; index < sequence.Children.Count; index++)
            {
                var item = sequence.Children[index];
                var itemPath = AppendPath(path, index.ToString());
                if (IsNullNode(item))
                    throw StructureError(itemPath, "A null interaction list entry is not allowed.");
                ValidateInteraction(item, field.MessageType, itemPath);
            }
        }
        else
        {
            ValidateNode(node, typeof(string), path, allowNull: true);
            if (field.FieldType == FieldType.Bool &&
                node is YamlScalarNode scalar && !bool.TryParse(scalar.Value, out _))
                throw StructureError(path, "Expected a boolean value (true or false).");
        }
    }

    private static YamlNode DecodeJsonNode(YamlNode node, string path)
    {
        if (node is not YamlScalarNode scalar)
            return node;
        try
        {
            using var document = JsonDocument.Parse(scalar.Value ?? string.Empty);
            var json = document.RootElement.GetRawText();
            WorkflowYamlResourceGuard.Validate(json);
            var stream = new YamlStream();
            using var reader = new StringReader(json);
            stream.Load(reader);
            return stream.Documents[0].RootNode;
        }
        catch (JsonException)
        {
            throw StructureError(path, "Expected structured YAML or a valid JSON object/array string.");
        }
    }
}
