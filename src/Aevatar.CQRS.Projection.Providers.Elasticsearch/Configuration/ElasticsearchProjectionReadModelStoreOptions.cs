namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;

public sealed class ElasticsearchProjectionReadModelStoreOptions
{
    public List<string> Endpoints { get; set; } = [];

    public string IndexPrefix { get; set; } = "aevatar";

    public int RequestTimeoutMs { get; set; } = 10000;

    public int ListTakeMax { get; set; } = 200;

    public bool AutoCreateIndex { get; set; } = true;

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string ListSortField { get; set; } = "";
}
