using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aevatar.Workflow.Core.Modules;

internal static class BoundedTemplateRenderer
{
    internal const int MaxTemplateBytes = 256 * 1024;
    internal const int MaxInputBytes = 4 * 1024 * 1024;
    internal const int MaxOutputBytes = 4 * 1024 * 1024;
    internal const int MaxLoopIterations = 10_000;
    internal const int MaxRecursionDepth = 64;
    private const int MaxJsonDepth = 64;

    public static string Render(string input, string templateText, CancellationToken cancellationToken)
    {
        EnsureNonEmptyTemplate(templateText);
        EnsureWithinLimit(templateText, MaxTemplateBytes, "template");
        EnsureWithinLimit(input, MaxInputBytes, "input");

        var template = Template.Parse(templateText, "workflow-transform-template");
        if (template.HasErrors)
            throw new InvalidOperationException("transform template is invalid.");

        object? data;
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaxJsonDepth,
            });
            data = ToScriptValue(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("transform template input must be valid bounded JSON.", ex);
        }

        var emptyBuiltins = new ScriptObject { IsReadOnly = true };
        var context = new TemplateContext(emptyBuiltins, StringComparer.Ordinal)
        {
            AutoIndent = false,
            CancellationToken = cancellationToken,
            EnableRelaxedFunctionAccess = false,
            EnableRelaxedIndexerAccess = false,
            EnableRelaxedMemberAccess = false,
            EnableRelaxedTargetAccess = false,
            LimitToString = MaxOutputBytes + 1,
            LoopLimit = MaxLoopIterations,
            MemberFilter = static _ => false,
            NewLine = "\n",
            ObjectRecursionLimit = MaxRecursionDepth,
            RecursiveLimit = MaxRecursionDepth,
            StrictVariables = true,
            TemplateLoader = null,
        };

        var globals = new ScriptObject(8, StringComparer.Ordinal);
        globals.SetValue("append", DelegateCustomFunction.CreateFunc<object?, object?, ScriptArray>(AppendValue), readOnly: true);
        globals.SetValue("data", data, readOnly: true);
        globals.SetValue("date", DelegateCustomFunction.CreateFunc<object?, string>(NormalizeDate), readOnly: true);
        globals.SetValue("get", DelegateCustomFunction.CreateFunc<object?, object?, object?, object?>(GetValue), readOnly: true);
        globals.SetValue("number", DelegateCustomFunction.CreateFunc<object?, decimal>(ParseNumber), readOnly: true);
        globals.SetValue("json", DelegateCustomFunction.CreateFunc<object?, string>(SerializeJson), readOnly: true);
        globals.SetValue("keys", DelegateCustomFunction.CreateFunc<object?, ScriptArray>(GetSortedKeys), readOnly: true);
        globals.SetValue("round", DelegateCustomFunction.CreateFunc<object?, int, decimal>(RoundNumber), readOnly: true);
        context.PushGlobal(globals);

        string output;
        try
        {
            output = template.Render(context);
        }
        catch (ScriptRuntimeException ex)
        {
            throw new InvalidOperationException("transform template evaluation failed.", ex);
        }

        if (Encoding.UTF8.GetByteCount(output) > MaxOutputBytes)
            throw new InvalidOperationException($"transform template output exceeds {MaxOutputBytes} bytes.");

        return output;
    }

    private static void EnsureNonEmptyTemplate(string templateText)
    {
        if (string.IsNullOrWhiteSpace(templateText))
            throw new InvalidOperationException("transform template requires a non-empty template.");
    }

    private static void EnsureWithinLimit(string value, int maxBytes, string label)
    {
        if (Encoding.UTF8.GetByteCount(value) > maxBytes)
            throw new InvalidOperationException($"transform template {label} exceeds {maxBytes} bytes.");
    }

    private static object? ToScriptValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => ToScriptObject(element),
            JsonValueKind.Array => ToScriptArray(element),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => ReadDecimal(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new InvalidOperationException("transform template input contains an unsupported JSON value."),
        };

    private static ScriptObject ToScriptObject(JsonElement element)
    {
        var result = new ScriptObject(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            result[property.Name] = ToScriptValue(property.Value);
        result.IsReadOnly = true;
        return result;
    }

    private static ScriptArray ToScriptArray(JsonElement element)
    {
        var result = new ScriptArray(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
            result.Add(ToScriptValue(item));
        result.IsReadOnly = true;
        return result;
    }

    private static decimal ReadDecimal(JsonElement element) =>
        element.TryGetDecimal(out var value)
            ? value
            : throw new InvalidOperationException("transform template input number is outside the supported decimal range.");

    private static decimal ParseNumber(object? value)
    {
        if (value is decimal decimalValue)
            return decimalValue;

        if (value is byte or sbyte or short or ushort or int or uint or long or ulong)
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);

        if (value is float floatValue && float.IsFinite(floatValue))
            return ConvertFloatingPointNumber(floatValue);

        if (value is double doubleValue && double.IsFinite(doubleValue))
            return ConvertFloatingPointNumber(doubleValue);

        if (value is string text &&
            decimal.TryParse(
                text,
                NumberStyles.Number | NumberStyles.AllowExponent | NumberStyles.AllowParentheses,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException("transform template number requires a decimal number or numeric string.");
    }

    private static decimal ConvertFloatingPointNumber(object value)
    {
        try
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException("transform template number is outside the supported decimal range.", ex);
        }
    }

    private static object? GetValue(object? source, object? key, object? defaultValue)
    {
        if (source is not ScriptObject scriptObject || key is not string member)
            throw new InvalidOperationException("transform template get requires an object and string key.");

        return scriptObject.ContainsKey(member) ? scriptObject[member] : defaultValue;
    }

    private static ScriptArray AppendValue(object? target, object? value)
    {
        if (target is not ScriptArray array || array.IsReadOnly)
            throw new InvalidOperationException("transform template append requires a mutable template array.");

        if (array.Count >= MaxLoopIterations)
            throw new InvalidOperationException($"transform template array exceeds {MaxLoopIterations} items.");

        array.Add(value);
        return array;
    }

    private static string NormalizeDate(object? value)
    {
        if (value is decimal unixMilliseconds &&
            unixMilliseconds == decimal.Truncate(unixMilliseconds) &&
            unixMilliseconds >= long.MinValue &&
            unixMilliseconds <= long.MaxValue)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds((long)unixMilliseconds)
                    .UtcDateTime
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new InvalidOperationException("transform template date is outside the supported range.", ex);
            }
        }

        if (value is not string text)
            throw new InvalidOperationException("transform template date requires a date string or Unix milliseconds.");

        text = text.Trim();
        var formats = new[] { "yyyy-MM-dd", "yyyy/M/d", "d/M/yyyy" };
        if (DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var segments = text.Split('/');
        if (segments.Length == 3 &&
            segments[2].Length == 2 &&
            int.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var day) &&
            int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
            int.TryParse(segments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var shortYear))
        {
            try
            {
                return new DateOnly(2000 + shortYear, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new InvalidOperationException("transform template date is invalid.", ex);
            }
        }

        throw new InvalidOperationException("transform template date has an unsupported format.");
    }

    private static ScriptArray GetSortedKeys(object? value)
    {
        if (value is not ScriptObject scriptObject)
            throw new InvalidOperationException("transform template keys requires an object.");

        var result = new ScriptArray(scriptObject.GetMembers().OrderBy(static key => key, StringComparer.Ordinal));
        result.IsReadOnly = true;
        return result;
    }

    private static decimal RoundNumber(object? value, int digits)
    {
        if (digits is < 0 or > 28)
            throw new InvalidOperationException("transform template round digits must be between zero and 28.");

        return decimal.Round(ParseNumber(value), digits, MidpointRounding.AwayFromZero);
    }

    private static string SerializeJson(object? value) =>
        JsonSerializer.Serialize(ToJsonValue(value));

    private static object? ToJsonValue(object? value) =>
        value switch
        {
            ScriptObject scriptObject => scriptObject.ToDictionary(
                pair => pair.Key,
                pair => ToJsonValue(pair.Value),
                StringComparer.Ordinal),
            ScriptArray scriptArray => scriptArray.Select(ToJsonValue).ToList(),
            null or string or bool or decimal or byte or sbyte or short or ushort or int or uint or long or ulong => value,
            float floatValue when float.IsFinite(floatValue) => floatValue,
            double doubleValue when double.IsFinite(doubleValue) => doubleValue,
            _ => throw new InvalidOperationException("transform template json only accepts JSON-compatible values."),
        };
}
