namespace Aevatar.Configuration.BackendConsole;

public sealed class BackendConsoleOptions
{
    public const string SectionName = "Aevatar:BackendConsole";

    public string OidcAuthority { get; set; } = string.Empty;

    public string OidcClientId { get; set; } = string.Empty;

    public string OidcScope { get; set; } = string.Empty;

    public string[] OidcResources { get; set; } = [];

    public string NyxApiBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Origin of the NyxID web frontend (the surface NyxID builds managementUrl
    /// links from). Distinct from <see cref="OidcAuthority"/>, which may point at
    /// the NyxID API/issuer host in split-host deployments.
    /// </summary>
    public string NyxWebBaseUrl { get; set; } = string.Empty;

    public string StorageKey { get; set; } = string.Empty;

    public string DefaultReturnPath { get; set; } = string.Empty;
}
