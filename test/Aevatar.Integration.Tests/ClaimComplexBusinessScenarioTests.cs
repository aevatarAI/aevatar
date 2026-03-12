using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Projection.Reducers;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class ClaimComplexBusinessScenarioTests
{
    [Fact]
    public async Task Should_execute_complex_claim_business_paths_with_ai_ports_projection_and_replay()
    {
        var aiCapability = new RecordingAICapability();

        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddSingleton<IAICapability>(aiCapability);
        services.AddScriptCapability();

        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var eventStore = provider.GetRequiredService<IEventStore>();

        const string definitionActorId = "claim-complex-definition";
        const string scriptId = "claim-complex-script";
        const string revision = "rev-claim-complex-1";
        var definitionActor = await runtime.CreateAsync<ScriptDefinitionGAgent>(definitionActorId);

        await definitionActor.HandleEventAsync(
            ScriptingCommandEnvelopeTestKit.CreateUpsertDefinition(
                definitionActorId,
                scriptId,
                revision,
                ComplexClaimScriptSource,
                "hash-claim-complex-1"),
            CancellationToken.None);

        var caseData = new[]
        {
            new ClaimCase("Case-A", 0.12, true, "Approved", false),
            new ClaimCase("Case-B", 0.91, true, "ManualReview", true),
            new ClaimCase("Case-C", 0.35, false, "Rejected", false),
        };

        foreach (var claimCase in caseData)
        {
            var runId = "run-" + claimCase.CaseId.ToLowerInvariant();
            var analystActor = await CreateFreshSinkActorAsync(runtime, "role-claim-analyst-" + runId);
            var fraudActor = await CreateFreshSinkActorAsync(runtime, "fraud-risk-agent-" + runId);
            var complianceActor = await CreateFreshSinkActorAsync(runtime, "compliance-rule-agent-" + runId);
            var runtimeActorId = "claim-complex-runtime-" + claimCase.CaseId.ToLowerInvariant();
            var runtimeActor = await runtime.CreateAsync<ScriptRuntimeGAgent>(runtimeActorId);
            var aiCountBefore = aiCapability.Calls.Count;

            await runtimeActor.HandleEventAsync(
                ScriptingCommandEnvelopeTestKit.CreateRunScript(
                    runtimeActorId,
                    runId,
                    BuildClaimPayload(
                        claimCase.CaseId,
                        claimCase.RiskScore,
                        claimCase.CompliancePassed),
                    revision,
                    definitionActorId),
                CancellationToken.None);

            var runtimeState = ((ScriptRuntimeGAgent)runtimeActor.Agent).State;
            runtimeState.StatePayloads.Should().ContainKey("claim_case");
            runtimeState.ReadModelPayloads.Should().ContainKey("claim_case");

            var stateStruct = runtimeState.StatePayloads["claim_case"].Unpack<Struct>();
            stateStruct.Fields["case_id"].StringValue.Should().Be(claimCase.CaseId);
            stateStruct.Fields["decision_status"].StringValue.Should().Be(claimCase.ExpectedDecisionStatus);
            stateStruct.Fields["manual_review_required"].BoolValue.Should().Be(claimCase.ManualReviewRequired);

            var aiCalls = aiCapability.Calls.Skip(aiCountBefore).ToArray();
            aiCalls.Should().ContainSingle();
            aiCalls[0].RunId.Should().Be(runId);
            aiCalls[0].CorrelationId.Should().Be(runId);
            aiCalls[0].Prompt.Should().Contain(claimCase.CaseId);

            ReadMessages(analystActor).Should().ContainSingle(x => x == "AnalyzeClaimNarrativeEvent");
            ReadMessages(fraudActor).Should().ContainSingle(x => x == "ScoreFraudRiskEvent");
            ReadMessages(complianceActor).Should().ContainSingle(x => x == "ValidateClaimComplianceEvent");

            var manualReviewActorId = "human-review-" + runId;
            if (claimCase.ManualReviewRequired)
            {
                (await runtime.ExistsAsync(manualReviewActorId)).Should().BeTrue();
                var manualReviewActor = await runtime.GetAsync(manualReviewActorId);
                manualReviewActor.Should().NotBeNull();
                ReadMessages(manualReviewActor!).Should().ContainSingle(x => x == "RequestManualReviewEvent");
            }
            else
            {
                (await runtime.ExistsAsync(manualReviewActorId)).Should().BeFalse();
            }

            var persistedEvents = await eventStore.GetEventsAsync(runtimeActorId, ct: CancellationToken.None);
            var committed = persistedEvents
                .Last(x => x.EventData?.Is(ScriptRunDomainEventCommitted.Descriptor) == true)
                .EventData!
                .Unpack<ScriptRunDomainEventCommitted>();

            var projectionContext = new ScriptProjectionContext
            {
                ProjectionId = "claim-complex-projection-" + claimCase.CaseId.ToLowerInvariant(),
                RootActorId = runtimeActorId,
                ScriptId = scriptId,
            };
            var dispatcher = new InMemoryScriptProjectionStoreDispatcher();
            var projector = new ScriptExecutionReadModelProjector(
                dispatcher,
                new FixedProjectionClock(DateTimeOffset.UtcNow),
                [new ScriptRunDomainEventCommittedReducer()]);
            await projector.InitializeAsync(projectionContext, CancellationToken.None);
            await projector.ProjectAsync(
                projectionContext,
                new EventEnvelope
                {
                    Id = "evt-" + runId,
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Payload = Any.Pack(committed),
                    Route = new EnvelopeRoute
                    {
                        PublisherActorId = runtimeActorId,
                        Direction = EventDirection.Self,
                    },
                    Propagation = new EnvelopePropagation
                    {
                        CorrelationId = runId,
                    },
                },
                CancellationToken.None);
            var readModel = await dispatcher.GetAsync(runtimeActorId, CancellationToken.None);
            readModel.Should().NotBeNull();
            readModel!.ReadModelPayloads.Should().ContainKey("claim_case");
            var claimCaseReadModel = readModel.ReadModelPayloads["claim_case"].Unpack<Struct>();
            claimCaseReadModel.Fields["decision_status"].StringValue.Should().Be(claimCase.ExpectedDecisionStatus);

            var stateBeforeReplay = runtimeState.Clone();
            await runtime.DestroyAsync(runtimeActorId, CancellationToken.None);
            var replayedRuntime = await runtime.CreateAsync<ScriptRuntimeGAgent>(runtimeActorId);
            var replayedState = ((ScriptRuntimeGAgent)replayedRuntime.Agent).State;
            replayedState.StatePayloads.Should().BeEquivalentTo(stateBeforeReplay.StatePayloads);
            replayedState.ReadModelPayloads.Should().BeEquivalentTo(stateBeforeReplay.ReadModelPayloads);
            replayedState.Revision.Should().Be(stateBeforeReplay.Revision);

            await runtime.DestroyAsync(analystActor.Id, CancellationToken.None);
            await runtime.DestroyAsync(fraudActor.Id, CancellationToken.None);
            await runtime.DestroyAsync(complianceActor.Id, CancellationToken.None);
            if (await runtime.ExistsAsync(manualReviewActorId))
                await runtime.DestroyAsync(manualReviewActorId, CancellationToken.None);
        }
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

    private sealed record ClaimCase(
        string CaseId,
        double RiskScore,
        bool CompliancePassed,
        string ExpectedDecisionStatus,
        bool ManualReviewRequired);

    private static async Task<IActor> CreateFreshSinkActorAsync(IActorRuntime runtime, string actorId)
    {
        if (await runtime.ExistsAsync(actorId))
            await runtime.DestroyAsync(actorId, CancellationToken.None);

        return await runtime.CreateAsync<ClaimMessageSinkGAgent>(actorId, CancellationToken.None);
    }

    private static IReadOnlyList<string> ReadMessages(IActor actor) =>
        ((ClaimMessageSinkGAgent)actor.Agent).State.Values
            .Select(static value => value.StringValue)
            .ToArray();

    private sealed class RecordingAICapability : IAICapability
    {
        public List<(string RunId, string CorrelationId, string Prompt)> Calls { get; } = [];

        public Task<string> AskAsync(
            string runId,
            string correlationId,
            string prompt,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((runId, correlationId, prompt));

            var response = prompt.Contains("Case-B", StringComparison.Ordinal)
                ? "high-risk-profile"
                : "normal-profile";
            return Task.FromResult(response);
        }
    }

    private sealed class InMemoryScriptProjectionStoreDispatcher
        : IProjectionStoreDispatcher<ScriptExecutionReadModel, string>
    {
        private readonly Dictionary<string, ScriptExecutionReadModel> _store = new(StringComparer.Ordinal);

        public Task UpsertAsync(ScriptExecutionReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _store[readModel.Id] = readModel;
            return Task.CompletedTask;
        }

        public Task MutateAsync(
            string key,
            Action<ScriptExecutionReadModel> mutate,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_store.TryGetValue(key, out var readModel))
            {
                readModel = new ScriptExecutionReadModel { Id = key };
                _store[key] = readModel;
            }

            mutate(readModel);
            return Task.CompletedTask;
        }

        public Task<ScriptExecutionReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _store.TryGetValue(key, out var readModel);
            return Task.FromResult(readModel);
        }

        public Task<IReadOnlyList<ScriptExecutionReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptExecutionReadModel>>(_store.Values.Take(take).ToArray());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private const string ComplexClaimScriptSource = """
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ClaimComplexBusinessScript : IScriptPackageRuntime, IScriptContractProvider
{
    public ScriptContractManifest ContractManifest => new(
        "claim_case_v1",
        new[] { "ClaimApprovedEvent", "ClaimManualReviewRequestedEvent", "ClaimRejectedEvent" },
        "claim_runtime_state_v1",
        "claim_case_readmodel_v1",
        new ScriptReadModelDefinition(
            "claim_case",
            "1",
            new[]
            {
                new ScriptReadModelFieldDefinition("case_id", "keyword", "case_id", false),
                new ScriptReadModelFieldDefinition("decision_status", "keyword", "decision_status", false),
                new ScriptReadModelFieldDefinition("manual_review_required", "boolean", "manual_review_required", false),
                new ScriptReadModelFieldDefinition("risk_score", "double", "risk_score", false),
            },
            new[]
            {
                new ScriptReadModelIndexDefinition("idx_case_id", new[] { "case_id" }, true, "elasticsearch"),
                new ScriptReadModelIndexDefinition("idx_decision_status", new[] { "decision_status" }, false, "elasticsearch"),
            },
            new ScriptReadModelRelationDefinition[] { }),
        new[] { "elasticsearch" });

    public async Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        var input = ParseInput(requestedEvent.Payload);
        var aiSummary = await context.Capabilities!.AskAIAsync("claim-case:" + input.CaseId, ct);

        await context.Capabilities.SendToAsync(
            "role-claim-analyst-" + context.RunId,
            new StringValue { Value = "AnalyzeClaimNarrativeEvent" },
            ct);
        await context.Capabilities.SendToAsync(
            "fraud-risk-agent-" + context.RunId,
            new StringValue { Value = "ScoreFraudRiskEvent" },
            ct);
        await context.Capabilities.SendToAsync(
            "compliance-rule-agent-" + context.RunId,
            new StringValue { Value = "ValidateClaimComplianceEvent" },
            ct);

        var decisionStatus = "Approved";
        var decisionEvent = "ClaimApprovedEvent";
        var manualReviewRequired = false;

        if (input.RiskScore >= 0.85m || aiSummary.Contains("high-risk", StringComparison.OrdinalIgnoreCase))
        {
            decisionStatus = "ManualReview";
            decisionEvent = "ClaimManualReviewRequestedEvent";
            manualReviewRequired = true;
            var manualReviewAgentId = await context.Capabilities.CreateAgentAsync(
                "Aevatar.Integration.Tests.ClaimMessageSinkGAgent, Aevatar.Integration.Tests",
                "human-review-" + context.RunId,
                ct);
            await context.Capabilities.SendToAsync(
                manualReviewAgentId,
                new StringValue { Value = "RequestManualReviewEvent" },
                ct);
        }
        else if (!input.CompliancePassed)
        {
            decisionStatus = "Rejected";
            decisionEvent = "ClaimRejectedEvent";
        }

        var snapshot = BuildSnapshot(
            input.CaseId,
            aiSummary,
            input.RiskScore,
            input.CompliancePassed,
            decisionStatus,
            manualReviewRequired);

        return new ScriptHandlerResult(
            new IMessage[]
            {
                new StringValue { Value = "ClaimFactsExtractedEvent" },
                new StringValue { Value = "ClaimRiskScoredEvent" },
                new StringValue { Value = "ClaimComplianceCheckedEvent" },
                new StringValue { Value = decisionEvent },
            },
            new Dictionary<string, Any>
            {
                ["claim_case"] = Any.Pack(snapshot),
            },
            new Dictionary<string, Any>
            {
                ["claim_case"] = Any.Pack(snapshot),
            });
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

    private static Struct BuildSnapshot(
        string caseId,
        string aiSummary,
        decimal riskScore,
        bool compliancePassed,
        string decisionStatus,
        bool manualReviewRequired)
    {
        return new Struct
        {
            Fields =
            {
                ["case_id"] = Value.ForString(caseId),
                ["ai_summary"] = Value.ForString(aiSummary),
                ["risk_score"] = Value.ForNumber((double)riskScore),
                ["compliance_passed"] = Value.ForBool(compliancePassed),
                ["decision_status"] = Value.ForString(decisionStatus),
                ["manual_review_required"] = Value.ForBool(manualReviewRequired),
            },
        };
    }

    private static ClaimInput ParseInput(Any payload)
    {
        if (payload != null && payload.Is(Struct.Descriptor))
        {
            var root = payload.Unpack<Struct>();
            var caseId = root.Fields.TryGetValue("caseId", out var caseIdValue)
                ? caseIdValue.StringValue
                : string.Empty;
            var riskScore = root.Fields.TryGetValue("riskScore", out var riskScoreValue)
                ? (decimal)riskScoreValue.NumberValue
                : 0m;
            var compliancePassed = root.Fields.TryGetValue("compliancePassed", out var complianceValue)
                && complianceValue.BoolValue;

            return new ClaimInput
            {
                CaseId = caseId,
                RiskScore = riskScore,
                CompliancePassed = compliancePassed,
            };
        }

        return new ClaimInput();
    }

    private sealed class ClaimInput
    {
        public string CaseId { get; set; } = string.Empty;
        public decimal RiskScore { get; set; }
        public bool CompliancePassed { get; set; }
    }
}
""";
}
