using Aevatar.CQRS.Projection.Runtime.Abstractions;
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
            .AddSingleton<IProjectionStoreDispatcher<ScriptEvolutionReadModel, string>>(new InMemoryStoreDispatcher())
            .BuildServiceProvider();
        var port = new ProjectionScriptEvolutionDecisionReadPort(services);

        var decision = await port.TryGetAsync("proposal-missing", CancellationToken.None);

        decision.Should().BeNull();
    }

    [Fact]
    public async Task TryGetAsync_ShouldReturnNull_WhenReadModelIsNotTerminal()
    {
        var dispatcher = new InMemoryStoreDispatcher();
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
            .AddSingleton<IProjectionStoreDispatcher<ScriptEvolutionReadModel, string>>(dispatcher)
            .BuildServiceProvider();
        var port = new ProjectionScriptEvolutionDecisionReadPort(services);

        var decision = await port.TryGetAsync("proposal-pending", CancellationToken.None);

        decision.Should().BeNull();
    }

    [Fact]
    public async Task TryGetAsync_ShouldMapPromotedReadModel()
    {
        var dispatcher = new InMemoryStoreDispatcher();
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
            .AddSingleton<IProjectionStoreDispatcher<ScriptEvolutionReadModel, string>>(dispatcher)
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
        var dispatcher = new InMemoryStoreDispatcher();
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
            .AddSingleton<IProjectionStoreDispatcher<ScriptEvolutionReadModel, string>>(dispatcher)
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
            .AddSingleton<IProjectionStoreDispatcher<ScriptEvolutionReadModel, string>>(new InMemoryStoreDispatcher())
            .BuildServiceProvider();
        var port = new ProjectionScriptEvolutionDecisionReadPort(services);

        var act = () => port.TryGetAsync(string.Empty, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TryGetAsync_ShouldReturnNull_WhenProjectionDispatcherCannotBeActivated()
    {
        var services = new ServiceCollection()
            .AddSingleton<IProjectionStoreDispatcher<ScriptEvolutionReadModel, string>>(_ => throw new InvalidOperationException("store-missing"))
            .BuildServiceProvider();
        var port = new ProjectionScriptEvolutionDecisionReadPort(services);

        var decision = await port.TryGetAsync("proposal-missing-store", CancellationToken.None);

        decision.Should().BeNull();
    }

    private sealed class InMemoryStoreDispatcher : IProjectionStoreDispatcher<ScriptEvolutionReadModel, string>
    {
        private readonly Dictionary<string, ScriptEvolutionReadModel> _store = new(StringComparer.Ordinal);

        public Task UpsertAsync(ScriptEvolutionReadModel readModel, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(readModel);
            ct.ThrowIfCancellationRequested();
            _store[readModel.Id] = Clone(readModel);
            return Task.CompletedTask;
        }

        public Task MutateAsync(string key, Action<ScriptEvolutionReadModel> mutate, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(mutate);
            ct.ThrowIfCancellationRequested();
            if (!_store.TryGetValue(key, out var readModel))
            {
                readModel = new ScriptEvolutionReadModel { Id = key };
                _store[key] = readModel;
            }

            mutate(readModel);
            return Task.CompletedTask;
        }

        public Task<ScriptEvolutionReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                _store.TryGetValue(key, out var readModel)
                    ? Clone(readModel)
                    : null);
        }

        public Task<IReadOnlyList<ScriptEvolutionReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptEvolutionReadModel>>(
                _store.Values
                    .Take(take)
                    .Select(Clone)
                    .ToList());
        }

        private static ScriptEvolutionReadModel Clone(ScriptEvolutionReadModel readModel)
        {
            return new ScriptEvolutionReadModel
            {
                Id = readModel.Id,
                ProposalId = readModel.ProposalId,
                ScriptId = readModel.ScriptId,
                BaseRevision = readModel.BaseRevision,
                CandidateRevision = readModel.CandidateRevision,
                ValidationStatus = readModel.ValidationStatus,
                PromotionStatus = readModel.PromotionStatus,
                RollbackStatus = readModel.RollbackStatus,
                FailureReason = readModel.FailureReason,
                DefinitionActorId = readModel.DefinitionActorId,
                CatalogActorId = readModel.CatalogActorId,
                Diagnostics = [.. readModel.Diagnostics],
                LastEventId = readModel.LastEventId,
                UpdatedAt = readModel.UpdatedAt,
            };
        }
    }
}
