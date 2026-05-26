using System.Text.Json;
using Aevatar.GAgentService.Abstractions.Ports;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Hosting.Serialization;

internal static class ServiceJsonPayloads
{
    private const string TypeUrlPrefix = "type.googleapis.com/";

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

    public static async Task<Any> PackJsonAsync(
        IServiceRevisionArtifactStore artifactStore,
        string serviceKey,
        string revisionId,
        string typeUrl,
        string payloadJson,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        if (string.IsNullOrWhiteSpace(typeUrl))
            throw new InvalidOperationException("payloadTypeUrl is required.");
        if (string.IsNullOrWhiteSpace(revisionId))
            throw new InvalidOperationException(
                "payloadJson requires a revisionId; provide one explicitly or activate a serving revision.");

        var artifact = await artifactStore.GetAsync(serviceKey, revisionId, ct);
        if (artifact == null || artifact.ProtocolDescriptorSet.IsEmpty)
            throw new InvalidOperationException(
                $"payloadTypeUrl '{typeUrl}' could not be resolved: revision '{revisionId}' has no protocol descriptor set.");

        var descriptor = ResolveDescriptor(artifact.ProtocolDescriptorSet, typeUrl);
        if (descriptor == null)
            throw new InvalidOperationException(
                $"payloadTypeUrl '{typeUrl}' was not found in revision '{revisionId}'.");

        byte[] bytes;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            bytes = JsonToProto.WriteMessage(descriptor, doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"payloadJson is not valid JSON: {ex.Message}", ex);
        }

        var fullTypeUrl = typeUrl.StartsWith(TypeUrlPrefix, StringComparison.Ordinal)
            ? typeUrl
            : TypeUrlPrefix + typeUrl;

        return new Any
        {
            TypeUrl = fullTypeUrl,
            Value = ByteString.CopyFrom(bytes),
        };
    }

    private static MessageDescriptor? ResolveDescriptor(ByteString descriptorSet, string typeUrl)
    {
        var fullName = typeUrl.StartsWith(TypeUrlPrefix, StringComparison.Ordinal)
            ? typeUrl[TypeUrlPrefix.Length..]
            : typeUrl;

        try
        {
            var fds = FileDescriptorSet.Parser.ParseFrom(descriptorSet);
            var byteStrings = fds.File.Select(f => f.ToByteString()).ToList();
            var fileDescriptors = FileDescriptor.BuildFromByteStrings(byteStrings);

            foreach (var fd in fileDescriptors)
            {
                var match = FindByFullName(fd.MessageTypes, fullName);
                if (match != null) return match;
            }
        }
        catch (InvalidProtocolBufferException)
        {
            // Descriptor set parsing failed; treat as unresolved.
        }

        return null;
    }

    private static MessageDescriptor? FindByFullName(IList<MessageDescriptor> messageTypes, string fullName)
    {
        foreach (var mt in messageTypes)
        {
            if (string.Equals(mt.FullName, fullName, StringComparison.Ordinal))
                return mt;
            var nested = FindByFullName(mt.NestedTypes, fullName);
            if (nested != null) return nested;
        }

        return null;
    }
}
