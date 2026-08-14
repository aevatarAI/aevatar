using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aevatar.GAgents.Platform.Lark;

/// <summary>
/// Converts complete JSON values found in Lark-bound text into bounded table presentations.
/// Invalid or incomplete JSON is left untouched so partial streaming chunks remain lossless.
/// </summary>
public static class LarkJsonTableFormatter
{
    private const int MaxNativeTables = 5;
    private const int MaxColumns = 50;
    private const int MaxRows = 100;
    private const int MaxCellLength = 1_000;
    private const int MaxHeaderLength = 120;
    private const int MaxTableTextLength = 24_000;
    private const int MaxDepth = 8;
    private const int KeyValueIndentSize = 4;
    private const string TruncationMarker = "...[truncated]";

    public static bool ContainsConvertibleJson(string? text) => Parse(text).HasTables;

    public static string FormatAsKeyValueText(string? text)
    {
        var presentation = Parse(text);
        return presentation.HasTables ? presentation.RenderKeyValueText() : text ?? string.Empty;
    }

    internal static LarkJsonTablePresentation Parse(string? text)
    {
        var source = text ?? string.Empty;
        if (source.Length == 0)
            return LarkJsonTablePresentation.TextOnly(source);

        if (TryParseJson(source, requireContainer: true, out var completeJson))
        {
            return new LarkJsonTablePresentation(
            [
                BuildTablePart(completeJson, nativeEligible: true),
            ]);
        }

        var parts = new List<LarkJsonPresentationPart>();
        var textStart = 0;
        var cursor = 0;
        var tableCount = 0;

        while (cursor < source.Length)
        {
            if (IsFenceStart(source, cursor) &&
                TryReadFence(source, cursor, out var fenceEnd, out var language, out var fencedContent))
            {
                if (IsJsonFenceLanguage(language) &&
                    TryParseJson(fencedContent, requireContainer: false, out var fencedJson))
                {
                    AppendText(parts, source[textStart..cursor]);
                    parts.Add(BuildTablePart(fencedJson, tableCount < MaxNativeTables));
                    tableCount++;
                    textStart = fenceEnd;
                }

                cursor = fenceEnd;
                continue;
            }

            if (source[cursor] == '`' &&
                TryReadInlineCode(source, cursor, out var inlineEnd, out var inlineContent))
            {
                if (TryParseJson(inlineContent, requireContainer: true, out var inlineJson))
                {
                    AppendText(parts, source[textStart..cursor]);
                    parts.Add(BuildTablePart(inlineJson, tableCount < MaxNativeTables));
                    tableCount++;
                    textStart = inlineEnd;
                }

                cursor = inlineEnd;
                continue;
            }

            if (source[cursor] is '{' or '[' &&
                TryReadContainer(source, cursor, out var containerEnd) &&
                TryParseJson(source[cursor..containerEnd], requireContainer: true, out var containerJson))
            {
                AppendText(parts, source[textStart..cursor]);
                parts.Add(BuildTablePart(containerJson, tableCount < MaxNativeTables));
                tableCount++;
                textStart = containerEnd;
                cursor = containerEnd;
                continue;
            }

            cursor++;
        }

        if (tableCount == 0)
            return LarkJsonTablePresentation.TextOnly(source);

        AppendText(parts, source[textStart..]);
        return new LarkJsonTablePresentation(parts);
    }

    private static LarkJsonTablePart BuildTablePart(JsonElement root, bool nativeEligible) =>
        new(
            BuildTable(root),
            RenderKeyValueText(root),
            nativeEligible);

    private static string RenderKeyValueText(JsonElement root)
    {
        var builder = new StringBuilder();
        AppendKeyValueElement(builder, key: null, root, depth: 0);
        return Truncate(builder.ToString().TrimEnd(), MaxTableTextLength);
    }

