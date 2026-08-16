using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Workflow.Core.Primitives;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Aevatar.Studio.Application.Delivery;

public interface IWorkflowDeliveryConfigurationRenderer
{
    WorkflowDeliveryRenderResult Render(
        WorkflowPackageVersionSnapshot package,
        IReadOnlyDictionary<string, JsonElement>? customerConfiguration,
        IReadOnlyDictionary<string, string>? connectionReferences);
}

public sealed record WorkflowDeliveryRenderResult(
    string SourceHash,
    string ResolvedHash,
    string ResolvedYaml,
    IReadOnlyDictionary<string, string> ConfigurationValues,
    IReadOnlyDictionary<string, string> ConnectionReferences);

public sealed class WorkflowDeliveryConfigurationRenderer : IWorkflowDeliveryConfigurationRenderer
{
    public WorkflowDeliveryRenderResult Render(
        WorkflowPackageVersionSnapshot package,
        IReadOnlyDictionary<string, JsonElement>? customerConfiguration,
        IReadOnlyDictionary<string, string>? connectionReferences)
    {
        ArgumentNullException.ThrowIfNull(package);
        var sourceHash = WorkflowDeliveryPackageCatalog.ComputeHash(package.SourceYaml);
        if (!string.Equals(sourceHash, package.SourceHash, StringComparison.Ordinal))
            throw new InvalidOperationException("workflow package source hash does not match its immutable YAML.");

        var supplied = customerConfiguration ?? new Dictionary<string, JsonElement>();
        var schema = package.VariableSchema.ToDictionary(static item => item.Key, StringComparer.Ordinal);
        var unknown = supplied.Keys.Where(key => !schema.ContainsKey(key)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length != 0)
            throw new WorkflowDeliveryConfigurationException("UNKNOWN_CONFIGURATION_FIELD", $"Unknown configuration field '{unknown[0]}'.");

        // Every unsupplied required variable leaves the package author's own default in the
        // installed YAML. Those defaults are real production identifiers, so omitting a
        // required field must fail closed rather than silently install someone else's target.
        var missing = package.VariableSchema
            .Where(item => item.Required && !supplied.ContainsKey(item.Key))
            .Select(static item => item.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length != 0)
        {
            throw new WorkflowDeliveryConfigurationException(
                "CONFIGURATION_FIELD_REQUIRED",
                $"Configuration field '{missing[0]}' is required.");
        }

        WorkflowYamlResourceGuard.Validate(package.SourceYaml);
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(package.SourceYaml);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new WorkflowDeliveryConfigurationException("INVALID_PACKAGE_YAML", ex.Message);
        }
        if (stream.Documents.Count != 1)
            throw new WorkflowDeliveryConfigurationException("INVALID_PACKAGE_YAML", "Workflow package must contain exactly one YAML document.");

