using Aevatar.Scripting.Application.AI;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.AI;

public class ClaimRoleIntegrationTests
{
    [Fact]
    public async Task Should_delegate_to_role_agent_capability_with_correlation()
    {
        var rolePort = new RecordingRoleAgentPort();
        var capability = new RoleAgentDelegateAICapability(rolePort);

        var output = await capability.AskAsync(
            runId: "run-claim-1",
            correlationId: "corr-claim-1",
            prompt: "extract claim facts",
            ct: CancellationToken.None);

        output.Should().Be("structured-facts");
        rolePort.RunId.Should().Be("run-claim-1");
        rolePort.CorrelationId.Should().Be("corr-claim-1");
        rolePort.Prompt.Should().Be("extract claim facts");
    }

    [Fact]
    public async Task Should_map_ai_output_to_ClaimFactsExtractedEvent()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
        var compilation = await compiler.CompileAsync(
            new ScriptPackageCompilationRequest(
                ScriptId: "claim-role-script",
                Revision: "rev-role-1",
                Source: """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ClaimRoleScript : IScriptPackageRuntime
{
    public async Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        _ = requestedEvent;
        var aiOutput = await context.Capabilities!.AskAIAsync("extract-claim-facts", ct);
        var mapped = string.IsNullOrWhiteSpace(aiOutput)
            ? "ClaimFactsExtractionFailedEvent"
            : "ClaimFactsExtractedEvent";
        return new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = mapped } });
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(new Dictionary<string, Any>());

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(new Dictionary<string, Any>());
}
"""),
            CancellationToken.None);
        compilation.IsSuccess.Should().BeTrue();

        var result = await compilation.CompiledDefinition!.HandleRequestedEventAsync(
            new ScriptRequestedEventEnvelope(
                EventType: "claim.role.analysis.requested",
                Payload: Any.Pack(new Struct()),
                EventId: "evt-role-1",
                CorrelationId: "corr-role-1",
                CausationId: "cause-role-1"),
            new ScriptExecutionContext(
                ActorId: "claim-role-runtime",
                ScriptId: "claim-role-script",
                Revision: "rev-role-1",
                RunId: "run-role-1",
                CorrelationId: "corr-role-1",
                Capabilities: new FakeCapabilities("facts-ready")),
            CancellationToken.None);

        result.DomainEvents.Should().ContainSingle();
        ((StringValue)result.DomainEvents[0]).Value.Should().Be("ClaimFactsExtractedEvent");
    }

    private sealed class RecordingRoleAgentPort : IRoleAgentPort
    {
        public string RunId { get; private set; } = string.Empty;
        public string CorrelationId { get; private set; } = string.Empty;
        public string Prompt { get; private set; } = string.Empty;

        public Task<string> RunAsync(
            string runId,
            string correlationId,
            string prompt,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RunId = runId;
            CorrelationId = correlationId;
            Prompt = prompt;
            return Task.FromResult("structured-facts");
        }
    }

    private sealed class FakeCapabilities(string aiResult) : IScriptRuntimeCapabilities
    {
        public Task<string> AskAIAsync(string prompt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _ = prompt;
            return Task.FromResult(aiResult);
        }

        public Task PublishAsync(IMessage eventPayload, Aevatar.Foundation.Abstractions.EventDirection direction, CancellationToken ct) =>
            Task.CompletedTask;

        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) =>
            Task.FromResult(actorId ?? "created");

        public Task DestroyAgentAsync(string actorId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<ScriptPromotionDecision> ProposeScriptEvolutionAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct) =>
            Task.FromResult(
                new ScriptPromotionDecision(
                    Accepted: true,
                    ProposalId: proposal.ProposalId,
                    ScriptId: proposal.ScriptId,
                    BaseRevision: proposal.BaseRevision,
                    CandidateRevision: proposal.CandidateRevision,
                    Status: "promoted",
                    FailureReason: string.Empty,
                    DefinitionActorId: $"script-definition:{proposal.ScriptId}",
                    CatalogActorId: "script-catalog",
                    ValidationReport: new ScriptEvolutionValidationReport(true, Array.Empty<string>())));

        public Task<string> UpsertScriptDefinitionAsync(
            string scriptId,
            string scriptRevision,
            string sourceText,
            string sourceHash,
            string? definitionActorId,
            CancellationToken ct) =>
            Task.FromResult(definitionActorId ?? "definition-1");

        public Task<string> SpawnScriptRuntimeAsync(
            string definitionActorId,
            string scriptRevision,
            string? runtimeActorId,
            CancellationToken ct) =>
            Task.FromResult(runtimeActorId ?? "runtime-1");

        public Task RunScriptInstanceAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task PromoteRevisionAsync(
            string catalogActorId,
            string scriptId,
            string revision,
            string definitionActorId,
            string sourceHash,
            string proposalId,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task RollbackRevisionAsync(
            string catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            CancellationToken ct) =>
            Task.CompletedTask;
    }
}
