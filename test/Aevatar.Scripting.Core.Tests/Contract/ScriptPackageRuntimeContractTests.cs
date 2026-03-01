using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
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

    public ValueTask<string> ApplyDomainEventAsync(
        string currentStateJson,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult("{\"state\":\"" + domainEvent.EventType + "\"}");
    }

    public ValueTask<string> ReduceReadModelAsync(
        string currentReadModelJson,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult("{\"projection\":\"" + domainEvent.EventType + "\"}");
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
            PayloadJson: "{\"caseId\":\"C-1\"}",
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
            PayloadJson: "{}",
            EventId: "evt-2",
            CorrelationId: "corr-1",
            CausationId: "cause-1");

        var nextState = await compiled.CompiledDefinition.ApplyDomainEventAsync("{}", domainEvent, CancellationToken.None);
        nextState.Should().Be("{\"state\":\"ClaimApprovedEvent\"}");

        var nextReadModel = await compiled.CompiledDefinition.ReduceReadModelAsync("{}", domainEvent, CancellationToken.None);
        nextReadModel.Should().Be("{\"projection\":\"ClaimApprovedEvent\"}");
    }

    [Fact]
    public async Task PackageRuntime_ShouldAllowScriptToPublishAndSendViaCapabilities()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
        var source = """
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

    public ValueTask<string> ApplyDomainEventAsync(string currentStateJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct)
        => ValueTask.FromResult(currentStateJson);

    public ValueTask<string> ReduceReadModelAsync(string currentReadModelJson, ScriptDomainEventEnvelope domainEvent, CancellationToken ct)
        => ValueTask.FromResult(currentReadModelJson);
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
            new ScriptRequestedEventEnvelope("claim.submitted", "{}", "evt-1", "corr-1", "cause-1"),
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
