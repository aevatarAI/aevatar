using System.Security.Cryptography;
using System.Text;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Microsoft.Extensions.Options;

namespace Aevatar.Audit.Core.Identity;

public sealed class AuditActorIdentityHasher : IAuditActorIdentityHasher
{
    private const string AuditActorIdPrefix = "audit_actor:hmac-sha256:";
    private readonly IReadOnlyDictionary<string, byte[]> _keys;
    private readonly string _activeKeyId;
    private readonly string[] _orderedKeyIds;

    public AuditActorIdentityHasher(IOptions<AuditActorIdentityHasherOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        var validator = new AuditActorIdentityHasherOptionsValidator();
        var result = validator.Validate(Options.DefaultName, value);
        if (result.Failed)
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(AuditActorIdentityHasherOptions),
                result.Failures);
        }

        _activeKeyId = value.ActiveKeyId!.Trim();
        _keys = value.Keys.ToDictionary(
            static key => key.KeyId!.Trim(),
            static key => AuditActorIdentityHasherKeyMaterial.Resolve(key),
            StringComparer.Ordinal);
        _orderedKeyIds =
        [
            _activeKeyId,
            .. _keys.Keys
                .Where(keyId => !string.Equals(keyId, _activeKeyId, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];
    }

    public AuditActorIdentity Hash(string canonicalActorKey)
    {
        var canonicalKey = NormalizeCanonicalActorKey(canonicalActorKey);
        var auditActorId = BuildAuditActorId(canonicalKey, _activeKeyId);

        return new AuditActorIdentity(auditActorId, _activeKeyId);
    }

    public IReadOnlyList<AuditActorIdentity> HashAll(string canonicalActorKey)
    {
        var canonicalKey = NormalizeCanonicalActorKey(canonicalActorKey);
        return _orderedKeyIds
            .Select(keyId => new AuditActorIdentity(BuildAuditActorId(canonicalKey, keyId), keyId))
            .ToArray();
    }

    public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId)
    {
        if (string.IsNullOrWhiteSpace(auditActorId) ||
            string.IsNullOrWhiteSpace(identityKeyId) ||
            !_keys.ContainsKey(identityKeyId.Trim()))
        {
            return false;
        }

        var canonicalKey = NormalizeCanonicalActorKey(canonicalActorKey);
        var expectedAuditActorId = BuildAuditActorId(canonicalKey, identityKeyId.Trim());

        return FixedTimeEquals(expectedAuditActorId, auditActorId.Trim());
    }

    private string BuildAuditActorId(string canonicalActorKey, string keyId)
    {
        using var hmac = new HMACSHA256(_keys[keyId]);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonicalActorKey));
        return AuditActorIdPrefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeCanonicalActorKey(string canonicalActorKey)
    {
        if (string.IsNullOrWhiteSpace(canonicalActorKey))
        {
            throw new ArgumentException("Canonical actor key is required.", nameof(canonicalActorKey));
        }

        return canonicalActorKey.Trim();
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);

        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
