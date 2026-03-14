using Aevatar.Foundation.Abstractions;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Slow")]
public sealed class ScriptExternalEvolutionE2ETests
{
    [Fact]
    public async Task ExternalEvolutionFlow_ShouldPromoteRevisionThroughUnifiedManagerChain()
    {
        await using var provider = ScriptEvolutionIntegrationTestKit.BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var evolutionService = provider.GetRequiredService<IScriptEvolutionApplicationService>();

        var source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
            "ExternalScriptV1",
            "EXTERNAL-V1",
            "external_normalization",
            "1");

        var decision = await evolutionService.ProposeAsync(
            new ProposeScriptEvolutionRequest(
                ScriptId: "external-script",
                BaseRevision: string.Empty,
                CandidateRevision: "rev-1",
                CandidateSource: source,
                CandidateSourceHash: string.Empty,
                Reason: "external pipeline rollout",
                ProposalId: "external-proposal-1"),
            CancellationToken.None);

        decision.Accepted.Should().BeTrue();
        decision.Status.Should().Be("promoted");
        decision.CandidateRevision.Should().Be("rev-1");
        decision.DefinitionActorId.Should().Be("script-definition:external-script");
        decision.CatalogActorId.Should().Be("script-catalog");

        var managerActor = await runtime.GetAsync("script-evolution-manager");
        managerActor.Should().NotBeNull();
        var manager = managerActor!.Agent.Should().BeOfType<ScriptEvolutionManagerGAgent>().Subject;
        manager.State.Proposals.Should().ContainKey("external-proposal-1");
        manager.State.Proposals["external-proposal-1"].Status.Should().Be("promoted");

        var catalogEntry = await ScriptEvolutionIntegrationTestKit.GetCatalogEntryAsync(
            provider,
            "external-script",
            CancellationToken.None,
            expectedRevision: "rev-1");
        catalogEntry.Should().NotBeNull();
        catalogEntry!.ActiveRevision.Should().Be("rev-1");
        catalogEntry.ActiveDefinitionActorId.Should().Be("script-definition:external-script");
        catalogEntry.RevisionHistory.Should().Contain("rev-1");

        var definition = await ScriptEvolutionIntegrationTestKit.GetDefinitionSnapshotAsync(
            provider,
            "script-definition:external-script",
            "rev-1",
            CancellationToken.None);
        definition.ScriptId.Should().Be("external-script");
        definition.Revision.Should().Be("rev-1");
        definition.SourceHash.Should().Be(ScriptingCommandEnvelopeTestKit.ComputeSourceHash(source).ToUpperInvariant());

        await ScriptEvolutionIntegrationTestKit.EnsureRuntimeAsync(
            provider,
            definitionActorId: decision.DefinitionActorId,
            revision: "rev-1",
            runtimeActorId: "external-runtime",
            ct: CancellationToken.None);

        var (_, snapshot) = await ScriptEvolutionIntegrationTestKit.RunAndReadAsync(
            provider,
            runtimeActorId: "external-runtime",
            runId: "external-run-1",
            inputPayload: Any.Pack(new TextNormalizationRequested
            {
                CommandId = "external-command-1",
                InputText = "roll out",
            }),
            revision: "rev-1",
            definitionActorId: "script-definition:external-script",
            requestedEventType: "external.requested",
            ct: CancellationToken.None);

        snapshot.ReadModelPayload.Should().NotBeNull();
        snapshot.ReadModelPayload!.Unpack<TextNormalizationReadModel>().NormalizedText.Should().Be("EXTERNAL-V1:ROLL OUT");

        var queryResult = await ScriptEvolutionIntegrationTestKit.QueryNormalizationAsync(
            provider,
            "external-runtime",
            "external-query-1",
            CancellationToken.None);
        queryResult.NormalizedText.Should().Be("EXTERNAL-V1:ROLL OUT");
        queryResult.Refs.ProfileId.Should().Be("EXTERNAL-V1");
    }
}
