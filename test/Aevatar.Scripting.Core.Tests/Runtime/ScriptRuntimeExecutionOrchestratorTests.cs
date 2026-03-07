using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptRuntimeExecutionOrchestratorTests
{
    [Fact]
    public async Task ExecuteRunAsync_ShouldDisposeCompiledDefinition_WhenDefinitionImplementsDisposableOnly()
    {
        var definition = new DisposableOnlyDefinition();
        var orchestrator = new ScriptRuntimeExecutionOrchestrator(
            new DisposableOnlyCompiler(definition),
            new NoopCapabilityComposer());

        var result = await orchestrator.ExecuteRunAsync(
            new ScriptRuntimeExecutionRequest(
                RuntimeActorId: "runtime-1",
                CurrentState: null,
                CurrentReadModel: null,
                RunEvent: new RunScriptRequestedEvent
                {
                    RunId = "run-1",
                    InputPayload = Any.Pack(new Struct()),
                    ScriptRevision = "rev-1",
                    DefinitionActorId = "definition-1",
                    RequestedEventType = "script.run.requested",
                },
                ScriptId: "script-1",
                ScriptRevision: "rev-1",
                SourceText: "source",
                ReadModelSchemaVersion: "v1",
                ReadModelSchemaHash: "hash-v1"),
            CancellationToken.None);

        result.Should().ContainSingle();
        definition.IsDisposed.Should().BeTrue();
    }

    private sealed class DisposableOnlyCompiler(DisposableOnlyDefinition definition) : IScriptPackageCompiler
    {
        private readonly DisposableOnlyDefinition _definition = definition;

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
                    ContractManifest: _definition.ContractManifest,
                    Diagnostics: Array.Empty<string>()));
        }
    }

    private sealed class DisposableOnlyDefinition : IScriptPackageDefinition, IDisposable
    {
        public bool IsDisposed { get; private set; }
        public string ScriptId => "script-1";
        public string Revision => "rev-1";
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
            return Task.FromResult(
                new ScriptHandlerResult([new StringValue { Value = "ScriptCompleted" }]));
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

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class NoopCapabilityComposer : IScriptRuntimeCapabilityComposer
    {
        private static readonly IScriptRuntimeCapabilities Capabilities = new NoopRuntimeCapabilities();

        public IScriptRuntimeCapabilities Compose(ScriptRuntimeCapabilityContext context)
        {
            _ = context;
            return Capabilities;
        }
    }

    private sealed class NoopRuntimeCapabilities : IScriptRuntimeCapabilities
    {
        public Task<string> AskAIAsync(string prompt, CancellationToken ct)
        {
            _ = prompt;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(string.Empty);
        }

        public Task PublishAsync(IMessage eventPayload, EventDirection direction, CancellationToken ct)
        {
            _ = eventPayload;
            _ = direction;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct)
        {
            _ = targetActorId;
            _ = eventPayload;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task InvokeAgentAsync(string targetAgentId, IMessage eventPayload, CancellationToken ct)
        {
            _ = targetAgentId;
            _ = eventPayload;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string> CreateAgentAsync(
            string agentTypeAssemblyQualifiedName,
            string? actorId,
            CancellationToken ct)
        {
            _ = agentTypeAssemblyQualifiedName;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(actorId ?? string.Empty);
        }

        public Task DestroyAgentAsync(string actorId, CancellationToken ct)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct)
        {
            _ = parentActorId;
            _ = childActorId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnlinkAgentAsync(string childActorId, CancellationToken ct)
        {
            _ = childActorId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<ScriptEvolutionDecision> ProposeScriptEvolutionAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new ScriptEvolutionDecision(
                    proposal.ProposalId ?? string.Empty,
                    proposal.ScriptId ?? string.Empty,
                    $"script-evolution-session:{proposal.ProposalId}",
                    Accepted: true,
                    Status: ScriptEvolutionStatuses.Promoted,
                    FailureReason: string.Empty,
                    DefinitionActorId: "script-definition:test",
                    CandidateRevision: proposal.CandidateRevision ?? string.Empty,
                    CatalogActorId: "script-catalog"));
        }

        public Task<string> UpsertScriptDefinitionAsync(
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
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(definitionActorId ?? string.Empty);
        }

        public Task<string> SpawnScriptRuntimeAsync(
            string definitionActorId,
            string scriptRevision,
            string? runtimeActorId,
            CancellationToken ct)
        {
            _ = definitionActorId;
            _ = scriptRevision;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(runtimeActorId ?? string.Empty);
        }

        public Task<ScriptRuntimeRunAccepted> RunScriptInstanceAsync(
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
            return Task.FromResult(new ScriptRuntimeRunAccepted(runtimeActorId, runId, definitionActorId, scriptRevision));
        }

        public Task PromoteRevisionAsync(
            string catalogActorId,
            string scriptId,
            string revision,
            string definitionActorId,
            string sourceHash,
            string proposalId,
            CancellationToken ct)
        {
            _ = catalogActorId;
            _ = scriptId;
            _ = revision;
            _ = definitionActorId;
            _ = sourceHash;
            _ = proposalId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RollbackRevisionAsync(
            string catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            CancellationToken ct)
        {
            _ = catalogActorId;
            _ = scriptId;
            _ = targetRevision;
            _ = reason;
            _ = proposalId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
