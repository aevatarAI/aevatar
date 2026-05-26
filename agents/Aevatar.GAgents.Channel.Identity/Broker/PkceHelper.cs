using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// RFC 7636 PKCE pair generator. <see cref="GeneratePair"/> returns a fresh
/// 128-bit verifier (URL-safe base64) plus the matching S256 challenge.
/// </summary>
internal static class PkceHelper
{
    public sealed record PkcePair(string CodeVerifier, string CodeChallenge);

    /// <summary>
    /// Generates a 32-byte random verifier (RFC 7636 §4.1) and the S256
    /// challenge derived from it (RFC 7636 §4.2). Verifier travels inside the
    /// HMAC-sealed <c>state</c> token; challenge goes on the wire to NyxID.
    /// </summary>
    public static PkcePair GeneratePair()
    {
        Span<byte> verifierBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(verifierBytes);
        var verifier = Base64UrlEncode(verifierBytes);

        Span<byte> challengeHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.ASCII.GetBytes(verifier), challengeHash);
        var challenge = Base64UrlEncode(challengeHash);

        return new PkcePair(verifier, challenge);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
