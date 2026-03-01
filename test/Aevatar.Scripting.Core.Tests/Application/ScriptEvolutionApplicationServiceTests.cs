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
        var port = new FakeScriptEvolutionPort();
        var resolver = new StaticAddressResolver();
        var service = new ScriptEvolutionApplicationService(port, resolver);

        var decision = await service.ProposeAsync(
            new ProposeScriptEvolutionRequest(
                ScriptId: "inventory-script",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "public sealed class InventoryScriptV2 {}",
                CandidateSourceHash: string.Empty,
                Reason: "external update",
                DefinitionActorId: string.Empty,
                CatalogActorId: string.Empty,
                RequestedByActorId: "ops-user",
                ProposalId: string.Empty,
                ManagerActorId: string.Empty),
            CancellationToken.None);

        decision.Accepted.Should().BeTrue();
        port.CapturedManagerActorId.Should().Be("resolver:evolution");
        port.CapturedProposal.Should().NotBeNull();
        port.CapturedProposal!.ScriptId.Should().Be("inventory-script");
        port.CapturedProposal.CandidateRevision.Should().Be("rev-2");
        port.CapturedProposal.DefinitionActorId.Should().Be("resolver:definition:inventory-script");
        port.CapturedProposal.CatalogActorId.Should().Be("resolver:catalog");
        port.CapturedProposal.RequestedByActorId.Should().Be("ops-user");
        port.CapturedProposal.ProposalId.Should().NotBeNullOrWhiteSpace();
        port.CapturedProposal.CandidateSourceHash.Should().MatchRegex("^[0-9A-F]{64}$");
    }

    [Fact]
    public async Task ProposeAsync_WhenScriptIdMissing_ShouldThrow()
    {
        var port = new FakeScriptEvolutionPort();
        var service = new ScriptEvolutionApplicationService(port, new StaticAddressResolver());

        var act = () => service.ProposeAsync(
            new ProposeScriptEvolutionRequest(
                ScriptId: string.Empty,
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "public sealed class InvalidScript {}",
                CandidateSourceHash: string.Empty,
                Reason: string.Empty,
                DefinitionActorId: string.Empty,
                CatalogActorId: string.Empty,
                RequestedByActorId: string.Empty,
                ProposalId: string.Empty,
                ManagerActorId: string.Empty),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("ScriptId is required.");
    }

    private sealed class FakeScriptEvolutionPort : IScriptEvolutionPort
    {
        public string? CapturedManagerActorId { get; private set; }

        public ScriptEvolutionProposal? CapturedProposal { get; private set; }

        public Task<ScriptPromotionDecision> ProposeAsync(
            string managerActorId,
            ScriptEvolutionProposal proposal,
            CancellationToken ct)
        {
            CapturedManagerActorId = managerActorId;
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
                DefinitionActorId: proposal.DefinitionActorId,
                CatalogActorId: proposal.CatalogActorId,
                ValidationReport: new ScriptEvolutionValidationReport(true, Array.Empty<string>())));
        }
    }

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetEvolutionManagerActorId() => "resolver:evolution";

        public string GetCatalogActorId() => "resolver:catalog";

        public string GetDefinitionActorId(string scriptId) => $"resolver:definition:{scriptId}";
    }
}
