using System.Text.Json;

namespace Aevatar.GAgents.NyxidChat;

public sealed class NyxIdActionSecretPolicyException : Exception
{
    public NyxIdActionSecretPolicyException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Rejects secret-shaped action inputs at the JSON/HTTP boundary. Accepted
/// values still enter the actor only after conversion to closed protobuf
/// messages; this policy never redacts a secret and then continues silently.
/// </summary>
public static class NyxIdActionSecretPolicy
{
    public const string ForbiddenFieldCode = "NYXID_ACTION_SECRET_FIELD_FORBIDDEN";
    public const string UnsafeUrlCode = "NYXID_ACTION_URL_UNSAFE";
    public const string InvalidJsonCode = "NYXID_ACTION_PARAMS_INVALID_JSON";

    private static readonly HashSet<string> ForbiddenFieldNames = new(
        StringComparer.Ordinal)
    {
        "token",
        "tokens",
        "accesstoken",
        "refreshtoken",
        "authorization",
        "cookie",
        "cookies",
        "secret",
        "secrets",
        "clientsecret",
        "password",
        "passphrase",
        "usercode",
        "devicecode",
        "rawbody",
        "rawupstreambody",
        "credential",
        "credentials",
    };

    public static void ValidateParamsJson(string paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
            throw new NyxIdActionSecretPolicyException(
                InvalidJsonCode,
                "Action params must be a JSON object.");

        try
        {
            using var document = JsonDocument.Parse(paramsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new NyxIdActionSecretPolicyException(
                    InvalidJsonCode,
                    "Action params must be a JSON object.");
            }

            ValidateElement(document.RootElement);
        }
        catch (JsonException)
        {
            throw new NyxIdActionSecretPolicyException(
                InvalidJsonCode,
                "Action params must be valid JSON.");
        }
    }

    public static string NormalizeSafeUrl(string value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new NyxIdActionSecretPolicyException(
                UnsafeUrlCode,
                "Action URLs must be absolute HTTPS URLs without userinfo, query, or fragment components.");
        }

        return uri.AbsoluteUri;
    }

    internal static void ValidateFieldName(string fieldName)
    {
        var normalized = NormalizeFieldName(fieldName);
        if (!ForbiddenFieldNames.Contains(normalized))
            return;

        throw new NyxIdActionSecretPolicyException(
            ForbiddenFieldCode,
            "Action params cannot contain credential or secret fields.");
    }

    private static void ValidateElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    ValidateFieldName(property.Name);
                    ValidateElement(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ValidateElement(item);
                break;
            case JsonValueKind.String:
                ValidateStringValue(element.GetString());
                break;
        }
    }

    private static void ValidateStringValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var normalized = value.Trim();
        if (normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            throw new NyxIdActionSecretPolicyException(
                ForbiddenFieldCode,
                "Action params cannot contain authorization credentials.");
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            (!string.IsNullOrEmpty(uri.UserInfo) ||
             !string.IsNullOrEmpty(uri.Query) ||
             !string.IsNullOrEmpty(uri.Fragment)))
        {
            throw new NyxIdActionSecretPolicyException(
                UnsafeUrlCode,
                "Action URLs cannot contain userinfo, query, or fragment components.");
        }
    }

    private static string NormalizeFieldName(string value) =>
        new(value
            .Where(static character => char.IsAsciiLetterOrDigit(character))
            .Select(static character => char.ToLowerInvariant(character))
            .ToArray());
}
