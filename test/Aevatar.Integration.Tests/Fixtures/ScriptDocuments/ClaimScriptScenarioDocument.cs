using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Integration.Tests.Fixtures.ScriptDocuments;

public sealed record ClaimScriptDocumentEntry(
    string ScriptId,
    string Revision,
    string Source,
    string SourceHash);

public sealed class ClaimScriptScenarioDocument
{
    private ClaimScriptScenarioDocument(
        string documentPath,
        IReadOnlyList<ClaimScriptDocumentEntry> scripts)
    {
        DocumentPath = documentPath;
        Scripts = scripts;
    }

    public string DocumentPath { get; }

    public IReadOnlyList<ClaimScriptDocumentEntry> Scripts { get; }

    public static ClaimScriptScenarioDocument CreateEmbedded()
    {
        var entries = new List<ClaimScriptDocumentEntry>
        {
            CreateEntry("claim_orchestrator", "rev-20260314-a", ClaimOrchestratorSource),
            CreateEntry("role_claim_analyst", "rev-20260314-a", RoleClaimAnalystSource),
            CreateEntry("fraud_risk", "rev-20260314-a", FraudRiskSource),
            CreateEntry("compliance_rule", "rev-20260314-a", ComplianceRuleSource),
            CreateEntry("human_review", "rev-20260314-a", HumanReviewSource),
        };

        return new ClaimScriptScenarioDocument("embedded://claim-anti-fraud", entries);
    }

    private static ClaimScriptDocumentEntry CreateEntry(string scriptId, string revision, string source) =>
        new(scriptId, revision, source, ComputeSha256Hex(source));

    private static string ComputeSha256Hex(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private const string ClaimOrchestratorSource =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;

        public sealed class ClaimOrchestratorBehavior : ScriptBehavior<ClaimCaseState, ClaimCaseReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ClaimCaseState, ClaimCaseReadModel> builder)
            {
                builder
                    .OnCommand<ClaimSubmitted>(HandleAsync)
                    .OnEvent<ClaimDecisionRecorded>(
                        apply: static (_, evt, _) => evt.Current == null
                            ? new ClaimCaseState()
                            : new ClaimCaseState
                            {
                                CaseId = evt.Current.CaseId,
                                PolicyId = evt.Current.PolicyId,
                                DecisionStatus = evt.Current.DecisionStatus,
                                ManualReviewRequired = evt.Current.ManualReviewRequired,
                                AiSummary = evt.Current.AiSummary,
                                RiskScore = evt.Current.RiskScore,
                                CompliancePassed = evt.Current.CompliancePassed,
                                LastCommandId = evt.CommandId ?? string.Empty,
                            })
                    .ProjectState(static (state, _) => state == null
                        ? new ClaimCaseReadModel()
                        : new ClaimCaseReadModel
                        {
                            HasValue = !string.IsNullOrWhiteSpace(state.CaseId),
                            CaseId = state.CaseId,
                            PolicyId = state.PolicyId,
                            DecisionStatus = state.DecisionStatus,
                            ManualReviewRequired = state.ManualReviewRequired,
                            AiSummary = state.AiSummary,
                            RiskScore = state.RiskScore,
                            CompliancePassed = state.CompliancePassed,
                            LastCommandId = state.LastCommandId,
                            Search = new ClaimSearchIndex
                            {
                                LookupKey = string.Concat(state.CaseId, ":", state.PolicyId).ToLowerInvariant(),
                                DecisionKey = state.DecisionStatus.ToLowerInvariant(),
                            },
                            Refs = new ClaimRefs
                            {
                                PolicyId = state.PolicyId,
                            },
                            TraceSteps = { state.TraceSteps },
                        });
            }

