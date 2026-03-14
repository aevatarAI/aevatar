using Aevatar.Foundation.Abstractions;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.AI;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class ClaimComplexBusinessScenarioTests
{
    [Fact]
    public async Task Should_execute_complex_claim_business_paths_with_ai_ports_projection_and_replay()
    {
        var aiCapability = new RecordingAICapability();
        await using var provider = ClaimIntegrationTestKit.BuildProvider(
            services => services.AddSingleton<IAICapability>(aiCapability));
        var runtime = provider.GetRequiredService<IActorRuntime>();

        const string definitionActorId = "claim-complex-definition";
        const string revision = "rev-20260314-a";
        await ClaimIntegrationTestKit.UpsertOrchestratorAsync(provider, definitionActorId, CancellationToken.None);

        var caseData = new[]
        {
            new ClaimCase("Case-A", 0.12d, true, "Approved", false),
            new ClaimCase("Case-B", 0.91d, true, "ManualReview", true),
            new ClaimCase("Case-C", 0.35d, false, "Rejected", false),
        };

        foreach (var claimCase in caseData)
        {
            var runId = "run-" + claimCase.CaseId.ToLowerInvariant();
            var analystActor = await ClaimIntegrationTestKit.CreateFreshSinkActorAsync(runtime, "role-claim-analyst-" + runId);
            var fraudActor = await ClaimIntegrationTestKit.CreateFreshSinkActorAsync(runtime, "fraud-risk-agent-" + runId);
            var complianceActor = await ClaimIntegrationTestKit.CreateFreshSinkActorAsync(runtime, "compliance-rule-agent-" + runId);
            var runtimeActorId = "claim-complex-runtime-" + claimCase.CaseId.ToLowerInvariant();
            var aiCountBefore = aiCapability.Calls.Count;

            var result = await ClaimIntegrationTestKit.RunClaimAsync(
                provider,
                definitionActorId,
                runtimeActorId,
                revision,
                runId,
                new ClaimSubmitted
                {
                    CommandId = runId,
                    CaseId = claimCase.CaseId,
                    PolicyId = "POLICY-" + claimCase.CaseId[^1],
                    RiskScore = claimCase.RiskScore,
                    CompliancePassed = claimCase.CompliancePassed,
                },
                CancellationToken.None);

            var committedPayload = result.Committed.DomainEventPayload;
            committedPayload.Should().NotBeNull();
            var committed = committedPayload!.Unpack<ClaimDecisionRecorded>();
            committed.Current.DecisionStatus.Should().Be(claimCase.ExpectedDecisionStatus);
            committed.Current.ManualReviewRequired.Should().Be(claimCase.ManualReviewRequired);

            var readModelPayload = result.Snapshot.ReadModelPayload;
            readModelPayload.Should().NotBeNull();
            var readModel = readModelPayload!.Unpack<ClaimCaseReadModel>();
            readModel.CaseId.Should().Be(claimCase.CaseId);
            readModel.DecisionStatus.Should().Be(claimCase.ExpectedDecisionStatus);
            readModel.ManualReviewRequired.Should().Be(claimCase.ManualReviewRequired);

            var runtimeActor = await runtime.GetAsync(runtimeActorId);
            var runtimeAgent = runtimeActor!.Agent.Should().BeOfType<ScriptBehaviorGAgent>().Subject;
            var stateRoot = runtimeAgent.State.StateRoot;
            stateRoot.Should().NotBeNull();
            var state = stateRoot!.Unpack<ClaimCaseState>();
            state.CaseId.Should().Be(claimCase.CaseId);
            state.DecisionStatus.Should().Be(claimCase.ExpectedDecisionStatus);
            state.ManualReviewRequired.Should().Be(claimCase.ManualReviewRequired);

            var aiCalls = aiCapability.Calls.Skip(aiCountBefore).ToArray();
            aiCalls.Should().ContainSingle();
            aiCalls[0].RunId.Should().Be(runId);
            aiCalls[0].CorrelationId.Should().Be(runId);
            aiCalls[0].Prompt.Should().Contain(claimCase.CaseId);

            ClaimIntegrationTestKit.ReadMessages(analystActor).Should().ContainSingle(x => x == nameof(ClaimAnalystReviewRequested));
            ClaimIntegrationTestKit.ReadMessages(fraudActor).Should().ContainSingle(x => x == nameof(ClaimFraudScoringRequested));
            ClaimIntegrationTestKit.ReadMessages(complianceActor).Should().ContainSingle(x => x == nameof(ClaimComplianceCheckRequested));

            var manualReviewActorId = "human-review-" + runId;
            if (claimCase.ManualReviewRequired)
            {
                (await runtime.ExistsAsync(manualReviewActorId)).Should().BeTrue();
                var manualReviewActor = await runtime.GetAsync(manualReviewActorId);
                manualReviewActor.Should().NotBeNull();
                ClaimIntegrationTestKit.ReadMessages(manualReviewActor!).Should().ContainSingle(x => x == nameof(ClaimManualReviewRequested));
            }
            else
            {
                (await runtime.ExistsAsync(manualReviewActorId)).Should().BeFalse();
            }

            var stateBeforeReplay = runtimeAgent.State.StateRoot!.Clone();
            await runtime.DestroyAsync(runtimeActorId, CancellationToken.None);
            await ClaimIntegrationTestKit.EnsureRuntimeAsync(provider, definitionActorId, revision, runtimeActorId, CancellationToken.None);
            var replayedActor = await runtime.GetAsync(runtimeActorId);
            var replayedAgent = replayedActor!.Agent.Should().BeOfType<ScriptBehaviorGAgent>().Subject;
            replayedAgent.State.StateRoot.Should().NotBeNull();
            replayedAgent.State.StateRoot!.Unpack<ClaimCaseState>().Should().BeEquivalentTo(stateBeforeReplay.Unpack<ClaimCaseState>());

            await runtime.DestroyAsync(analystActor.Id, CancellationToken.None);
            await runtime.DestroyAsync(fraudActor.Id, CancellationToken.None);
            await runtime.DestroyAsync(complianceActor.Id, CancellationToken.None);
            if (await runtime.ExistsAsync(manualReviewActorId))
                await runtime.DestroyAsync(manualReviewActorId, CancellationToken.None);
        }
    }

    private sealed record ClaimCase(
        string CaseId,
        double RiskScore,
        bool CompliancePassed,
        string ExpectedDecisionStatus,
        bool ManualReviewRequired);

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
}
