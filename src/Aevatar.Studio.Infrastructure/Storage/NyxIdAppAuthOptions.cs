namespace Aevatar.Studio.Infrastructure.Storage;

/// <summary>
/// Minimal NyxID auth options needed by ChronoStorage client to resolve NyxProxy base URL.
/// </summary>
public sealed class NyxIdAppAuthOptions
{
    public const string SectionName = "Cli:App:NyxId";

    public bool? Enabled { get; set; }
    public string Authority { get; set; } = "https://nyx-api.chrono-ai.fun";
    public bool RequireHttpsMetadata { get; set; } = true;
}
