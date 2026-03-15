using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Ports;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptEvolutionExecutionServicesTests
{
    [Fact]
    public void PolicyEvaluator_ShouldRejectMissingFields_AndSameRevision()
    {
        var evaluator = new DefaultScriptEvolutionPolicyEvaluator();

        evaluator.EvaluateFailure(new ScriptEvolutionProposal(
                ProposalId: "proposal-1",
                ScriptId: string.Empty,
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source",
                CandidateSourceHash: "hash",
                Reason: string.Empty))
            .Should().Be("ScriptId is required.");

        evaluator.EvaluateFailure(new ScriptEvolutionProposal(
                ProposalId: "proposal-1",
                ScriptId: "script-1",
                BaseRevision: "rev-2",
                CandidateRevision: "rev-2",
                CandidateSource: "source",
                CandidateSourceHash: "hash",
                Reason: string.Empty))
            .Should().Be("CandidateRevision must differ from BaseRevision.");
    }

    [Fact]
    public async Task ValidationService_ShouldDisposeCompiledArtifact_AndReturnDiagnostics()
    {
        var compiler = new DisposableTrackingCompiler(false, ["compile-failed"]);
        var service = new RuntimeScriptEvolutionValidationService(compiler);

        var result = await service.ValidateAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-1",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source",
                CandidateSourceHash: "hash",
                Reason: string.Empty),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(x => x == "compile-failed");
        compiler.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task CatalogBaselineReader_ShouldReturnEmptyBaseline_WhenBaseRevisionMissing()
    {
        var service = new RuntimeScriptCatalogBaselineReader(new StaticAddressResolver());

        var result = await service.ReadAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-1",
                ScriptId: "script-1",
                BaseRevision: string.Empty,
                CandidateRevision: "rev-2",
                CandidateSource: "source",
                CandidateSourceHash: "hash",
                Reason: string.Empty),
            CancellationToken.None);

        result.HasFailure.Should().BeFalse();
        result.Baseline.Should().BeNull();
        result.BaselineSource.Should().Be("no_baseline");
        result.CatalogActorId.Should().Be("script-catalog");
    }

    [Fact]
    public async Task CatalogBaselineReader_ShouldUseProposalBaseRevision_WhenPresent()
    {
        var service = new RuntimeScriptCatalogBaselineReader(new StaticAddressResolver());

        var result = await service.ReadAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-1",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source",
                CandidateSourceHash: "hash",
                Reason: string.Empty),
            CancellationToken.None);

        result.HasFailure.Should().BeFalse();
        result.BaselineSource.Should().Be("proposal_base_revision");
        result.Baseline.Should().NotBeNull();
        result.Baseline!.ActiveRevision.Should().Be("rev-1");
    }

    [Fact]
    public async Task CompensationService_ShouldRollbackToPreviousRevision_WhenBaselineExists()
    {
        var port = new RecordingCatalogCommandPort();
        var service = new RuntimeScriptPromotionCompensationService(port);

        var result = await service.TryCompensateAsync(
            "script-catalog",
            new ScriptEvolutionProposal(
                ProposalId: "proposal-1",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source",
                CandidateSourceHash: "hash",
                Reason: string.Empty),
            new ScriptCatalogEntrySnapshot(
                ScriptId: "script-1",
                ActiveRevision: "rev-1",
                ActiveDefinitionActorId: "definition-1",
                ActiveSourceHash: "hash-1",
                PreviousRevision: string.Empty,
                RevisionHistory: ["rev-1"],
                LastProposalId: "proposal-prev"),
            CancellationToken.None);

        result.Should().Be("rollback_to_previous_active_revision_success");
        port.RollbackCalls.Should().ContainSingle();
        port.RollbackCalls[0].TargetRevision.Should().Be("rev-1");
        port.RollbackCalls[0].ExpectedCurrentRevision.Should().Be("rev-2");
    }

    [Fact]
    public async Task RollbackService_ShouldUseDefaultCatalogActorId_WhenRequestCatalogMissing()
    {
        var port = new RecordingCatalogCommandPort();
        var service = new RuntimeScriptEvolutionRollbackService(port, new StaticAddressResolver());

        await service.RollbackAsync(
            new ScriptRollbackRequest(
                ProposalId: "proposal-1",
                ScriptId: "script-1",
                TargetRevision: "rev-1",
                CatalogActorId: string.Empty,
                Reason: "rollback",
                ExpectedCurrentRevision: "rev-2"),
            CancellationToken.None);

        port.RollbackCalls.Should().ContainSingle();
        port.RollbackCalls[0].CatalogActorId.Should().Be("script-catalog");
    }

    private sealed class DisposableTrackingCompiler(
        bool isSuccess,
        IReadOnlyList<string> diagnostics) : IScriptBehaviorCompiler
    {
        public bool IsDisposed { get; private set; }

        public ScriptBehaviorCompilationResult Compile(ScriptBehaviorCompilationRequest request)
        {
            _ = request;
            return new ScriptBehaviorCompilationResult(
                IsSuccess: isSuccess,
                Artifact: new ScriptBehaviorArtifact(
                    "script-1",
                    "rev-2",
                    "hash",
                    new NoopBehavior().Descriptor,
                    new NoopBehavior().Descriptor.ToContract(),
                    static () => new NoopBehavior(),
                    dispose: () =>
                    {
                        IsDisposed = true;
                        return ValueTask.CompletedTask;
                    }),
                Diagnostics: diagnostics);
        }
    }

    private sealed class NoopBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder
                .OnCommand<SimpleTextCommand>(HandleAsync)
                .OnEvent<SimpleTextEvent>(
                    apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty },
                    project: static (_, evt, _) => evt.Current)
                .OnQuery<SimpleTextQueryRequested, SimpleTextQueryResponded>(HandleQueryAsync);
        }

        private static Task HandleAsync(
            SimpleTextCommand command,
            ScriptCommandContext<SimpleTextState> context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            context.Emit(new SimpleTextEvent
            {
                CommandId = command.CommandId ?? string.Empty,
                Current = new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = command.Value ?? string.Empty,
                },
            });
            return Task.CompletedTask;
        }

        private static Task<SimpleTextQueryResponded?> HandleQueryAsync(
            SimpleTextQueryRequested query,
            ScriptQueryContext<SimpleTextReadModel> snapshot,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<SimpleTextQueryResponded?>(new SimpleTextQueryResponded
            {
                RequestId = query.RequestId ?? string.Empty,
                Current = snapshot.CurrentReadModel ?? new SimpleTextReadModel(),
            });
        }
    }
    private sealed class RecordingCatalogCommandPort : IScriptCatalogCommandPort
    {
        public List<RollbackCall> RollbackCalls { get; } = [];

        public Task PromoteCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string expectedBaseRevision,
            string revision,
            string definitionActorId,
            string sourceHash,
            string proposalId,
            CancellationToken ct)
        {
            _ = catalogActorId;
            _ = scriptId;
            _ = expectedBaseRevision;
            _ = revision;
            _ = definitionActorId;
            _ = sourceHash;
            _ = proposalId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RollbackCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            string expectedCurrentRevision,
            CancellationToken ct)
        {
            _ = reason;
            _ = proposalId;
            ct.ThrowIfCancellationRequested();
            RollbackCalls.Add(new RollbackCall(
                catalogActorId ?? string.Empty,
                scriptId,
                targetRevision,
                expectedCurrentRevision));
            return Task.CompletedTask;
        }
    }

    private sealed record RollbackCall(
        string CatalogActorId,
        string ScriptId,
        string TargetRevision,
        string ExpectedCurrentRevision);

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetEvolutionManagerActorId() => "script-evolution-manager";
        public string GetEvolutionSessionActorId(string proposalId) => $"script-evolution-session:{proposalId}";
        public string GetCatalogActorId() => "script-catalog";
        public string GetDefinitionActorId(string scriptId) => $"script-definition:{scriptId}";
    }
}