    private static void AppendKeyValueElement(
        StringBuilder builder,
        string? key,
        JsonElement element,
        int depth)
    {
        if (builder.Length >= MaxTableTextLength)
            return;

        if (element.ValueKind == JsonValueKind.String &&
            TryParseJson(element.GetString() ?? string.Empty, requireContainer: true, out var embeddedJson))
        {
            element = embeddedJson;
        }

        if (depth >= MaxDepth)
        {
            AppendKeyValueLine(builder, depth, key ?? "Value", "(nested value)");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                AppendKeyValueObject(builder, key, element, depth);
                break;
            case JsonValueKind.Array:
                AppendKeyValueArray(builder, key, element, depth);
                break;
            case JsonValueKind.String when ContainsLineBreak(element.GetString()):
                // Multiline text (card bodies, approval descriptions, …) loses
                // its meaning when line breaks are collapsed into "; ". Render
                // it as an indented block under the key instead.
                AppendKeyValueMultilineBlock(builder, depth, key ?? "Value", element.GetString() ?? string.Empty);
                break;
            default:
                AppendKeyValueLine(builder, depth, key ?? "Value", FormatKeyValueScalar(element));
                break;
        }
    }

    private static bool ContainsLineBreak(string? value) =>
        value is not null && (value.Contains('\n') || value.Contains('\r'));

