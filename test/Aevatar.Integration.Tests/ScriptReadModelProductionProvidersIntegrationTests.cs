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
            semanticDocument.GetProperty("id").GetString().Should().Be(runtimeActorId);
            semanticDocument.GetProperty("script_id").GetString().Should().Be("claim_orchestrator");
            semanticDocument.GetProperty("revision").GetString().Should().Be(revision);

            var nativeDocument = await FindSingleElasticsearchDocumentAsync(
                client,
                $"{indexPrefix}-script-native-*",
                runtimeActorId,
                CancellationToken.None);
            nativeDocument.GetProperty("schema_id").GetString().Should().Be("claim_case");
            nativeDocument.GetProperty("fields").GetProperty("case_id").GetString().Should().Be("Case-ES");
            nativeDocument.GetProperty("fields").GetProperty("policy_id").GetString().Should().Be("POLICY-ES");
            nativeDocument.GetProperty("fields").GetProperty("search").GetProperty("lookup_key").GetString()
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

        var graphStore = provider.GetRequiredService<IProjectionGraphStore>();
        var subgraph = await graphStore.GetSubgraphAsync(new ProjectionGraphQuery
        {
            Scope = "script-native-claim_case",
            RootNodeId = $"script:claim_case:{runtimeActorId}",
            Depth = 1,
            Take = 20,
        }, CancellationToken.None);

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
            var snapshot = await queryService.GetSnapshotAsync(runtimeActorId, CancellationToken.None);
            snapshot.Should().NotBeNull();
            snapshot!.ReadModelPayload.Should().NotBeNull();
            snapshot.ReadModelPayload!.Unpack<ClaimCaseReadModel>().CaseId.Should().Be("Case-PROD");

            var nativeDocument = await FindSingleElasticsearchDocumentAsync(
                client,
                $"{indexPrefix}-script-native-*",
                runtimeActorId,
                CancellationToken.None);
            nativeDocument.GetProperty("fields").GetProperty("policy_id").GetString().Should().Be("POLICY-PROD");

            var graphStore = provider.GetRequiredService<IProjectionGraphStore>();
            var subgraph = await graphStore.GetSubgraphAsync(new ProjectionGraphQuery
            {
                Scope = "script-native-claim_case",
                RootNodeId = $"script:claim_case:{runtimeActorId}",
                Depth = 1,
                Take = 20,
            }, CancellationToken.None);
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
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{indexPattern}/_search")
        {
            Content = new StringContent(
                $$"""
                  {
                    "size": 1,
                    "query": {
                      "term": {
                        "id": {
                          "value": "{{documentId}}"
                        }
                      }
                    }
                  }
                  """,
                Encoding.UTF8,
                "application/json"),
        };
        using var response = await client.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        response.IsSuccessStatusCode.Should().BeTrue($"Elasticsearch search failed. body={payload}");

        using var document = JsonDocument.Parse(payload);
        var hits = document.RootElement
            .GetProperty("hits")
            .GetProperty("hits");
        hits.GetArrayLength().Should().BeGreaterThan(0, $"Expected Elasticsearch document `{documentId}` in `{indexPattern}`.");
        return hits[0].GetProperty("_source").Clone();
    }

    private static async Task DeleteElasticsearchIndicesAsync(
        HttpClient client,
        string indexPattern,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, indexPattern);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;

        var payload = await response.Content.ReadAsStringAsync(ct);
        response.IsSuccessStatusCode.Should().BeTrue($"Elasticsearch index cleanup failed. body={payload}");
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
                Timeout = TimeSpan.FromSeconds(30),
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
