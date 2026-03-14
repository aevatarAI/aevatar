using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Projection.ReadPorts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Scripting.Core.Tests.Projection;

public class ProjectionScriptEvolutionDecisionReadPortTests
{
    [Fact]
    public async Task TryGetAsync_ShouldReturnNull_WhenReadModelIsMissing()
    {
        var services = new ServiceCollection()
            .AddSingleton<IProjectionDocumentReader<ScriptEvolutionReadModel, string>>(
                new InMemoryProjectionDocumentStore<ScriptEvolutionReadModel>())
            .BuildServiceProvider();
        var port = new ProjectionScriptEvolutionDecisionReadPort(services);

        var decision = await port.TryGetAsync("proposal-missing", CancellationToken.None);

        decision.Should().BeNull();
    }

    [Fact]
    public async Task TryGetAsync_ShouldReturnNull_WhenReadModelIsNotTerminal()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptEvolutionReadModel>();
        await dispatcher.UpsertAsync(new ScriptEvolutionReadModel
        {
            Id = "proposal-pending",
            ProposalId = "proposal-pending",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            ValidationStatus = ScriptEvolutionStatuses.Validated,
            PromotionStatus = ScriptEvolutionStatuses.Pending,
            Diagnostics = ["compile-ok"],
        });
        var services = new ServiceCollection()
            .AddSingleton<IProjectionDocumentReader<ScriptEvolutionReadModel, string>>(dispatcher)
            .BuildServiceProvider();
        var port = new ProjectionScriptEvolutionDecisionReadPort(services);

        var decision = await port.TryGetAsync("proposal-pending", CancellationToken.None);

        decision.Should().BeNull();
    }

    [Fact]
    public async Task TryGetAsync_ShouldMapPromotedReadModel()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptEvolutionReadModel>();
        await dispatcher.UpsertAsync(new ScriptEvolutionReadModel
        {
            Id = "proposal-promoted",
            ProposalId = "proposal-promoted",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            ValidationStatus = ScriptEvolutionStatuses.Validated,
            PromotionStatus = ScriptEvolutionStatuses.Promoted,
            DefinitionActorId = "definition-1",
            CatalogActorId = "catalog-1",
            Diagnostics = ["compile-ok"],
        });
        var services = new ServiceCollection()
            .AddSingleton<IProjectionDocumentReader<ScriptEvolutionReadModel, string>>(dispatcher)
            .BuildServiceProvider();
        var port = new ProjectionScriptEvolutionDecisionReadPort(services);

        var decision = await port.TryGetAsync("proposal-promoted", CancellationToken.None);

        decision.Should().NotBeNull();
        decision!.Accepted.Should().BeTrue();
        decision.Status.Should().Be(ScriptEvolutionStatuses.Promoted);
        decision.ValidationReport.IsSuccess.Should().BeTrue();
        decision.ValidationReport.Diagnostics.Should().ContainSingle("compile-ok");
    }

    [Fact]
    public async Task TryGetAsync_ShouldMapRejectedReadModel()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptEvolutionReadModel>();
        await dispatcher.UpsertAsync(new ScriptEvolutionReadModel
        {
            Id = "proposal-rejected",
            ProposalId = "proposal-rejected",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            ValidationStatus = ScriptEvolutionStatuses.ValidationFailed,
            PromotionStatus = ScriptEvolutionStatuses.Rejected,
            FailureReason = "validation failed",
            DefinitionActorId = "definition-1",
            CatalogActorId = "catalog-1",
            Diagnostics = ["compile-error"],
        });
        var services = new ServiceCollection()
            .AddSingleton<IProjectionDocumentReader<ScriptEvolutionReadModel, string>>(dispatcher)
            .BuildServiceProvider();
        var port = new ProjectionScriptEvolutionDecisionReadPort(services);

        var decision = await port.TryGetAsync("proposal-rejected", CancellationToken.None);

        decision.Should().NotBeNull();
        decision!.Accepted.Should().BeFalse();
        decision.Status.Should().Be(ScriptEvolutionStatuses.Rejected);
        decision.FailureReason.Should().Be("validation failed");
        decision.ValidationReport.IsSuccess.Should().BeFalse();
        decision.ValidationReport.Diagnostics.Should().ContainSingle("compile-error");
    }

    [Fact]
    public async Task TryGetAsync_ShouldThrow_WhenProposalIdIsMissing()
    {
        var services = new ServiceCollection()
            .AddSingleton<IProjectionDocumentReader<ScriptEvolutionReadModel, string>>(
                new InMemoryProjectionDocumentStore<ScriptEvolutionReadModel>())
            .BuildServiceProvider();
        var port = new ProjectionScriptEvolutionDecisionReadPort(services);

        var act = () => port.TryGetAsync(string.Empty, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TryGetAsync_ShouldReturnNull_WhenProjectionDispatcherCannotBeActivated()
    {
        var services = new ServiceCollection()
            .AddSingleton<IProjectionDocumentReader<ScriptEvolutionReadModel, string>>(
                _ => throw new InvalidOperationException("store-missing"))
            .BuildServiceProvider();
        var port = new ProjectionScriptEvolutionDecisionReadPort(services);

        var decision = await port.TryGetAsync("proposal-missing-store", CancellationToken.None);

        decision.Should().BeNull();
    }
}
