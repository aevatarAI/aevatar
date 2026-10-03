using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.ToolProviders.NyxId.CatalogSkills;
using Aevatar.GAgentService.Abstractions.CatalogSkills;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>Reads the catalog-owned document surface, never a caller's instance override.</summary>
public sealed class NyxIdCatalogSkillContentReader(NyxIdCatalogSkillClient catalogs, ILogger logger)
{
    public async Task<CatalogSkillContentInput> ReadAsync(string token, string catalogServiceId, CancellationToken ct)
    {
        var catalog = await catalogs.ReadCatalogAsync(token, catalogServiceId, ct).ConfigureAwait(false);
        var body = await catalogs.ReadOpenApiAsync(token, catalog.Id, ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            EnsureUniqueProperties(root);
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("openapi", out var version) || version.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
                throw InvalidContract();

            var (endpoints, issues, _) = NyxIdMcpOperationCatalog.ParseOpenApiOperations(catalog.Id, catalog.Slug, root, paths);
            foreach (var issue in issues.GroupBy(static issue => issue.Code))
                logger.LogInformation("Catalog API operation diagnostic. catalogServiceId={CatalogServiceId} stage=read_contract code={Code} count={Count}",
                    catalog.Id, issue.Key, issue.Count());

            var operations = new List<CatalogApiOperation>();
            foreach (var endpoint in endpoints)
            {
                var operation = paths.GetProperty(endpoint.PathTemplate).EnumerateObject()
                    .Single(property => string.Equals(property.Name, endpoint.Method, StringComparison.OrdinalIgnoreCase)).Value;
                var references = new NyxIdOpenApiReferenceResolver(root);
                var responses = operation.GetProperty("responses").EnumerateObject()
                    .Select(response => ReadResponse(response.Name, references.ResolveObject(response.Value), references))
                    .ToArray();
                operations.Add(new CatalogApiOperation(endpoint.EndpointId, endpoint.Name, endpoint.Method,
                    endpoint.PathTemplate, endpoint.ExecutionPolicy.Risk,
                    endpoint.Parameters.Select(parameter => new CatalogApiParameter(parameter.Name, parameter.In,
                        parameter.Required, NormalizeSchema(parameter.Schema), parameter.Description)).ToArray(),
                    endpoint.RequestBodySchema is null ? null : new CatalogApiRequestBody(endpoint.RequestBodyRequired,
                        [new CatalogApiMediaType(endpoint.RequestBodyMediaType ?? "application/json", NormalizeSchema(endpoint.RequestBodySchema))]),
                    responses));
            }
            if (operations.Count == 0)
                throw InvalidContract();
            return new CatalogSkillContentInput(catalog.Id, catalog.Slug, catalog.Name, catalog.Description ?? string.Empty,
                new CatalogApiContract(operations));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                         KeyNotFoundException or InvalidJsonException or InvalidProtocolBufferException)
        {
            // External document text and provider exception messages must not enter diagnostics.
            throw InvalidContract();
        }
    }

