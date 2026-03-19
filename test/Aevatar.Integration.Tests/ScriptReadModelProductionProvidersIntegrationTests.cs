using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Application.Queries;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

[Trait("Category", "ProviderIntegration")]
[Trait("Feature", "ScriptingReadModelProviders")]
public sealed class ScriptReadModelProductionProvidersIntegrationTests
{
    private static readonly TimeSpan ElasticsearchClientTimeout = TimeSpan.FromSeconds(60);
    private const int ElasticsearchRequestTimeoutMs = 30000;
    private const int Neo4jRequestTimeoutMs = 15000;

    [ElasticsearchIntegrationFact]
    public async Task RunClaimAsync_ShouldPersistSemanticAndNativeDocumentsIntoElasticsearch()
    {
        var indexPrefix = BuildIndexPrefix("script-es");
        var configuration = ProjectionProviderIntegrationTestConfiguration.Build(
            indexPrefix,
            useElasticsearchDocument: true,
            useNeo4jGraph: false,
            productionPolicy: false);

        await using var provider = ClaimIntegrationTestKit.BuildProvider(configuration);
        using var client = ProjectionProviderIntegrationTestConfiguration.CreateElasticsearchClient();
        var revision = Fixtures.ScriptDocuments.ClaimScriptScenarioDocument.CreateEmbedded()
            .Scripts
            .Single(x => x.ScriptId == "claim_orchestrator")
            .Revision;
        var definitionActorId = "claim-es-definition-" + Guid.NewGuid().ToString("N");
        var runtimeActorId = "claim-es-runtime-" + Guid.NewGuid().ToString("N");
        var runId = "claim-es-run-" + Guid.NewGuid().ToString("N");

        try
        {
            await ClaimIntegrationTestKit.UpsertOrchestratorAsync(provider, definitionActorId, CancellationToken.None);
            var result = await ClaimIntegrationTestKit.RunClaimAsync(
                provider,
                definitionActorId,
                runtimeActorId,
                revision,
                runId,
                new ClaimSubmitted
                {
                    CommandId = runId,
                    CaseId = "Case-ES",
                    PolicyId = "POLICY-ES",
                    RiskScore = 0.82d,
                    CompliancePassed = true,
                },
                CancellationToken.None);

            var snapshot = result.Snapshot;
            snapshot.ReadModelPayload.Should().NotBeNull();
            snapshot.ReadModelPayload!.Unpack<ClaimCaseReadModel>().PolicyId.Should().Be("POLICY-ES");

            var semanticDocument = await FindSingleElasticsearchDocumentAsync(
                client,
                $"{indexPrefix}-script-read-model-documents",
                runtimeActorId,
                CancellationToken.None);
            GetRequiredStringProperty(semanticDocument, "id").Should().Be(runtimeActorId);
            GetRequiredStringProperty(semanticDocument, "script_id").Should().Be("claim_orchestrator");
            GetRequiredStringProperty(semanticDocument, "revision").Should().Be(revision);

            var nativeDocument = await FindSingleElasticsearchDocumentAsync(
                client,
                $"{indexPrefix}-script-native-*",
                runtimeActorId,
                CancellationToken.None);
            nativeDocument.GetProperty("schema_id").GetString().Should().Be("claim_case");
            nativeDocument.GetProperty("fields_value").GetProperty("case_id").GetString().Should().Be("Case-ES");
            nativeDocument.GetProperty("fields_value").GetProperty("policy_id").GetString().Should().Be("POLICY-ES");
            nativeDocument.GetProperty("fields_value").GetProperty("search").GetProperty("lookup_key").GetString()
                .Should().Be("case-es:policy-es");
        }
        finally
        {
            await DeleteElasticsearchIndicesAsync(client, $"{indexPrefix}-*", CancellationToken.None);
        }
    }

