using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionProviderE2EIntegrationTests
{
    [ElasticsearchIntegrationFact]
    public async Task ElasticsearchStore_ShouldRoundtripUpsertAndMutate()
    {
        var endpoint = GetRequiredEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_ENDPOINT");
        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            Endpoints = [endpoint],
            IndexPrefix = "aevatar-e2e",
            AutoCreateIndex = true,
            RequestTimeoutMs = 10000,
        };
        var indexScope = "projection-provider-e2e-" + Guid.NewGuid().ToString("N");
        using var store = new ElasticsearchProjectionDocumentStore<ProviderStoreSmokeReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: indexScope,
                Mappings: new Dictionary<string, object?>(),
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            model => model.Id);

        var readModel = new ProviderStoreSmokeReadModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Value = "v1",
            UpdatedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await store.UpsertAsync(readModel);
        var fetched = await store.GetAsync(readModel.Id);
        fetched.Should().NotBeNull();
        fetched!.Value.Should().Be("v1");

        await store.MutateAsync(readModel.Id, model =>
        {
            model.Value = "v2";
            model.UpdatedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        });

        var mutated = await store.GetAsync(readModel.Id);
        mutated.Should().NotBeNull();
        mutated!.Value.Should().Be("v2");
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();

        throw new InvalidOperationException($"Environment variable '{name}' is required.");
    }

    private sealed class ProviderStoreSmokeReadModel
    {
        public string Id { get; set; } = "";

        public string Value { get; set; } = "";

        public long UpdatedAtEpochMs { get; set; }
    }
}
