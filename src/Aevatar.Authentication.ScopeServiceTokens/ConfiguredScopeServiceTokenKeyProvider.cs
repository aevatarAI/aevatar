using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Authentication.ScopeServiceTokens;

public sealed class ConfiguredScopeServiceTokenKeyProvider : IScopeServiceTokenKeyProvider, IDisposable
{
    private const int MinHmacKeyBytes = 32;
    private readonly List<IDisposable> _disposables = [];

    public ConfiguredScopeServiceTokenKeyProvider(IOptions<ScopeServiceTokenOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        var keys = BuildKeys(value);
        if (keys.Count == 0)
            throw new InvalidOperationException("At least one scope service token signing key is required.");

        Issuer = NormalizeRequired(value.Issuer, nameof(value.Issuer));
        Audience = NormalizeOptional(value.Audience);
        ClockSkew = TimeSpan.FromSeconds(value.ClockSkewSeconds < 0 ? 0 : value.ClockSkewSeconds);
        ValidationKeys = keys;
        CurrentSigningKey = ResolveCurrentKey(value, keys);
    }

    public string Issuer { get; }

    public string? Audience { get; }

    public TimeSpan ClockSkew { get; }

    public ScopeServiceSigningKey CurrentSigningKey { get; }

    public IReadOnlyList<ScopeServiceSigningKey> ValidationKeys { get; }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();
    }

    private List<ScopeServiceSigningKey> BuildKeys(ScopeServiceTokenOptions options)
    {
        ScopeServiceTokenSigningKeyOptions[] configuredKeys = options.SigningKeys is { Length: > 0 }
            ? options.SigningKeys
            : options.SigningKey == null
                ? []
                : [options.SigningKey];

        return configuredKeys
            .Select(BuildKey)
            .ToList();
    }

    private ScopeServiceSigningKey BuildKey(ScopeServiceTokenSigningKeyOptions options)
    {
        var kid = NormalizeRequired(options.Kid, nameof(options.Kid));
        var algorithm = NormalizeAlgorithm(options.Algorithm);
        return algorithm switch
        {
            ScopeServiceTokenAlgorithms.HmacSha256 => BuildHmacKey(kid, options),
            ScopeServiceTokenAlgorithms.RsaSha256 => BuildRsaKey(kid, options),
            _ => throw new InvalidOperationException($"Unsupported scope service token signing algorithm '{options.Algorithm}'."),
        };
    }

    private static ScopeServiceSigningKey BuildHmacKey(
        string kid,
        ScopeServiceTokenSigningKeyOptions options)
    {
        var keyBytes = ResolveSymmetricKeyBytes(options);
        if (keyBytes.Length < MinHmacKeyBytes)
            throw new InvalidOperationException("Scope service token HS256 signing key must be at least 32 bytes.");

        var key = new SymmetricSecurityKey(keyBytes) { KeyId = kid };
        return new ScopeServiceSigningKey(kid, SecurityAlgorithms.HmacSha256, key, key);
    }

    private ScopeServiceSigningKey BuildRsaKey(
        string kid,
        ScopeServiceTokenSigningKeyOptions options)
    {
        var pem = ResolvePem(options);
        var signingRsa = RSA.Create();
        signingRsa.ImportFromPem(pem);
        _disposables.Add(signingRsa);

        var validationRsa = RSA.Create();
        validationRsa.ImportParameters(signingRsa.ExportParameters(false));
        _disposables.Add(validationRsa);

        var signingKey = new RsaSecurityKey(signingRsa) { KeyId = kid };
        var validationKey = new RsaSecurityKey(validationRsa) { KeyId = kid };
        return new ScopeServiceSigningKey(kid, SecurityAlgorithms.RsaSha256, signingKey, validationKey);
    }

    private static ScopeServiceSigningKey ResolveCurrentKey(
        ScopeServiceTokenOptions options,
        IReadOnlyList<ScopeServiceSigningKey> keys)
    {
        ScopeServiceTokenSigningKeyOptions[] configuredKeys = options.SigningKeys is { Length: > 0 }
            ? options.SigningKeys
            : options.SigningKey == null ? [] : [options.SigningKey];

        var configured = configuredKeys
            .Select((key, index) => new { key, index })
            .Where(x => x.key.Current)
            .ToList();

        if (configured.Count > 1)
            throw new InvalidOperationException("Only one scope service token signing key can be marked current.");

        return configured.Count == 1
            ? keys[configured[0].index]
            : keys[0];
    }

    private static byte[] ResolveSymmetricKeyBytes(ScopeServiceTokenSigningKeyOptions options)
    {
        var base64 = NormalizeOptional(options.KeyBase64);
        if (base64 != null)
            return Convert.FromBase64String(base64);

        var key = NormalizeOptional(options.Key);
        if (key == null)
            throw new InvalidOperationException("Scope service token HS256 signing key requires Key or KeyBase64.");

        return System.Text.Encoding.UTF8.GetBytes(key);
    }

    private static string ResolvePem(ScopeServiceTokenSigningKeyOptions options)
    {
        var pem = NormalizeOptional(options.Pem);
        if (pem != null)
            return pem.Replace("\\n", "\n", StringComparison.Ordinal);

        var pemPath = NormalizeOptional(options.PemPath);
        if (pemPath == null)
            throw new InvalidOperationException("Scope service token RS256 signing key requires Pem or PemPath.");

        return File.ReadAllText(pemPath);
    }

    private static string NormalizeAlgorithm(string? value)
    {
        var normalized = NormalizeRequired(value, nameof(ScopeServiceTokenSigningKeyOptions.Algorithm));
        return normalized.ToUpperInvariant() switch
        {
            "HS256" or "HMACSHA256" or "HMAC_SHA256" => ScopeServiceTokenAlgorithms.HmacSha256,
            "RS256" or "RSASHA256" or "RSA_SHA256" => ScopeServiceTokenAlgorithms.RsaSha256,
            _ => normalized.ToUpperInvariant(),
        };
    }

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = NormalizeOptional(value);
        if (normalized == null)
            throw new InvalidOperationException($"Scope service token {name} is required.");

        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
