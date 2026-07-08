using System.Text.Json;

namespace Aevatar.GAgents.NyxidChat;

internal static class ChannelLarkProxyResponse
{
    public const int NoPermissionToReact = 231002;

    public static bool TryGetError(string? response, out int? larkCode, out string detail)
    {
        larkCode = null;
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("code", out var topCodeProperty) &&
                topCodeProperty.ValueKind == JsonValueKind.Number &&
                topCodeProperty.TryGetInt32(out var topCode) &&
                topCode != 0)
            {
                larkCode = topCode;
                detail = TryReadString(root, "msg") ?? $"code={topCode}";
                return true;
            }

            if (!root.TryGetProperty("error", out var errorProperty))
                return false;

            var hasErrorFlag = errorProperty.ValueKind == JsonValueKind.True ||
                               (errorProperty.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(errorProperty.GetString()));
            if (!hasErrorFlag)
                return false;

            if (TryParseNestedLarkBody(root, out var nestedCode, out var nestedDetail))
            {
                larkCode = nestedCode;
                detail = nestedDetail;
                return true;
            }

            if (errorProperty.ValueKind == JsonValueKind.True)
            {
                detail = TryReadString(root, "message")
                         ?? TryReadString(root, "body")
                         ?? FormatStatusFallback(root)
                         ?? "proxy_error";
                return true;
            }

            if (errorProperty.ValueKind == JsonValueKind.String)
            {
                detail = errorProperty.GetString()!.Trim();
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool TryParseNestedLarkBody(JsonElement root, out int? larkCode, out string detail)
    {
        larkCode = null;
        detail = string.Empty;

        var rawBody = TryReadString(root, "body");
        if (string.IsNullOrEmpty(rawBody))
            return false;

        try
        {
            using var nested = JsonDocument.Parse(rawBody);
            var nestedRoot = nested.RootElement;
            if (nestedRoot.ValueKind != JsonValueKind.Object)
                return false;

            if (!nestedRoot.TryGetProperty("code", out var codeProperty) ||
                codeProperty.ValueKind != JsonValueKind.Number ||
                !codeProperty.TryGetInt32(out var code) ||
                code == 0)
                return false;

            larkCode = code;
            var msg = TryReadString(nestedRoot, "msg") ?? $"code={code}";
            var status = TryReadInt32(root, "status");
            detail = status is { } s
                ? $"nyx_status={s} lark_code={code} msg={msg}"
                : $"lark_code={code} msg={msg}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FormatStatusFallback(JsonElement root)
    {
        var status = TryReadInt32(root, "status");
        return status is { } s ? $"nyx_status={s}" : null;
    }

    private static int? TryReadInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value))
        {
            return null;
        }

        return value;
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
