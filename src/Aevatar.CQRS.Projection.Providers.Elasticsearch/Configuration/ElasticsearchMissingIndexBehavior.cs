namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;

public enum ElasticsearchMissingIndexBehavior
{
    Throw = 0,
    WarnAndReturnEmpty = 1,
}
