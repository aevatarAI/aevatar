namespace Aevatar.Tools.Cli.Studio.Infrastructure.Storage;

internal sealed class ConnectorCatalogStorageOptions
{
    public const string SectionName = "Cli:App:Connectors:ChronoStorage";

    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;

    public string Prefix { get; set; } = "aevatar/connectors/v1";

    public string MasterKey { get; set; } = string.Empty;

    public int PresignedUrlExpiresInSeconds { get; set; } = 300;

    public bool CreateBucketIfMissing { get; set; } = true;

    public string? StaticBearerToken { get; set; }
}
