using System.Globalization;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.Schedules;

public static class ScheduledDispatchPromptTemplate
{
    private const string PlaceholderPrefix = "@schedule.";
    private const int MaxTemplateBytes = 64 * 1024;
    private const int MaxRenderedBytes = 256 * 1024;
    private const int MaxPlaceholders = 128;
    private const int MaxJsonDepth = 32;

    private static readonly HashSet<string> SupportedPlaceholders =
    [
        "@schedule.fire_at_utc",
        "@schedule.timezone",
        "@schedule.run_date",
        "@schedule.run_year",
        "@schedule.run_month",
        "@schedule.days_until_month_end",
    ];

    public static ScheduledDispatchValidationResult ValidatePayload(Any? payload)
    {
        if (payload?.Is(ChatRequestEvent.Descriptor) != true)
            return ScheduledDispatchValidationResult.Success();

        try
        {
            return Validate(payload.Unpack<ChatRequestEvent>().Prompt);
        }
        catch (InvalidProtocolBufferException)
        {
            return ScheduledDispatchValidationResult.Failed(
                "Scheduled chat payload is malformed.");
        }
    }

    public static ScheduledDispatchValidationResult Validate(string? template)
    {
        if (string.IsNullOrEmpty(template) || !ContainsSchedulePlaceholder(template))
            return ScheduledDispatchValidationResult.Success();
        if (Encoding.UTF8.GetByteCount(template) > MaxTemplateBytes)
        {
            return ScheduledDispatchValidationResult.Failed(
                "Scheduled prompt template exceeds the supported size.");
        }

        try
        {
            using var document = JsonDocument.Parse(template, DocumentOptions);
            var placeholderCount = 0;
            return ValidateElement(document.RootElement, ref placeholderCount)
                ?? ScheduledDispatchValidationResult.Success();
        }
        catch (JsonException)
        {
            return ScheduledDispatchValidationResult.Failed(
                "Scheduled prompt templates must be valid JSON with placeholders in string values.");
        }
    }

    public static string Render(
        string template,
        ScheduledDispatchFireContext fireContext)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(fireContext);
        if (!ContainsSchedulePlaceholder(template))
            return template;

        var validation = Validate(template);
        if (!validation.Succeeded)
            throw new InvalidOperationException(validation.Error);
        if (!ScheduledDispatchCalculator.TryResolveTimeZone(
                fireContext.Timezone,
                out var timeZone,
                out _))
        {
            throw new InvalidOperationException("Scheduled dispatch fire timezone is invalid.");
        }

