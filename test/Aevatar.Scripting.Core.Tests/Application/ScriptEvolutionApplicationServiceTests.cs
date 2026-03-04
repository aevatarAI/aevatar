using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Application;

public class ScriptEvolutionApplicationServiceTests
{
    [Fact]
    public async Task ProposeAsync_ShouldNormalizeDefaults_AndForwardToEvolutionPort()
    {
        var port = new FakeScriptLifecyclePort();
        var service = new ScriptEvolutionApplicationService(port);

        var decision = await service.ProposeAsync(
            new ProposeScriptEvolutionRequest(
                ScriptId: "inventory-script",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "public sealed class InventoryScriptV2 {}",
                CandidateSourceHash: string.Empty,
                Reason: "external update",
                ProposalId: string.Empty),
            CancellationToken.None);

        decision.Accepted.Should().BeTrue();
        port.CapturedProposal.Should().NotBeNull();
        port.CapturedProposal!.ScriptId.Should().Be("inventory-script");
        port.CapturedProposal.CandidateRevision.Should().Be("rev-2");
        port.CapturedProposal.ProposalId.Should().NotBeNullOrWhiteSpace();
        port.CapturedProposal.CandidateSourceHash.Should().MatchRegex("^[0-9A-F]{64}$");
    }

    [Fact]
    public async Task ProposeAsync_WhenScriptIdMissing_ShouldThrow()
    {
        var port = new FakeScriptLifecyclePort();
        var service = new ScriptEvolutionApplicationService(port);

        var act = () => service.ProposeAsync(
            new ProposeScriptEvolutionRequest(
                ScriptId: string.Empty,
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "public sealed class InvalidScript {}",
                CandidateSourceHash: string.Empty,
                Reason: string.Empty,
                ProposalId: string.Empty),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("ScriptId is required.");
    }

    private sealed class FakeScriptLifecyclePort : IScriptLifecyclePort
    {
        public ScriptEvolutionProposal? CapturedProposal { get; private set; }

        public Task<ScriptPromotionDecision> ProposeAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct)
        {
            CapturedProposal = proposal;
            ct.ThrowIfCancellationRequested();

            return Task.FromResult(new ScriptPromotionDecision(
                Accepted: true,
                ProposalId: proposal.ProposalId,
                ScriptId: proposal.ScriptId,
                BaseRevision: proposal.BaseRevision,
                CandidateRevision: proposal.CandidateRevision,
                Status: "promoted",
                FailureReason: string.Empty,
                DefinitionActorId: string.Empty,
                CatalogActorId: string.Empty,
                ValidationReport: new ScriptEvolutionValidationReport(true, Array.Empty<string>())));
        }

        public Task<string> UpsertDefinitionAsync(
            string scriptId,
            string scriptRevision,
            string sourceText,
            string sourceHash,
            string? definitionActorId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<string> SpawnRuntimeAsync(
            string definitionActorId,
            string scriptRevision,
            string? runtimeActorId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Google.Protobuf.WellKnownTypes.Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            CancellationToken ct) => throw new NotSupportedException();

        public Task PromoteCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string expectedBaseRevision,
            string revision,
            string definitionActorId,
            string sourceHash,
            string proposalId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task RollbackCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            string expectedCurrentRevision,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
            string? catalogActorId,
            string scriptId,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
