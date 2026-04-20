namespace Aevatar.Studio.Hosting;

public sealed class StudioHostingOptions
{
    public const string SectionName = "Studio:Hosting";

    public bool AllowUnauthenticatedScopeQueryFallback { get; set; }
}
