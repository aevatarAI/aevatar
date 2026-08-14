using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatDomainJourneyTests
{
    private static readonly Timestamp Now = Timestamp.FromDateTimeOffset(
        new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ReimbursementEvidence_ShouldNormalizeThreeInvoicesAndOneExactDuplicate()
    {
        NyxIdChatDomainEvidenceContract.TryParseReimbursement(
            ReimbursementArguments(),
            out var evidence).Should().BeTrue();

        evidence.SourceInvoices.Select(static invoice => invoice.SourceOrdinal)
            .Should().Equal(1, 2, 3);
        evidence.RetainedSourceOrdinals.Should().Equal(1, 2);
        evidence.DuplicateInvoices.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { DuplicateSourceOrdinal = 3, RetainedSourceOrdinal = 1 });
        evidence.GuardedToolName.Should().Be("approval_instance_create");
    }

    [Fact]
    public void CandidateEvidence_ShouldRejectAComputedTotalThatDiffersFromTheRubricScores()
    {
        var arguments = CandidateArguments(totalScore: 79);

        NyxIdChatDomainEvidenceContract.TryParseCandidateScreening(
            arguments,
            out _).Should().BeFalse();
    }

    [Fact]
    public void CandidateEvidence_ShouldCommitBeforeTheActorEvaluatesTheUserThreshold()
    {
        var state = ActiveLlmState(numericThreshold: 75);
        var committed = NyxIdChatTaskLifecycle.ApplyOperationResult(
            state,
            DomainEvidenceSignal(
                state,
                NyxIdChatDomainEvidenceContract.CandidateScreeningToolName,
                CandidateArguments(totalScore: 80)),
            Now);

        committed.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        committed.State.ActiveTask.SchemaVersion.Should().Be(6);
        committed.State.ActiveTask.Domain.CandidateScreening.TotalScore.Should().Be(80);
        committed.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.DomainContinuation);
        var evidenceId = committed.State.ActiveTask.Domain.CandidateScreening.EvidenceId;

        var condition = NyxIdChatTaskLifecycle.ApplyOperationResult(
            committed.State,
            ConditionSignal(committed.NextCommand.Key, evidenceId, observedValue: 80),
            Now);

        condition.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        condition.State.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.Condition)
            .Source.Condition.Condition.Should().BeEquivalentTo(new
            {
                SourceInputRequestId = "input-domain",
                SourceEvidenceId = evidenceId,
                EffectiveThreshold = 75L,
                ObservedValue = 80L,
                Outcome = NyxIdChatConditionOutcome.True,
                GuardedToolName = "bitable_record_create",
            }, options => options.ExcludingMissingMembers());
    }

    [Fact]
    public void ReimbursementEvidence_ShouldFailClosedWhenTheNextEffectDoesNotMatchItsGuard()
    {
        var state = ActiveLlmState();
        var committed = NyxIdChatTaskLifecycle.ApplyOperationResult(
            state,
            DomainEvidenceSignal(
                state,
                NyxIdChatDomainEvidenceContract.ReimbursementToolName,
                ReimbursementArguments()),
            Now);

        var rejected = NyxIdChatTaskLifecycle.ApplyOperationResult(
            committed.State,
            ToolSignal(committed.NextCommand!.Key, "approval_instance_cancel"),
            Now);

        rejected.ReasonCode.Should().Be(NyxIdChatTaskLifecycle.DomainGuardMismatch);
        rejected.NextCommand.Should().BeNull();
        rejected.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        rejected.State.ActiveTask.Artifact.Should().BeNull();
    }

    [Fact]
    public void AppliedExactReadBack_ShouldMaterializeCandidateArtifactFromActorFacts()
    {
        var state = VerificationState();
        var verificationStep = state.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Postcondition);
        var readBack = verificationStep.Source.Postcondition.ToolReadBack;

        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(
            state,
            new NyxIdChatOperationResultSignal
            {
                Key = verificationStep.Operation.Key.Clone(),
                ToolVerification = new NyxIdChatToolVerificationResult
                {
                    EffectStepId = "step-write",
                    Disposition = NyxIdChatToolVerificationDisposition.Applied,
                    ReadOperation = readBack.ReadOperation.Clone(),
                    CheckName = readBack.CheckName,
                },
            },
            Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.State.ActiveTask.Artifact.CandidateTracker.Should().BeEquivalentTo(new
        {
            ProviderRecordId = "rec-candidate-alpha",
            CandidateName = "Candidate Alpha",
            Score = 80,
            Threshold = 75L,
            TrackerTable = "Candidate Tracker",
            TrackerTableId = "tbl-candidates",
            Stage = "accepted",
        }, options => options.ExcludingMissingMembers());
        decision.State.ActiveTask.Artifact.CheckName.Should().Be("bitable.record.exists");
    }

    [Fact]
    public void AppliedExactReadBack_ShouldMaterializeReimbursementArtifactFromActorFacts()
    {
        var state = ReimbursementVerificationState();
        var verificationStep = state.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Postcondition);
        var readBack = verificationStep.Source.Postcondition.ToolReadBack;

        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(
            state,
            new NyxIdChatOperationResultSignal
            {
                Key = verificationStep.Operation.Key.Clone(),
                ToolVerification = new NyxIdChatToolVerificationResult
                {
                    EffectStepId = "step-write",
                    Disposition = NyxIdChatToolVerificationDisposition.Applied,
                    ReadOperation = readBack.ReadOperation.Clone(),
                    CheckName = readBack.CheckName,
                },
            },
            Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.State.ActiveTask.Artifact.Reimbursement.Should().BeEquivalentTo(new
        {
            ProviderInstanceId = "approval-instance-alpha",
            CostCenter = "cc-42",
            RetainedItemCount = 2,
            DuplicateItemCount = 1,
        }, options => options.ExcludingMissingMembers());
        decision.State.ActiveTask.Artifact.CheckName.Should()
            .Be("approval.instance.exists");
    }

    [Fact]
    public void CandidateTrueBranch_ShouldRejectCompletionBeforeVerifiedArtifactExists()
    {
        var state = ActiveLlmState(numericThreshold: 75);
        var committed = NyxIdChatTaskLifecycle.ApplyOperationResult(
            state,
            DomainEvidenceSignal(
                state,
                NyxIdChatDomainEvidenceContract.CandidateScreeningToolName,
                CandidateArguments(totalScore: 80)),
            Now);
        var evidenceId = committed.State.ActiveTask.Domain.CandidateScreening.EvidenceId;
        var condition = NyxIdChatTaskLifecycle.ApplyOperationResult(
            committed.State,
            ConditionSignal(committed.NextCommand!.Key, evidenceId, observedValue: 80),
            Now);

        var rejected = NyxIdChatTaskLifecycle.ApplyOperationResult(
            condition.State,
            new NyxIdChatOperationResultSignal
            {
                Key = condition.NextCommand!.Key.Clone(),
                Llm = new NyxIdChatLLMOperationResult
                {
                    Content = "The candidate row was created.",
                },
            },
            Now);

        rejected.ReasonCode.Should().Be(
            NyxIdChatTaskLifecycle.DomainCompletionEvidenceRequired);
        rejected.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        rejected.State.ActiveTask.Artifact.Should().BeNull();
    }

    [Fact]
    public void CandidateBelowThreshold_ShouldSkipWriteWithoutApprovalOrArtifact()
    {
        var state = ActiveLlmState(numericThreshold: 85);
        var committed = NyxIdChatTaskLifecycle.ApplyOperationResult(
            state,
            DomainEvidenceSignal(
                state,
                NyxIdChatDomainEvidenceContract.CandidateScreeningToolName,
                CandidateArguments(totalScore: 80)),
            Now);
        var evidenceId = committed.State.ActiveTask.Domain.CandidateScreening.EvidenceId;
        var condition = NyxIdChatTaskLifecycle.ApplyOperationResult(
            committed.State,
            ConditionSignal(committed.NextCommand!.Key, evidenceId, observedValue: 80),
            Now);

        var completed = NyxIdChatTaskLifecycle.ApplyOperationResult(
            condition.State,
            new NyxIdChatOperationResultSignal
            {
                Key = condition.NextCommand!.Key.Clone(),
                Llm = new NyxIdChatLLMOperationResult
                {
                    Content = "The candidate was below the user's threshold.",
                },
            },
            Now);

        completed.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        completed.State.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.Condition)
            .Source.Condition.Condition.Outcome.Should().Be(NyxIdChatConditionOutcome.False);
        completed.State.ActiveTask.Steps.Single(step =>
                step.Guard?.RequiredOutcome == NyxIdChatConditionOutcome.True)
            .Status.Should().Be(NyxIdChatStepStatus.Skipped);
        completed.State.PendingApproval.Should().BeNull();
        completed.State.ActiveTask.Artifact.Should().BeNull();
        completed.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
    }

    private static NyxIdChatConversationGAgentState ActiveLlmState(long? numericThreshold = null)
    {
        var key = Key("step-llm", "operation-llm");
        var state = new NyxIdChatConversationGAgentState
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
            },
            LatestTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                ActorId = "conversation-alpha",
                TaskId = "task-alpha",
                TurnId = "turn-alpha",
                PlanId = "plan-alpha",
                PlanRevision = 1,
                SchemaVersion = 5,
                Status = NyxIdChatTaskStatus.Active,
                ActiveStepId = key.StepId,
                ActiveOperationId = key.OperationId,
            },
        };
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = "step-input",
            Order = 1,
            Kind = NyxIdChatStepKind.Input,
            Status = NyxIdChatStepStatus.Done,
            Required = true,
            Source = new NyxIdChatStepSource
            {
                Input = new NyxIdChatInputStepSource { RequestId = "input-domain" },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
        });
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = key.StepId,
            Order = 2,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Source = new NyxIdChatStepSource { Llm = new NyxIdChatLLMStepSource() },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            DependsOn = { "step-input" },
            Operation = new NyxIdChatOperationState
            {
                Key = key,
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Dispatched,
            },
        });
        var resolution = new NyxIdChatInputResolutionState
        {
            RequestId = "input-domain",
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            CommittedAt = Now.Clone(),
        };
        if (numericThreshold is { } threshold)
        {
            resolution.NumericThreshold = new NyxIdChatNumericThresholdResolution
            {
                SuggestedValue = 70,
                EffectiveValue = threshold,
                Origin = NyxIdChatThresholdOrigin.UserOverride,
            };
        }
        state.RecentInputResolutions.Add(resolution);
        state.LatestInputResolution = resolution.Clone();
        return state;
    }

    private static NyxIdChatConversationGAgentState VerificationState()
    {
        var state = ActiveLlmState(numericThreshold: 75);
        state.ActiveTask.Steps.Clear();
        state.ActiveTask.Domain = new NyxIdChatTaskDomainState
        {
            CandidateScreening = new NyxIdChatCandidateScreeningEvidence
            {
                EvidenceId = "candidate-evidence-alpha",
                SourceInputRequestId = "input-domain",
                CandidateName = "Candidate Alpha",
                RoleTitle = "Platform Engineer",
                TotalScore = 80,
                TrackerTable = "Candidate Tracker",
                TrackerTableId = "tbl-candidates",
                Stage = "accepted",
                GuardedToolName = "bitable_record_create",
                CommittedAt = Now.Clone(),
            },
        };
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = "step-write",
            Order = 1,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Waiting,
            Required = true,
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource
                {
                    ToolName = "bitable_record_create",
                    ProviderResourceId = "rec-candidate-alpha",
                },
            },
            ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
        });
        var readOperation = new AgentToolOperationAdmissionPayload
        {
            ServiceInstanceId = "svc-lark-alpha",
            ServiceSlug = "api-lark-bot",
            HttpMethod = "GET",
            PathTemplate = "/records/{record_id}",
        };
        var verificationKey = Key("step-verify", "operation-verify");
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = verificationKey.StepId,
            Order = 2,
            Kind = NyxIdChatStepKind.Postcondition,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            DependsOn = { "step-write" },
            Source = new NyxIdChatStepSource
            {
                Postcondition = new NyxIdChatPostconditionStepSource
                {
                    EffectStepId = "step-write",
                    Check = "bitable.record.exists",
                    ProviderResourceId = "rec-candidate-alpha",
                    ToolReadBack = new AgentToolOperationReadBackPayload
                    {
                        ReadOperation = readOperation,
                        CheckName = "bitable.record.exists",
                    },
                },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = verificationKey,
                Kind = NyxIdChatStepKind.Postcondition,
                Phase = NyxIdChatOperationPhase.Dispatched,
            },
        });
        state.ActiveTask.ActiveStepId = verificationKey.StepId;
        state.ActiveTask.ActiveOperationId = verificationKey.OperationId;
        return state;
    }

    private static NyxIdChatConversationGAgentState ReimbursementVerificationState()
    {
        NyxIdChatDomainEvidenceContract.TryParseReimbursement(
            ReimbursementArguments(),
            out var evidence).Should().BeTrue();
        evidence.EvidenceId = "reimbursement-evidence-alpha";
        evidence.CommittedAt = Now.Clone();
        var state = ActiveLlmState();
        state.ActiveTask.Steps.Clear();
        state.ActiveTask.Domain = new NyxIdChatTaskDomainState
        {
            Reimbursement = evidence,
        };
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = "step-write",
            Order = 1,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Waiting,
            Required = true,
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource
                {
                    ToolName = "approval_instance_create",
                    ProviderResourceId = "approval-instance-alpha",
                },
            },
            ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
        });
        var readOperation = new AgentToolOperationAdmissionPayload
        {
            ServiceInstanceId = "svc-approval-alpha",
            ServiceSlug = "approval-service",
            HttpMethod = "GET",
            PathTemplate = "/instances/{instance_id}",
        };
        var verificationKey = Key("step-verify", "operation-verify");
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = verificationKey.StepId,
            Order = 2,
            Kind = NyxIdChatStepKind.Postcondition,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            DependsOn = { "step-write" },
            Source = new NyxIdChatStepSource
            {
                Postcondition = new NyxIdChatPostconditionStepSource
                {
                    EffectStepId = "step-write",
                    Check = "approval.instance.exists",
                    ProviderResourceId = "approval-instance-alpha",
                    ToolReadBack = new AgentToolOperationReadBackPayload
                    {
                        ReadOperation = readOperation,
                        CheckName = "approval.instance.exists",
                    },
                },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = verificationKey,
                Kind = NyxIdChatStepKind.Postcondition,
                Phase = NyxIdChatOperationPhase.Dispatched,
            },
        });
        state.ActiveTask.ActiveStepId = verificationKey.StepId;
        state.ActiveTask.ActiveOperationId = verificationKey.OperationId;
        return state;
    }

    private static NyxIdChatOperationResultSignal DomainEvidenceSignal(
        NyxIdChatConversationGAgentState state,
        string toolName,
        string arguments) =>
        ToolSignal(
            state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Llm)
                .Operation.Key,
            toolName,
            arguments,
            isReadOnly: true);

    private static NyxIdChatOperationResultSignal ConditionSignal(
        NyxIdChatOperationKey key,
        string evidenceId,
        int observedValue) =>
        ToolSignal(
            key,
            NyxIdChatConditionEvaluateContract.ToolName,
            $$"""{"source_input_request_id":"input-domain","source_evidence_id":"{{evidenceId}}","observed_value":{{observedValue}},"guarded_tool_name":"bitable_record_create"}""",
            isReadOnly: true);

    private static NyxIdChatOperationResultSignal ToolSignal(
        NyxIdChatOperationKey key,
        string toolName,
        string arguments = "{}",
        bool isReadOnly = false) =>
        new()
        {
            Key = key.Clone(),
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = $"call-{toolName}",
                        ToolName = toolName,
                        ArgumentsJson = arguments,
                        Safety = new NyxIdChatToolCallSafety
                        {
                            IsReadOnly = isReadOnly,
                            MayChangeExternalState = !isReadOnly,
                            SideEffectKind = isReadOnly ? string.Empty : "provider.write",
                        },
                    },
                },
            },
        };

    private static NyxIdChatOperationKey Key(string stepId, string operationId) => new()
    {
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = stepId,
        OperationId = operationId,
        OperationGeneration = 1,
    };

    private static string ReimbursementArguments() => """
        {
          "source_input_request_id": "input-domain",
          "expense_category": "travel",
          "cost_center": "cc-42",
          "reimbursement_currency_instruction": "Submit in SGD",
          "guarded_tool_name": "approval_instance_create",
          "source_invoices": [
            {"source_ordinal":1,"vendor":"Northwind Air","invoice_number":"INV-001","invoice_date":"2026-08-01","amount":{"currency_code":"SGD","minor_units":12500,"fraction_digits":2}},
            {"source_ordinal":2,"vendor":"Contoso Hotel","invoice_number":"INV-002","invoice_date":"2026-08-02","amount":{"currency_code":"SGD","minor_units":24000,"fraction_digits":2}},
            {"source_ordinal":3,"vendor":"Northwind Air","invoice_number":"INV-001","invoice_date":"2026-08-01","amount":{"currency_code":"SGD","minor_units":12500,"fraction_digits":2}}
          ],
          "retained_source_ordinals": [1, 2],
          "duplicate_invoices": [
            {"duplicate_source_ordinal":3,"retained_source_ordinal":1}
          ]
        }
        """;

    private static string CandidateArguments(int totalScore) => $$"""
        {
          "source_input_request_id": "input-domain",
          "candidate_name": "Candidate Alpha",
          "role_title": "Platform Engineer",
          "rubric": [
            {"criterion_id":"systems","title":"Systems","maximum_points":60},
            {"criterion_id":"delivery","title":"Delivery","maximum_points":40}
          ],
          "scores": [
            {"criterion_id":"systems","awarded_points":48,"evidence":"Designed actor protocols."},
            {"criterion_id":"delivery","awarded_points":32,"evidence":"Shipped production changes."}
          ],
          "total_score": {{totalScore}},
          "tracker_table": "Candidate Tracker",
          "tracker_table_id": "tbl-candidates",
          "stage": "accepted",
          "guarded_tool_name": "bitable_record_create"
        }
        """;
}
