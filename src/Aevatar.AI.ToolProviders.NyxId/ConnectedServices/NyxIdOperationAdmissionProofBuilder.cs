using System.Globalization;
using System.Text.Json.Nodes;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

internal static class NyxIdOperationAdmissionProofBuilder
{
    private const int MaxSchemaDepth = 16;

    public static ExternalWorkflowCapabilityRef Build(
        string userServiceId,
        string serviceSlug,
        ConnectedServiceToolOperation operation,
        string contractDigest)
    {
        var proof = new NyxIdUserServiceCapabilityRef
        {
            UserServiceId = userServiceId,
            ServiceSlugSnapshot = serviceSlug,
            OperationId = operation.OperationId,
            HttpMethod = operation.Method.ToUpperInvariant(),
            PathTemplate = operation.PathTemplate,
            ContractDigest = contractDigest,
            ResponsePolicy = new NyxIdOperationResponsePolicy
            {
                TextAllowed = true,
                FileArtifactAllowed = operation.ResponseMediaTypes.Any(IsBinaryMediaType),
            },
        };
        proof.ResponsePolicy.MediaTypes.Add(operation.ResponseMediaTypes);

        foreach (var parameter in operation.Parameters
                     .OrderBy(static parameter => parameter.In)
                     .ThenBy(static parameter => parameter.Name, StringComparer.Ordinal))
        {
            proof.Parameters.Add(new NyxIdOperationParameterContract
            {
                Name = parameter.Name,
                Location = MapLocation(parameter.In),
                Required = parameter.Required || parameter.In == ParameterLocation.Path,
                Schema = ConvertSchema(parameter.Schema, depth: 0),
            });
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

        if (schema.ContainsKey("oneOf") || schema.ContainsKey("anyOf") ||
            schema.ContainsKey("allOf") || schema.ContainsKey("not"))
        {
            throw new NyxIdOperationSchemaUnsupportedException(
                "OpenAPI composed schemas are not supported by workflow operation admission.");
        }

        var type = schema["type"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(type))
            type = schema["properties"] is JsonObject ? "object" : "string";
        var result = new NyxIdOperationSchema { ValueKind = MapValueKind(type) };

        if (schema["enum"] is JsonArray allowedValues)
        {
            foreach (var value in allowedValues)
                result.AllowedValues.Add(ToCanonicalScalar(value));
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

            if (schema["required"] is JsonArray required)
            {
                result.RequiredProperties.Add(required
                    .Select(static item => item?.GetValue<string>()?.Trim() ?? string.Empty)
                    .Where(static value => value.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static value => value, StringComparer.Ordinal));
            }

            if (schema["properties"] is JsonObject properties)
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
            result.Items = ConvertSchema(schema["items"], depth + 1);
        }

        return result;
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

    private static string ToCanonicalScalar(JsonNode? node)
    {
        if (node is null)
            return "null";
        if (node is not JsonValue value)
            throw new NyxIdOperationSchemaUnsupportedException("OpenAPI enum values must be scalar.");
        if (value.TryGetValue<string>(out var text))
            return text;
        if (value.TryGetValue<bool>(out var boolean))
            return boolean ? "true" : "false";
        if (value.TryGetValue<long>(out var integer))
            return integer.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var number))
            return number.ToString("R", CultureInfo.InvariantCulture);
        throw new NyxIdOperationSchemaUnsupportedException("OpenAPI enum values must be scalar.");
    }

    private static bool IsBinaryMediaType(string mediaType)
    {
        var normalized = mediaType.Trim().ToLowerInvariant();
        return normalized == "application/octet-stream" ||
               normalized == "application/pdf" ||
               normalized == "application/zip" ||
               normalized.StartsWith("image/", StringComparison.Ordinal) ||
               normalized.StartsWith("audio/", StringComparison.Ordinal) ||
               normalized.StartsWith("video/", StringComparison.Ordinal);
    }
}

internal sealed class NyxIdOperationSchemaUnsupportedException(string message)
    : InvalidOperationException(message);
