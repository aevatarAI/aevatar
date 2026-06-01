namespace Aevatar.Studio.Hosting.NyxId;

internal sealed class NyxIdLlmCatalogCacheOptions
{
    public const string SectionName = "Aevatar:Studio:NyxIdLlmCatalogCache";

    public bool Enabled { get; set; } = true;

    public TimeSpan FreshTtl { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan StaleTtl { get; set; } = TimeSpan.FromMinutes(5);

    public int MaxEntries { get; set; } = 1024;
}