    [Neo4jIntegrationFact]
    public async Task RunClaimAsync_ShouldPersistNativeGraphIntoNeo4j()
    {
        var configuration = ProjectionProviderIntegrationTestConfiguration.Build(
            indexPrefix: BuildIndexPrefix("script-neo4j"),
            useElasticsearchDocument: false,
            useNeo4jGraph: true,
            productionPolicy: false);
        await using var provider = ClaimIntegrationTestKit.BuildProvider(configuration);
        var revision = Fixtures.ScriptDocuments.ClaimScriptScenarioDocument.CreateEmbedded()
            .Scripts
            .Single(x => x.ScriptId == "claim_orchestrator")
            .Revision;
        var definitionActorId = "claim-neo-definition-" + Guid.NewGuid().ToString("N");
        var runtimeActorId = "claim-neo-runtime-" + Guid.NewGuid().ToString("N");
        var runId = "claim-neo-run-" + Guid.NewGuid().ToString("N");

        await ClaimIntegrationTestKit.UpsertOrchestratorAsync(provider, definitionActorId, CancellationToken.None);
        _ = await ClaimIntegrationTestKit.RunClaimAsync(
            provider,
            definitionActorId,
            runtimeActorId,
            revision,
            runId,
            new ClaimSubmitted
            {
                CommandId = runId,
                CaseId = "Case-N4J",
                PolicyId = "POLICY-N4J",
                RiskScore = 0.41d,
                CompliancePassed = false,
            },
            CancellationToken.None);

        var subgraph = await ScriptEvolutionIntegrationTestKit.WaitForGraphSubgraphAsync(
            provider,
            scope: "script-native-claim_case",
            rootNodeId: $"script:claim_case:{runtimeActorId}",
            isReady: graph =>
                graph.Nodes.Any(x => x.NodeId == "ref:policy:POLICY-N4J") &&
                graph.Edges.Any(x =>
                    x.FromNodeId == $"script:claim_case:{runtimeActorId}" &&
                    x.ToNodeId == "ref:policy:POLICY-N4J" &&
                    x.EdgeType == "rel_policy"),
            CancellationToken.None);

        subgraph.Nodes.Should().Contain(x => x.NodeId == $"script:claim_case:{runtimeActorId}");
        subgraph.Nodes.Should().Contain(x => x.NodeId == "ref:policy:POLICY-N4J");
        subgraph.Edges.Should().ContainSingle(x =>
            x.FromNodeId == $"script:claim_case:{runtimeActorId}" &&
            x.ToNodeId == "ref:policy:POLICY-N4J" &&
            x.EdgeType == "rel_policy");
    }

    [ProjectionProvidersIntegrationFact]
    public async Task RunClaimAsync_ShouldPersistSemanticDocumentsToElasticsearch_AndGraphToNeo4j()
    {
        var indexPrefix = BuildIndexPrefix("script-prod");
        var configuration = ProjectionProviderIntegrationTestConfiguration.Build(
            indexPrefix,
            useElasticsearchDocument: true,
            useNeo4jGraph: true,
            productionPolicy: true);

        await using var provider = ClaimIntegrationTestKit.BuildProvider(configuration);
        using var client = ProjectionProviderIntegrationTestConfiguration.CreateElasticsearchClient();
        var revision = Fixtures.ScriptDocuments.ClaimScriptScenarioDocument.CreateEmbedded()
            .Scripts
            .Single(x => x.ScriptId == "claim_orchestrator")
            .Revision;
        var definitionActorId = "claim-prod-definition-" + Guid.NewGuid().ToString("N");
        var runtimeActorId = "claim-prod-runtime-" + Guid.NewGuid().ToString("N");
        var runId = "claim-prod-run-" + Guid.NewGuid().ToString("N");

        try
        {
            await ClaimIntegrationTestKit.UpsertOrchestratorAsync(provider, definitionActorId, CancellationToken.None);
            _ = await ClaimIntegrationTestKit.RunClaimAsync(
                provider,
                definitionActorId,
                runtimeActorId,
                revision,
                runId,
                new ClaimSubmitted
                {
                    CommandId = runId,
                    CaseId = "Case-PROD",
                    PolicyId = "POLICY-PROD",
                    RiskScore = 0.93d,
                    CompliancePassed = true,
                },
                CancellationToken.None);

            var queryService = provider.GetRequiredService<IScriptReadModelQueryApplicationService>();
            var snapshot = await ScriptEvolutionIntegrationTestKit.WaitForSnapshotAsync(
                provider,
                runtimeActorId,
                CancellationToken.None);
            snapshot.Should().NotBeNull();
            snapshot!.ReadModelPayload.Should().NotBeNull();
            snapshot.ReadModelPayload!.Unpack<ClaimCaseReadModel>().CaseId.Should().Be("Case-PROD");

            var nativeDocument = await FindSingleElasticsearchDocumentAsync(
                client,
                $"{indexPrefix}-script-native-*",
                runtimeActorId,
                CancellationToken.None);
            nativeDocument.GetProperty("fields_value").GetProperty("policy_id").GetString().Should().Be("POLICY-PROD");

            var subgraph = await ScriptEvolutionIntegrationTestKit.WaitForGraphSubgraphAsync(
                provider,
                scope: "script-native-claim_case",
                rootNodeId: $"script:claim_case:{runtimeActorId}",
                isReady: graph =>
                    graph.Edges.Any(x =>
                        x.FromNodeId == $"script:claim_case:{runtimeActorId}" &&
                        x.ToNodeId == "ref:policy:POLICY-PROD" &&
                        x.EdgeType == "rel_policy"),
                CancellationToken.None);
            subgraph.Edges.Should().ContainSingle(x =>
                x.FromNodeId == $"script:claim_case:{runtimeActorId}" &&
                x.ToNodeId == "ref:policy:POLICY-PROD" &&
                x.EdgeType == "rel_policy");
        }
        finally
        {
            await DeleteElasticsearchIndicesAsync(client, $"{indexPrefix}-*", CancellationToken.None);
        }
    }

