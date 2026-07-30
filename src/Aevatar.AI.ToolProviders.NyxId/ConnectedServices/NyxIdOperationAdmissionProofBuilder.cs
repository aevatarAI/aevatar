using System.Globalization;
using System.Text.Json.Nodes;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

internal static class NyxIdOperationAdmissionProofBuilder
{
    private const int MaxSchemaDepth = 16;
    private static readonly HashSet<string> SupportedSchemaKeywords = new(StringComparer.Ordinal)
    {
        "type", "enum", "properties", "required", "items", "additionalProperties",
        "title", "description", "default", "example", "examples", "deprecated",
    };

    public static ExternalWorkflowCapabilityRef Build(
        string userServiceId,
        string serviceSlug,
        NyxIdMcpEndpoint operation,
        string contractDigest)
    {
        var proof = new NyxIdUserServiceCapabilityRef
        {
            UserServiceId = userServiceId,
            ServiceSlugSnapshot = serviceSlug,
            EndpointId = operation.EndpointId,
            HttpMethod = operation.Method.ToUpperInvariant(),
            PathTemplate = operation.PathTemplate,
            ContractDigest = contractDigest,
            ExecutionPolicy = operation.ExecutionPolicy,
            ResponsePolicy = new NyxIdOperationResponsePolicy
            {
                TextAllowed = operation.BinaryArtifact is not true,
                FileArtifactAllowed = operation.BinaryArtifact is true,
            },
        };
        proof.ResponsePolicy.MediaTypes.Add(operation.ResponseMediaTypes);

        foreach (var parameter in operation.Parameters
                     .OrderBy(static parameter => parameter.In)
                     .ThenBy(static parameter => parameter.Name, StringComparer.Ordinal))
        {
            var contract = new NyxIdOperationParameterContract
            {
                Name = parameter.Name,
                Location = MapLocation(parameter.In),
                Required = parameter.Required || parameter.In == ParameterLocation.Path,
                Schema = ConvertSchema(parameter.Schema, depth: 0),
            };
            if (!IsRequiredHeaderSatisfiable(contract))
            {
                throw new NyxIdOperationSchemaUnsupportedException(
                    $"Required header '{contract.Name}' has no value accepted by the workflow header policy.");
            }
            proof.Parameters.Add(contract);
        }

        if (operation.RequestBodySchema is not null)
        {
            proof.RequestBody = new NyxIdOperationRequestBodyContract
            {
                Required = operation.RequestBodyRequired,
                MediaType = operation.RequestBodyMediaType ?? "application/json",
                Schema = ConvertSchema(operation.RequestBodySchema, depth: 0),
            };
        }

        return new ExternalWorkflowCapabilityRef { NyxIdUserService = proof };
    }