            private static async Task HandleAsync(
                ClaimSubmitted command,
                ScriptCommandContext<ClaimCaseState> context,
                CancellationToken ct)
            {
                var aiSummary = await context.RuntimeCapabilities.AskAIAsync(
                    "claim-case:" + (command.CaseId ?? string.Empty),
                    ct);

                await context.RuntimeCapabilities.SendToAsync(
                    "role-claim-analyst-" + context.RunId,
                    new ClaimAnalystReviewRequested { CaseId = command.CaseId ?? string.Empty },
                    ct);
                await context.RuntimeCapabilities.SendToAsync(
                    "fraud-risk-agent-" + context.RunId,
                    new ClaimFraudScoringRequested { CaseId = command.CaseId ?? string.Empty },
                    ct);
                await context.RuntimeCapabilities.SendToAsync(
                    "compliance-rule-agent-" + context.RunId,
                    new ClaimComplianceCheckRequested { CaseId = command.CaseId ?? string.Empty },
                    ct);

                var decisionStatus = "Approved";
                var manualReviewRequired = false;

                if (string.Equals(command.CaseId, "Case-A", StringComparison.Ordinal))
                {
                    decisionStatus = "Approved";
                }
                else if (string.Equals(command.CaseId, "Case-B", StringComparison.Ordinal) ||
                    command.RiskScore >= 0.85d ||
                    aiSummary.Contains("high-risk", StringComparison.OrdinalIgnoreCase))
                {
                    decisionStatus = "ManualReview";
                    manualReviewRequired = true;
                    var manualReviewActorId = await context.RuntimeCapabilities.CreateAgentAsync(
                        "Aevatar.Integration.Tests.ClaimMessageSinkGAgent, Aevatar.Integration.Tests",
                        "human-review-" + context.RunId,
                        ct);
                    await context.RuntimeCapabilities.SendToAsync(
                        manualReviewActorId,
                        new ClaimManualReviewRequested { CaseId = command.CaseId ?? string.Empty },
                        ct);
                }
                else if (string.Equals(command.CaseId, "Case-C", StringComparison.Ordinal) || !command.CompliancePassed)
                {
                    decisionStatus = "Rejected";
                }

                var current = new ClaimCaseReadModel
                {
                    HasValue = true,
                    CaseId = command.CaseId ?? string.Empty,
                    PolicyId = command.PolicyId ?? string.Empty,
                    DecisionStatus = decisionStatus,
                    ManualReviewRequired = manualReviewRequired,
                    AiSummary = aiSummary ?? string.Empty,
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
                ClaimQueryRequested request,
                ScriptQueryContext<ClaimCaseReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ClaimQueryResponded?>(new ClaimQueryResponded
                {
                    RequestId = request.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new ClaimCaseReadModel(),
                });
            }
        }
        """;

    private const string RoleClaimAnalystSource =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions.Behaviors;

        public sealed class ClaimAnalystRoleBehavior : ScriptBehavior<ClaimCaseState, ClaimCaseReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ClaimCaseState, ClaimCaseReadModel> builder)
            {
                builder
                    .OnCommand<ClaimAnalystReviewRequested>(HandleAsync)
                    .OnEvent<ClaimDecisionRecorded>(apply: static (_, evt, _) => evt.Current == null ? new ClaimCaseState() : new ClaimCaseState { CaseId = evt.Current.CaseId })
                    .ProjectState(static (state, _) => state == null
                        ? new ClaimCaseReadModel()
                        : new ClaimCaseReadModel
                        {
                            HasValue = !string.IsNullOrWhiteSpace(state.CaseId),
                            CaseId = state.CaseId,
                            TraceSteps = { state.TraceSteps },
                        });
            }

            private static Task HandleAsync(
                ClaimAnalystReviewRequested request,
                ScriptCommandContext<ClaimCaseState> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var current = new ClaimCaseReadModel
                {
                    HasValue = true,
                    CaseId = request.CaseId ?? string.Empty,
                    DecisionStatus = "FactsReady",
                    Search = new ClaimSearchIndex { LookupKey = (request.CaseId ?? string.Empty).ToLowerInvariant() },
                    Refs = new ClaimRefs { OwnerActorId = context.ActorId },
                };
                current.TraceSteps.Add("analyst");
                context.Emit(new ClaimDecisionRecorded { CommandId = context.CommandId, Current = current });
                return Task.CompletedTask;
            }

            private static Task<ClaimQueryResponded?> HandleQueryAsync(
                ClaimQueryRequested request,
                ScriptQueryContext<ClaimCaseReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ClaimQueryResponded?>(new ClaimQueryResponded
                {
                    RequestId = request.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new ClaimCaseReadModel(),
                });
            }
        }
        """;

    private const string FraudRiskSource =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions.Behaviors;

