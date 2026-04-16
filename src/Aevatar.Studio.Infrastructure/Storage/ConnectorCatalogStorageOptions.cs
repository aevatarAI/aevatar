namespace Aevatar.Studio.Infrastructure.Storage;

public sealed class ConnectorCatalogStorageOptions
{
    public const string SectionName = "Cli:App:Connectors:ChronoStorage";

    public bool Enabled { get; set; } = true;

    public bool UseNyxProxy { get; set; } = true;

    public string NyxProxyBaseUrl { get; set; } = string.Empty;

    public string NyxProxyServiceSlug { get; set; } = "chrono-storage-service";

    public string BaseUrl { get; set; } = string.Empty;

    public string Bucket { get; set; } = "chrono-platform-aevatar-studio";

    /// <summary>
    /// Generic chrono-storage blob prefix used by the Explorer as a fallback
    /// when serving legacy workflow/script/media objects that were uploaded
    /// under <c>Cli:App:Connectors:ChronoStorage:Prefix</c> before the actor-backed
    /// catalogs landed. Do not treat this as the current write target for any
    /// catalog — role and connector catalogs are actor-backed and never use
    /// this prefix. Leave empty unless you are upgrading an environment that
    /// configured this value.
    /// </summary>
    public string Prefix { get; set; } = string.Empty;

    public string UserConfigPrefix { get; set; } = string.Empty;

    public int PresignedUrlExpiresInSeconds { get; set; } = 300;

    public string? StaticBearerToken { get; set; }
}
