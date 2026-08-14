using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.Modules;

internal static class WorkflowToolResponseProjector
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = 64,
    };

    public static string Project(string responseJson, WorkflowToolResponseProjection projection)
    {
        WorkflowToolResponseProjectionContract.ValidateOrThrow(projection);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(responseJson ?? string.Empty, documentOptions: DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new WorkflowToolResponseProjectionException(
                "The tool response is not valid JSON.",
                exception);
        }

        var output = new JsonObject();
        foreach (var field in projection.Fields)
            output[field.OutputName] = ProjectField(root, field)?.DeepClone();

        var projected = output.ToJsonString();
        if (Encoding.UTF8.GetByteCount(projected) >
            WorkflowToolResponseProjectionContract.MaxProjectedResponseBytes)
        {
            throw new WorkflowToolResponseProjectionException(
                "The projected tool response exceeds the durable response limit.");
        }

        return projected;
    }

    private static JsonNode? ProjectField(
        JsonNode? root,
        WorkflowToolResponseProjectionField field)
    {
        var current = root;
        foreach (var operation in field.Operations)
        {
            current = operation.OperationCase switch
            {
                WorkflowToolResponseProjectionOperation.OperationOneofCase.JsonPointer =>
                    ResolveRequiredPointer(current, operation.JsonPointer, field.OutputName),
                WorkflowToolResponseProjectionOperation.OperationOneofCase.ParseJson =>
                    ParseRequiredJson(current, field.OutputName),
                WorkflowToolResponseProjectionOperation.OperationOneofCase.ArrayMatch =>
                    ResolveRequiredArrayMatch(current, operation.ArrayMatch, field.OutputName),
                _ => throw new WorkflowToolResponseProjectionException(
                    $"Projection field '{field.OutputName}' contains an unsupported operation."),
            };
        }

        return current;
    }

    private static JsonNode? ResolveRequiredPointer(
        JsonNode? current,
        string pointer,
        string outputName)
    {
        if (TryResolvePointer(current, pointer, out var selected))
            return selected;

        throw new WorkflowToolResponseProjectionException(
            $"Projection field '{outputName}' could not resolve a required JSON pointer.");
    }

    private static JsonNode? ParseRequiredJson(JsonNode? current, string outputName)
    {
        if (current is not JsonValue value || !value.TryGetValue<string>(out var encoded))
        {
            throw new WorkflowToolResponseProjectionException(
                $"Projection field '{outputName}' can only parse a JSON string.");
        }

        try
        {
            return JsonNode.Parse(encoded, documentOptions: DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new WorkflowToolResponseProjectionException(
                $"Projection field '{outputName}' selected an invalid encoded JSON value.",
                exception);
        }
    }

    private static JsonNode? ResolveRequiredArrayMatch(
        JsonNode? current,
        WorkflowToolResponseProjectionArrayMatch match,
        string outputName)
    {
        if (current is not JsonArray array)
        {
            throw new WorkflowToolResponseProjectionException(
                $"Projection field '{outputName}' can only match elements in a JSON array.");
        }

        JsonNode? selected = null;
        var matches = 0;
        foreach (var element in array)
        {
            if (!TryResolvePointer(element, match.ElementJsonPointer, out var candidate) ||
                candidate is not JsonValue value ||
                !value.TryGetValue<string>(out var candidateText) ||
                !string.Equals(candidateText, match.ExpectedString, StringComparison.Ordinal))
            {
                continue;
            }

            selected = element;
            matches++;
        }

        if (matches != 1)
        {
            throw new WorkflowToolResponseProjectionException(
                $"Projection field '{outputName}' requires exactly one matching array element.");
        }

        return selected;
    }

    private static bool TryResolvePointer(
        JsonNode? root,
        string pointer,
        out JsonNode? selected)
    {
        selected = root;
        if (pointer.Length == 0)
            return true;

        foreach (var encodedSegment in pointer.Split('/').Skip(1))
        {
            var segment = encodedSegment
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            switch (selected)
            {
                case JsonObject jsonObject:
                    if (!jsonObject.TryGetPropertyValue(segment, out selected))
                        return false;
                    break;
                case JsonArray jsonArray:
                    if (!TryParseArrayIndex(segment, jsonArray.Count, out var index))
                        return false;
                    selected = jsonArray[index];
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool TryParseArrayIndex(string segment, int count, out int index)
    {
        index = -1;
        if (segment.Length == 0 ||
            (segment.Length > 1 && segment[0] == '0') ||
            !int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < 0 ||
            parsed >= count)
        {
            return false;
        }

        index = parsed;
        return true;
    }
}

internal sealed class WorkflowToolResponseProjectionException : InvalidOperationException
{
    public WorkflowToolResponseProjectionException(string message)
        : base(message)
    {
    }

    public WorkflowToolResponseProjectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