    private static NyxIdOperationSchema ConvertSchema(JsonNode? node, int depth)
    {
        if (depth >= MaxSchemaDepth)
            throw new NyxIdOperationSchemaUnsupportedException("OpenAPI schema nesting exceeds the supported admission limit.");

        if (node is null)
            return new NyxIdOperationSchema { ValueKind = NyxIdOperationValueKind.String };
        if (node is not JsonObject schema)
            throw new NyxIdOperationSchemaUnsupportedException("OpenAPI parameter schema must be an object.");
        EnsureSupportedSchema(schema);

        if (schema.ContainsKey("oneOf") || schema.ContainsKey("anyOf") ||
            schema.ContainsKey("allOf") || schema.ContainsKey("not"))
        {
            throw new NyxIdOperationSchemaUnsupportedException(
                "OpenAPI composed schemas are not supported by workflow operation admission.");
        }

        var type = ReadSchemaType(schema);
        var result = new NyxIdOperationSchema { ValueKind = MapValueKind(type) };

        if (schema["enum"] is JsonArray allowedValues)
        {
            foreach (var value in allowedValues)
                result.AllowedValues.Add(ToCanonicalScalar(value, result.ValueKind));
        }

        if (result.ValueKind == NyxIdOperationValueKind.Object)
        {
            if (schema["additionalProperties"] is JsonValue additional &&
                additional.TryGetValue<bool>(out var allowed))
            {
                result.AdditionalPropertiesAllowed = allowed;
            }
            else
            {
                result.AdditionalPropertiesAllowed = false;
            }

            var properties = schema["properties"] as JsonObject;
            var required = schema["required"] is JsonArray requiredArray
                ? ReadRequiredProperties(requiredArray).ToArray()
                : [];
            if (required.Any(name => properties is null || !properties.ContainsKey(name)))
            {
                throw new NyxIdOperationSchemaUnsupportedException(
                    "OpenAPI required properties must name declared object properties.");
            }
            result.RequiredProperties.Add(required);

            if (properties is not null)
            {
                foreach (var (name, propertySchema) in properties.OrderBy(static item => item.Key, StringComparer.Ordinal))
                {
                    result.Properties.Add(new NyxIdOperationSchemaProperty
                    {
                        Name = name,
                        Schema = ConvertSchema(propertySchema, depth + 1),
                    });
                }
            }
        }
        else if (result.ValueKind == NyxIdOperationValueKind.Array)
        {
            if (schema["items"] is not JsonObject)
            {
                throw new NyxIdOperationSchemaUnsupportedException(
                    "OpenAPI array schemas must publish an item schema.");
            }
            result.Items = ConvertSchema(schema["items"], depth + 1);
        }

        return result;
    }

    private static bool IsRequiredHeaderSatisfiable(NyxIdOperationParameterContract parameter)
    {
        if (!parameter.Required || parameter.Location != NyxIdOperationParameterLocation.Header)
            return true;

        var candidates = parameter.Schema.AllowedValues.Count > 0
            ? parameter.Schema.AllowedValues
            : parameter.Schema.ValueKind switch
            {
                NyxIdOperationValueKind.String =>
                    [string.Equals(parameter.Name, "Accept", StringComparison.OrdinalIgnoreCase)
                        ? "application/json"
                        : "*"],
                NyxIdOperationValueKind.Integer or NyxIdOperationValueKind.Number => ["1"],
                NyxIdOperationValueKind.Boolean => ["true"],
                _ => [],
            };
        return candidates.Any(value =>
            NyxIdOperationHeaderPolicy.IsValidWorkflowHeader(parameter.Name, value));
    }

    private static void EnsureSupportedSchema(JsonObject schema)
    {
        if (HasMalformedKeyword(schema) || HasContradictoryKeywords(schema))
        {
            throw new NyxIdOperationSchemaUnsupportedException(
                "OpenAPI schema contains unsupported or malformed validation keywords.");
        }

        if (schema["properties"] is JsonObject properties &&
            properties.Any(static property => property.Value is not JsonObject))
        {
            throw new NyxIdOperationSchemaUnsupportedException(
                "OpenAPI object properties must contain schema objects.");
        }
    }

    private static bool HasMalformedKeyword(JsonObject schema) =>
        schema.Any(static property => !SupportedSchemaKeywords.Contains(property.Key)) ||
        schema.ContainsKey("type") && !IsNormalizedSchemaType(schema["type"]) ||
        schema.ContainsKey("enum") && schema["enum"] is not JsonArray ||
        schema["enum"] is JsonArray { Count: 0 } ||
        schema.ContainsKey("properties") && schema["properties"] is not JsonObject ||
        schema.ContainsKey("required") && schema["required"] is not JsonArray ||
        schema.ContainsKey("items") && schema["items"] is not JsonObject ||
        schema.ContainsKey("additionalProperties") && !IsBoolean(schema["additionalProperties"]);

