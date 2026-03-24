namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ConnectorCatalogStorageOptions
{
    public const string SectionName = "Cli:App:Connectors:ChronoStorage";

    public bool Enabled { get; set; } = true;

    public bool UseNyxProxy { get; set; } = true;

    public string NyxProxyBaseUrl { get; set; } = string.Empty;

    public string NyxProxyServiceSlug { get; set; } = "chrono-storage-service";

    public string BaseUrl { get; set; } = string.Empty;

    public string Bucket { get; set; } = "studio-catalogs";

    public string Prefix { get; set; } = "aevatar/connectors/v1";

    public string RolesPrefix { get; set; } = "aevatar/roles/v1";

    public string MasterKey { get; set; } = string.Empty;

    public int PresignedUrlExpiresInSeconds { get; set; } = 300;

    public bool CreateBucketIfMissing { get; set; }

    public string? StaticBearerToken { get; set; }
}
