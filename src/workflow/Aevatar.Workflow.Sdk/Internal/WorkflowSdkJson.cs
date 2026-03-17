using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Workflow.Sdk.Errors;

namespace Aevatar.Workflow.Sdk.Internal;

internal static class WorkflowSdkJson
{
    public static JsonSerializerOptions CreateSerializerOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

    public static AevatarWorkflowException BuildHttpException(
        HttpStatusCode statusCode,
        string? rawPayload,
        string fallbackMessage)
    {
        var message = fallbackMessage;
        string? code = null;

        if (!string.IsNullOrWhiteSpace(rawPayload))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawPayload);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    code = TryReadString(doc.RootElement, "code");
                    message =
                        TryReadString(doc.RootElement, "message") ??
                        TryReadString(doc.RootElement, "error") ??
                        fallbackMessage;
                }
            }
            catch (JsonException)
            {
                message = rawPayload!;
            }
        }

        return new AevatarWorkflowException(
            AevatarWorkflowErrorKind.Http,
            message,
            errorCode: code,
            statusCode: statusCode,
            rawPayload: rawPayload);
    }

    public static string? TryReadString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}
