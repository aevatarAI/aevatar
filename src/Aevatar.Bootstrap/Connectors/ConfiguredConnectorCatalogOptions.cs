namespace Aevatar.Bootstrap.Connectors;

public sealed class ConfiguredConnectorCatalogOptions
{
    public bool IncludeDefaultHomeConfig { get; set; } = true;

    public IList<string> AdditionalConfigPaths { get; } = [];
}