    private static string BuildIndexPrefix(string scenario)
    {
        var prefix = $"aevatar-{scenario}-{Guid.NewGuid():N}";
        return prefix.Length <= 48 ? prefix : prefix[..48];
    }

    private static async Task<JsonElement> FindSingleElasticsearchDocumentAsync(
        HttpClient client,
        string indexPattern,
        string documentId,
        CancellationToken ct)
    {
        var document = await ScriptEvolutionIntegrationTestKit.WaitForAsync(
            token => TryFindSingleElasticsearchDocumentAsync(client, indexPattern, documentId, token),
            static candidate => candidate.HasValue,
            $"Expected Elasticsearch document `{documentId}` in `{indexPattern}`, but no matching document was found.",
            ct);
        return document.Value;
    }

    private static async Task<JsonElement?> TryFindSingleElasticsearchDocumentAsync(
        HttpClient client,
        string indexPattern,
        string documentId,
        CancellationToken ct)
    {
        var indices = indexPattern.Contains('*', StringComparison.Ordinal)
            ? await ResolveElasticsearchIndicesAsync(client, indexPattern, ct)
            : new[] { indexPattern };
        foreach (var indexName in indices)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{indexName}/_doc/{Uri.EscapeDataString(documentId)}");
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                continue;

            var payload = await response.Content.ReadAsStringAsync(ct);
            response.IsSuccessStatusCode.Should().BeTrue($"Elasticsearch document lookup failed. body={payload}");

            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("found", out var foundNode) || !foundNode.GetBoolean())
                continue;

