namespace Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;

public sealed class Neo4jProjectionReadModelStoreOptions
{
    public string Uri { get; set; } = "bolt://localhost:7687";

    public string Username { get; set; } = "neo4j";

    public string Password { get; set; } = "";

    public string Database { get; set; } = "";

    public int RequestTimeoutMs { get; set; } = 5000;

    public int ListTakeMax { get; set; } = 200;

    public bool AutoCreateConstraints { get; set; } = true;

    public string NodeLabel { get; set; } = "ProjectionReadModel";
}
