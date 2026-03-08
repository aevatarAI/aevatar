namespace Aevatar.App.Application.Services;

public sealed class ToltOptions
{
    public const string SectionName = "Tolt";

    public string BaseUrl { get; set; } = "https://api.tolt.com";
    public string ApiKey { get; set; } = string.Empty;
}
