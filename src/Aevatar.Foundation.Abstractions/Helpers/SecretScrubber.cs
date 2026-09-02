// ─────────────────────────────────────────────────────────────
// SecretScrubber — masks secret-shaped substrings before text
// crosses a persistence or logging boundary.
// ─────────────────────────────────────────────────────────────

using System.Text.RegularExpressions;

namespace Aevatar.Foundation.Abstractions.Helpers;

/// <summary>
/// Masks secret-shaped substrings inside free-form text (tool arguments/results, model
/// content, upstream HTTP error bodies) before it is persisted into a read model or written
/// to a log. This is a defense-in-depth boundary scrubber, not a parser: it errs toward
/// masking anything that looks like a credential rather than toward preserving readability.
///
/// Masks, in order:
///   1. JWT / bearer-token shapes (<c>eyJ…​.…​.…</c>, the base64url header.payload.signature form);
///   2. <c>Authorization: Bearer &lt;token&gt;</c> / <c>Authorization: Basic &lt;token&gt;</c> header lines
///      (keeps the scheme, masks the credential);
///   3. common secret-bearing JSON string values — <c>"password" | "secret" | "api_key" |
///      "apikey" | "client_secret" | "token" | "access_token" | "refresh_token" |
///      "signing_key" | "private_key"</c> — masking the VALUE while keeping the key;
///   4. remaining long high-entropy tokens (long unbroken base64url/hex runs).
///
/// Callers are still responsible for length-truncation; masking runs first so a secret split
/// across the truncation boundary is masked before it is cut.
/// </summary>
public static class SecretScrubber
{
    /// <summary>
    /// Marker substituted in place of a masked secret. Distinctive so it is greppable in logs
    /// and read models and never collides with legitimate content.
    /// </summary>
    public const string Marker = "***REDACTED***";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // JWT / bearer shape: base64url header.payload.signature. The `eyJ` prefix is the base64url
    // encoding of the opening `{"` of a JWT JSON header, so it anchors real tokens tightly.
    private static readonly Regex JwtRegex = new(
        @"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    // Authorization header line: "Authorization: Bearer <token>" / "Authorization: Basic <token>".
    // Keep the header name + scheme, mask the credential that follows.
    private static readonly Regex AuthorizationHeaderRegex = new(
        @"(?<prefix>Authorization\s*[:=]\s*(?:Bearer|Basic|Token)\s+)(?<secret>[^\s""']+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    // Secret-bearing JSON string values. Keeps the key literal, masks the quoted value.
    // Matches: "access_token" : "<value>"  (whitespace/casing tolerant).
    private static readonly Regex JsonSecretValueRegex = new(
        @"(?<key>""(?:password|secret|api[_-]?key|apikey|client_secret|token|access_token|refresh_token|signing_key|private_key)""\s*:\s*)""(?<secret>(?:\\.|[^""\\])*)""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    // Long high-entropy token: an unbroken run of base64url/hex-ish characters of a length that
    // no legitimate identifier reaches. Deliberately conservative (>= 40) so ordinary words,
    // hashes-in-prose, and short ids survive.
    private static readonly Regex LongTokenRegex = new(
        @"(?<![A-Za-z0-9_\-])[A-Za-z0-9_\-]{40,}(?![A-Za-z0-9_\-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    /// <summary>
    /// Returns <paramref name="value"/> with secret-shaped substrings replaced by
    /// <see cref="Marker"/>. Returns the input unchanged when it is null/empty or contains no
    /// secret-shaped content. On the (pathological) regex-timeout path, fails closed by returning
    /// <see cref="Marker"/> rather than risking an un-scrubbed leak.
    /// </summary>
    public static string Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        try
        {
            var scrubbed = JwtRegex.Replace(value, Marker);
            scrubbed = AuthorizationHeaderRegex.Replace(scrubbed, m => m.Groups["prefix"].Value + Marker);
            scrubbed = JsonSecretValueRegex.Replace(scrubbed, m => m.Groups["key"].Value + "\"" + Marker + "\"");
            scrubbed = LongTokenRegex.Replace(scrubbed, Marker);
            return scrubbed;
        }
        catch (RegexMatchTimeoutException)
        {
            // Adversarial / pathological input defeated a matcher; fail closed rather than leak.
            return Marker;
        }
    }
}
