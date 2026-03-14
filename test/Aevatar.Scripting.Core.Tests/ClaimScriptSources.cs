using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Scripting.Core.Tests;

internal static class ClaimScriptSources
{
    public static readonly string DecisionBehavior =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Core.Tests.Messages;

        public sealed class ClaimDecisionBehavior : ScriptBehavior<ClaimState, ClaimReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ClaimState, ClaimReadModel> builder)
            {
                builder
                    .OnCommand<ClaimSubmitted>(HandleAsync)
                    .OnEvent<ClaimDecisionRecorded>(
                        apply: static (_, evt, _) => evt.Current == null
                            ? new ClaimState()
                            : new ClaimState
                            {
                                CaseId = evt.Current.CaseId,
                                PolicyId = evt.Current.PolicyId,
                                DecisionStatus = evt.Current.DecisionStatus,
                                ManualReviewRequired = evt.Current.ManualReviewRequired,
                                AiSummary = evt.Current.AiSummary,
                                RiskScore = evt.Current.RiskScore,
                                CompliancePassed = evt.Current.CompliancePassed,
                                LastCommandId = evt.CommandId ?? string.Empty,
                            },
                        reduce: static (_, evt, _) => evt.Current)
                    .OnQuery<ClaimQueryRequested, ClaimQueryResponded>(HandleQueryAsync);
            }

            private static async Task HandleAsync(
                ClaimSubmitted command,
                ScriptCommandContext<ClaimState> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                await context.RuntimeCapabilities.SendToAsync(
                    "claim-analyst-" + context.RunId,
                    new ClaimAnalystReviewRequested { CaseId = command.CaseId ?? string.Empty },
                    ct);
                await context.RuntimeCapabilities.SendToAsync(
                    "claim-fraud-" + context.RunId,
                    new ClaimFraudScoringRequested { CaseId = command.CaseId ?? string.Empty },
                    ct);
                await context.RuntimeCapabilities.SendToAsync(
                    "claim-compliance-" + context.RunId,
                    new ClaimComplianceCheckRequested { CaseId = command.CaseId ?? string.Empty },
                    ct);

                var decisionStatus = "Approved";
                var manualReviewRequired = false;
                if (string.Equals(command.CaseId, "Case-B", StringComparison.Ordinal) || command.RiskScore >= 0.85d)
                {
                    decisionStatus = "ManualReview";
                    manualReviewRequired = true;
                    await context.RuntimeCapabilities.SendToAsync(
                        "claim-manual-review-" + context.RunId,
                        new ClaimManualReviewRequested { CaseId = command.CaseId ?? string.Empty },
                        ct);
                }
                else if (string.Equals(command.CaseId, "Case-C", StringComparison.Ordinal) || !command.CompliancePassed)
                {
                    decisionStatus = "Rejected";
                }

                var current = new ClaimReadModel
                {
                    HasValue = true,
                    CaseId = command.CaseId ?? string.Empty,
                    PolicyId = command.PolicyId ?? string.Empty,
                    DecisionStatus = decisionStatus,
                    ManualReviewRequired = manualReviewRequired,
                    AiSummary = manualReviewRequired ? "high-risk-profile" : "normal-profile",
                    RiskScore = command.RiskScore,
                    CompliancePassed = command.CompliancePassed,
                    LastCommandId = command.CommandId ?? string.Empty,
                    Search = new ClaimSearchIndex
                    {
                        LookupKey = string.Concat(command.CaseId ?? string.Empty, ":", command.PolicyId ?? string.Empty).ToLowerInvariant(),
                        DecisionKey = decisionStatus.ToLowerInvariant(),
                    },
                    Refs = new ClaimRefs
                    {
                        PolicyId = command.PolicyId ?? string.Empty,
                        OwnerActorId = context.ActorId,
                    },
                };
                current.TraceSteps.Add("facts");
                current.TraceSteps.Add("risk");
                current.TraceSteps.Add("compliance");
                current.TraceSteps.Add(decisionStatus);

                context.Emit(new ClaimDecisionRecorded
                {
                    CommandId = command.CommandId ?? string.Empty,
                    Current = current,
                });
            }

            private static Task<ClaimQueryResponded?> HandleQueryAsync(
                ClaimQueryRequested query,
                ScriptQueryContext<ClaimReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ClaimQueryResponded?>(new ClaimQueryResponded
                {
                    RequestId = query.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new ClaimReadModel(),
                });
            }
        }
        """;

    public static readonly string DecisionBehaviorHash = ComputeSourceHash(DecisionBehavior);

    public static readonly string RoleBehavior =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Core.Tests.Messages;

        public sealed class ClaimRoleBehavior : ScriptBehavior<ClaimState, ClaimReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ClaimState, ClaimReadModel> builder)
            {
                builder
                    .OnCommand<ClaimSubmitted>(HandleAsync)
                    .OnEvent<ClaimDecisionRecorded>(
                        apply: static (_, evt, _) => evt.Current == null
                            ? new ClaimState()
                            : new ClaimState
                            {
                                CaseId = evt.Current.CaseId,
                                PolicyId = evt.Current.PolicyId,
                                DecisionStatus = evt.Current.DecisionStatus,
                                ManualReviewRequired = evt.Current.ManualReviewRequired,
                                AiSummary = evt.Current.AiSummary,
                                RiskScore = evt.Current.RiskScore,
                                CompliancePassed = evt.Current.CompliancePassed,
                                LastCommandId = evt.CommandId ?? string.Empty,
                            },
                        reduce: static (_, evt, _) => evt.Current)
                    .OnQuery<ClaimQueryRequested, ClaimQueryResponded>(HandleQueryAsync);
            }

            private static async Task HandleAsync(
                ClaimSubmitted command,
                ScriptCommandContext<ClaimState> context,
                CancellationToken ct)
            {
                var aiSummary = await context.RuntimeCapabilities.AskAIAsync(
                    "extract claim facts for " + (command.CaseId ?? string.Empty),
                    ct);
                var current = new ClaimReadModel
                {
                    HasValue = true,
                    CaseId = command.CaseId ?? string.Empty,
                    PolicyId = command.PolicyId ?? string.Empty,
                    DecisionStatus = string.IsNullOrWhiteSpace(aiSummary) ? "Failed" : "FactsReady",
                    ManualReviewRequired = false,
                    AiSummary = aiSummary ?? string.Empty,
                    RiskScore = command.RiskScore,
                    CompliancePassed = command.CompliancePassed,
                    LastCommandId = command.CommandId ?? string.Empty,
                };
                current.TraceSteps.Add(string.IsNullOrWhiteSpace(aiSummary) ? "ai-failed" : "ai-facts-ready");

                context.Emit(new ClaimDecisionRecorded
                {
                    CommandId = command.CommandId ?? string.Empty,
                    Current = current,
                });
            }

            private static Task<ClaimQueryResponded?> HandleQueryAsync(
                ClaimQueryRequested query,
                ScriptQueryContext<ClaimReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ClaimQueryResponded?>(new ClaimQueryResponded
                {
                    RequestId = query.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new ClaimReadModel(),
                });
            }
        }
        """;

    public static readonly string RoleBehaviorHash = ComputeSourceHash(RoleBehavior);

    private static string ComputeSourceHash(string source)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