    private static void AppendKeyValueMultilineBlock(
        StringBuilder builder,
        int depth,
        string key,
        string value)
    {
        AppendKeyValueHeader(builder, depth, key);
        var lines = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        foreach (var line in lines)
        {
            if (builder.Length >= MaxTableTextLength)
                return;

            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                builder.AppendLine();
                continue;
            }

            AppendKeyValueIndent(builder, depth + 1);
            builder.AppendLine(trimmed);
        }
    }

    private static void AppendKeyValueObject(
        StringBuilder builder,
        string? key,
        JsonElement element,
        int depth)
    {
        var properties = element.EnumerateObject().Take(MaxRows + 1).ToArray();
        if (properties.Length == 0)
        {
            AppendKeyValueLine(builder, depth, key ?? "Value", "(empty object)");
            return;
        }

        var childDepth = depth;
        if (!string.IsNullOrWhiteSpace(key))
        {
            AppendKeyValueHeader(builder, depth, key);
            childDepth++;
        }

        foreach (var property in properties.Take(MaxRows))
        {
            AppendKeyValueElement(builder, property.Name, property.Value, childDepth);
            if (builder.Length >= MaxTableTextLength)
                return;
        }

        if (properties.Length > MaxRows)
        {
            AppendKeyValueLine(
                builder,
                childDepth,
                "...",
                $"More than {MaxRows} fields; remaining fields were not shown.");
        }
    }

    private static void AppendKeyValueArray(
        StringBuilder builder,
        string? key,
        JsonElement element,
        int depth)
    {
        var items = element.EnumerateArray().Take(MaxRows + 1).ToArray();
        if (items.Length == 0)
        {
            AppendKeyValueLine(builder, depth, key ?? "Value", "(empty array)");
            return;
        }

        var itemDepth = depth;
        if (!string.IsNullOrWhiteSpace(key))
        {
            AppendKeyValueHeader(builder, depth, key);
            itemDepth++;
        }

        for (var index = 0; index < Math.Min(items.Length, MaxRows); index++)
        {
            if (index > 0)
                AppendKeyValueBlankLine(builder);

            AppendKeyValueLine(
                builder,
                itemDepth,
                "Item",
                (index + 1).ToString(CultureInfo.InvariantCulture));
            AppendKeyValueElement(builder, key: null, items[index], itemDepth + 1);
            if (builder.Length >= MaxTableTextLength)
                return;
        }

        if (items.Length > MaxRows)
        {
            AppendKeyValueLine(
                builder,
                itemDepth,
                "...",
                $"More than {MaxRows} items; remaining items were not shown.");
        }
    }

    private static string FormatKeyValueScalar(JsonElement element) =>
        NormalizeKeyValueLineText(
            element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "(null)",
                JsonValueKind.Undefined => "(undefined)",
                _ => string.Empty,
            },
            string.Empty);

    private static void AppendKeyValueHeader(StringBuilder builder, int depth, string key)
    {
        AppendKeyValueIndent(builder, depth);
        builder
            .Append(NormalizeKeyValueLineText(key, "Value"))
            .AppendLine(":");
    }

    private static void AppendKeyValueLine(
        StringBuilder builder,
        int depth,
        string key,
        string value)
    {
        AppendKeyValueIndent(builder, depth);
        builder
            .Append(NormalizeKeyValueLineText(key, "Value"))
            .Append(": ")
            .AppendLine(NormalizeKeyValueLineText(value, string.Empty));
    }

    private static void AppendKeyValueIndent(StringBuilder builder, int depth) =>
        builder.Append(' ', Math.Max(0, depth) * KeyValueIndentSize);

    private static void AppendKeyValueBlankLine(StringBuilder builder)
    {
        if (builder.Length > 1 && builder[^1] == '\n' && builder[^2] != '\n')
            builder.AppendLine();
    }

    private static string NormalizeKeyValueLineText(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\r\n", "; ", StringComparison.Ordinal)
            .Replace("\r", "; ", StringComparison.Ordinal)
            .Replace("\n", "; ", StringComparison.Ordinal)
            .Trim();
        return normalized.Length == 0 ? fallback : normalized;
    }

    private static LarkJsonTable BuildTable(JsonElement root)
    {
        var title = string.Empty;
        while (root.ValueKind == JsonValueKind.Object)
        {
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 1 ||
                properties[0].Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                break;
            }

            title = string.IsNullOrWhiteSpace(title)
                ? properties[0].Name
                : $"{title}.{properties[0].Name}";
            root = properties[0].Value;
        }

        return root.ValueKind switch
        {
            JsonValueKind.Array => BuildArrayTable(root, title),
            JsonValueKind.Object => BuildObjectTable(root, title),
            _ => BuildScalarTable(root, title),
        };
    }

    private static LarkJsonTable BuildArrayTable(JsonElement array, string title)
    {
        var items = array.EnumerateArray().Take(MaxRows + 1).ToArray();
        if (items.Length == 0)
        {
            return CreateTable(
                title,
                ["Value"],
                [["(empty array)"]]);
        }

        var visibleItems = items.Take(MaxRows).ToArray();
        if (visibleItems.All(static item => item.ValueKind == JsonValueKind.Object))
        {
            var flattenedRows = visibleItems
                .Select(static item => FlattenObject(item))
                .ToArray();
            var headers = flattenedRows
                .SelectMany(static row => row.Keys)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxColumns + 1)
                .ToArray();

            if (headers.Length <= MaxColumns)
            {
                var rows = flattenedRows
                    .Select(row => headers
                        .Select(header => row.TryGetValue(header, out var value) ? value : string.Empty)
                        .ToArray())
                    .ToList();
                AppendRemainingRow(rows, headers.Length, items.Length > MaxRows);
                return CreateTable(title, headers, rows);
            }

            return BuildPathValueTable(visibleItems, title, items.Length > MaxRows);
        }

        var values = visibleItems
            .Select((item, index) => new[]
            {
                (index + 1).ToString(CultureInfo.InvariantCulture),
                FormatValue(item, depth: 0),
            })
            .ToList();
        if (items.Length > MaxRows)
            values.Add(["...", $"More than {MaxRows} items; remaining rows were not shown."]);

        return CreateTable(title, ["Item", "Value"], values);
    }

    private static LarkJsonTable BuildObjectTable(JsonElement root, string title)
    {
        var flattened = FlattenObject(root);
        if (flattened.Count == 0)
        {
            return CreateTable(
                title,
                ["Field", "Value"],
                [["(empty object)", string.Empty]]);
        }

        if (flattened.Count <= MaxColumns)
        {
            return CreateTable(
                title,
                flattened.Keys.ToArray(),
                [flattened.Values.ToArray()]);
        }

        var rows = flattened
            .Take(MaxRows)
            .Select(static pair => new[] { pair.Key, pair.Value })
            .ToList();
        if (flattened.Count > MaxRows)
            rows.Add(["...", $"{flattened.Count - MaxRows} more fields were not shown."]);

        return CreateTable(title, ["Field", "Value"], rows);
    }

    private static LarkJsonTable BuildScalarTable(JsonElement root, string title) =>
        CreateTable(title, ["Value"], [[FormatValue(root, depth: 0)]]);

    private static LarkJsonTable BuildPathValueTable(
        IReadOnlyList<JsonElement> items,
        string title,
        bool hasRemainingItems)
    {
        var rows = new List<string[]>();
        for (var itemIndex = 0; itemIndex < items.Count && rows.Count < MaxRows; itemIndex++)
        {
            foreach (var field in FlattenObject(items[itemIndex]))
            {
                if (rows.Count == MaxRows)
                    break;

                rows.Add(
                [
                    (itemIndex + 1).ToString(CultureInfo.InvariantCulture),
                    field.Key,
                    field.Value,
                ]);
            }
        }

        if (hasRemainingItems || rows.Count == MaxRows)
            rows.Add(["...", "...", "Additional values were not shown."]);

        return CreateTable(title, ["Item", "Field", "Value"], rows);
    }

    private static Dictionary<string, string> FlattenObject(JsonElement root)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        FlattenObject(root, prefix: string.Empty, depth: 0, fields);
        return fields;
    }

    private static void FlattenObject(
        JsonElement element,
        string prefix,
        int depth,
        IDictionary<string, string> fields)
    {
        if (depth >= MaxDepth || element.ValueKind != JsonValueKind.Object)
        {
            fields[ResolveFieldName(prefix)] = FormatValue(element, depth);
            return;
        }

        var properties = element.EnumerateObject().ToArray();
        if (properties.Length == 0)
        {
            fields[ResolveFieldName(prefix)] = "(empty object)";
            return;
        }

        foreach (var property in properties)
        {
            var path = string.IsNullOrWhiteSpace(prefix)
                ? property.Name
                : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
                FlattenObject(property.Value, path, depth + 1, fields);
            else if (property.Value.ValueKind == JsonValueKind.String &&
                     TryParseJson(property.Value.GetString() ?? string.Empty, requireContainer: true, out var embeddedJson) &&
                     embeddedJson.ValueKind == JsonValueKind.Object)
            {
                FlattenObject(embeddedJson, path, depth + 1, fields);
            }
            else
                fields[path] = FormatValue(property.Value, depth + 1);
        }
    }

    private static string FormatValue(JsonElement value, int depth)
    {
        if (depth >= MaxDepth)
            return "(nested value)";

        var formatted = value.ValueKind switch
        {
            JsonValueKind.String => FormatStringValue(value, depth),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "(null)",
            JsonValueKind.Undefined => "(undefined)",
            JsonValueKind.Array => FormatArrayValue(value, depth + 1),
            JsonValueKind.Object => FormatObjectValue(value, depth + 1),
            _ => string.Empty,
        };
        return TruncateCell(formatted);
    }

    private static string FormatStringValue(JsonElement value, int depth)
    {
        var text = value.GetString() ?? string.Empty;
        return TryParseJson(text, requireContainer: true, out var embeddedJson)
            ? FormatValue(embeddedJson, depth + 1)
            : text;
    }

    private static string FormatArrayValue(JsonElement array, int depth)
    {
        var items = array.EnumerateArray().Take(MaxRows + 1).ToArray();
        if (items.Length == 0)
            return "(empty array)";

        var lines = items
            .Take(MaxRows)
            .Select((item, index) => $"{index + 1}. {FormatValue(item, depth)}")
            .ToList();
        if (items.Length > MaxRows)
            lines.Add($"... more than {MaxRows} items");
        return string.Join("\n", lines);
    }

    private static string FormatObjectValue(JsonElement value, int depth)
    {
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length == 0)
            return "(empty object)";

        return string.Join(
            "\n",
            properties.Select(property => $"{property.Name}: {FormatValue(property.Value, depth)}"));
    }

    private static LarkJsonTable CreateTable(
        string title,
        IReadOnlyList<string> headers,
        IReadOnlyList<string[]> rows)
    {
        var normalizedTitle = Truncate(title, MaxHeaderLength);
        var normalizedHeaders = (headers.Count == 0 ? ["Value"] : headers.Take(MaxColumns).ToArray())
            .Select(static header => Truncate(header, MaxHeaderLength))
            .ToArray();
        var columns = normalizedHeaders
            .Select((header, index) => new LarkJsonTableColumn($"c{index}", header))
            .ToArray();
        var remainingTextLength = Math.Max(
            0,
            MaxTableTextLength -
            new StringInfo(normalizedTitle).LengthInTextElements -
            normalizedHeaders.Sum(static header => new StringInfo(header).LengthInTextElements));
        var normalizedRows = new List<string[]>();
        foreach (var row in rows.Take(MaxRows))
        {
            if (remainingTextLength <= 0)
                break;

            var normalizedRow = new string[columns.Length];
            Array.Fill(normalizedRow, string.Empty);
            for (var index = 0; index < columns.Length; index++)
            {
                if (remainingTextLength <= 0)
                    break;

                var raw = index < row.Length ? row[index] : string.Empty;
                var cellLimit = Math.Min(MaxCellLength, remainingTextLength);
                normalizedRow[index] = Truncate(raw, cellLimit);
                remainingTextLength -= new StringInfo(normalizedRow[index]).LengthInTextElements;
            }
            normalizedRows.Add(normalizedRow);
        }

        if (normalizedRows.Count < rows.Count && columns.Length > 0)
        {
            if (normalizedRows.Count == MaxRows)
                normalizedRows.RemoveAt(normalizedRows.Count - 1);

            var marker = new string[columns.Length];
            marker[0] = "Additional values were not shown.";
            normalizedRows.Add(marker);
        }

        return new LarkJsonTable(normalizedTitle, columns, normalizedRows);
    }

    private static void AppendRemainingRow(List<string[]> rows, int columnCount, bool hasRemainingItems)
    {
        if (!hasRemainingItems || columnCount == 0)
            return;

        var marker = new string[columnCount];
        marker[0] = $"More than {MaxRows} items; remaining rows were not shown.";
        rows.Add(marker);
    }

    private static bool TryParseJson(string candidate, bool requireContainer, out JsonElement root)
    {
        root = default;
        var trimmed = candidate.Trim();
        if (trimmed.Length == 0)
            return false;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            root = document.RootElement.Clone();
            for (var depth = 0; depth < MaxDepth && root.ValueKind == JsonValueKind.String; depth++)
            {
                var encodedValue = root.GetString();
                if (string.IsNullOrWhiteSpace(encodedValue) ||
                    !TryParseJsonContainer(encodedValue, out var decodedRoot))
                {
                    break;
                }

                root = decodedRoot;
            }

            if (requireContainer && root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseJsonContainer(string candidate, out JsonElement root)
    {
        root = default;
        try
        {
            using var document = JsonDocument.Parse(candidate.Trim());
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                return false;

            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadContainer(string source, int start, out int endExclusive)
    {
        endExclusive = start;
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;

        for (var index = start; index < source.Length; index++)
        {
            var current = source[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == '"')
                    inString = false;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current is '{' or '[')
            {
                stack.Push(current);
                continue;
            }

            if (current is not ('}' or ']'))
                continue;
            if (stack.Count == 0 || !IsMatchingPair(stack.Pop(), current))
                return false;
            if (stack.Count != 0)
                continue;

            endExclusive = index + 1;
            return true;
        }

        return false;
    }

    private static bool TryReadFence(
        string source,
        int start,
        out int endExclusive,
        out string language,
        out string content)
    {
        endExclusive = start;
        language = string.Empty;
        content = string.Empty;

        var openingLineEnd = source.IndexOf('\n', start + 3);
        if (openingLineEnd < 0)
            return false;

        language = source[(start + 3)..openingLineEnd].Trim();
        var closingStart = openingLineEnd + 1;
        while (closingStart < source.Length)
        {
            closingStart = source.IndexOf("```", closingStart, StringComparison.Ordinal);
            if (closingStart < 0)
                return false;

            var startsLine = closingStart == 0 || source[closingStart - 1] == '\n';
            var closingLineEnd = source.IndexOf('\n', closingStart + 3);
            var suffixEnd = closingLineEnd < 0 ? source.Length : closingLineEnd;
            var hasOnlyWhitespaceSuffix = string.IsNullOrWhiteSpace(source[(closingStart + 3)..suffixEnd]);
            if (startsLine && hasOnlyWhitespaceSuffix)
            {
                var contentEnd = closingStart > openingLineEnd + 1 && source[closingStart - 1] == '\n'
                    ? closingStart - 1
                    : closingStart;
                content = source[(openingLineEnd + 1)..contentEnd];
                endExclusive = closingStart + 3;
                return true;
            }

            closingStart += 3;
        }

        return false;
    }

    private static bool TryReadInlineCode(
        string source,
        int start,
        out int endExclusive,
        out string content)
    {
        endExclusive = start;
        content = string.Empty;
        if (IsFenceStart(source, start))
            return false;

        var closing = source.IndexOf('`', start + 1);
        if (closing < 0)
            return false;

        content = source[(start + 1)..closing];
        endExclusive = closing + 1;
        return true;
    }

    private static bool IsFenceStart(string source, int index) =>
        index + 2 < source.Length &&
        source[index] == '`' &&
        source[index + 1] == '`' &&
        source[index + 2] == '`';

    private static bool IsJsonFenceLanguage(string language) =>
        string.IsNullOrWhiteSpace(language) ||
        string.Equals(language, "json", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(language, "application/json", StringComparison.OrdinalIgnoreCase);

    private static bool IsMatchingPair(char opening, char closing) =>
        (opening == '{' && closing == '}') || (opening == '[' && closing == ']');

    private static void AppendText(ICollection<LarkJsonPresentationPart> parts, string text)
    {
        if (text.Length > 0)
            parts.Add(new LarkJsonTextPart(text));
    }

    private static string ResolveFieldName(string prefix) =>
        string.IsNullOrWhiteSpace(prefix) ? "Value" : prefix;

    private static string TruncateCell(string? value)
        => Truncate(value, MaxCellLength);

    private static string Truncate(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        var textInfo = new StringInfo(text);
        if (maxLength <= 0 || textInfo.LengthInTextElements <= maxLength)
            return text;

        var markerLength = new StringInfo(TruncationMarker).LengthInTextElements;
        if (maxLength <= markerLength)
            return textInfo.SubstringByTextElements(0, maxLength);

        return textInfo.SubstringByTextElements(0, maxLength - markerLength) + TruncationMarker;
    }
}

internal sealed record LarkJsonTablePresentation(IReadOnlyList<LarkJsonPresentationPart> Parts)
{
    public bool HasTables => Parts.Any(static part => part is LarkJsonTablePart);

    public string RenderKeyValueText()
    {
        var builder = new StringBuilder();
        foreach (var part in Parts)
        {
            switch (part)
            {
                case LarkJsonTextPart text:
                    builder.Append(text.Text);
                    break;
                case LarkJsonTablePart table:
                    AppendSeparated(builder, table.KeyValueText);
                    break;
            }
        }

        return builder.ToString().Trim();
    }

    public string RenderProse()
    {
        var builder = new StringBuilder();
        foreach (var text in Parts.OfType<LarkJsonTextPart>())
            builder.Append(text.Text);
        return builder.ToString().Trim();
    }

    public static LarkJsonTablePresentation TextOnly(string text) =>
        new([new LarkJsonTextPart(text)]);

    private static void AppendSeparated(StringBuilder builder, string value)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
            builder.AppendLine().AppendLine();
        builder.Append(value);
        if (builder.Length > 0 && builder[^1] != '\n')
            builder.AppendLine().AppendLine();
    }
}

internal abstract record LarkJsonPresentationPart;

internal sealed record LarkJsonTextPart(string Text) : LarkJsonPresentationPart;

internal sealed record LarkJsonTablePart(
    LarkJsonTable Table,
    string KeyValueText,
    bool NativeEligible) : LarkJsonPresentationPart;

internal sealed record LarkJsonTable(
    string Title,
    IReadOnlyList<LarkJsonTableColumn> Columns,
    IReadOnlyList<string[]> Rows)
{
    public object BuildNativeElement(string elementId)
    {
        var rows = Rows.Select(row =>
        {
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < Columns.Count; index++)
                cells[Columns[index].Key] = index < row.Length ? row[index] : string.Empty;
            return cells;
        }).ToArray();

        return new
        {
            tag = "table",
            element_id = elementId,
            page_size = Math.Clamp(rows.Length, 1, 10),
            row_height = "auto",
            row_max_height = "120px",
            freeze_first_column = Columns.Count > 3,
            header_style = new
            {
                text_align = "left",
                text_size = "normal",
                background_style = "grey",
                text_color = "default",
                bold = true,
                lines = 2,
            },
            columns = Columns.Select(column => new
            {
                name = column.Key,
                display_name = column.DisplayName,
                width = "auto",
                data_type = "text",
                vertical_align = "top",
                horizontal_align = "left",
            }).ToArray(),
            rows,
        };
    }

}

internal sealed record LarkJsonTableColumn(string Key, string DisplayName);
