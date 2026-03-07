using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Infrastructure.Ports;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class RuntimeScriptEvolutionFlowPortTests
{
    [Fact]
    public async Task ExecuteAsync_WhenPromoteFailsAfterUpsert_ShouldCompensateAndReturnPartialPromotion()
    {
        var lifecycle = new RecordingLifecyclePort
        {
            PromoteException = new InvalidOperationException("promote-failed"),
            CatalogBefore = new ScriptCatalogEntrySnapshot(
                ScriptId: "script-1",
                ActiveRevision: "rev-1",
                ActiveDefinitionActorId: "definition-rev-1",
                ActiveSourceHash: "hash-rev-1",
                PreviousRevision: "rev-0",
                RevisionHistory: ["rev-0", "rev-1"],
                LastProposalId: "proposal-prev"),
        };
        var port = new RuntimeScriptEvolutionFlowPort(
            new SuccessCompiler(),
            lifecycle,
            new StaticAddressResolver());

        var result = await port.ExecuteAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-2",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source-v2",
                CandidateSourceHash: "hash-v2",
                Reason: "upgrade"),
            CancellationToken.None);

        result.Status.Should().Be(ScriptEvolutionFlowStatus.PromotionFailed);
        result.Promotion.Should().NotBeNull();
        result.Promotion!.DefinitionActorId.Should().Be("definition-rev-2");
        result.Promotion.CatalogActorId.Should().Be("script-catalog");
        lifecycle.RollbackCalls.Should().ContainSingle();
        lifecycle.RollbackCalls[0].TargetRevision.Should().Be("rev-1");
        lifecycle.RollbackCalls[0].ExpectedCurrentRevision.Should().Be("rev-2");
        result.FailureReason.Should().Contain("compensation=rollback_to_previous_active_revision_success");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCompensationRollbackConflicts_ShouldNotRollbackAndReturnConflictFailureReason()
    {
        var lifecycle = new RecordingLifecyclePort
        {
            PromoteException = new InvalidOperationException("promote-conflict"),
            RollbackException = new InvalidOperationException(
                "Rollback conflict for script `script-1`. expected_current_revision=`rev-2` actual_active_revision=`rev-3`."),
            CatalogBefore = new ScriptCatalogEntrySnapshot(
                ScriptId: "script-1",
                ActiveRevision: "rev-1",
                ActiveDefinitionActorId: "definition-rev-1",
                ActiveSourceHash: "hash-rev-1",
                PreviousRevision: "rev-0",
                RevisionHistory: ["rev-0", "rev-1", "rev-2", "rev-3"],
                LastProposalId: "proposal-prev"),
        };
        var port = new RuntimeScriptEvolutionFlowPort(
            new SuccessCompiler(),
            lifecycle,
            new StaticAddressResolver());

        var result = await port.ExecuteAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-2",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source-v2",
                CandidateSourceHash: "hash-v2",
                Reason: "upgrade"),
            CancellationToken.None);

        result.Status.Should().Be(ScriptEvolutionFlowStatus.PromotionFailed);
        lifecycle.RollbackCalls.Should().ContainSingle();
        lifecycle.RollbackCalls[0].TargetRevision.Should().Be("rev-1");
        lifecycle.RollbackCalls[0].ExpectedCurrentRevision.Should().Be("rev-2");
        result.FailureReason.Should().Contain("compensation=rollback_failed:");
        result.FailureReason.Should().Contain("expected_current_revision=`rev-2`");
    }

    [Fact]
    public async Task ExecuteAsync_WhenUpsertFails_ShouldReturnPromotionFailedWithoutRollback()
    {
        var lifecycle = new RecordingLifecyclePort
        {
            UpsertException = new InvalidOperationException("upsert-failed"),
        };
        var port = new RuntimeScriptEvolutionFlowPort(
            new SuccessCompiler(),
            lifecycle,
            new StaticAddressResolver());

        var result = await port.ExecuteAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-3",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source-v2",
                CandidateSourceHash: "hash-v2",
                Reason: "upgrade"),
            CancellationToken.None);

        result.Status.Should().Be(ScriptEvolutionFlowStatus.PromotionFailed);
        result.Promotion.Should().BeNull();
        lifecycle.RollbackCalls.Should().BeEmpty();
        result.FailureReason.Should().Contain("Failed to upsert candidate definition");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCatalogBaselineQueryFailsWithoutBaseRevision_ShouldFailBeforeUpsert()
    {
        var lifecycle = new RecordingLifecyclePort
        {
            CatalogQueryException = new InvalidOperationException("catalog-query-failed"),
        };
        var port = new RuntimeScriptEvolutionFlowPort(
            new SuccessCompiler(),
            lifecycle,
            new StaticAddressResolver());

        var result = await port.ExecuteAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-query-failed",
                ScriptId: "script-1",
                BaseRevision: string.Empty,
                CandidateRevision: "rev-2",
                CandidateSource: "source-v2",
                CandidateSourceHash: "hash-v2",
                Reason: "upgrade"),
            CancellationToken.None);

        result.Status.Should().Be(ScriptEvolutionFlowStatus.PromotionFailed);
        result.FailureReason.Should().Contain("no base revision fallback");
        lifecycle.UpsertCallCount.Should().Be(0);
        lifecycle.RollbackCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenCatalogBaselineQueryFailsWithBaseRevision_ShouldFallbackAndCompensate()
    {
        var lifecycle = new RecordingLifecyclePort
        {
            CatalogQueryException = new InvalidOperationException("catalog-query-failed"),
            PromoteException = new InvalidOperationException("promote-failed"),
        };
        var port = new RuntimeScriptEvolutionFlowPort(
            new SuccessCompiler(),
            lifecycle,
            new StaticAddressResolver());

        var result = await port.ExecuteAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-query-fallback",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source-v2",
                CandidateSourceHash: "hash-v2",
                Reason: "upgrade"),
            CancellationToken.None);

        result.Status.Should().Be(ScriptEvolutionFlowStatus.PromotionFailed);
        lifecycle.UpsertCallCount.Should().Be(1);
        lifecycle.RollbackCalls.Should().ContainSingle();
        lifecycle.RollbackCalls[0].TargetRevision.Should().Be("rev-1");
        lifecycle.RollbackCalls[0].ExpectedCurrentRevision.Should().Be("rev-2");
        result.FailureReason.Should().Contain("catalog_baseline_source=fallback_base_revision_after_query_failure");
        result.FailureReason.Should().Contain("compensation=rollback_to_previous_active_revision_success");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDisposeCompiledDefinition_WhenCompilerReturnsAsyncDisposableDefinition()
    {
        var definition = new DisposableNoopDefinition();
        var lifecycle = new RecordingLifecyclePort
        {
            UpsertException = new InvalidOperationException("upsert-failed"),
        };
        var port = new RuntimeScriptEvolutionFlowPort(
            new DisposableTrackingCompiler(definition),
            lifecycle,
            new StaticAddressResolver());

        var result = await port.ExecuteAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-dispose",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source-v2",
                CandidateSourceHash: "hash-v2",
                Reason: "upgrade"),
            CancellationToken.None);

        result.Status.Should().Be(ScriptEvolutionFlowStatus.PromotionFailed);
        definition.IsDisposed.Should().BeTrue();
    }

    private sealed class SuccessCompiler : IScriptPackageCompiler
    {
        public Task<ScriptPackageCompilationResult> CompileAsync(
            ScriptPackageCompilationRequest request,
            CancellationToken ct)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new ScriptPackageCompilationResult(
                    IsSuccess: true,
                    CompiledDefinition: new NoopDefinition(),
                    ContractManifest: new ScriptContractManifest("input", [], "state", "readmodel"),
                    Diagnostics: Array.Empty<string>()));
        }
    }

    private sealed class DisposableTrackingCompiler(DisposableNoopDefinition definition) : IScriptPackageCompiler
    {
        private readonly DisposableNoopDefinition _definition = definition;

        public Task<ScriptPackageCompilationResult> CompileAsync(
            ScriptPackageCompilationRequest request,
            CancellationToken ct)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new ScriptPackageCompilationResult(
                    IsSuccess: true,
                    CompiledDefinition: _definition,
                    ContractManifest: new ScriptContractManifest("input", [], "state", "readmodel"),
                    Diagnostics: Array.Empty<string>()));
        }
    }

    private sealed class NoopDefinition : IScriptPackageDefinition
    {
        public string ScriptId => "noop";
        public string Revision => "noop";
        public ScriptContractManifest ContractManifest { get; } =
            new("input", [], "state", "readmodel");

        public Task<ScriptHandlerResult> HandleRequestedEventAsync(
            ScriptRequestedEventEnvelope requestedEvent,
            ScriptExecutionContext context,
            CancellationToken ct)
        {
            _ = requestedEvent;
            _ = context;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ScriptHandlerResult(Array.Empty<Google.Protobuf.IMessage>()));
        }

        public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
            IReadOnlyDictionary<string, Any> currentState,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            _ = domainEvent;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentState);
        }

        public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
            IReadOnlyDictionary<string, Any> currentReadModel,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            _ = domainEvent;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentReadModel);
        }
    }

    private sealed class DisposableNoopDefinition : IScriptPackageDefinition, IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }
        public string ScriptId => "noop";
        public string Revision => "noop";
        public ScriptContractManifest ContractManifest { get; } =
            new("input", [], "state", "readmodel");

        public Task<ScriptHandlerResult> HandleRequestedEventAsync(
            ScriptRequestedEventEnvelope requestedEvent,
            ScriptExecutionContext context,
            CancellationToken ct)
        {
            _ = requestedEvent;
            _ = context;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ScriptHandlerResult(Array.Empty<Google.Protobuf.IMessage>()));
        }

        public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
            IReadOnlyDictionary<string, Any> currentState,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            _ = domainEvent;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentState);
        }

        public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
            IReadOnlyDictionary<string, Any> currentReadModel,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            _ = domainEvent;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentReadModel);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLifecyclePort : IScriptLifecyclePort
    {
        public int UpsertCallCount { get; private set; }
        public Exception? UpsertException { get; set; }
        public Exception? PromoteException { get; set; }
        public ScriptCatalogEntrySnapshot? CatalogBefore { get; set; }
        public Exception? CatalogQueryException { get; set; }
        public Exception? RollbackException { get; set; }
        public List<(string CatalogActorId, string ScriptId, string TargetRevision, string ExpectedCurrentRevision)> RollbackCalls { get; } = [];

        public Task<ScriptPromotionDecision> ProposeAsync(ScriptEvolutionProposal proposal, CancellationToken ct)
        {
            _ = proposal;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public Task<string> UpsertDefinitionAsync(
            string scriptId,
            string scriptRevision,
            string sourceText,
            string sourceHash,
            string? definitionActorId,
            CancellationToken ct)
        {
            _ = scriptId;
            _ = scriptRevision;
            _ = sourceText;
            _ = sourceHash;
            _ = definitionActorId;
            ct.ThrowIfCancellationRequested();
            UpsertCallCount++;
            if (UpsertException != null)
                throw UpsertException;
            return Task.FromResult("definition-rev-2");
        }

        public Task<string> SpawnRuntimeAsync(
            string definitionActorId,
            string scriptRevision,
            string? runtimeActorId,
            CancellationToken ct)
        {
            _ = definitionActorId;
            _ = scriptRevision;
            _ = runtimeActorId;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            CancellationToken ct)
        {
            _ = runtimeActorId;
            _ = runId;
            _ = inputPayload;
            _ = scriptRevision;
            _ = definitionActorId;
            _ = requestedEventType;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

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
            if (PromoteException != null)
                throw PromoteException;
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
            RollbackCalls.Add((catalogActorId ?? string.Empty, scriptId, targetRevision, expectedCurrentRevision ?? string.Empty));
            if (RollbackException != null)
                throw RollbackException;
            return Task.CompletedTask;
        }

        public Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
            string? catalogActorId,
            string scriptId,
            CancellationToken ct)
        {
            _ = catalogActorId;
            _ = scriptId;
            ct.ThrowIfCancellationRequested();
            if (CatalogQueryException != null)
                throw CatalogQueryException;
            return Task.FromResult(CatalogBefore);
        }
    }

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetEvolutionSessionActorId(string proposalId) => "script-evolution-session:" + proposalId;
        public string GetCatalogActorId() => "script-catalog";
        public string GetDefinitionActorId(string scriptId) => "script-definition:" + scriptId;
    }
}
