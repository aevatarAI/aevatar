using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevatar.Tools.MockNyxId;

/// <summary>
/// Minimal JWT helper for mock testing. Issues HS256 tokens and loosely validates them.
/// NOT for production use — no real cryptographic verification on the validation side.
/// </summary>
public sealed class MockJwtHelper
{
    private readonly byte[] _keyBytes;

    public MockJwtHelper(MockNyxIdOptions options)
    {
        _keyBytes = Encoding.UTF8.GetBytes(options.JwtSigningKey);
    }

    /// <summary>Generate a test JWT with the given subject (user ID) and optional scope.</summary>
    public string GenerateToken(string userId, string? scope = null)
    {
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payloadObj = new Dictionary<string, object>
        {
            ["sub"] = userId,
            ["iat"] = now,
            ["exp"] = now + 86400, // 24 hours
        };
        if (!string.IsNullOrWhiteSpace(scope))
            payloadObj["scope"] = scope;

        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payloadObj));
        var signature = Base64UrlEncode(HMACSHA256.HashData(_keyBytes, Encoding.UTF8.GetBytes($"{header}.{payload}")));

        return $"{header}.{payload}.{signature}";
    }

    /// <summary>
    /// Loosely extract the 'sub' claim from a JWT without verifying the signature.
    /// Returns null if the token is malformed.
    /// </summary>
    public static string? TryExtractSubject(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;

            var payloadBase64 = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payloadBase64.Length % 4)
            {
                case 2: payloadBase64 += "=="; break;
                case 3: payloadBase64 += "="; break;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payloadBase64));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("sub", out var sub) ? sub.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
