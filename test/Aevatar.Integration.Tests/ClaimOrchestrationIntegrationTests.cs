using Aevatar.Integration.Tests.Fixtures.ScriptDocuments;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Integration.Tests;

public class ClaimOrchestrationIntegrationTests
{
    [Fact]
    public void Should_not_resolve_agents_from_IServiceProvider()
    {
        typeof(ScriptExecutionContext).GetProperty("Services").Should().BeNull();
        typeof(IScriptRuntimeCapabilities).GetMethod(
            "GetRequiredService",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Static).Should().BeNull();
    }

    [Fact]
    public async Task Should_call_agents_via_actor_messaging_and_runtime_only()
    {
        var document = ClaimScriptScenarioDocument.CreateEmbedded();
        var orchestratorScript = document.Scripts.Single(x => x.ScriptId == "claim_orchestrator");
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
        var compilation = await compiler.CompileAsync(
            new ScriptPackageCompilationRequest(orchestratorScript.ScriptId, orchestratorScript.Revision, orchestratorScript.Source),
            CancellationToken.None);

        var runtime = new RecordingRuntime();
        var sender = new RecordingMessageSender();
        var orchestrator = new ClaimRuntimeOrchestrator(compilation.CompiledDefinition!, runtime, sender);

        await orchestrator.ExecuteAsync(
            runId: "run-claim-b",
            correlationId: "corr-claim-b",
            inputPayload: BuildClaimPayload("Case-B", 0.91, true),
            CancellationToken.None);

        sender.Calls.Should().Contain(x => x.PayloadValue == "ClaimFactsExtractionRequestedEvent");
        sender.Calls.Should().Contain(x => x.PayloadValue == "ClaimRiskScoringRequestedEvent");
        sender.Calls.Should().Contain(x => x.PayloadValue == "ClaimComplianceValidationRequestedEvent");
        sender.Calls.Should().Contain(x => x.PayloadValue == "ClaimManualReviewRequestedEvent");
        runtime.Created.Should().HaveCount(1);
    }

    [Fact]
    public async Task Should_not_create_manual_review_agent_when_not_needed()
    {
        var document = ClaimScriptScenarioDocument.CreateEmbedded();
        var orchestratorScript = document.Scripts.Single(x => x.ScriptId == "claim_orchestrator");
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
        var compilation = await compiler.CompileAsync(
            new ScriptPackageCompilationRequest(orchestratorScript.ScriptId, orchestratorScript.Revision, orchestratorScript.Source),
            CancellationToken.None);

        var runtime = new RecordingRuntime();
        var sender = new RecordingMessageSender();
        var orchestrator = new ClaimRuntimeOrchestrator(compilation.CompiledDefinition!, runtime, sender);

        await orchestrator.ExecuteAsync(
            runId: "run-claim-a",
            correlationId: "corr-claim-a",
            inputPayload: BuildClaimPayload("Case-A", 0.12, true),
            CancellationToken.None);

        sender.Calls.Should().Contain(x => x.PayloadValue == "ClaimApprovedEvent");
        runtime.Created.Should().BeEmpty();
    }

    private sealed class ClaimRuntimeOrchestrator(
        Aevatar.Scripting.Abstractions.Definitions.IScriptPackageDefinition definition,
        IActorRuntime runtime,
        RecordingMessageSender sender)
    {
        public async Task ExecuteAsync(
            string runId,
            string correlationId,
            Any inputPayload,
            CancellationToken ct)
        {
            var decision = await definition.HandleRequestedEventAsync(
                new Aevatar.Scripting.Abstractions.Definitions.ScriptRequestedEventEnvelope(
                    EventType: "claim.submitted",
                    Payload: inputPayload,
                    EventId: "evt-" + runId,
                    CorrelationId: correlationId,
                    CausationId: "cause-" + runId),
                new Aevatar.Scripting.Abstractions.Definitions.ScriptExecutionContext(
                    ActorId: "orchestrator-runtime",
                    ScriptId: definition.ScriptId,
                    Revision: definition.Revision,
                    RunId: runId,
                    CorrelationId: correlationId,
                    InputPayload: inputPayload),
                ct);

            foreach (var evt in decision.DomainEvents.OfType<StringValue>())
            {
                var eventName = evt.Value ?? string.Empty;
                if (string.Equals(eventName, "ClaimManualReviewRequestedEvent", StringComparison.Ordinal))
                {
                    var reviewActorId = await CreateRequiredAgentAsync(
                        runtime,
                        typeof(ScriptRuntimeGAgent).AssemblyQualifiedName!,
                        "manual-review-" + runId,
                        ct);
                    await sender.SendToAsync(
                        reviewActorId,
                        new StringValue { Value = eventName },
                        ct);
                    continue;
                }

                await sender.SendToAsync(
                    targetActorId: "agent-" + eventName,
                    eventPayload: new StringValue { Value = eventName },
                    ct: ct);
            }
        }

        private static async Task<string> CreateRequiredAgentAsync(
            IActorRuntime runtime,
            string agentTypeAssemblyQualifiedName,
            string? actorId,
            CancellationToken ct)
        {
            var agentType = global::System.Type.GetType(
                agentTypeAssemblyQualifiedName,
                throwOnError: false,
                ignoreCase: false)
                ?? throw new InvalidOperationException($"Unable to resolve GAgent type: {agentTypeAssemblyQualifiedName}");
            if (!typeof(IAgent).IsAssignableFrom(agentType))
                throw new InvalidOperationException(
                    $"Resolved type does not implement IAgent: {agentTypeAssemblyQualifiedName}");

            var actor = await runtime.CreateAsync(agentType, actorId, ct);
            return actor.Id;
        }
    }

    private sealed class RecordingMessageSender
    {
        public List<(string TargetActorId, string PayloadValue, string CorrelationId)> Calls { get; } = [];

        public Task SendToAsync(
            string targetActorId,
            IMessage eventPayload,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var payloadValue = eventPayload is StringValue sv ? sv.Value : eventPayload.Descriptor.Name;
            Calls.Add((targetActorId, payloadValue, string.Empty));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRuntime : IActorRuntime
    {
        public List<(global::System.Type AgentType, string? ActorId)> Created { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(global::System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Created.Add((agentType, id));
            return Task.FromResult<IActor>(new FakeActor(id ?? "created-runtime"));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static Any BuildClaimPayload(string caseId, double riskScore, bool compliancePassed)
    {
        return Any.Pack(new Struct
        {
            Fields =
            {
                ["caseId"] = Google.Protobuf.WellKnownTypes.Value.ForString(caseId),
                ["riskScore"] = Google.Protobuf.WellKnownTypes.Value.ForNumber(riskScore),
                ["compliancePassed"] = Google.Protobuf.WellKnownTypes.Value.ForBool(compliancePassed),
            },
        });
    }
}
