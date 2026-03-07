using Aevatar.Scripting.Abstractions.Definitions;
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
        };
        var port = new RuntimeScriptEvolutionFlowPort(
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
        lifecycle.RollbackCalls.Should().BeEmpty();
        result.FailureReason.Should().NotContain("compensation=");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPromoteFailsAfterUpsert_ShouldReturnPromotionFailureWithoutRollback()
    {
        var lifecycle = new RecordingLifecyclePort
        {
            PromoteException = new InvalidOperationException("promote-conflict"),
        };
        var port = new RuntimeScriptEvolutionFlowPort(
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
        lifecycle.RollbackCalls.Should().BeEmpty();
        result.FailureReason.Should().Contain("promote-conflict");
    }

    [Fact]
    public async Task ExecuteAsync_WhenUpsertFails_ShouldReturnPromotionFailedWithoutRollback()
    {
        var lifecycle = new RecordingLifecyclePort
        {
            UpsertException = new InvalidOperationException("upsert-failed"),
        };
        var port = new RuntimeScriptEvolutionFlowPort(
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
    public async Task ExecuteAsync_WhenDefinitionMutationIsRejected_ShouldReturnValidationFailed()
    {
        var lifecycle = new RecordingLifecyclePort
        {
            UpsertException = new ScriptDefinitionMutationRejectedException(
                "validation-failed",
                ["compile-failed", "schema-invalid"]),
        };
        var port = new RuntimeScriptEvolutionFlowPort(
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

        result.Status.Should().Be(ScriptEvolutionFlowStatus.ValidationFailed);
        result.ValidationReport.Should().NotBeNull();
        result.ValidationReport!.IsSuccess.Should().BeFalse();
        result.ValidationReport.Diagnostics.Should().Contain("compile-failed");
        result.ValidationReport.Diagnostics.Should().Contain("schema-invalid");
    }

    private sealed class RecordingLifecyclePort : IScriptLifecyclePort
    {
        public int UpsertCallCount { get; private set; }
        public Exception? UpsertException { get; set; }
        public Exception? PromoteException { get; set; }
        public Exception? RollbackException { get; set; }
        public List<(string CatalogActorId, string ScriptId, string TargetRevision, string ExpectedCurrentRevision)> RollbackCalls { get; } = [];

        public Task<ScriptEvolutionCommandAccepted> ProposeAsync(ScriptEvolutionProposal proposal, CancellationToken ct)
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

        public Task<ScriptRuntimeRunAccepted> RunRuntimeAsync(
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
            return Task.FromResult<ScriptCatalogEntrySnapshot?>(null);
        }
    }

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetEvolutionSessionActorId(string proposalId) => "script-evolution-session:" + proposalId;
        public string GetCatalogActorId() => "script-catalog";
        public string GetDefinitionActorId(string scriptId) => "script-definition:" + scriptId;
    }
}
