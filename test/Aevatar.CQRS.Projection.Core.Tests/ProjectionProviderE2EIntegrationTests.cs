using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
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
            StateVersion = 1,
            LastEventId = $"event-{id}-1",
            Value = "v1",
            UpdatedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var initialWrite = await store.UpsertAsync(readModel);
        initialWrite.Disposition.Should().Be(ProjectionWriteDisposition.Applied);
        var fetched = await store.GetAsync(readModel.Id);
        fetched.Should().NotBeNull();
        fetched!.Value.Should().Be("v1");

        readModel.StateVersion = 2;
        readModel.LastEventId = $"event-{id}-2";
        readModel.Value = "v2";
        readModel.UpdatedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var overwriteWrite = await store.UpsertAsync(readModel);
        overwriteWrite.Disposition.Should().Be(ProjectionWriteDisposition.Applied);

        var mutated = await store.GetAsync(readModel.Id);
        mutated.Should().NotBeNull();
        mutated!.Value.Should().Be("v2");
    }

    [ElasticsearchIntegrationFact]
    public async Task ElasticsearchStore_WhenAliasPointsAtOldFingerprintPhysical_ShouldReindexAndSwapAlias()
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

            var oldDocumentId = Guid.NewGuid().ToString("N");
            await PutJsonAsync(
                httpClient,
                $"{oldPhysical}/_doc/{oldDocumentId}?refresh=true",
                new
                {
                    id = oldDocumentId,
                    actor_id = "actor-old",
                    value = "old-value",
                    updated_at_epoch_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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

            await ((IProjectionIndexReconcileTarget)store).ReconcileIndexAsync();

            await store.UpsertAsync(readModel);

            var fetchedOld = await store.GetAsync(oldDocumentId);
            fetchedOld.Should().NotBeNull();
            fetchedOld!.Value.Should().Be("old-value");

            var fetchedNew = await store.GetAsync(readModel.Id);
            fetchedNew.Should().NotBeNull();
            fetchedNew!.Value.Should().Be("dynamic-mapping-drift");

            var aliasTarget = await GetSingleAliasTargetAsync(httpClient, aliasName);
            aliasTarget.Should().NotBe(oldPhysical);
            aliasTarget.Should().StartWith($"{aliasName}-v");
        }
        finally
        {
            await DeleteAliasTargetIfExistsAsync(httpClient, aliasName);
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

    private static async Task<string?> GetSingleAliasTargetAsync(HttpClient httpClient, string aliasName)
    {
        using var response = await httpClient.GetAsync($"_alias/{aliasName}");
        await EnsureSuccessAsync(response, $"GET _alias/{aliasName}");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateObject().Single().Name;
    }

    private static async Task DeleteAliasTargetIfExistsAsync(HttpClient httpClient, string aliasName)
    {
        using var response = await httpClient.GetAsync($"_alias/{aliasName}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;

        await EnsureSuccessAsync(response, $"GET _alias/{aliasName}");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        foreach (var target in document.RootElement.EnumerateObject().Select(item => item.Name).ToArray())
        {
            await DeleteIfExistsAsync(httpClient, target);
        }
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