        var canonicalValues = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in supplied.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var definition = schema[key];
            var typedValue = ConvertValue(definition, value);
            var yamlNode = ResolveYamlPointer(stream.Documents[0].RootNode, definition.YamlPointer);
            if (definition.JsonPointer.Length != 0)
            {
                if (yamlNode is not YamlScalarNode scalar || scalar.Value == null)
                    throw InvalidPointer(definition.Key);
                JsonNode json;
                try
                {
                    json = JsonNode.Parse(scalar.Value)
                        ?? throw new JsonException("Embedded JSON value is null.");
                }
                catch (JsonException ex)
                {
                    throw new WorkflowDeliveryConfigurationException(
                        "INVALID_PACKAGE_JSON",
                        $"Package field '{definition.Key}' does not contain valid embedded JSON: {ex.Message}");
                }
                ReplaceJsonPointer(json, definition.JsonPointer, typedValue.Node, definition.Key);
                scalar.Value = json.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                scalar.Style = ScalarStyle.SingleQuoted;
            }
            else if (yamlNode is YamlScalarNode scalar)
            {
                scalar.Value = typedValue.Canonical;
                scalar.Style = ScalarStyle.Plain;
            }
            else
            {
                throw InvalidPointer(definition.Key);
            }
            canonicalValues.Add(key, typedValue.Canonical);
        }

        var resolvedConnections = ResolveConnections(package, connectionReferences);
        if (resolvedConnections.Count != 0)
        {
            var replacements = ReplaceUserServiceIds(
                stream.Documents[0].RootNode,
                resolvedConnections.Values.Single());
            if (replacements == 0)
            {
                throw new WorkflowDeliveryConfigurationException(
                    "CONNECTION_BINDING_NOT_FOUND",
                    "The workflow package has no structured user_service_id fields for its connection slot.");
            }
        }

        string resolvedYaml;
        using (var writer = new StringWriter(CultureInfo.InvariantCulture))
        {
            stream.Save(writer, assignAnchors: false);
            resolvedYaml = writer.ToString();
        }
        return new WorkflowDeliveryRenderResult(
            package.SourceHash,
            WorkflowDeliveryPackageCatalog.ComputeHash(resolvedYaml),
            resolvedYaml,
            canonicalValues,
            resolvedConnections);
    }

    private static IReadOnlyDictionary<string, string> ResolveConnections(
        WorkflowPackageVersionSnapshot package,
        IReadOnlyDictionary<string, string>? connectionReferences)
    {
        var supplied = connectionReferences ?? new Dictionary<string, string>();
        var slots = package.ConnectionSlots.ToDictionary(static slot => slot.Key, StringComparer.Ordinal);
        var unknown = supplied.Keys.Where(key => !slots.ContainsKey(key)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length != 0)
            throw new WorkflowDeliveryConfigurationException("UNKNOWN_CONNECTION_SLOT", $"Unknown connection slot '{unknown[0]}'.");
        var resolved = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in package.ConnectionSlots)
        {
            if (!supplied.TryGetValue(slot.Key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                if (slot.Required)
                    throw new WorkflowDeliveryConfigurationException("CONNECTION_REQUIRED", $"Connection slot '{slot.Key}' is required.");
                continue;
            }
            resolved.Add(slot.Key, raw.Trim());
        }
        if (resolved.Count > 1)
            throw new WorkflowDeliveryConfigurationException("MULTIPLE_CONNECTIONS_UNSUPPORTED", "This MVP supports one connection slot per workflow package.");
        return resolved;
    }

    private static TypedValue ConvertValue(WorkflowDeliveryVariableDefinition definition, JsonElement value)
    {
        try
        {
            return definition.Kind switch
            {
                WorkflowDeliveryVariableKind.String when value.ValueKind == JsonValueKind.String =>
                    StringValue(value.GetString() ?? string.Empty),
                WorkflowDeliveryVariableKind.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer) =>
                    new TypedValue(integer.ToString(CultureInfo.InvariantCulture), JsonValue.Create(integer)!),
                WorkflowDeliveryVariableKind.Number when value.ValueKind == JsonValueKind.Number =>
                    NumberValue(value),
                WorkflowDeliveryVariableKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                    new TypedValue(value.GetBoolean() ? "true" : "false", JsonValue.Create(value.GetBoolean())!),
                _ => throw new WorkflowDeliveryConfigurationException(
                    "INVALID_CONFIGURATION_TYPE",
                    $"Configuration field '{definition.Key}' has the wrong value type."),
            };
        }
        catch (FormatException)
        {
            throw new WorkflowDeliveryConfigurationException(
                "INVALID_CONFIGURATION_TYPE",
                $"Configuration field '{definition.Key}' has the wrong value type.");
        }
    }

    private static TypedValue StringValue(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
            throw new WorkflowDeliveryConfigurationException("CONFIGURATION_REQUIRED", "Configuration string values must not be blank.");
        if (normalized.Length > 2048 || normalized.Any(char.IsControl))
            throw new WorkflowDeliveryConfigurationException("INVALID_CONFIGURATION_VALUE", "Configuration string value is invalid or too long.");
        return new TypedValue(normalized, JsonValue.Create(normalized)!);
    }

    private static TypedValue NumberValue(JsonElement value)
    {
        var number = value.GetDouble();
        if (!double.IsFinite(number))
            throw new FormatException();
        return new TypedValue(number.ToString("R", CultureInfo.InvariantCulture), JsonValue.Create(number)!);
    }

    private static YamlNode ResolveYamlPointer(YamlNode root, string pointer)
    {
        if (pointer.Length == 0)
            return root;
        if (!pointer.StartsWith("/", StringComparison.Ordinal))
            throw new WorkflowDeliveryConfigurationException("INVALID_PACKAGE_POINTER", "YAML pointer must start with '/'.");
        var current = root;
        foreach (var rawSegment in pointer.Split('/').Skip(1))
        {
            var segment = DecodePointerSegment(rawSegment);
            current = current switch
            {
                YamlMappingNode mapping => mapping.Children
                    .Where(pair => pair.Key is YamlScalarNode key && string.Equals(key.Value, segment, StringComparison.Ordinal))
                    .Select(static pair => pair.Value)
                    .SingleOrDefault() ?? throw new WorkflowDeliveryConfigurationException("INVALID_PACKAGE_POINTER", $"YAML pointer segment '{segment}' was not found."),
                YamlSequenceNode sequence when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sequence.Children.Count => sequence.Children[index],
                _ => throw new WorkflowDeliveryConfigurationException("INVALID_PACKAGE_POINTER", $"YAML pointer segment '{segment}' cannot be traversed."),
            };
        }
        return current;
    }

    private static void ReplaceJsonPointer(JsonNode root, string pointer, JsonNode replacement, string fieldKey)
    {
        if (!pointer.StartsWith("/", StringComparison.Ordinal))
            throw InvalidPointer(fieldKey);
        var segments = pointer.Split('/').Skip(1).Select(DecodePointerSegment).ToArray();
        if (segments.Length == 0)
            throw InvalidPointer(fieldKey);
        JsonNode current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = current switch
            {
                JsonObject obj when obj.TryGetPropertyValue(segments[i], out var child) && child != null => child,
                JsonArray array when int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < array.Count && array[index] != null => array[index]!,
                _ => throw InvalidPointer(fieldKey),
            };
        }
        var leaf = segments[^1];
        switch (current)
        {
            case JsonObject obj when obj.ContainsKey(leaf):
                obj[leaf] = replacement;
                break;
            case JsonArray array when int.TryParse(leaf, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < array.Count:
                array[index] = replacement;
                break;
            default:
                throw InvalidPointer(fieldKey);
        }
    }

    private static int ReplaceUserServiceIds(YamlNode node, string userServiceId)
    {
        var count = 0;
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var pair in mapping.Children)
                {
                    if (pair.Key is YamlScalarNode key &&
                        string.Equals(key.Value, "user_service_id", StringComparison.Ordinal) &&
                        pair.Value is YamlScalarNode value)
                    {
                        value.Value = userServiceId;
                        value.Style = ScalarStyle.Plain;
                        count++;
                    }
                    else
                    {
                        count += ReplaceUserServiceIds(pair.Value, userServiceId);
                    }
                }
                break;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                    count += ReplaceUserServiceIds(child, userServiceId);
                break;
        }
        return count;
    }

    private static string DecodePointerSegment(string value) =>
        value.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    private static WorkflowDeliveryConfigurationException InvalidPointer(string fieldKey) =>
        new("INVALID_PACKAGE_POINTER", $"Package pointer for field '{fieldKey}' is invalid.");

    private sealed record TypedValue(string Canonical, JsonNode Node);
}

public sealed class WorkflowDeliveryConfigurationException(string code, string safeMessage)
    : InvalidOperationException(safeMessage)
{
    public string Code { get; } = code;
}
