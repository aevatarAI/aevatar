using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevatar.Workflow.Infrastructure.Runs.DocumentExtraction;

internal sealed class DocumentSchemaContract
{
    private const int MaxSchemaContractChars = 32_000;
    private static readonly JsonWriterOptions CanonicalWriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
    };

    private static readonly HashSet<string> AllowedSchemaKeywords = new(StringComparer.Ordinal)
    {
        "$schema",
        "type",
        "properties",
        "required",
        "additionalProperties",
        "items",
        "enum",
        "description",
        "title",
    };

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "object",
        "array",
        "string",
        "number",
        "integer",
        "boolean",
        "null",
    };

    private DocumentSchemaContract(
        string name,
        string? description,
        JsonElement schema,
        string canonicalSchemaJson,
        string hash)
    {
        Name = name;
        Description = description;
        Schema = schema.Clone();
        CanonicalSchemaJson = canonicalSchemaJson;
        Hash = hash;
    }

    public string Name { get; }

    public string? Description { get; }

    public JsonElement Schema { get; }

    public string CanonicalSchemaJson { get; }

    public string Hash { get; }

    public static DocumentSchemaContract Parse(JsonElement contractElement)
    {
        if (contractElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("document_extract schema_contract must be a JSON object.");

        var name = GetRequiredString(contractElement, "name", "schema_contract.name");
        if (!IsSafeName(name))
            throw new ArgumentException(
                "document_extract schema_contract.name must contain only letters, numbers, underscores, or hyphens.");

        var description = GetOptionalString(contractElement, "description", "schema_contract.description");
        if (!contractElement.TryGetProperty("schema", out var schemaElement))
            throw new ArgumentException("document_extract schema_contract.schema is required.");
        if (schemaElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("document_extract schema_contract.schema must be a JSON object.");

        var canonicalSchemaJson = Canonicalize(schemaElement);
        if (canonicalSchemaJson.Length > MaxSchemaContractChars)
            throw new ArgumentException(
                $"document_extract schema_contract.schema exceeds {MaxSchemaContractChars} canonical JSON characters.");

        ValidateSchema(schemaElement, path: "schema_contract.schema");
        var hash = ComputeHash(canonicalSchemaJson);
        return new DocumentSchemaContract(name, description, schemaElement, canonicalSchemaJson, hash);
    }

    public void ValidateResult(JsonElement resultElement)
    {
        ValidateValueAgainstSchema(
            resultElement,
            Schema,
            path: "structured_result",
            strictAdditionalProperties: true);
    }

    public static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CanonicalWriterOptions))
        {
            WriteCanonicalElement(writer, element);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ComputeHash(string canonicalSchemaJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSchemaJson));
        return $"sha256:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static void ValidateSchema(JsonElement schemaElement, string path)
    {
        foreach (var property in schemaElement.EnumerateObject())
        {
            if (!AllowedSchemaKeywords.Contains(property.Name))
                throw new ArgumentException(
                    $"document_extract schema_contract contains unsupported schema keyword '{property.Name}'.");
        }

        var types = GetSchemaTypes(schemaElement);
        if (types.Count == 0)
            throw new ArgumentException($"document_extract {path}.type is required.");

        foreach (var type in types)
        {
            if (!AllowedTypes.Contains(type))
                throw new ArgumentException($"document_extract {path}.type '{type}' is not supported.");
        }

        if (types.Contains("object"))
            ValidateObjectSchema(schemaElement, path);
        if (types.Contains("array"))
            ValidateArraySchema(schemaElement, path);
        if (schemaElement.TryGetProperty("enum", out var enumElement) &&
            enumElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"document_extract {path}.enum must be an array.");
    }

    private static void ValidateObjectSchema(JsonElement schemaElement, string path)
    {
        if (schemaElement.TryGetProperty("properties", out var propertiesElement))
        {
            if (propertiesElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"document_extract {path}.properties must be an object.");

            foreach (var property in propertiesElement.EnumerateObject())
            {
                if (IsUnsafeResultPropertyName(property.Name))
                    throw new ArgumentException(
                        $"document_extract schema_contract contains unsafe result property '{property.Name}'.");
                if (property.Value.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException($"document_extract {path}.properties.{property.Name} must be an object.");

                ValidateSchema(property.Value, $"{path}.properties.{property.Name}");
            }
        }

        if (schemaElement.TryGetProperty("required", out var requiredElement))
        {
            if (requiredElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException($"document_extract {path}.required must be an array.");

            foreach (var item in requiredElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    throw new ArgumentException($"document_extract {path}.required entries must be strings.");
            }
        }

        if (schemaElement.TryGetProperty("additionalProperties", out var additionalPropertiesElement) &&
            additionalPropertiesElement.ValueKind != JsonValueKind.True &&
            additionalPropertiesElement.ValueKind != JsonValueKind.False)
        {
            throw new ArgumentException($"document_extract {path}.additionalProperties must be a boolean.");
        }
    }

    private static void ValidateArraySchema(JsonElement schemaElement, string path)
    {
        if (!schemaElement.TryGetProperty("items", out var itemsElement))
            throw new ArgumentException($"document_extract {path}.items is required for array schemas.");
        if (itemsElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"document_extract {path}.items must be an object.");

        ValidateSchema(itemsElement, $"{path}.items");
    }

    private static void ValidateValueAgainstSchema(
        JsonElement valueElement,
        JsonElement schemaElement,
        string path,
        bool strictAdditionalProperties)
    {
        var types = GetSchemaTypes(schemaElement);
        if (!MatchesAnyType(valueElement, types))
            throw new DocumentSchemaValidationException();

        if (schemaElement.TryGetProperty("enum", out var enumElement) &&
            !MatchesEnumValue(valueElement, enumElement))
            throw new DocumentSchemaValidationException();

        if (valueElement.ValueKind == JsonValueKind.Object && types.Contains("object"))
        {
            ValidateObjectValue(valueElement, schemaElement, path, strictAdditionalProperties);
            return;
        }

        if (valueElement.ValueKind == JsonValueKind.Array && types.Contains("array"))
            ValidateArrayValue(valueElement, schemaElement, path, strictAdditionalProperties);
    }

    private static void ValidateObjectValue(
        JsonElement valueElement,
        JsonElement schemaElement,
        string path,
        bool strictAdditionalProperties)
    {
        var properties = schemaElement.TryGetProperty("properties", out var propertiesElement) &&
                         propertiesElement.ValueKind == JsonValueKind.Object
            ? propertiesElement
            : default;

        if (schemaElement.TryGetProperty("required", out var requiredElement) &&
            requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var required in requiredElement.EnumerateArray())
            {
                var requiredName = required.GetString();
                if (string.IsNullOrWhiteSpace(requiredName) ||
                    !valueElement.TryGetProperty(requiredName, out _))
                    throw new DocumentSchemaValidationException();
            }
        }

        var additionalProperties = schemaElement.TryGetProperty(
                                       "additionalProperties",
                                       out var additionalPropertiesElement) &&
                                   additionalPropertiesElement.ValueKind == JsonValueKind.False
            ? false
            : !strictAdditionalProperties;

        foreach (var property in valueElement.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateValueAgainstSchema(
                    property.Value,
                    propertySchema,
                    $"{path}.{property.Name}",
                    strictAdditionalProperties);
                continue;
            }

            if (!additionalProperties)
                throw new DocumentSchemaValidationException();
        }
    }

    private static void ValidateArrayValue(
        JsonElement valueElement,
        JsonElement schemaElement,
        string path,
        bool strictAdditionalProperties)
    {
        if (!schemaElement.TryGetProperty("items", out var itemSchema))
            throw new DocumentSchemaValidationException();

        var index = 0;
        foreach (var item in valueElement.EnumerateArray())
        {
            ValidateValueAgainstSchema(
                item,
                itemSchema,
                $"{path}[{index}]",
                strictAdditionalProperties);
            index++;
        }
    }

    private static List<string> GetSchemaTypes(JsonElement schemaElement)
    {
        if (!schemaElement.TryGetProperty("type", out var typeElement))
            return [];

        if (typeElement.ValueKind == JsonValueKind.String)
            return [NormalizeType(typeElement.GetString())];

        if (typeElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("document_extract schema_contract schema type must be a string or string array.");

        var types = new List<string>();
        foreach (var typeItem in typeElement.EnumerateArray())
        {
            if (typeItem.ValueKind != JsonValueKind.String)
                throw new ArgumentException("document_extract schema_contract schema type entries must be strings.");
            types.Add(NormalizeType(typeItem.GetString()));
        }

        return types;
    }

    private static string NormalizeType(string? type) =>
        string.IsNullOrWhiteSpace(type) ? string.Empty : type.Trim().ToLowerInvariant();

    private static bool MatchesAnyType(JsonElement valueElement, IReadOnlyCollection<string> types) =>
        types.Any(type => MatchesType(valueElement, type));

    private static bool MatchesType(JsonElement valueElement, string type) =>
        type switch
        {
            "object" => valueElement.ValueKind == JsonValueKind.Object,
            "array" => valueElement.ValueKind == JsonValueKind.Array,
            "string" => valueElement.ValueKind == JsonValueKind.String,
            "number" => valueElement.ValueKind == JsonValueKind.Number,
            "integer" => valueElement.ValueKind == JsonValueKind.Number &&
                         valueElement.TryGetInt64(out _),
            "boolean" => valueElement.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => valueElement.ValueKind == JsonValueKind.Null,
            _ => false,
        };

    private static bool MatchesEnumValue(JsonElement valueElement, JsonElement enumElement)
    {
        var canonicalValue = Canonicalize(valueElement);
        return enumElement.EnumerateArray()
            .Any(enumValue => string.Equals(canonicalValue, Canonicalize(enumValue), StringComparison.Ordinal));
    }

    private static string GetRequiredString(JsonElement source, string propertyName, string path)
    {
        if (!source.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"document_extract {path} is required.");

        return value.GetString()!.Trim();
    }

    private static string? GetOptionalString(JsonElement source, string propertyName, string path)
    {
        if (!source.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return value.ValueKind == JsonValueKind.String
            ? string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim()
            : throw new ArgumentException($"document_extract {path} must be a string.");
    }

    private static bool IsSafeName(string value) =>
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static bool IsUnsafeResultPropertyName(string value)
    {
        var normalized = new string(value.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized.Contains("base64", StringComparison.Ordinal) ||
               normalized.Contains("datauri", StringComparison.Ordinal) ||
               normalized.Contains("rawpayload", StringComparison.Ordinal) ||
               normalized.Contains("rawbody", StringComparison.Ordinal) ||
               normalized.Contains("rawprompt", StringComparison.Ordinal) ||
               normalized == "raw" ||
               normalized == "prompt" ||
               normalized == "providerraw";
    }

    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalElement(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException("document_extract JSON value is not supported.");
        }
    }
}

internal sealed class DocumentSchemaValidationException : Exception;
