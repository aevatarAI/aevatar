using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Hosting.Serialization;

internal static class ServiceJsonPayloads
{
    public static Any PackBase64(string typeUrl, string? payloadBase64)
    {
        if (string.IsNullOrWhiteSpace(typeUrl))
            throw new InvalidOperationException("payloadTypeUrl is required.");

        var bytes = string.IsNullOrWhiteSpace(payloadBase64)
            ? []
            : Convert.FromBase64String(payloadBase64);
        return new Any
        {
            TypeUrl = typeUrl,
            Value = ByteString.CopyFrom(bytes),
        };
    }
}
