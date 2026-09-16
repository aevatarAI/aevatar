using System.Text;
using System.Text.Json;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class WorkflowWebhookPromptTemplate
{
    public static WorkflowWebhookPromptTemplateValidation Validate(string template)
    {
        if (Encoding.UTF8.GetByteCount(template) > WorkflowWebhookIngressLimits.MaxPromptTemplateBytes)
        {
            return WorkflowWebhookPromptTemplateValidation.Failure(
                "WEBHOOK_PROMPT_TEMPLATE_TOO_LARGE",
                "promptTemplate exceeds the supported size.");
        }

        try
        {
            using var document = JsonDocument.Parse(template, DocumentOptions);
            var placeholderCount = 0;
            var validation = ValidateElement(document.RootElement, ref placeholderCount);
            return validation ?? WorkflowWebhookPromptTemplateValidation.Success;
        }
        catch (JsonException)
        {
            return WorkflowWebhookPromptTemplateValidation.Failure(
                "WEBHOOK_PROMPT_TEMPLATE_INVALID",
                "promptTemplate must be a valid JSON document.");
        }
    }

    public static WorkflowWebhookPromptRenderResult Render(
        string template,
        JsonElement payload,
        DateTimeOffset receivedAt,
        string? timeZoneId)
    {
        var validation = Validate(template);
        if (!validation.Succeeded)
        {
            return WorkflowWebhookPromptRenderResult.ConfigurationFailure(
                validation.ErrorCode!,
                validation.ErrorMessage!);
        }

        try
        {
            using var document = JsonDocument.Parse(template, DocumentOptions);
            using var output = new CappedMemoryStream(WorkflowWebhookIngressLimits.MaxPromptBytes);
            using (var writer = new Utf8JsonWriter(output))
            {
                var placeholderCount = 0;
                var writeFailure = WriteElement(
                    writer,
                    document.RootElement,
                    payload,
                    receivedAt,
                    ResolveTimeZone(timeZoneId),
                    ref placeholderCount);
                if (writeFailure != null)
                    return writeFailure;
            }

            return WorkflowWebhookPromptRenderResult.Success(
                Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length)));
        }
        catch (PromptOutputTooLargeException)
        {
            return WorkflowWebhookPromptRenderResult.PayloadFailure(
                "WEBHOOK_PROMPT_TOO_LARGE",
                "Webhook prompt mapping exceeds the supported output size.",
                413);
        }
        catch (JsonException)
        {
            return WorkflowWebhookPromptRenderResult.ConfigurationFailure(
                "WEBHOOK_PROMPT_TEMPLATE_INVALID",
                "promptTemplate must be a valid JSON document.");
        }
    }

    private static WorkflowWebhookPromptTemplateValidation? ValidateElement(
        JsonElement element,
        ref int placeholderCount)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ContainsTemplateDelimiter(property.Name))
                    {
                        return WorkflowWebhookPromptTemplateValidation.Failure(
                            "WEBHOOK_PROMPT_TEMPLATE_INVALID",
                            "promptTemplate placeholders are allowed only in JSON string values.");
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

    private static WorkflowWebhookPromptTemplateValidation? ValidateTemplateString(
        string value,
        ref int placeholderCount)
    {
        var position = 0;
        while (position < value.Length)
        {
            var start = value.IndexOf("{{", position, StringComparison.Ordinal);
            var strayEnd = value.IndexOf("}}", position, StringComparison.Ordinal);
            if (start < 0)
            {
                return strayEnd >= 0
                    ? WorkflowWebhookPromptTemplateValidation.Failure(
                        "WEBHOOK_PROMPT_TEMPLATE_INVALID",
                        "promptTemplate contains an unmatched placeholder delimiter.")
                    : null;
            }

            if (strayEnd >= 0 && strayEnd < start)
            {
                return WorkflowWebhookPromptTemplateValidation.Failure(
                    "WEBHOOK_PROMPT_TEMPLATE_INVALID",
                    "promptTemplate contains an unmatched placeholder delimiter.");
            }

            var end = value.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                return WorkflowWebhookPromptTemplateValidation.Failure(
                    "WEBHOOK_PROMPT_TEMPLATE_INVALID",
                    "promptTemplate contains an unmatched placeholder delimiter.");
            }

            var placeholder = value[(start + 2)..end].Trim();
            placeholderCount++;
            if (placeholderCount > WorkflowWebhookIngressLimits.MaxPromptPlaceholders ||
                placeholder.Length == 0)
            {
                return WorkflowWebhookPromptTemplateValidation.Failure(
                    "WEBHOOK_PROMPT_TEMPLATE_INVALID",
                    "promptTemplate contains too many or empty placeholders.");
            }

            if (placeholder.StartsWith('@'))
            {
                if (!IsKnownIngressPlaceholder(placeholder))
                {
                    return WorkflowWebhookPromptTemplateValidation.Failure(
                        "WEBHOOK_PROMPT_TEMPLATE_INVALID",
                        "promptTemplate contains an unknown ingress placeholder.");
                }
            }
            else if (!WorkflowWebhookJsonPath.IsValid(placeholder))
            {
                return WorkflowWebhookPromptTemplateValidation.Failure(
                    "WEBHOOK_PROMPT_TEMPLATE_INVALID",
                    "promptTemplate contains an invalid JSON path placeholder.");
            }

            position = end + 2;
        }

        return null;
    }

    private static WorkflowWebhookPromptRenderResult? WriteElement(
        Utf8JsonWriter writer,
        JsonElement element,
        JsonElement payload,
        DateTimeOffset receivedAt,
        TimeZoneInfo timeZone,
        ref int placeholderCount)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    var failure = WriteElement(
                        writer,
                        property.Value,
                        payload,
                        receivedAt,
                        timeZone,
                        ref placeholderCount);
                    if (failure != null)
                        return failure;
                }
                writer.WriteEndObject();
                return null;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    var failure = WriteElement(
                        writer,
                        item,
                        payload,
                        receivedAt,
                        timeZone,
                        ref placeholderCount);
                    if (failure != null)
                        return failure;
                }
                writer.WriteEndArray();
                return null;
            case JsonValueKind.String:
                var expansion = ExpandString(
                    element.GetString() ?? string.Empty,
                    payload,
                    receivedAt,
                    timeZone,
                    ref placeholderCount);
                if (!expansion.Succeeded)
                    return expansion;
                writer.WriteStringValue(expansion.Prompt);
                return null;
            default:
                element.WriteTo(writer);
                return null;
        }
    }

    private static WorkflowWebhookPromptRenderResult ExpandString(
        string value,
        JsonElement payload,
        DateTimeOffset receivedAt,
        TimeZoneInfo timeZone,
        ref int placeholderCount)
    {
        var builder = new StringBuilder(value.Length);
        var position = 0;
        while (position < value.Length)
        {
            var start = value.IndexOf("{{", position, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(value, position, value.Length - position);
                break;
            }

            builder.Append(value, position, start - position);
            var end = value.IndexOf("}}", start + 2, StringComparison.Ordinal);
            var placeholder = value[(start + 2)..end].Trim();
            placeholderCount++;

            string? replacement;
            if (placeholder.StartsWith('@'))
            {
                replacement = ResolveIngressPlaceholder(placeholder, receivedAt, timeZone);
            }
            else if (!WorkflowWebhookJsonPath.TryExtractScalar(payload, placeholder, out replacement))
            {
                return WorkflowWebhookPromptRenderResult.PayloadFailure(
                    "WEBHOOK_PROMPT_PATH_MISSING",
                    "Webhook payload is missing a required prompt value.",
                    400);
            }

            builder.Append(replacement);
            position = end + 2;
        }

        return WorkflowWebhookPromptRenderResult.Success(builder.ToString());
    }

    private static bool ContainsTemplateDelimiter(string value) =>
        value.Contains("{{", StringComparison.Ordinal) || value.Contains("}}", StringComparison.Ordinal);

    private static bool IsKnownIngressPlaceholder(string placeholder) =>
        placeholder is "@run_date" or "@received_at_unix_ms";

    private static string? ResolveIngressPlaceholder(
        string placeholder,
        DateTimeOffset receivedAt,
        TimeZoneInfo timeZone) =>
        placeholder switch
        {
            "@run_date" => TimeZoneInfo.ConvertTime(receivedAt, timeZone).ToString("yyyy-MM-dd"),
            "@received_at_unix_ms" => receivedAt.ToUnixTimeMilliseconds().ToString(),
            _ => null,
        };

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = WorkflowWebhookIngressLimits.MaxJsonDepth,
    };

    private sealed class CappedMemoryStream(int maxBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityFor(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityFor(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacityFor(1);
            base.WriteByte(value);
        }

        private void EnsureCapacityFor(int count)
        {
            if (Length + count > maxBytes)
                throw new PromptOutputTooLargeException();
        }
    }

    private sealed class PromptOutputTooLargeException : Exception;
}

internal sealed record WorkflowWebhookPromptTemplateValidation(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static WorkflowWebhookPromptTemplateValidation Success { get; } = new(true, null, null);

    public static WorkflowWebhookPromptTemplateValidation Failure(string code, string message) =>
        new(false, code, message);
}

internal sealed record WorkflowWebhookPromptRenderResult(
    string? Prompt,
    string? ErrorCode,
    string? ErrorMessage,
    int StatusCode,
    bool IsConfigurationError = false)
{
    public bool Succeeded => Prompt != null && ErrorCode == null;

    public static WorkflowWebhookPromptRenderResult Success(string prompt) =>
        new(prompt, null, null, 200);

    public static WorkflowWebhookPromptRenderResult PayloadFailure(string code, string message, int statusCode) =>
        new(null, code, message, statusCode);

    public static WorkflowWebhookPromptRenderResult ConfigurationFailure(string code, string message) =>
        new(null, code, message, 500, true);
}
