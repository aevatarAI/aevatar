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
    private const int MaxCacheEntries = 500;

    private readonly IServiceRevisionArtifactStore _artifactStore;
    private readonly ConcurrentDictionary<string, CacheEntry<string?>> _schemaCache = new();
    private readonly ConcurrentDictionary<string, CacheEntry<MessageDescriptor?>> _descriptorCache = new();

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
        if (_schemaCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
            return cached.Value;

        var descriptor = await ResolveDescriptorAsync(serviceKey, revisionId, requestTypeUrl, ct);
        if (descriptor == null)
            return null;

        var schema = ProtoToJsonSchemaConverter.Convert(descriptor);
        SetCache(_schemaCache, cacheKey, schema, ttl: null);
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
        if (_descriptorCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
            return cached.Value;

        var descriptor = await ResolveDescriptorCoreAsync(serviceKey, revisionId, requestTypeUrl, ct);

        if (descriptor != null)
        {
            SetCache(_descriptorCache, cacheKey, descriptor, ttl: null);
        }
        else
        {
            // Negative results use short TTL so transient unavailability self-heals
            SetCache(_descriptorCache, cacheKey, (MessageDescriptor?)null, ttl: TimeSpan.FromMinutes(2));
        }

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

    private static void SetCache<T>(ConcurrentDictionary<string, CacheEntry<T>> cache, string key, T value, TimeSpan? ttl)
    {
        // Evict oldest entries when cache grows too large
        if (cache.Count >= MaxCacheEntries)
        {
            var expired = cache.Where(kv => kv.Value.IsExpired).Select(kv => kv.Key).Take(50).ToList();
            foreach (var k in expired)
                cache.TryRemove(k, out _);

            // If still too large, remove arbitrary entries
            if (cache.Count >= MaxCacheEntries)
            {
                var excess = cache.Keys.Take(50).ToList();
                foreach (var k in excess)
                    cache.TryRemove(k, out _);
            }
        }

        cache[key] = new CacheEntry<T>(value, ttl);
    }

    private readonly struct CacheEntry<T>(T value, TimeSpan? ttl)
    {
        public T Value { get; } = value;
        private long ExpiresAtTicks { get; } = ttl.HasValue
            ? Environment.TickCount64 + (long)ttl.Value.TotalMilliseconds
            : long.MaxValue;

        public bool IsExpired => Environment.TickCount64 >= ExpiresAtTicks;
    }
}
