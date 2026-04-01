using System.Collections.Concurrent;
using Aevatar.GAgentService.Abstractions.Ports;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.ToolProviders.ServiceInvoke.Schema;

/// <summary>
/// Resolves endpoint request/response type URLs to JSON Schema strings and
/// performs typed JSON-to-Proto conversion using <c>protocol_descriptor_set</c>
/// from service revision artifacts.
/// </summary>
public sealed class EndpointSchemaProvider
{
    private const string TypeUrlPrefix = "type.googleapis.com/";

    private readonly IServiceRevisionArtifactStore _artifactStore;
    private readonly ConcurrentDictionary<string, string?> _schemaCache = new();
    private readonly ConcurrentDictionary<string, MessageDescriptor?> _descriptorCache = new();

    public EndpointSchemaProvider(IServiceRevisionArtifactStore artifactStore)
    {
        _artifactStore = artifactStore;
    }

    /// <summary>
    /// Resolves a request type URL to a JSON Schema string.
    /// Returns null if the descriptor set is unavailable or the type is not found.
    /// </summary>
    public async Task<string?> GetJsonSchemaAsync(
        string serviceKey,
        string revisionId,
        string requestTypeUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestTypeUrl))
            return null;

        var cacheKey = $"{serviceKey}:{revisionId}:{requestTypeUrl}";
        if (_schemaCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var descriptor = await ResolveDescriptorAsync(serviceKey, revisionId, requestTypeUrl, ct);
        if (descriptor == null)
        {
            _schemaCache[cacheKey] = null;
            return null;
        }

        var schema = ProtoToJsonSchemaConverter.Convert(descriptor);
        _schemaCache[cacheKey] = schema;
        return schema;
    }

    /// <summary>
    /// Tries to convert JSON to a typed <see cref="Any"/> matching the endpoint's declared RequestTypeUrl.
    /// Returns null if the type cannot be resolved (caller should fall back to Struct).
    /// </summary>
    public async Task<Any?> TryPackTypedAsync(
        string serviceKey,
        string revisionId,
        string requestTypeUrl,
        string payloadJson,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestTypeUrl))
            return null;

        try
        {
            var descriptor = await ResolveDescriptorAsync(serviceKey, revisionId, requestTypeUrl, ct);
            if (descriptor == null)
                return null;

            var registry = TypeRegistry.FromMessages(descriptor);
            var parserSettings = JsonParser.Settings.Default.WithTypeRegistry(registry);
            var parser = new JsonParser(parserSettings);

            var message = parser.Parse(payloadJson, descriptor);

            // Build Any with the correct TypeUrl
            var typeUrl = requestTypeUrl.StartsWith(TypeUrlPrefix, StringComparison.Ordinal)
                ? requestTypeUrl
                : TypeUrlPrefix + requestTypeUrl;

            return new Any
            {
                TypeUrl = typeUrl,
                Value = message.ToByteString(),
            };
        }
        catch
        {
            // Typed conversion failed — caller falls back to Struct
            return null;
        }
    }

    private async Task<MessageDescriptor?> ResolveDescriptorAsync(
        string serviceKey,
        string revisionId,
        string requestTypeUrl,
        CancellationToken ct)
    {
        var cacheKey = $"{serviceKey}:{revisionId}:{requestTypeUrl}";
        if (_descriptorCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var descriptor = await ResolveDescriptorCoreAsync(serviceKey, revisionId, requestTypeUrl, ct);
        _descriptorCache[cacheKey] = descriptor;
        return descriptor;
    }

    private async Task<MessageDescriptor?> ResolveDescriptorCoreAsync(
        string serviceKey,
        string revisionId,
        string requestTypeUrl,
        CancellationToken ct)
    {
        var artifact = await _artifactStore.GetAsync(serviceKey, revisionId, ct);
        if (artifact == null || artifact.ProtocolDescriptorSet.IsEmpty)
            return null;

        var fullName = requestTypeUrl.StartsWith(TypeUrlPrefix, StringComparison.Ordinal)
            ? requestTypeUrl[TypeUrlPrefix.Length..]
            : requestTypeUrl;

        try
        {
            var fds = FileDescriptorSet.Parser.ParseFrom(artifact.ProtocolDescriptorSet);
            var byteStrings = fds.File.Select(f => f.ToByteString()).ToList();
            var fileDescriptors = FileDescriptor.BuildFromByteStrings(byteStrings);

            foreach (var fd in fileDescriptors)
            {
                var md = fd.MessageTypes
                    .FirstOrDefault(m => string.Equals(m.FullName, fullName, StringComparison.Ordinal));
                if (md != null) return md;

                md = FindNestedType(fd.MessageTypes, fullName);
                if (md != null) return md;
            }
        }
        catch
        {
            // Descriptor set parsing failed
        }

        return null;
    }

    private static MessageDescriptor? FindNestedType(
        IList<MessageDescriptor> messageTypes,
        string fullName)
    {
        foreach (var mt in messageTypes)
        {
            if (string.Equals(mt.FullName, fullName, StringComparison.Ordinal))
                return mt;
            var nested = FindNestedType(mt.NestedTypes, fullName);
            if (nested != null) return nested;
        }

        return null;
    }
}
