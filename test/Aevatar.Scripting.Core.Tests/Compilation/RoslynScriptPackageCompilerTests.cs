using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Compilation;

public class RoslynScriptPackageCompilerTests
{
    [Fact]
    public async Task CompileAsync_ShouldReject_WhenSandboxPolicyFails()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
        var request = new ScriptPackageCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-1",
            Source: "Task.Run(() => 1);");

        var result = await compiler.CompileAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().NotBeEmpty();
        result.CompiledDefinition.Should().BeNull();
    }

    [Fact]
    public async Task CompileAsync_ShouldReject_WhenSourceHasSyntaxError()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
        var request = new ScriptPackageCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-2",
            Source: "if (true {");

        var result = await compiler.CompileAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().NotBeEmpty();
        result.CompiledDefinition.Should().BeNull();
    }

    [Fact]
    public async Task CompileAsync_ShouldCreateDefinition_WhenSourceIsValid()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
        var request = new ScriptPackageCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-3",
            Source: """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class PlainScript : IScriptPackageRuntime
{
    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
        => Task.FromResult(new ScriptHandlerResult(System.Array.Empty<IMessage>()));

    public ValueTask<System.Collections.Generic.IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(System.Collections.Generic.IReadOnlyDictionary<string, Any> currentState, ScriptDomainEventEnvelope domainEvent, CancellationToken ct)
        => ValueTask.FromResult<System.Collections.Generic.IReadOnlyDictionary<string, Any>?>(currentState);

    public ValueTask<System.Collections.Generic.IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(System.Collections.Generic.IReadOnlyDictionary<string, Any> currentReadModel, ScriptDomainEventEnvelope domainEvent, CancellationToken ct)
        => ValueTask.FromResult<System.Collections.Generic.IReadOnlyDictionary<string, Any>?>(currentReadModel);
}
""");

        var result = await compiler.CompileAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
        result.CompiledDefinition.Should().NotBeNull();
        result.CompiledDefinition!.ScriptId.Should().Be("script-1");
        result.CompiledDefinition!.Revision.Should().Be("rev-3");
        result.ContractManifest.Should().NotBeNull();
        result.CompiledDefinition!.ContractManifest.Should().NotBeNull();
    }

    [Fact]
    public async Task CompileAsync_ShouldReject_WhenRuntimeInterfaceIsOnlyMentionedButNotImplemented()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
        var request = new ScriptPackageCompilationRequest(
            ScriptId: "script-invalid-runtime",
            Revision: "rev-invalid-runtime",
            Source: """
// IScriptPackageRuntime should be implemented by a concrete runtime class.
using System.Threading;
using System.Threading.Tasks;

public sealed class InvalidRuntimeScript
{
    public Task<string> HandleRequestedEventAsync(CancellationToken ct) => Task.FromResult("invalid");
}
""");

        var result = await compiler.CompileAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.CompiledDefinition.Should().BeNull();
        result.Diagnostics.Should().Contain(x => x.Contains("IScriptPackageRuntime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompileAsync_ShouldExtractContractManifest_FromScriptContractProvider()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
        var request = new ScriptPackageCompilationRequest(
            ScriptId: "script-claim",
            Revision: "rev-contract",
            Source: """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ContractOnlyScript : IScriptPackageRuntime, IScriptContractProvider
{
    public ScriptContractManifest ContractManifest => new(
        "claim_case_v1",
        new [] { "ClaimApprovedEvent", "ClaimManualReviewRequestedEvent", "ClaimRejectedEvent" },
        "claim_runtime_state_v1",
        "claim_case_readmodel_v1");

    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
        => Task.FromResult(new ScriptHandlerResult(System.Array.Empty<IMessage>()));

    public ValueTask<System.Collections.Generic.IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(System.Collections.Generic.IReadOnlyDictionary<string, Any> currentState, ScriptDomainEventEnvelope domainEvent, CancellationToken ct)
        => ValueTask.FromResult<System.Collections.Generic.IReadOnlyDictionary<string, Any>?>(currentState);

    public ValueTask<System.Collections.Generic.IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(System.Collections.Generic.IReadOnlyDictionary<string, Any> currentReadModel, ScriptDomainEventEnvelope domainEvent, CancellationToken ct)
        => ValueTask.FromResult<System.Collections.Generic.IReadOnlyDictionary<string, Any>?>(currentReadModel);
}
""");

        var result = await compiler.CompileAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ContractManifest.Should().NotBeNull();
        result.ContractManifest!.InputSchema.Should().Be("claim_case_v1");
        result.ContractManifest.StateSchema.Should().Be("claim_runtime_state_v1");
        result.ContractManifest.ReadModelSchema.Should().Be("claim_case_readmodel_v1");
        result.ContractManifest.OutputEvents.Should().Equal(
            "ClaimApprovedEvent",
            "ClaimManualReviewRequestedEvent",
            "ClaimRejectedEvent");
    }

    [Fact]
    public async Task CompileAsync_ShouldExtractReadModelSchemaDefinition_FromScriptContractProvider()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
        var request = new ScriptPackageCompilationRequest(
            ScriptId: "script-schema",
            Revision: "rev-schema-1",
            Source: """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class SchemaScript : IScriptPackageRuntime, IScriptContractProvider
{
    public ScriptContractManifest ContractManifest => new(
        "claim_case_v1",
        new[] { "ClaimApprovedEvent" },
        "claim_runtime_state_v1",
        "claim_case_readmodel_v2",
        new ScriptReadModelDefinition(
            "claim_case",
            "2",
            new[]
            {
                new ScriptReadModelFieldDefinition("claim_case_id", "keyword", "claim_case_id", false),
                new ScriptReadModelFieldDefinition("risk_score", "double", "risk_score", false),
                new ScriptReadModelFieldDefinition("decision", "keyword", "decision", false),
            },
            new[]
            {
                new ScriptReadModelIndexDefinition("idx_claim_case_id", new[] { "claim_case_id" }, true, "elasticsearch"),
                new ScriptReadModelIndexDefinition("idx_decision", new[] { "decision" }, false, "elasticsearch"),
            },
            new[]
            {
                new ScriptReadModelRelationDefinition(
                    "rel_policy",
                    "policy_id",
                    "policy",
                    "policy_id",
                    "many_to_one",
                    "neo4j"),
            }),
        new[] { "elasticsearch", "neo4j" });

    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
        => Task.FromResult(new ScriptHandlerResult(System.Array.Empty<IMessage>()));

    public ValueTask<System.Collections.Generic.IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(System.Collections.Generic.IReadOnlyDictionary<string, Any> currentState, ScriptDomainEventEnvelope domainEvent, CancellationToken ct)
        => ValueTask.FromResult<System.Collections.Generic.IReadOnlyDictionary<string, Any>?>(currentState);

    public ValueTask<System.Collections.Generic.IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(System.Collections.Generic.IReadOnlyDictionary<string, Any> currentReadModel, ScriptDomainEventEnvelope domainEvent, CancellationToken ct)
        => ValueTask.FromResult<System.Collections.Generic.IReadOnlyDictionary<string, Any>?>(currentReadModel);
}
""");

        var result = await compiler.CompileAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ContractManifest.Should().NotBeNull();
        result.ContractManifest!.ReadModelDefinition.Should().NotBeNull();
        result.ContractManifest.ReadModelDefinition!.SchemaId.Should().Be("claim_case");
        result.ContractManifest.ReadModelDefinition.SchemaVersion.Should().Be("2");
        result.ContractManifest.ReadModelDefinition.Fields.Should().HaveCount(3);
        result.ContractManifest.ReadModelDefinition.Indexes.Should().HaveCount(2);
        result.ContractManifest.ReadModelDefinition.Relations.Should().ContainSingle(x => x.Name == "rel_policy");
        result.ContractManifest.ReadModelStoreCapabilities.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task HandleApplyReduce_ShouldExecuteScriptPackageRuntimeContract()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
        var request = new ScriptPackageCompilationRequest(
            ScriptId: "script-contract",
            Revision: "rev-runtime-1",
            Source: """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ContractRuntimeScript : IScriptPackageRuntime
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
""");

        var compileResult = await compiler.CompileAsync(request, CancellationToken.None);
        compileResult.IsSuccess.Should().BeTrue();

        var decision = await compileResult.CompiledDefinition!.HandleRequestedEventAsync(
            new ScriptRequestedEventEnvelope(
                "claim.submitted",
                Any.Pack(new Struct { Fields = { ["caseId"] = Google.Protobuf.WellKnownTypes.Value.ForString("C-1") } }),
                "evt-1",
                "corr-1",
                "cause-1"),
            new ScriptExecutionContext(
                ActorId: "runtime-1",
                ScriptId: "script-contract",
                Revision: "rev-runtime-1",
                InputPayload: Any.Pack(new Struct { Fields = { ["caseId"] = Google.Protobuf.WellKnownTypes.Value.ForString("C-1") } })),
            CancellationToken.None);

        decision.DomainEvents.Should().ContainSingle();
        ((StringValue)decision.DomainEvents[0]).Value.Should().Be("claim.submitted");

        var domainEvent = new ScriptDomainEventEnvelope(
            EventType: "ClaimApprovedEvent",
            Payload: Any.Pack(new Struct()),
            EventId: "evt-2",
            CorrelationId: "corr-1",
            CausationId: "cause-1");

        var state = await compileResult.CompiledDefinition.ApplyDomainEventAsync(
            new Dictionary<string, Any>(StringComparer.Ordinal)
            {
                ["seed"] = Any.Pack(new StringValue { Value = "seed-state" }),
            },
            domainEvent,
            CancellationToken.None);
        state.Should().NotBeNull();
        state!.Should().ContainKey("state");
        state["state"].Unpack<StringValue>().Value.Should().Be("state:ClaimApprovedEvent");

        var readModel = await compileResult.CompiledDefinition.ReduceReadModelAsync(
            new Dictionary<string, Any>(StringComparer.Ordinal)
            {
                ["seed"] = Any.Pack(new StringValue { Value = "seed-read-model" }),
            },
            domainEvent,
            CancellationToken.None);
        readModel.Should().NotBeNull();
        readModel!.Should().ContainKey("view");
        readModel["view"].Unpack<StringValue>().Value.Should().Be("projection:ClaimApprovedEvent");
    }

    [Fact]
    public async Task HandleRequestedEvent_ShouldAllowScriptToUseCapabilities_IncludingPublishAndSendTo()
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
        var request = new ScriptPackageCompilationRequest(
            ScriptId: "script-capability-1",
            Revision: "rev-capability-1",
            Source: """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class CapabilityRuntimeScript : IScriptPackageRuntime
{
    public async Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        var aiResult = await context.Capabilities!.AskAIAsync("risk-assessment", ct);
        if (string.Equals(aiResult, "manual-review", StringComparison.Ordinal))
        {
            await context.Capabilities.PublishAsync(new StringValue { Value = "PublishedEvent" }, EventDirection.Up, ct);
            await context.Capabilities.SendToAsync("target-1", new StringValue { Value = "SentEvent" }, ct);

            var reviewActorId = await context.Capabilities.CreateAgentAsync("Fake.ManualReviewAgent, Fake", "manual-" + context.RunId, ct);
            await context.Capabilities.InvokeAgentAsync(reviewActorId, new StringValue { Value = "ClaimManualReviewRequestedEvent" }, ct);

            return new ScriptHandlerResult(
                new IMessage[] { new StringValue { Value = requestedEvent.EventType } },
                new Dictionary<string, Any>
                {
                    ["state"] = Any.Pack(new StringValue { Value = "manual-review" }),
                },
                new Dictionary<string, Any>
                {
                    ["view"] = Any.Pack(new StringValue { Value = "manual-review" }),
                });
        }

        return new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = "ClaimApprovedEvent" } },
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new StringValue { Value = "approved" }),
            },
            new Dictionary<string, Any>
            {
                ["view"] = Any.Pack(new StringValue { Value = "approved" }),
            });
    }

    public ValueTask<System.Collections.Generic.IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(System.Collections.Generic.IReadOnlyDictionary<string, Any> currentState, ScriptDomainEventEnvelope domainEvent, CancellationToken ct)
        => ValueTask.FromResult<System.Collections.Generic.IReadOnlyDictionary<string, Any>?>(currentState);

    public ValueTask<System.Collections.Generic.IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(System.Collections.Generic.IReadOnlyDictionary<string, Any> currentReadModel, ScriptDomainEventEnvelope domainEvent, CancellationToken ct)
        => ValueTask.FromResult<System.Collections.Generic.IReadOnlyDictionary<string, Any>?>(currentReadModel);
}
""");

        var compileResult = await compiler.CompileAsync(request, CancellationToken.None);
        compileResult.IsSuccess.Should().BeTrue();

        var capabilities = new RecordingScriptRuntimeCapabilities("manual-review");
        var decision = await compileResult.CompiledDefinition!.HandleRequestedEventAsync(
            new ScriptRequestedEventEnvelope(
                "claim.submitted",
                Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["caseId"] = Google.Protobuf.WellKnownTypes.Value.ForString("Case-B"),
                        ["riskScore"] = Google.Protobuf.WellKnownTypes.Value.ForNumber(0.91),
                        ["compliancePassed"] = Google.Protobuf.WellKnownTypes.Value.ForBool(true),
                    },
                }),
                "evt-1",
                "corr-1",
                "cause-1"),
            new ScriptExecutionContext(
                ActorId: "runtime-1",
                ScriptId: "script-capability-1",
                Revision: "rev-capability-1",
                RunId: "run-capability-1",
                CorrelationId: "corr-capability-1",
                CurrentState: new Dictionary<string, Any>(StringComparer.Ordinal)
                {
                    ["seed"] = Any.Pack(new StringValue { Value = "seed" }),
                },
                InputPayload: Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["caseId"] = Google.Protobuf.WellKnownTypes.Value.ForString("Case-B"),
                        ["riskScore"] = Google.Protobuf.WellKnownTypes.Value.ForNumber(0.91),
                        ["compliancePassed"] = Google.Protobuf.WellKnownTypes.Value.ForBool(true),
                    },
                }),
                Capabilities: capabilities),
            CancellationToken.None);

        decision.DomainEvents.Select(x => ((StringValue)x).Value)
            .Should().ContainSingle(x => x == "claim.submitted");
        decision.StatePayloads.Should().NotBeNull();
        decision.StatePayloads!.Should().ContainKey("state");
        decision.StatePayloads["state"].Unpack<StringValue>().Value.Should().Be("manual-review");
        decision.ReadModelPayloads.Should().NotBeNull();
        decision.ReadModelPayloads!.Should().ContainKey("view");
        decision.ReadModelPayloads["view"].Unpack<StringValue>().Value.Should().Be("manual-review");
        capabilities.AskPrompts.Should().ContainSingle(x => x == "risk-assessment");
        capabilities.Published.Should().ContainSingle(x => x.Direction == EventDirection.Up && x.EventName == "PublishedEvent");
        capabilities.Sent.Should().ContainSingle(x => x.TargetActorId == "target-1" && x.EventName == "SentEvent");
        capabilities.CreatedAgents.Should().ContainSingle(x => x.actorId == "manual-run-capability-1");
        capabilities.Invocations.Should().ContainSingle(x => x.target == "manual-run-capability-1" && x.eventName == "ClaimManualReviewRequestedEvent");
    }

    private sealed class RecordingScriptRuntimeCapabilities(string aiResult) : IScriptRuntimeCapabilities
    {
        public List<string> AskPrompts { get; } = [];
        public List<(EventDirection Direction, string EventName)> Published { get; } = [];
        public List<(string TargetActorId, string EventName)> Sent { get; } = [];
        public List<(string typeName, string? actorId)> CreatedAgents { get; } = [];
        public List<(string target, string eventName)> Invocations { get; } = [];

        public Task<string> AskAIAsync(string prompt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            AskPrompts.Add(prompt);
            return Task.FromResult(aiResult);
        }

        public Task PublishAsync(IMessage eventPayload, EventDirection direction, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var eventName = eventPayload is StringValue sv ? sv.Value : eventPayload.Descriptor.Name;
            Published.Add((direction, eventName));
            return Task.CompletedTask;
        }

        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var eventName = eventPayload is StringValue sv ? sv.Value : eventPayload.Descriptor.Name;
            Sent.Add((targetActorId, eventName));
            return Task.CompletedTask;
        }

        public Task InvokeAgentAsync(string targetAgentId, IMessage eventPayload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var eventName = eventPayload is StringValue sv ? sv.Value : eventPayload.Descriptor.Name;
            Invocations.Add((targetAgentId, eventName));
            return Task.CompletedTask;
        }

        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CreatedAgents.Add((agentTypeAssemblyQualifiedName, actorId));
            return Task.FromResult(actorId ?? "created-agent");
        }

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
                    DefinitionActorId: proposal.DefinitionActorId,
                    CatalogActorId: proposal.CatalogActorId,
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