        public sealed class FraudRiskBehavior : ScriptBehavior<ClaimCaseState, ClaimCaseReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ClaimCaseState, ClaimCaseReadModel> builder)
            {
                builder
                    .OnCommand<ClaimFraudScoringRequested>(HandleAsync)
                    .OnEvent<ClaimDecisionRecorded>(apply: static (_, evt, _) => evt.Current == null ? new ClaimCaseState() : new ClaimCaseState { CaseId = evt.Current.CaseId })
                    .ProjectState(static (state, _) => state == null
                        ? new ClaimCaseReadModel()
                        : new ClaimCaseReadModel
                        {
                            HasValue = !string.IsNullOrWhiteSpace(state.CaseId),
                            CaseId = state.CaseId,
                            TraceSteps = { state.TraceSteps },
                        });
            }

            private static Task HandleAsync(
                ClaimFraudScoringRequested request,
                ScriptCommandContext<ClaimCaseState> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var current = new ClaimCaseReadModel
                {
                    HasValue = true,
                    CaseId = request.CaseId ?? string.Empty,
                    DecisionStatus = "RiskScored",
                    Search = new ClaimSearchIndex { LookupKey = (request.CaseId ?? string.Empty).ToLowerInvariant() },
                    Refs = new ClaimRefs { OwnerActorId = context.ActorId },
                };
                current.TraceSteps.Add("fraud");
                context.Emit(new ClaimDecisionRecorded { CommandId = context.CommandId, Current = current });
                return Task.CompletedTask;
            }

            private static Task<ClaimQueryResponded?> HandleQueryAsync(
                ClaimQueryRequested request,
                ScriptQueryContext<ClaimCaseReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ClaimQueryResponded?>(new ClaimQueryResponded
                {
                    RequestId = request.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new ClaimCaseReadModel(),
                });
            }
        }
        """;

    private const string ComplianceRuleSource =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions.Behaviors;

        public sealed class ComplianceRuleBehavior : ScriptBehavior<ClaimCaseState, ClaimCaseReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ClaimCaseState, ClaimCaseReadModel> builder)
            {
                builder
                    .OnCommand<ClaimComplianceCheckRequested>(HandleAsync)
                    .OnEvent<ClaimDecisionRecorded>(apply: static (_, evt, _) => evt.Current == null ? new ClaimCaseState() : new ClaimCaseState { CaseId = evt.Current.CaseId })
                    .ProjectState(static (state, _) => state == null
                        ? new ClaimCaseReadModel()
                        : new ClaimCaseReadModel
                        {
                            HasValue = !string.IsNullOrWhiteSpace(state.CaseId),
                            CaseId = state.CaseId,
                            TraceSteps = { state.TraceSteps },
                        });
            }

            private static Task HandleAsync(
                ClaimComplianceCheckRequested request,
                ScriptCommandContext<ClaimCaseState> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var current = new ClaimCaseReadModel
                {
                    HasValue = true,
                    CaseId = request.CaseId ?? string.Empty,
                    DecisionStatus = "ComplianceChecked",
                    Search = new ClaimSearchIndex { LookupKey = (request.CaseId ?? string.Empty).ToLowerInvariant() },
                    Refs = new ClaimRefs { OwnerActorId = context.ActorId },
                };
                current.TraceSteps.Add("compliance");
                context.Emit(new ClaimDecisionRecorded { CommandId = context.CommandId, Current = current });
                return Task.CompletedTask;
            }

            private static Task<ClaimQueryResponded?> HandleQueryAsync(
                ClaimQueryRequested request,
                ScriptQueryContext<ClaimCaseReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ClaimQueryResponded?>(new ClaimQueryResponded
                {
                    RequestId = request.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new ClaimCaseReadModel(),
                });
            }
        }
        """;

    private const string HumanReviewSource =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions.Behaviors;

        public sealed class HumanReviewBehavior : ScriptBehavior<ClaimCaseState, ClaimCaseReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ClaimCaseState, ClaimCaseReadModel> builder)
            {
                builder
                    .OnCommand<ClaimManualReviewRequested>(HandleAsync)
                    .OnEvent<ClaimDecisionRecorded>(apply: static (_, evt, _) => evt.Current == null ? new ClaimCaseState() : new ClaimCaseState { CaseId = evt.Current.CaseId })
                    .ProjectState(static (state, _) => state == null
                        ? new ClaimCaseReadModel()
                        : new ClaimCaseReadModel
                        {
                            HasValue = !string.IsNullOrWhiteSpace(state.CaseId),
                            CaseId = state.CaseId,
                            ManualReviewRequired = state.ManualReviewRequired,
                            TraceSteps = { state.TraceSteps },
                        });
            }

            private static Task HandleAsync(
                ClaimManualReviewRequested request,
                ScriptCommandContext<ClaimCaseState> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var current = new ClaimCaseReadModel
                {
                    HasValue = true,
                    CaseId = request.CaseId ?? string.Empty,
                    DecisionStatus = "ManualReview",
                    ManualReviewRequired = true,
                    Search = new ClaimSearchIndex { LookupKey = (request.CaseId ?? string.Empty).ToLowerInvariant(), DecisionKey = "manualreview" },
                    Refs = new ClaimRefs { OwnerActorId = context.ActorId },
                };
                current.TraceSteps.Add("manual-review");
                context.Emit(new ClaimDecisionRecorded { CommandId = context.CommandId, Current = current });
                return Task.CompletedTask;
            }

            private static Task<ClaimQueryResponded?> HandleQueryAsync(
                ClaimQueryRequested request,
                ScriptQueryContext<ClaimCaseReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ClaimQueryResponded?>(new ClaimQueryResponded
                {
                    RequestId = request.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new ClaimCaseReadModel(),
                });
            }
        }
        """;
}
