namespace Aevatar.Configuration.BackendConsole;

public sealed class BackendConsoleOptions
{
    public const string SectionName = "Aevatar:BackendConsole";

    public string OidcAuthority { get; set; } = string.Empty;

    public string OidcClientId { get; set; } = string.Empty;

    public string OidcScope { get; set; } = string.Empty;

    public string[] OidcResources { get; set; } = [];

    public string NyxApiBaseUrl { get; set; } = string.Empty;

    public string StorageKey { get; set; } = string.Empty;

    public string DefaultReturnPath { get; set; } = string.Empty;
}
