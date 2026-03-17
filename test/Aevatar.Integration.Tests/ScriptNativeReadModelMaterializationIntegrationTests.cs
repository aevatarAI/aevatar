using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public sealed class ScriptNativeReadModelMaterializationIntegrationTests
{
    [Fact]
    public async Task RunClaimAsync_ShouldMaterializeNativeDocumentAndGraphReadModels()
    {
        await using var provider = ClaimIntegrationTestKit.BuildProvider();
        const string definitionActorId = "claim-native-definition";
        const string runtimeActorId = "claim-native-runtime";
        var revision = Fixtures.ScriptDocuments.ClaimScriptScenarioDocument.CreateEmbedded()
            .Scripts
            .Single(x => x.ScriptId == "claim_orchestrator")
            .Revision;

        await ClaimIntegrationTestKit.UpsertOrchestratorAsync(provider, definitionActorId, CancellationToken.None);
        await ClaimIntegrationTestKit.EnsureRuntimeAsync(provider, definitionActorId, revision, runtimeActorId, CancellationToken.None);

        _ = await ClaimIntegrationTestKit.RunClaimAsync(
            provider,
            definitionActorId,
            runtimeActorId,
            revision,
            "run-native",
            new ClaimSubmitted
            {
                CommandId = "run-native",
                CaseId = "Case-B",
                PolicyId = "POLICY-B",
                RiskScore = 0.91d,
                CompliancePassed = true,
            },
            CancellationToken.None);

        var nativeDocumentStore = provider.GetRequiredService<IProjectionDocumentReader<ScriptNativeDocumentReadModel, string>>();
        var nativeDocument = await nativeDocumentStore.GetAsync(runtimeActorId, CancellationToken.None);
        nativeDocument.Should().NotBeNull();
        nativeDocument!.SchemaId.Should().Be("claim_case");
        nativeDocument.Fields["case_id"].Should().Be("Case-B");
        nativeDocument.Fields["policy_id"].Should().Be("POLICY-B");
        nativeDocument.Fields["search"].Should().BeAssignableTo<IDictionary<string, object?>>();
        nativeDocument.Fields["search"].As<IDictionary<string, object?>>()["lookup_key"].Should().Be("case-b:policy-b");

        var subgraph = await ScriptEvolutionIntegrationTestKit.WaitForGraphSubgraphAsync(
            provider,
            scope: "script-native-claim_case",
            rootNodeId: "script:claim_case:claim-native-runtime",
            isReady: graph =>
                graph.Nodes.Any(x => x.NodeId == "ref:policy:POLICY-B") &&
                graph.Edges.Any(x =>
                    x.FromNodeId == "script:claim_case:claim-native-runtime" &&
                    x.ToNodeId == "ref:policy:POLICY-B" &&
                    x.EdgeType == "rel_policy"),
            CancellationToken.None);

        subgraph.Nodes.Should().Contain(x => x.NodeId == "script:claim_case:claim-native-runtime");
        subgraph.Nodes.Should().Contain(x => x.NodeId == "ref:policy:POLICY-B");
        subgraph.Edges.Should().ContainSingle(x =>
            x.FromNodeId == "script:claim_case:claim-native-runtime" &&
            x.ToNodeId == "ref:policy:POLICY-B" &&
            x.EdgeType == "rel_policy");
    }
}
