using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Hosting.Endpoints;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aevatar.AppPlatform.Hosting.Serialization;

internal sealed class AppFunctionInvokeRequestSerializer : IAppFunctionInvokeRequestSerializer
{
    private readonly TypeRegistry _typeRegistry;
    private readonly JsonParser _jsonParser;

    public AppFunctionInvokeRequestSerializer()
        : this(AppPlatformJsonTypeRegistry.CreateDefault())
    {
    }

    internal AppFunctionInvokeRequestSerializer(TypeRegistry typeRegistry)
    {
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _jsonParser = new JsonParser(
            JsonParser.Settings.Default.WithTypeRegistry(_typeRegistry));
    }

    public AppFunctionInvokeRequest Deserialize(AppPlatformEndpointModels.FunctionInvokeHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hasBinaryPayload = request.BinaryPayload != null;
        var hasTypedPayload = request.TypedPayload != null;
        if (hasBinaryPayload == hasTypedPayload)
        {
            throw new InvalidOperationException("Exactly one of binaryPayload or typedPayload is required.");
        }

        return new AppFunctionInvokeRequest
        {
            Payload = hasBinaryPayload
                ? DeserializeBinaryPayload(request.BinaryPayload!)
                : DeserializeTypedPayload(request.TypedPayload!),
            CommandId = request.CommandId ?? string.Empty,
            CorrelationId = request.CorrelationId ?? string.Empty,
            Caller = new AppFunctionCaller
            {
                ServiceKey = request.CallerServiceKey ?? string.Empty,
                TenantId = request.CallerTenantId ?? string.Empty,
                AppId = request.CallerAppId ?? string.Empty,
                ScopeId = request.CallerScopeId ?? string.Empty,
                SessionId = request.CallerSessionId ?? string.Empty,
            },
        };
    }

    private static Any DeserializeBinaryPayload(AppPlatformEndpointModels.FunctionInvokeBinaryPayloadHttpRequest payload)
    {
        var typeUrl = NormalizeRequired(payload.TypeUrl, "binaryPayload.typeUrl");

        ByteString value;
        try
        {
            var bytes = string.IsNullOrWhiteSpace(payload.PayloadBase64)
                ? []
                : Convert.FromBase64String(payload.PayloadBase64);
            value = ByteString.CopyFrom(bytes);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("binaryPayload.payloadBase64 must be valid base64.", ex);
        }

        return new Any
        {
            TypeUrl = typeUrl,
            Value = value,
        };
    }

    private Any DeserializeTypedPayload(AppPlatformEndpointModels.FunctionInvokeTypedPayloadHttpRequest payload)
    {
        var typeUrl = NormalizeRequired(payload.TypeUrl, "typedPayload.typeUrl");
        if (!payload.PayloadJson.HasValue)
            throw new InvalidOperationException("typedPayload.payloadJson is required.");

        EnsureKnownType(typeUrl);
        var wrappedAnyJson = BuildAnyJson(typeUrl, payload.PayloadJson.Value);

        try
        {
            return _jsonParser.Parse<Any>(wrappedAnyJson.ToJsonString());
        }
        catch (InvalidJsonException ex)
        {
            throw new InvalidOperationException(
                $"typedPayload.payloadJson is not valid protobuf JSON for '{typeUrl}'.",
                ex);
        }
    }

    private void EnsureKnownType(string typeUrl)
    {
        var typeName = ExtractTypeName(typeUrl);
        if (_typeRegistry.Find(typeName) != null)
            return;

        throw new InvalidOperationException($"typedPayload.typeUrl '{typeUrl}' is not registered in the current protobuf type registry.");
    }

    private static JsonObject BuildAnyJson(string typeUrl, JsonElement payloadJson)
    {
        var wrapped = new JsonObject
        {
            ["@type"] = typeUrl,
        };

        if (payloadJson.ValueKind == JsonValueKind.Object)
        {
            var payloadNode = JsonNode.Parse(payloadJson.GetRawText()) as JsonObject
                              ?? throw new InvalidOperationException("typedPayload.payloadJson must be a JSON object.");
            foreach (var property in payloadNode)
            {
                if (string.Equals(property.Key, "@type", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("typedPayload.payloadJson must not include '@type'.");
                }

                wrapped[property.Key] = property.Value?.DeepClone();
            }

            return wrapped;
        }

        wrapped["value"] = JsonNode.Parse(payloadJson.GetRawText());
        return wrapped;
    }

    private static string ExtractTypeName(string typeUrl)
    {
        var trimmed = typeUrl.Trim();
        var slashIndex = trimmed.LastIndexOf('/');
        return slashIndex >= 0 && slashIndex < trimmed.Length - 1
            ? trimmed[(slashIndex + 1)..]
            : trimmed;
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{fieldName} is required.");

        return normalized;
    }
}
