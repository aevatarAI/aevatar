namespace Aevatar.Studio.Hosting;

public sealed class StudioHostingOptions
{
    public const string SectionName = "Studio:Hosting";

    /// <summary>
    /// Allows local debugging without Studio authentication by honoring an explicit
    /// <c>scopeId</c> query parameter for scoped draft reads.
    /// Scoped mutations still require authenticated Studio scope.
    /// Keep disabled outside local development.
    /// </summary>
    public bool AllowUnauthenticatedScopeQueryFallback { get; set; }
}
