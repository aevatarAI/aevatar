namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;

public sealed class ElasticsearchProjectionDocumentStoreOptions
{
    public List<string> Endpoints { get; set; } = [];

    public string IndexPrefix { get; set; } = "aevatar";

    public int RequestTimeoutMs { get; set; } = 10000;

    public int QueryTakeMax { get; set; } = 200;

    public bool AutoCreateIndex { get; set; } = true;

    public ElasticsearchMissingIndexBehavior MissingIndexBehavior { get; set; } = ElasticsearchMissingIndexBehavior.Throw;

    public int MutateMaxRetryCount { get; set; } = 3;

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string DefaultSortField { get; set; } = "";
}