        var fireAtUtc = fireContext.FireAtUtc.ToUniversalTime();
        var localFireAt = TimeZoneInfo.ConvertTime(fireAtUtc, timeZone);
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["@schedule.fire_at_utc"] = fireAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["@schedule.timezone"] = ScheduledDispatchCalculator.NormalizeTimezone(fireContext.Timezone),
            ["@schedule.run_date"] = localFireAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["@schedule.run_year"] = localFireAt.Year.ToString(CultureInfo.InvariantCulture),
            ["@schedule.run_month"] = localFireAt.Month.ToString(CultureInfo.InvariantCulture),
            ["@schedule.days_until_month_end"] =
                (DateTime.DaysInMonth(localFireAt.Year, localFireAt.Month) - localFireAt.Day)
                .ToString(CultureInfo.InvariantCulture),
        };

        try
        {
            using var document = JsonDocument.Parse(template, DocumentOptions);
            using var output = new CappedMemoryStream(MaxRenderedBytes);
            using (var writer = new Utf8JsonWriter(output))
                WriteElement(writer, document.RootElement, replacements);
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        catch (PromptOutputTooLargeException)
        {
            throw new InvalidOperationException(
                "Scheduled prompt template output exceeds the supported size.");
        }
    }

    private static ScheduledDispatchValidationResult? ValidateElement(
        JsonElement element,
        ref int placeholderCount)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ContainsSchedulePlaceholder(property.Name))
                    {
                        return ScheduledDispatchValidationResult.Failed(
                            "Scheduled prompt placeholders are allowed only in JSON string values.");
                    }

                    var failure = ValidateElement(property.Value, ref placeholderCount);
                    if (failure != null)
                        return failure;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var failure = ValidateElement(item, ref placeholderCount);
                    if (failure != null)
                        return failure;
                }
                break;
            case JsonValueKind.String:
                return ValidateTemplateString(element.GetString() ?? string.Empty, ref placeholderCount);
        }

        return null;
    }

    private static ScheduledDispatchValidationResult? ValidateTemplateString(
        string value,
        ref int placeholderCount)
    {
        var cursor = 0;
        while (cursor < value.Length)
        {
            var start = value.IndexOf("{{", cursor, StringComparison.Ordinal);
            var strayEnd = value.IndexOf("}}", cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                return strayEnd >= 0
                    ? ScheduledDispatchValidationResult.Failed(
                        "Scheduled prompt template contains an unmatched placeholder delimiter.")
                    : null;
            }
            if (strayEnd >= 0 && strayEnd < start)
            {
                return ScheduledDispatchValidationResult.Failed(
                    "Scheduled prompt template contains an unmatched placeholder delimiter.");
            }
            var end = value.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                return ScheduledDispatchValidationResult.Failed(
                    "Scheduled prompt template contains an unmatched placeholder delimiter.");
            }
            var nestedStart = value.IndexOf("{{", start + 2, StringComparison.Ordinal);
            if (nestedStart >= 0 && nestedStart < end)
            {
                return ScheduledDispatchValidationResult.Failed(
                    "Scheduled prompt template contains nested placeholder delimiters.");
            }

            var placeholder = value[(start + 2)..end].Trim();
            if (placeholder.StartsWith(PlaceholderPrefix, StringComparison.Ordinal))
            {
                placeholderCount++;
                if (placeholderCount > MaxPlaceholders)
                {
                    return ScheduledDispatchValidationResult.Failed(
                        "Scheduled prompt template contains too many placeholders.");
                }
                if (!SupportedPlaceholders.Contains(placeholder))
                {
                    return ScheduledDispatchValidationResult.Failed(
                        $"Unsupported scheduled prompt placeholder '{placeholder}'.");
                }
            }
            cursor = end + 2;
        }

        return null;
    }

    private static void WriteElement(
        Utf8JsonWriter writer,
        JsonElement element,
        IReadOnlyDictionary<string, string> replacements)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value, replacements);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteElement(writer, item, replacements);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(ReplaceSchedulePlaceholders(
                    element.GetString() ?? string.Empty,
                    replacements));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string ReplaceSchedulePlaceholders(
        string template,
        IReadOnlyDictionary<string, string> replacements)
    {
        var builder = new StringBuilder(template.Length);
        var cursor = 0;
        while (cursor < template.Length)
        {
            var start = template.IndexOf("{{", cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(template, cursor, template.Length - cursor);
                break;
            }

            builder.Append(template, cursor, start - cursor);
            var end = template.IndexOf("}}", start + 2, StringComparison.Ordinal);
            var placeholder = template[(start + 2)..end].Trim();
            if (replacements.TryGetValue(placeholder, out var value))
                builder.Append(value);
            else
                builder.Append(template, start, end + 2 - start);
            cursor = end + 2;
        }

        return builder.ToString();
    }

    private static bool ContainsSchedulePlaceholder(string value)
    {
        var prefixIndex = value.IndexOf(PlaceholderPrefix, StringComparison.Ordinal);
        while (prefixIndex >= 0)
        {
            var start = value.LastIndexOf("{{", prefixIndex, StringComparison.Ordinal);
            if (start >= 0 && string.IsNullOrWhiteSpace(value[(start + 2)..prefixIndex]))
                return true;
            prefixIndex = value.IndexOf(PlaceholderPrefix, prefixIndex + PlaceholderPrefix.Length, StringComparison.Ordinal);
        }

        return false;
    }

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = MaxJsonDepth,
    };

    private sealed class CappedMemoryStream(int maxBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > maxBytes)
                throw new PromptOutputTooLargeException();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > maxBytes)
                throw new PromptOutputTooLargeException();
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            if (Length + 1 > maxBytes)
                throw new PromptOutputTooLargeException();
            base.WriteByte(value);
        }
    }

    private sealed class PromptOutputTooLargeException : Exception;
}
