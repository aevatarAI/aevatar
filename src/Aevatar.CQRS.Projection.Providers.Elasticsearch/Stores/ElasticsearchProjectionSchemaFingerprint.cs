using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

/// <summary>
/// Computes a stable 8-character hex fingerprint over the augmented Elasticsearch
/// index mappings declared by code. Two metadata instances yield the same
/// fingerprint iff their canonicalized <c>Mappings</c> dictionaries are equal
/// (key-order independent, recursive). The fingerprint is the version suffix
/// on physical Elasticsearch index names. When proto/descriptor changes shift
/// the augmented mappings, the fingerprint shifts. The lifecycle manager uses
/// that mismatch as the migration trigger and remains the only place allowed
/// to create the expected physical index, reindex, and swap aliases; query and
/// probe paths never read live mappings or repair schema drift.
/// </summary>
internal static class ElasticsearchProjectionSchemaFingerprint
{
    internal static string Compute(DocumentIndexMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var canonical = JsonSerializer.Serialize(Canonicalize(metadata.Mappings));
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static object? Canonicalize(object? value) => value switch
    {
        null => null,
        IReadOnlyDictionary<string, object?> dict => CanonicalizeDictionary(dict),
        IDictionary<string, object?> mutableDict => CanonicalizeDictionary(mutableDict),
        IEnumerable<object?> list when value is not string => list.Select(Canonicalize).ToArray(),
        _ => value,
    };

    private static SortedDictionary<string, object?> CanonicalizeDictionary(
        IEnumerable<KeyValuePair<string, object?>> dict)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in dict)
            sorted[k] = Canonicalize(v);
        return sorted;
    }
}
