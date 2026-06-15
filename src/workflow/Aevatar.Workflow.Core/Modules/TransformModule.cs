// ─────────────────────────────────────────────────────────────
// TransformModule — 确定性变换模块
// 对 input 执行纯函数变换（count, take, join, split 等）
// 不调用 LLM，纯确定性逻辑
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>确定性变换模块。处理 type=transform 的步骤。</summary>
public sealed class TransformModule : IEventModule<IWorkflowExecutionContext>
{
    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        WriteIndented = true,
    };

    public string Name => "transform";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "transform") return;

        var op = request.Parameters.GetValueOrDefault("op", "identity").Trim().ToLowerInvariant();
        var input = request.Input ?? "";
        var separator = WorkflowParameterValueParser.NormalizeEscapedText(
            WorkflowParameterValueParser.GetString(request.Parameters, "\n", "separator", "delimiter"),
            "\n");
        var n = WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 5, 1, 10_000, "n", "count");

        string output;
        try
        {
            output = op switch
            {
                "identity" => input,
                "count" => CountLines(input).ToString(),
                "count_words" => input.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length.ToString(),
                "take" => TakeLines(input, n),
                "take_last" => TakeLastLines(input, n),
                "join" => string.Join(separator, SplitSections(input)),
                "split" => string.Join(
                    "\n---\n",
                    WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(input, separator)),
                "json_extract" => JsonExtract(input, request.Parameters, n),
                "distinct" => string.Join("\n", input.Split('\n').Distinct()),
                "uppercase" => input.ToUpperInvariant(),
                "lowercase" => input.ToLowerInvariant(),
                "trim" => input.Trim(),
                "reverse_lines" => string.Join("\n", input.Split('\n').Reverse()),
                _ => input, // 未知操作返回原文
            };
        }
        catch (Exception ex)
        {
            output = $"Transform 错误: {ex.Message}";
        }

        ctx.Logger.LogInformation("Transform {StepId}: op={Op}, input_len={InLen}, output_len={OutLen}",
            request.StepId, op, input.Length, output.Length);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true, Output = output,
        }, TopologyAudience.Self, ct);
    }

    private static int CountLines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    private static string TakeLines(string s, int n) => string.Join("\n", s.Split('\n').Take(n));
    private static string TakeLastLines(string s, int n) => string.Join("\n", s.Split('\n').TakeLast(n));
    private static string[] SplitSections(string s) => s.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);

    private static string JsonExtract(string input, IReadOnlyDictionary<string, string> parameters, int n)
    {
        using var document = JsonDocument.Parse(input);

        var path = WorkflowParameterValueParser.GetString(parameters, string.Empty, "path", "json_path").Trim();
        var fields = WorkflowParameterValueParser.GetStringList(parameters, "field", "fields");
        var sortBy = WorkflowParameterValueParser.GetString(parameters, string.Empty, "sort_by").Trim();
        var order = WorkflowParameterValueParser.GetString(parameters, "asc", "order").Trim();

        var target = ResolveJsonPath(document.RootElement, path);
        if (target.ValueKind == JsonValueKind.Array)
        {
            var items = target.EnumerateArray().ToList();
            if (!string.IsNullOrWhiteSpace(sortBy))
            {
                items.Sort((left, right) => ComparePathValues(left, right, sortBy, order));
            }

            var limited = items.Take(n).ToList();
            if (fields.Count == 0)
                return SerializeJsonNodes(limited.Select(ToJsonNode));

            return SerializeJsonNodes(limited.Select(item => ProjectFields(item, fields)));
        }

        if (fields.Count == 0)
            return SerializeJsonNode(ToJsonNode(target));

        if (target.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("json_extract field projection requires a JSON object or array of objects.");

        return SerializeJsonNode(ProjectFields(target, fields));
    }

    private static string SerializeJsonNodes(IEnumerable<JsonNode?> nodes) =>
        JsonSerializer.Serialize(nodes.ToArray(), JsonOutputOptions);

    private static string SerializeJsonNode(JsonNode? node) =>
        JsonSerializer.Serialize(node, JsonOutputOptions);

    private static JsonNode? ToJsonNode(JsonElement element) =>
        JsonNode.Parse(element.GetRawText());

    private static JsonObject ProjectFields(JsonElement source, IReadOnlyList<string> fields)
    {
        if (source.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("json_extract field projection only supports JSON objects.");

        var projected = new JsonObject();
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field))
                continue;

            if (!TryResolveJsonPath(source, field, out var value))
                continue;

            SetProjectedValue(projected, field, ToJsonNode(value));
        }

        return projected;
    }

    private static void SetProjectedValue(JsonObject root, string path, JsonNode? value)
    {
        var segments = SplitPath(path);
        if (segments.Count == 0)
            return;

        JsonObject current = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var segment = segments[i];
            if (current[segment] is not JsonObject child)
            {
                child = new JsonObject();
                current[segment] = child;
            }

            current = child;
        }

        current[segments[^1]] = value?.DeepClone();
    }

    private static int ComparePathValues(JsonElement left, JsonElement right, string path, string order)
    {
        var hasLeft = TryResolveJsonPath(left, path, out var leftValue);
        var hasRight = TryResolveJsonPath(right, path, out var rightValue);

        var comparison = CompareJsonValues(hasLeft ? leftValue : (JsonElement?)null, hasRight ? rightValue : (JsonElement?)null);
        return string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase) ? -comparison : comparison;
    }

    private static int CompareJsonValues(JsonElement? left, JsonElement? right)
    {
        if (!left.HasValue && !right.HasValue)
            return 0;
        if (!left.HasValue)
            return -1;
        if (!right.HasValue)
            return 1;

        var leftValue = left.Value;
        var rightValue = right.Value;

        if (TryGetNumericValue(leftValue, out var leftNumber) && TryGetNumericValue(rightValue, out var rightNumber))
            return leftNumber.CompareTo(rightNumber);

        if (TryGetDateTimeValue(leftValue, out var leftDate) && TryGetDateTimeValue(rightValue, out var rightDate))
            return leftDate.CompareTo(rightDate);

        if (leftValue.ValueKind == JsonValueKind.True || leftValue.ValueKind == JsonValueKind.False ||
            rightValue.ValueKind == JsonValueKind.True || rightValue.ValueKind == JsonValueKind.False)
        {
            return GetBooleanValue(leftValue).CompareTo(GetBooleanValue(rightValue));
        }

        return string.Compare(GetComparableText(leftValue), GetComparableText(rightValue), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetNumericValue(JsonElement element, out decimal value)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetDecimal(out value);

        if (element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            return true;

        value = default;
        return false;
    }

    private static bool TryGetDateTimeValue(JsonElement element, out DateTimeOffset value)
    {
        if (element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value))
            return true;

        value = default;
        return false;
    }

    private static bool GetBooleanValue(JsonElement element) =>
        element.ValueKind == JsonValueKind.True ||
        (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var parsed) && parsed);

    private static string GetComparableText(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();

    private static JsonElement ResolveJsonPath(JsonElement element, string path)
    {
        if (TryResolveJsonPath(element, path, out var resolved))
            return resolved;

        throw new InvalidOperationException($"json_extract path '{path}' was not found.");
    }

    private static bool TryResolveJsonPath(JsonElement element, string path, out JsonElement resolved)
    {
        resolved = element;
        var segments = SplitPath(path);
        if (segments.Count == 0)
            return true;

        foreach (var segment in segments)
        {
            if (resolved.ValueKind == JsonValueKind.Object &&
                resolved.TryGetProperty(segment, out var child))
            {
                resolved = child;
                continue;
            }

            if (resolved.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                TryGetArrayElement(resolved, index, out child))
            {
                resolved = child;
                continue;
            }

            resolved = default;
            return false;
        }

        return true;
    }

    private static bool TryGetArrayElement(JsonElement array, int index, out JsonElement element)
    {
        if (index < 0)
        {
            element = default;
            return false;
        }

        var current = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (current == index)
            {
                element = item;
                return true;
            }

            current++;
        }

        element = default;
        return false;
    }

    private static List<string> SplitPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? []
            : path
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
}
