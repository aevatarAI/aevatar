using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using FluentAssertions;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Aevatar.CQRS.Projection.Core.Tests;

[Trait("Category", "ProviderIntegration")]
[Trait("Feature", "ProjectionProviders")]
public sealed class ProjectionProviderE2EIntegrationTests
{
    private const int ElasticsearchRequestTimeoutMs = 30000;

    [ElasticsearchIntegrationFact]
    public async Task ElasticsearchStore_ShouldRoundtripUpsertAndOverwrite()
    {
        var endpoint = GetRequiredEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_ENDPOINT");
        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            Endpoints = [endpoint],
            IndexPrefix = "aevatar-e2e",
            AutoCreateIndex = true,
            RequestTimeoutMs = ElasticsearchRequestTimeoutMs,
        };
        var indexScope = "projection-provider-e2e-" + Guid.NewGuid().ToString("N");
        using var store = new ElasticsearchProjectionDocumentStore<TestProviderStoreSmokeReadModel, string>(
            options,
            new DocumentIndexMetadata(
                IndexName: indexScope,
                Mappings: new Dictionary<string, object?>(),
                Settings: new Dictionary<string, object?>(),
                Aliases: new Dictionary<string, object?>()),
            model => model.Id);

        var id = Guid.NewGuid().ToString("N");
        var readModel = new TestProviderStoreSmokeReadModel
        {
            Id = id,
            ActorId = id,
            Value = "v1",
            UpdatedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await store.UpsertAsync(readModel);
        var fetched = await store.GetAsync(readModel.Id);
        fetched.Should().NotBeNull();
        fetched!.Value.Should().Be("v1");

        readModel.Value = "v2";
        readModel.UpdatedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await store.UpsertAsync(readModel);

        var mutated = await store.GetAsync(readModel.Id);
        mutated.Should().NotBeNull();
        mutated!.Value.Should().Be("v2");
    }

    [ElasticsearchIntegrationFact]
    public async Task ElasticsearchStore_WhenAliasPointsAtDynamicMappingPhysical_ShouldFailLoud()
    {
        var endpoint = GetRequiredEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_ENDPOINT");
        var indexScope = "projection-provider-drift-" + Guid.NewGuid().ToString("N");
        var aliasName = $"aevatar-e2e-{indexScope}";
        var oldPhysical = $"{aliasName}-v00000000";

        using var httpClient = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };
        try
        {
            await PutJsonAsync(
                httpClient,
                oldPhysical,
                new
                {
                    mappings = new
                    {
                        dynamic = true,
                        properties = new
                        {
                            value = new { type = "text" },
                        },
                    },
                });
            await PostJsonAsync(
                httpClient,
                "_aliases",
                new
                {
                    actions = new object[]
                    {
                        new { add = new { index = oldPhysical, alias = aliasName } },
                    },
                });

            var options = new ElasticsearchProjectionDocumentStoreOptions
            {
                Endpoints = [endpoint],
                IndexPrefix = "aevatar-e2e",
                AutoCreateIndex = true,
                RequestTimeoutMs = ElasticsearchRequestTimeoutMs,
            };
            using var store = new ElasticsearchProjectionDocumentStore<TestProviderStoreSmokeReadModel, string>(
                options,
                new DocumentIndexMetadata(
                    IndexName: indexScope,
                    Mappings: new Dictionary<string, object?>(),
                    Settings: new Dictionary<string, object?>(),
                    Aliases: new Dictionary<string, object?>()),
                model => model.Id);

            var readModel = new TestProviderStoreSmokeReadModel
            {
                Id = Guid.NewGuid().ToString("N"),
                ActorId = "actor-drift",
                Value = "dynamic-mapping-drift",
                UpdatedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            // Refactor (iter98/cluster-743): Old pattern: a drifted live ES mapping
            // could be masked by query-time actual mapping reads or lifecycle repair.
            // New principle: alias+fingerprint lifecycle is the truth source and
            // provider writes fail before dynamic-mapping drift silently breaks queries.
            var act = () => store.UpsertAsync(readModel);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*schema drift detected*");
        }
        finally
        {
            await DeleteIfExistsAsync(httpClient, oldPhysical);
        }
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();

        throw new InvalidOperationException($"Environment variable '{name}' is required.");
    }

    private static async Task PutJsonAsync(HttpClient httpClient, string path, object payload)
    {
        using var response = await httpClient.PutAsJsonAsync(path, payload);
        await EnsureSuccessAsync(response, $"PUT {path}");
    }

    private static async Task PostJsonAsync(HttpClient httpClient, string path, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var response = await httpClient.PostAsync(
            path,
            new StringContent(json, Encoding.UTF8, "application/json"));
        await EnsureSuccessAsync(response, $"POST {path}");
    }

    private static async Task DeleteIfExistsAsync(HttpClient httpClient, string path)
    {
        using var response = await httpClient.DeleteAsync(path);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, $"DELETE {path}");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(
            $"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}. body={body}");
    }

}
