using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Infrastructure.Ports;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

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
    public async Task ValidationService_ShouldDisposeCompiledDefinition_AndReturnDiagnostics()
    {
        var definition = new DisposableNoopDefinition();
        var service = new RuntimeScriptEvolutionValidationService(new DisposableTrackingCompiler(definition, false, ["compile-failed"]));

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
        definition.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task CatalogBaselineReader_ShouldReturnFailure_WhenQueryFailsWithoutBaseRevision()
    {
        var service = new RuntimeScriptCatalogBaselineReader(
            new RecordingCatalogQueryPort { Exception = new InvalidOperationException("catalog-query-failed") },
            new StaticAddressResolver());

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

        result.HasFailure.Should().BeTrue();
        result.FailureReason.Should().Contain("no base revision fallback");
        result.CatalogActorId.Should().Be("script-catalog");
    }

    [Fact]
    public async Task CatalogBaselineReader_ShouldFallbackToBaseRevision_WhenQueryFails()
    {
        var service = new RuntimeScriptCatalogBaselineReader(
            new RecordingCatalogQueryPort { Exception = new InvalidOperationException("catalog-query-failed") },
            new StaticAddressResolver());

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
        result.BaselineSource.Should().Be("fallback_base_revision_after_query_failure");
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
        DisposableNoopDefinition definition,
        bool isSuccess,
        IReadOnlyList<string> diagnostics) : IScriptPackageCompiler
    {
        private readonly DisposableNoopDefinition _definition = definition;
        private readonly bool _isSuccess = isSuccess;
        private readonly IReadOnlyList<string> _diagnostics = diagnostics;

        public Task<ScriptPackageCompilationResult> CompileAsync(
            ScriptPackageCompilationRequest request,
            CancellationToken ct)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new ScriptPackageCompilationResult(
                    IsSuccess: _isSuccess,
                    CompiledDefinition: _definition,
                    ContractManifest: new ScriptContractManifest("input", [], "state", "readmodel"),
                    Diagnostics: _diagnostics));
        }
    }

    private sealed class DisposableNoopDefinition : IScriptPackageDefinition, IAsyncDisposable
    {
        public string ScriptId => "script-1";
        public string Revision => "rev-2";
        public ScriptContractManifest ContractManifest { get; } = new("input", [], "state", "readmodel");
        public bool IsDisposed { get; private set; }

        public Task<ScriptHandlerResult> HandleRequestedEventAsync(
            ScriptRequestedEventEnvelope requestedEvent,
            ScriptExecutionContext context,
            CancellationToken ct)
        {
            _ = requestedEvent;
            _ = context;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ScriptHandlerResult([new StringValue { Value = "noop" }]));
        }

        public ValueTask<IReadOnlyDictionary<string, Google.Protobuf.WellKnownTypes.Any>?> ApplyDomainEventAsync(
            IReadOnlyDictionary<string, Google.Protobuf.WellKnownTypes.Any> currentState,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            _ = domainEvent;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyDictionary<string, Google.Protobuf.WellKnownTypes.Any>?>(currentState);
        }

        public ValueTask<IReadOnlyDictionary<string, Google.Protobuf.WellKnownTypes.Any>?> ReduceReadModelAsync(
            IReadOnlyDictionary<string, Google.Protobuf.WellKnownTypes.Any> currentReadModel,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            _ = domainEvent;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyDictionary<string, Google.Protobuf.WellKnownTypes.Any>?>(currentReadModel);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCatalogQueryPort : IScriptCatalogQueryPort
    {
        public Exception? Exception { get; init; }

        public Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
            string? catalogActorId,
            string scriptId,
            CancellationToken ct)
        {
            _ = catalogActorId;
            _ = scriptId;
            ct.ThrowIfCancellationRequested();
            if (Exception != null)
                throw Exception;

            return Task.FromResult<ScriptCatalogEntrySnapshot?>(null);
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
