namespace Aevatar.Authentication.ScopeServiceTokens;

public sealed class ScopeServiceTokenOptions
{
    public const string SectionName = "Aevatar:Authentication:ScopeServiceTokens";

    public bool Enabled { get; set; }

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public int TokenLifetimeMinutes { get; set; } = 60;

    public int ClockSkewSeconds { get; set; } = 60;

    public ScopeServiceTokenSigningKeyOptions[] SigningKeys { get; set; } = [];

    public ScopeServiceTokenSigningKeyOptions? SigningKey { get; set; }
}

public sealed class ScopeServiceTokenSigningKeyOptions
{
    public string Kid { get; set; } = string.Empty;

    public string Algorithm { get; set; } = ScopeServiceTokenAlgorithms.HmacSha256;

    public string Key { get; set; } = string.Empty;

    public string KeyBase64 { get; set; } = string.Empty;

    public string Pem { get; set; } = string.Empty;

    public string PemPath { get; set; } = string.Empty;

    public bool Current { get; set; }
}

public static class ScopeServiceTokenAlgorithms
{
    public const string HmacSha256 = "HS256";
    public const string RsaSha256 = "RS256";
}