            return document.RootElement.GetProperty("_source").Clone();
        }

        return null;
    }

    private static async Task DeleteElasticsearchIndicesAsync(
        HttpClient client,
        string indexPattern,
        CancellationToken ct)
    {
        var indices = await ResolveElasticsearchIndicesAsync(client, indexPattern, ct);
        if (indices.Count == 0)
            return;

        using var request = new HttpRequestMessage(HttpMethod.Delete, string.Join(',', indices));
        using var response = await client.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        response.IsSuccessStatusCode.Should().BeTrue($"Elasticsearch index cleanup failed. body={payload}");
    }

    private static async Task<IReadOnlyList<string>> ResolveElasticsearchIndicesAsync(
        HttpClient client,
        string indexPattern,
        CancellationToken ct)
    {
        var encodedPattern = Uri.EscapeDataString(indexPattern);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"_cat/indices/{encodedPattern}?format=json&h=index");
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return Array.Empty<string>();

        var payload = await response.Content.ReadAsStringAsync(ct);
        response.IsSuccessStatusCode.Should().BeTrue($"Elasticsearch index resolution failed. body={payload}");

        using var document = JsonDocument.Parse(payload);
        var indices = new List<string>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("index", out var indexElement))
                continue;

            var index = indexElement.GetString();
            if (!string.IsNullOrWhiteSpace(index))
                indices.Add(index);
        }

        return indices;
    }

    private static string? GetRequiredStringProperty(
        JsonElement source,
        string propertyName)
    {
        if (source.TryGetProperty(propertyName, out var property))
            return property.GetString();

        throw new KeyNotFoundException($"Property `{propertyName}` was not found in Elasticsearch document source.");
    }

    private static class ProjectionProviderIntegrationTestConfiguration
    {
        public static IConfiguration Build(
            string indexPrefix,
            bool useElasticsearchDocument,
            bool useNeo4jGraph,
            bool productionPolicy)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = useElasticsearchDocument.ToString(),
                ["Projection:Document:Providers:InMemory:Enabled"] = (!useElasticsearchDocument).ToString(),
                ["Projection:Graph:Providers:Neo4j:Enabled"] = useNeo4jGraph.ToString(),
                ["Projection:Graph:Providers:InMemory:Enabled"] = (!useNeo4jGraph).ToString(),
            };

            if (productionPolicy)
            {
                values["Projection:Policies:Environment"] = "Production";
            }

            if (useElasticsearchDocument)
            {
                values["Projection:Document:Providers:Elasticsearch:Endpoints:0"] =
                    GetRequiredEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_ENDPOINT");
                values["Projection:Document:Providers:Elasticsearch:IndexPrefix"] = indexPrefix;
                values["Projection:Document:Providers:Elasticsearch:AutoCreateIndex"] = "true";
                values["Projection:Document:Providers:Elasticsearch:RequestTimeoutMs"] =
                    ElasticsearchRequestTimeoutMs.ToString();

                var username = Environment.GetEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_USERNAME");
                var password = Environment.GetEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_PASSWORD");
                if (!string.IsNullOrWhiteSpace(username))
                    values["Projection:Document:Providers:Elasticsearch:Username"] = username.Trim();
                if (!string.IsNullOrWhiteSpace(password))
                    values["Projection:Document:Providers:Elasticsearch:Password"] = password;
            }

            if (useNeo4jGraph)
            {
                values["Projection:Graph:Providers:Neo4j:Uri"] =
                    GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_URI");
                values["Projection:Graph:Providers:Neo4j:Username"] =
                    GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_USERNAME");
                values["Projection:Graph:Providers:Neo4j:Password"] =
                    GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_PASSWORD");
                values["Projection:Graph:Providers:Neo4j:RequestTimeoutMs"] =
                    Neo4jRequestTimeoutMs.ToString();

                var database = Environment.GetEnvironmentVariable("AEVATAR_TEST_NEO4J_DATABASE");
                if (!string.IsNullOrWhiteSpace(database))
                    values["Projection:Graph:Providers:Neo4j:Database"] = database.Trim();
            }

            return new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
        }

        public static HttpClient CreateElasticsearchClient()
        {
            var endpoint = ResolveElasticsearchEndpoint(GetRequiredEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_ENDPOINT"));
            var client = new HttpClient
            {
                BaseAddress = endpoint,
                Timeout = ElasticsearchClientTimeout,
            };
            var username = Environment.GetEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_USERNAME");
            if (!string.IsNullOrWhiteSpace(username))
            {
                var raw = $"{username.Trim()}:{Environment.GetEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_PASSWORD") ?? string.Empty}";
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            }

            return client;
        }

        private static Uri ResolveElasticsearchEndpoint(string rawEndpoint)
        {
            var endpoint = rawEndpoint.Trim();
            if (!endpoint.Contains("://", StringComparison.Ordinal))
                endpoint = "http://" + endpoint;
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                return uri;

            throw new InvalidOperationException($"Invalid Elasticsearch endpoint '{rawEndpoint}'.");
        }

        private static string GetRequiredEnvironmentVariable(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();

            throw new InvalidOperationException($"Environment variable '{name}' is required.");
        }
    }
}