    private static bool HasContradictoryKeywords(JsonObject schema)
    {
        var type = NormalizedSchemaType(schema["type"]);
        var hasObjectKeywords = schema.ContainsKey("properties") ||
                                schema.ContainsKey("required") ||
                                schema.ContainsKey("additionalProperties");
        var hasItems = schema.ContainsKey("items");
        return type == "object" && hasItems ||
               type == "array" && (hasObjectKeywords || !hasItems) ||
               type is not (null or "object" or "array") && (hasObjectKeywords || hasItems) ||
               type is null && (hasItems || !schema.ContainsKey("properties") && hasObjectKeywords);
    }

    private static bool IsNormalizedSchemaType(JsonNode? node) =>
        NormalizedSchemaType(node) is { Length: > 0 };

    private static string? NormalizedSchemaType(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var type)
            ? type.Trim().ToLowerInvariant()
            : null;

    private static bool IsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out _);

    private static bool IsBoolean(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out _);

    private static IEnumerable<string> ReadRequiredProperties(JsonArray required)
    {
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in required)
        {
            if (item is not JsonValue value ||
                !value.TryGetValue<string>(out var property) ||
                string.IsNullOrEmpty(property) ||
                !string.Equals(property, property.Trim(), StringComparison.Ordinal))
            {
                throw new NyxIdOperationSchemaUnsupportedException(
                    "OpenAPI required properties must be normalized non-empty strings.");
            }

            properties.Add(property);
        }

        return properties.OrderBy(static property => property, StringComparer.Ordinal);
    }

    private static string ReadSchemaType(JsonObject schema)
    {
        var type = NormalizedSchemaType(schema["type"]);
        if (type is null)
            return schema["properties"] is JsonObject ? "object" : "string";
        return type;
    }

    private static NyxIdOperationValueKind MapValueKind(string type) => type switch
    {
        "string" => NyxIdOperationValueKind.String,
        "integer" => NyxIdOperationValueKind.Integer,
        "number" => NyxIdOperationValueKind.Number,
        "boolean" => NyxIdOperationValueKind.Boolean,
        "object" => NyxIdOperationValueKind.Object,
        "array" => NyxIdOperationValueKind.Array,
        _ => throw new NyxIdOperationSchemaUnsupportedException(
            $"OpenAPI schema type '{type}' is not supported by workflow operation admission."),
    };

    private static NyxIdOperationParameterLocation MapLocation(ParameterLocation location) => location switch
    {
        ParameterLocation.Path => NyxIdOperationParameterLocation.Path,
        ParameterLocation.Query => NyxIdOperationParameterLocation.Query,
        ParameterLocation.Header => NyxIdOperationParameterLocation.Header,
        _ => NyxIdOperationParameterLocation.Unspecified,
    };

    private static string ToCanonicalScalar(
        JsonNode? node,
        NyxIdOperationValueKind valueKind)
    {
        if (node is null)
            throw new NyxIdOperationSchemaUnsupportedException("OpenAPI enum values must not be null.");
        if (node is not JsonValue value)
            throw new NyxIdOperationSchemaUnsupportedException("OpenAPI enum values must be scalar.");

        if (valueKind == NyxIdOperationValueKind.String &&
            value.TryGetValue<string>(out var text))
            return text;
        if (valueKind == NyxIdOperationValueKind.Boolean &&
            value.TryGetValue<bool>(out var boolean))
            return boolean ? "true" : "false";
        if (valueKind == NyxIdOperationValueKind.Integer &&
            value.TryGetValue<long>(out var integer))
            return integer.ToString(CultureInfo.InvariantCulture);
        if (valueKind == NyxIdOperationValueKind.Number)
        {
            if (value.TryGetValue<long>(out var wholeNumber))
                return wholeNumber.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue<double>(out var number))
                return number.ToString("R", CultureInfo.InvariantCulture);
        }

        throw new NyxIdOperationSchemaUnsupportedException(
            "OpenAPI enum values must match the schema type and use a runtime-enforced scalar kind.");
    }

}

internal sealed class NyxIdOperationSchemaUnsupportedException(string message)
    : InvalidOperationException(message);
