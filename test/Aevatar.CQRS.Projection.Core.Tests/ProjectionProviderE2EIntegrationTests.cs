using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionProviderE2EIntegrationTests
{
    [ElasticsearchIntegrationFact]
    public async Task ElasticsearchStore_ShouldRoundtripUpsertAndMutate()
    {
        var endpoint = GetRequiredEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_ENDPOINT");
        var options = new ElasticsearchProjectionReadModelStoreOptions
        {
            Endpoints = [endpoint],
            IndexPrefix = "aevatar-e2e",
            AutoCreateIndex = true,
            RequestTimeoutMs = 10000,
        };
        var indexScope = "projection-provider-e2e-" + Guid.NewGuid().ToString("N");
        using var store = new ElasticsearchProjectionReadModelStore<ProviderStoreSmokeReadModel, string>(
            options,
            indexScope,
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

    [Neo4jIntegrationFact]
    public async Task Neo4jStore_ShouldRoundtripUpsertMutateAndList()
    {
        var uri = GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_URI");
        var username = GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_USERNAME");
        var password = GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_PASSWORD");
        var options = new Neo4jProjectionReadModelStoreOptions
        {
            Uri = uri,
            Username = username,
            Password = password,
            NodeLabel = "ProjectionReadModelE2E",
            AutoCreateConstraints = true,
            RequestTimeoutMs = 5000,
        };
        var scope = "projection-provider-e2e-" + Guid.NewGuid().ToString("N");
        await using var store = new Neo4jProjectionReadModelStore<ProviderStoreSmokeReadModel, string>(
            options,
            scope,
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

        var listed = await store.ListAsync(20);
        listed.Select(model => model.Id).Should().Contain(readModel.Id);
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
