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

    public string UserConfigPrefix { get; set; } = string.Empty;

    public int PresignedUrlExpiresInSeconds { get; set; } = 300;

    public string? StaticBearerToken { get; set; }
}
