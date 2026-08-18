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
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

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
        TransformOperationSpec? transformOperation = null;
        var separator = WorkflowParameterValueParser.NormalizeEscapedText(
            WorkflowParameterValueParser.GetString(request.Parameters, "\n", "separator", "delimiter"),
            "\n");
        var n = WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 5, 1, 10_000, "n", "count");

        string output;
        try
        {
            transformOperation = ResolveTransformOperation(request);
            if (transformOperation is not null)
            {
                output = ExecuteTransformOperation(input, transformOperation, ct);
            }
            else
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
                    "json_parse" => JsonParse(input, request.Parameters),
                    "rss_extract_items" => ExtractRssItems(input, request.Parameters),
                    "distinct" => string.Join("\n", input.Split('\n').Distinct()),
                    "uppercase" => input.ToUpperInvariant(),
                    "lowercase" => input.ToLowerInvariant(),
                    "trim" => input.Trim(),
                    "reverse_lines" => string.Join("\n", input.Split('\n').Reverse()),
                    _ => input, // Unknown operations keep the legacy identity fallback.
                };
            }
        }
        catch (Exception ex)
        {
            if (transformOperation is not null ||
                IsRecognizedTransformOperationName(op) ||
                string.Equals(op, "json_parse", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(op, "rss_extract_items", StringComparison.OrdinalIgnoreCase))
            {
                await PublishFailureAsync(request, ctx, ex.Message, ct);
                return;
            }

            output = $"Transform 错误: {ex.Message}";
        }

        ctx.Logger.LogInformation("Transform {StepId}: op={Op}, input_len={InLen}, output_len={OutLen}",
            request.StepId, op, input.Length, output.Length);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            ExecutionId = request.ExecutionId,
            Success = true, Output = output,
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
        }, TopologyAudience.Self, ct);
    }

    private static Task PublishFailureAsync(
        StepRequestEvent request,
        IWorkflowExecutionContext ctx,
        string error,
        CancellationToken ct) =>
        ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            ExecutionId = request.ExecutionId,
            Success = false,
            Output = string.Empty,
            Error = string.IsNullOrWhiteSpace(error) ? "Transform operation failed." : error,
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
        }, TopologyAudience.Self, ct);

    private static TransformOperationSpec? ResolveTransformOperation(StepRequestEvent request)
    {
        if (request.StepParameters?.TransformOperation is { Kind: not TransformOperationKind.Unspecified } typed)
            return typed;

        var op = request.Parameters.GetValueOrDefault("op", "identity").Trim();
        var kind = ParseTransformOperationKind(op);
        if (kind == TransformOperationKind.Unspecified)
            return null;

        var spec = new TransformOperationSpec
        {
            Kind = kind,
            Key = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "key", "group_key", "group_by").Trim(),
            Value = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "value", "value_field", "field").Trim(),
            Aggregate = ParseTransformAggregateKind(
                WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "aggregate", "agg")),
            Template = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "template"),
        };

        var precision = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "precision", "scale").Trim();
        if (!string.IsNullOrWhiteSpace(precision))
        {
            if (!int.TryParse(precision, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPrecision))
                throw new InvalidOperationException("transform precision must be an integer.");

            spec.Precision = parsedPrecision;
        }

        return spec;
    }

    private static TransformOperationKind ParseTransformOperationKind(string? value) =>
        NormalizeOperationToken(value) switch
        {
            "sum" => TransformOperationKind.Sum,
            "subtract" => TransformOperationKind.Subtract,
            "multiply" => TransformOperationKind.Multiply,
            "divide" => TransformOperationKind.Divide,
            "round" => TransformOperationKind.Round,
            "min" => TransformOperationKind.Min,
            "max" => TransformOperationKind.Max,
            "groupby" => TransformOperationKind.GroupBy,
            "template" => TransformOperationKind.Template,
            _ => TransformOperationKind.Unspecified,
        };

    private static bool IsRecognizedTransformOperationName(string? value) =>
        ParseTransformOperationKind(value) != TransformOperationKind.Unspecified;

    private static TransformAggregateKind ParseTransformAggregateKind(string? value) =>
        NormalizeOperationToken(value) switch
        {
            "sum" => TransformAggregateKind.Sum,
            "count" => TransformAggregateKind.Count,
            "avg" => TransformAggregateKind.Avg,
            _ => TransformAggregateKind.Unspecified,
        };

    private static string NormalizeOperationToken(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static string ExecuteTransformOperation(
        string input,
        TransformOperationSpec spec,
        CancellationToken cancellationToken)
    {
        if (spec.HasPrecision && spec.Precision < 0)
            throw new InvalidOperationException("transform precision must be zero or greater.");

        return spec.Kind switch
        {
            TransformOperationKind.Sum => FormatDecimal(ApplyPrecision(ReadNumericValues(input, spec).Sum(), spec)),
            TransformOperationKind.Subtract => FormatDecimal(ApplyPrecision(Subtract(ReadNumericValues(input, spec)), spec)),
            TransformOperationKind.Multiply => FormatDecimal(ApplyPrecision(Multiply(ReadNumericValues(input, spec)), spec)),
            TransformOperationKind.Divide => FormatDecimal(ApplyPrecision(Divide(ReadNumericValues(input, spec)), spec)),
            TransformOperationKind.Round => FormatDecimal(ApplyPrecision(Round(ReadNumericValues(input, spec), spec), spec)),
            TransformOperationKind.Min => FormatDecimal(ReadNumericValues(input, spec).Min()),
            TransformOperationKind.Max => FormatDecimal(ReadNumericValues(input, spec).Max()),
            TransformOperationKind.GroupBy => GroupBy(input, spec),
            TransformOperationKind.Template => BoundedTemplateRenderer.Render(input, spec.Template, cancellationToken),
            _ => input,
        };
    }

    private static IReadOnlyList<decimal> ReadNumericValues(string input, TransformOperationSpec spec)
    {
        var values = TryReadJsonNumericValues(input, spec.Value)
            ?? ReadDelimitedNumericValues(input);

        if (values.Count == 0)
            throw new InvalidOperationException("transform numeric input did not contain any numbers.");

        return values;
    }

    private static List<decimal>? TryReadJsonNumericValues(string input, string valueField)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        try
        {
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return TryReadDecimal(root, out var single) ? [single] : null;

            var values = new List<decimal>();
            foreach (var item in root.EnumerateArray())
            {
                var value = item;
                if (!string.IsNullOrWhiteSpace(valueField))
                {
                    if (!TryResolveJsonPath(item, valueField, out value))
                        throw new InvalidOperationException($"transform value field '{valueField}' was not found.");
                }

                if (!TryReadDecimal(value, out var parsed))
                    throw new InvalidOperationException("transform numeric input contains a non-numeric value.");

                values.Add(parsed);
            }

            return values;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<decimal> ReadDelimitedNumericValues(string input)
    {
        var values = new List<decimal>();
        foreach (var token in Regex.Split(input, @"[\s,;|]+"))
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;

            if (!decimal.TryParse(token.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException($"transform numeric input contains non-numeric value '{token}'.");

            values.Add(value);
        }

        return values;
    }

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
            return true;

        if (element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static decimal Subtract(IReadOnlyList<decimal> values)
    {
        var result = values[0];
        for (var i = 1; i < values.Count; i++)
            result -= values[i];
        return result;
    }

    private static decimal Multiply(IReadOnlyList<decimal> values)
    {
        var result = 1m;
        foreach (var value in values)
            result *= value;
        return result;
    }

    private static decimal Divide(IReadOnlyList<decimal> values)
    {
        var result = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] == 0m)
                throw new InvalidOperationException("transform divide cannot divide by zero.");

            result /= values[i];
        }

        return result;
    }

    private static decimal Round(IReadOnlyList<decimal> values, TransformOperationSpec spec)
    {
        if (!spec.HasPrecision)
            throw new InvalidOperationException("transform round requires precision.");

        return decimal.Round(values[0], spec.Precision, MidpointRounding.AwayFromZero);
    }

    private static decimal ApplyPrecision(decimal value, TransformOperationSpec spec) =>
        spec.HasPrecision
            ? decimal.Round(value, spec.Precision, MidpointRounding.AwayFromZero)
            : value;

    private static string FormatDecimal(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string GroupBy(string input, TransformOperationSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Key))
            throw new InvalidOperationException("transform group_by requires key.");

        if (spec.Aggregate == TransformAggregateKind.Unspecified)
            throw new InvalidOperationException("transform group_by requires aggregate.");

        if (spec.Aggregate is TransformAggregateKind.Sum or TransformAggregateKind.Avg &&
            string.IsNullOrWhiteSpace(spec.Value))
        {
            throw new InvalidOperationException("transform group_by sum and avg require value.");
        }

        using var document = JsonDocument.Parse(input);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("transform group_by input must be a JSON array.");

        var groups = new SortedDictionary<string, List<decimal>>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("transform group_by input items must be JSON objects.");

            if (!TryResolveJsonPath(item, spec.Key, out var keyElement))
                throw new InvalidOperationException($"transform group_by key '{spec.Key}' was not found.");

            var key = GetJsonScalarText(keyElement);
            if (!groups.TryGetValue(key, out var values))
            {
                values = [];
                groups[key] = values;
            }

            if (spec.Aggregate == TransformAggregateKind.Count)
            {
                values.Add(1m);
                continue;
            }

            if (!TryResolveJsonPath(item, spec.Value, out var valueElement))
                throw new InvalidOperationException($"transform group_by value '{spec.Value}' was not found.");

            if (!TryReadDecimal(valueElement, out var value))
                throw new InvalidOperationException("transform group_by value contains a non-numeric value.");

            values.Add(value);
        }

        var output = new JsonArray();
        foreach (var (key, values) in groups)
        {
            var aggregateValue = spec.Aggregate switch
            {
                TransformAggregateKind.Sum => values.Sum(),
                TransformAggregateKind.Count => values.Count,
                TransformAggregateKind.Avg => values.Count == 0 ? 0m : values.Sum() / values.Count,
                _ => throw new InvalidOperationException("transform group_by aggregate is unsupported."),
            };

            var item = new JsonObject
            {
                ["key"] = key,
                ["value"] = decimal.Parse(FormatDecimal(ApplyPrecision(aggregateValue, spec)), CultureInfo.InvariantCulture),
            };
            output.Add(item);
        }

        return JsonSerializer.Serialize(output, JsonOutputOptions);
    }

    private static string GetJsonScalarText(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();

    private static string ExtractRssItems(string input, IReadOnlyDictionary<string, string> parameters)
    {
        var sourceId = WorkflowParameterValueParser.GetString(parameters, string.Empty, "source_id").Trim();
        var sourceUrl = WorkflowParameterValueParser.GetString(parameters, string.Empty, "source_url").Trim();
        using var stringReader = new StringReader(input);
        using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });
        var document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("rss_extract_items input is empty XML.");

        var items = IsAtomFeed(root)
            ? ExtractAtomItems(root, sourceId, sourceUrl)
            : ExtractRss2Items(root, sourceId, sourceUrl);

        return SerializeJsonNodes(items);
    }

    private static bool IsAtomFeed(XElement root) =>
        string.Equals(root.Name.LocalName, "feed", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<JsonObject> ExtractRss2Items(XElement root, string sourceId, string sourceUrl)
    {
        var channel = root.Elements().FirstOrDefault(x => string.Equals(x.Name.LocalName, "channel", StringComparison.OrdinalIgnoreCase));
        var itemNodes = (channel ?? root).Elements().Where(x => string.Equals(x.Name.LocalName, "item", StringComparison.OrdinalIgnoreCase));
        foreach (var item in itemNodes)
        {
            var link = ChildText(item, "link");
            yield return BuildFeedItem(
                sourceId,
                sourceUrl,
                FirstNonEmpty(ChildText(item, "guid"), link),
                ChildText(item, "title"),
                link,
                FormatPublishedAt(FirstNonEmpty(ChildText(item, "pubDate"), ChildText(item, "published"), ChildText(item, "updated"))),
                FirstNonEmpty(ChildText(item, "description"), ChildText(item, "summary")));
        }
    }

    private static IEnumerable<JsonObject> ExtractAtomItems(XElement root, string sourceId, string sourceUrl)
    {
        foreach (var item in root.Elements().Where(x => string.Equals(x.Name.LocalName, "entry", StringComparison.OrdinalIgnoreCase)))
        {
            var link = ResolveAtomLink(item);
            yield return BuildFeedItem(
                sourceId,
                sourceUrl,
                FirstNonEmpty(ChildText(item, "id"), link),
                ChildText(item, "title"),
                link,
                FormatPublishedAt(FirstNonEmpty(ChildText(item, "published"), ChildText(item, "updated"))),
                FirstNonEmpty(ChildText(item, "summary"), ChildText(item, "content")));
        }
    }

    private static JsonObject BuildFeedItem(
        string sourceId,
        string sourceUrl,
        string id,
        string title,
        string link,
        string publishedAt,
        string summary) =>
        new()
        {
            ["source_id"] = sourceId,
            ["source_url"] = sourceUrl,
            ["id"] = id,
            ["title"] = title,
            ["link"] = link,
            ["published_at"] = publishedAt,
            ["summary"] = summary,
        };

    private static string ResolveAtomLink(XElement item)
    {
        var alternate = item.Elements()
            .Where(x => string.Equals(x.Name.LocalName, "link", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(x =>
                string.IsNullOrWhiteSpace((string?)x.Attribute("rel")) ||
                string.Equals((string?)x.Attribute("rel"), "alternate", StringComparison.OrdinalIgnoreCase));
        return ((string?)alternate?.Attribute("href") ?? alternate?.Value ?? string.Empty).Trim();
    }

    private static string ChildText(XElement element, string childName) =>
        element.Elements()
            .FirstOrDefault(x => string.Equals(x.Name.LocalName, childName, StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim() ?? string.Empty;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string FormatPublishedAt(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : string.Empty;

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

    private static string JsonParse(string input, IReadOnlyDictionary<string, string> parameters)
    {
        var path = WorkflowParameterValueParser.GetString(parameters, string.Empty, "path", "json_path").Trim();
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("json_parse requires a path parameter.");

        using var document = ParseJsonParseInput(input);
        if (!TryResolveJsonPath(document.RootElement, path, out var target))
            throw new InvalidOperationException($"json_parse path '{path}' was not found.");

        if (target.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("json_parse selected node must be a JSON string.");

        try
        {
            return SerializeJsonNode(JsonNode.Parse(target.GetString() ?? string.Empty));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("json_parse selected string must contain valid JSON.", ex);
        }
    }

    private static JsonDocument ParseJsonParseInput(string input)
    {
        try
        {
            return JsonDocument.Parse(input);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("json_parse input must be valid JSON.", ex);
        }
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