    private static CatalogApiResponse ReadResponse(string status, JsonElement response, NyxIdOpenApiReferenceResolver references)
    {
        var description = response.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? string.Empty : string.Empty;
        var media = new List<CatalogApiMediaType>();
        if (response.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in content.EnumerateObject())
            {
                var schema = item.Value.TryGetProperty("schema", out var value) ? NormalizeSchema(references.ResolveSchema(value)) : null;
                media.Add(new CatalogApiMediaType(item.Name, schema));
            }
        }
        return new CatalogApiResponse(status, description, media);
    }

    private static Value? NormalizeSchema(JsonNode? value)
    {
        if (value is null)
            return null;
        var schemaJson = CleanSchema(value).ToJsonString();
        var normalized = JsonParser.Default.Parse<Value>(schemaJson);
        using var original = JsonDocument.Parse(schemaJson);
        using var roundTrip = JsonDocument.Parse(JsonFormatter.Default.Format(normalized));
        EnsureNumericFidelity(original.RootElement, roundTrip.RootElement);
        return normalized;
    }

    private static void EnsureNumericFidelity(JsonElement original, JsonElement roundTrip)
    {
        switch (original.ValueKind)
        {
            case JsonValueKind.Number:
                if (roundTrip.ValueKind != JsonValueKind.Number ||
                    CanonicalNumber(original.GetRawText()) != CanonicalNumber(roundTrip.GetRawText()))
                    throw InvalidContract();
                break;
            case JsonValueKind.Object:
                foreach (var property in original.EnumerateObject())
                    EnsureNumericFidelity(property.Value, roundTrip.GetProperty(property.Name));
                break;
            case JsonValueKind.Array:
                for (var index = 0; index < original.GetArrayLength(); index++)
                    EnsureNumericFidelity(original[index], roundTrip[index]);
                break;
        }
    }

    private static (string Significand, long Exponent) CanonicalNumber(string number)
    {
        // Compare decimal values without rounding through another floating-point type or
        // expanding large powers. Formatting differences such as 125 and 1.2500e2 are equal.
        var exponentIndex = number.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? number : number[..exponentIndex];
        var pointIndex = mantissa.IndexOf('.');
        var fractionalDigits = pointIndex < 0 ? 0 : mantissa.Length - pointIndex - 1;
        var digits = (pointIndex < 0 ? mantissa : mantissa.Remove(pointIndex, 1))
            .TrimStart('-').TrimStart('0');
        if (digits.Length == 0)
            return ("0", 0);

        long exponent = 0;
        if (exponentIndex >= 0 && !long.TryParse(number.AsSpan(exponentIndex + 1),
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            throw InvalidContract();
        var significantDigits = digits.TrimEnd('0');
        var trailingZeros = digits.Length - significantDigits.Length;
        if (exponent < long.MinValue + fractionalDigits)
            throw InvalidContract();
        exponent -= fractionalDigits;
        if (exponent > long.MaxValue - trailingZeros)
            throw InvalidContract();
        return ((number[0] == '-' ? "-" : string.Empty) + significantDigits, exponent + trailingZeros);
    }

    private static JsonNode CleanSchema(JsonNode schema)
    {
        var result = schema.DeepClone();
        if (result is not JsonObject properties)
            return result;
        foreach (var name in properties.Select(property => property.Key).ToArray())
        {
            // Remove source annotations only at schema positions. A business property
            // literally called examples, service_slug or x-value retains its identity.
            if (name is "example" or "examples" or "externalDocs" || name.StartsWith("x-", StringComparison.Ordinal))
            {
                properties.Remove(name);
                continue;
            }
            if (IsSchemaMapKeyword(name) && properties[name] is JsonObject children)
            {
                foreach (var child in children.ToArray())
                {
                    // Legacy dependencies can also contain literal arrays of property names.
                    if (name == "dependencies" && child.Value is JsonArray)
                        continue;
                    if (child.Value is not null) children[child.Key] = CleanSchema(child.Value);
                }
            }
            else if (IsSchemaKeyword(name) && properties[name] is JsonObject child)
                properties[name] = CleanSchema(child);
            else if (IsSchemaArrayKeyword(name) && properties[name] is JsonArray array)
                for (var index = 0; index < array.Count; index++)
                    if (array[index] is { } item) array[index] = CleanSchema(item);
        }
        return result;
    }

    private static bool IsSchemaMapKeyword(string name) =>
        name is "properties" or "patternProperties" or "$defs" or "definitions" or "dependentSchemas" or "dependencies";

    private static bool IsSchemaKeyword(string name) =>
        name is "items" or "additionalProperties" or "additionalItems" or "not" or "contains" or
            "propertyNames" or "if" or "then" or "else" or "unevaluatedProperties" or "unevaluatedItems" or "contentSchema";

    private static bool IsSchemaArrayKeyword(string name) =>
        name is "allOf" or "anyOf" or "oneOf" or "prefixItems" or "items";

    private static void EnsureUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw InvalidContract();
                EnsureUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) EnsureUniqueProperties(child);
    }

    private static CatalogRecommendedSkillUpdateException InvalidContract() => new(
        CatalogRecommendedSkillUpdateErrorCode.ContractInvalid, CatalogRecommendedSkillUpdateStage.ReadContract,
        "The authoritative catalog document does not provide a supported API contract.");
}
