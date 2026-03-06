using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Contract;

public class ScriptPackageRuntimeContractTests
{
    [Fact]
    public async Task PackageRuntime_ShouldSupport_Handle_Apply_And_Reduce()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
var source = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ContractDrivenScript : IScriptPackageRuntime
{
    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = requestedEvent.EventType } }));
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new StringValue { Value = "state:" + domainEvent.EventType }),
            });
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["view"] = Any.Pack(new StringValue { Value = "projection:" + domainEvent.EventType }),
            });
    }
}
""";

        var compiled = await compiler.CompileAsync(
            new ScriptPackageCompilationRequest("script-contract", "rev-1", source),
            CancellationToken.None);

        compiled.IsSuccess.Should().BeTrue();

        var context = new ScriptExecutionContext(
            ActorId: "runtime-1",
            ScriptId: "script-contract",
            Revision: "rev-1");

        var requestedEvent = new ScriptRequestedEventEnvelope(
            EventType: "claim.submitted",
            Payload: Any.Pack(new Struct { Fields = { ["caseId"] = Google.Protobuf.WellKnownTypes.Value.ForString("C-1") } }),
            EventId: "evt-1",
            CorrelationId: "corr-1",
            CausationId: "cause-1");

        var handle = await compiled.CompiledDefinition!.HandleRequestedEventAsync(
            requestedEvent,
            context,
            CancellationToken.None);

        handle.DomainEvents.Should().ContainSingle();
        ((StringValue)handle.DomainEvents[0]).Value.Should().Be("claim.submitted");

        var domainEvent = new ScriptDomainEventEnvelope(
            EventType: "ClaimApprovedEvent",
            Payload: Any.Pack(new Struct()),
            EventId: "evt-2",
            CorrelationId: "corr-1",
            CausationId: "cause-1");

        var nextState = await compiled.CompiledDefinition.ApplyDomainEventAsync(
            new Dictionary<string, Any>(StringComparer.Ordinal)
            {
                ["seed"] = Any.Pack(new StringValue { Value = "seed-state" }),
            },
            domainEvent,
            CancellationToken.None);
        nextState.Should().NotBeNull();
        nextState!.Should().ContainKey("state");
        nextState["state"].Unpack<StringValue>().Value.Should().Be("state:ClaimApprovedEvent");

        var nextReadModel = await compiled.CompiledDefinition.ReduceReadModelAsync(
            new Dictionary<string, Any>(StringComparer.Ordinal)
            {
                ["seed"] = Any.Pack(new StringValue { Value = "seed-readmodel" }),
            },
            domainEvent,
            CancellationToken.None);
        nextReadModel.Should().NotBeNull();
        nextReadModel!.Should().ContainKey("view");
        nextReadModel["view"].Unpack<StringValue>().Value.Should().Be("projection:ClaimApprovedEvent");
    }

    [Fact]
    public async Task PackageRuntime_ShouldAllowScriptToPublishAndSendViaCapabilities()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
var source = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class CapabilityScript : IScriptPackageRuntime
{
    public async Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        await context.Capabilities!.PublishAsync(new StringValue { Value = "PublishedEvent" }, EventDirection.Up, ct);
        await context.Capabilities.SendToAsync("target-1", new StringValue { Value = "SentEvent" }, ct);
        return new ScriptHandlerResult(new IMessage[] { new StringValue { Value = requestedEvent.EventType } });
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentState);

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentReadModel);
}
""";

        var compiled = await compiler.CompileAsync(
            new ScriptPackageCompilationRequest("script-capability", "rev-1", source),
            CancellationToken.None);
        compiled.IsSuccess.Should().BeTrue();

        var capabilities = new RecordingCapabilities();
        var context = new ScriptExecutionContext(
            ActorId: "runtime-1",
            ScriptId: "script-capability",
            Revision: "rev-1",
            RunId: "run-1",
            CorrelationId: "corr-1",
            Capabilities: capabilities);

        var result = await compiled.CompiledDefinition!.HandleRequestedEventAsync(
            new ScriptRequestedEventEnvelope(
                "claim.submitted",
                Any.Pack(new Struct()),
                "evt-1",
                "corr-1",
                "cause-1"),
            context,
            CancellationToken.None);

        result.DomainEvents.Should().ContainSingle();
        capabilities.Published.Should().ContainSingle(x => x.Direction == EventDirection.Up && x.EventName == "PublishedEvent");
        capabilities.Sent.Should().ContainSingle(x => x.TargetActorId == "target-1" && x.EventName == "SentEvent");
    }

    private sealed class RecordingCapabilities : IScriptRuntimeCapabilities
    {
        public List<(EventDirection Direction, string EventName)> Published { get; } = [];
        public List<(string TargetActorId, string EventName)> Sent { get; } = [];

        public Task<string> AskAIAsync(string prompt, CancellationToken ct) => Task.FromResult("ok");

        public Task InvokeAgentAsync(string targetAgentId, IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;

        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) =>
            Task.FromResult(actorId ?? "created");

        public Task DestroyAgentAsync(string actorId, CancellationToken ct) => Task.CompletedTask;

        public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;

        public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;

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

        public Task PublishAsync(IMessage eventPayload, EventDirection direction, CancellationToken ct)
        {
            var eventName = eventPayload is StringValue sv ? sv.Value : eventPayload.Descriptor.Name;
            Published.Add((direction, eventName));
            return Task.CompletedTask;
        }

        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct)
        {
            var eventName = eventPayload is StringValue sv ? sv.Value : eventPayload.Descriptor.Name;
            Sent.Add((targetActorId, eventName));
            return Task.CompletedTask;
        }
    }
}
