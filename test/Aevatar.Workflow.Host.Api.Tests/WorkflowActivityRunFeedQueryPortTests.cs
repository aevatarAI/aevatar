using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowActivityRunFeedQueryPortTests
{
    [Fact]
    public void WorkflowExecutionReadModelMapper_ShouldExposeSafeActivityRunSummaryFields()
    {
        var mapper = new WorkflowExecutionReadModelMapper();
        var completedAt = DateTimeOffset.Parse("2026-08-07T02:10:00+00:00");
        var snapshot = mapper.ToActorSnapshot(new WorkflowExecutionCurrentStateDocument
        {
            RootActorId = "actor-alpha",
            RunId = "run-alpha",
            WorkflowId = "wf-alpha",
            WorkflowName = "workflow alpha",
            ScopeId = "scope-alpha",
            Status = "completed",
            CompilationError = "workflow definition failed to compile",
            CompletedAtUtcValue = Timestamp.FromDateTimeOffset(completedAt),
            DurationMs = 62_000,
            InputSummary = "safe input summary",
            ActivityInitiator = new WorkflowRunActivityInitiatorReadModel
            {
                ExternalUserId = "m-alpha",
                BindingId = "svc-alpha",
                DisplayValue = "alice@example.com",
                Availability = "available",
            },
            ActivityCurrentStep = new WorkflowRunActivityStepReadModel
            {
                StepId = "step-current",
                InputSummary = "step input",
                Availability = "available",
            },
            ActivityFirstFailure = new WorkflowRunActivityFailureReadModel
            {
                StepId = "step-failed",
                Message = "first failure",
                Availability = "available",
            },
            ActivityWaiting = new WorkflowRunActivityWaitingReadModel
            {
                StepId = "step-wait",
                WaitingKind = "signal",
                Prompt = "signal-name",
                Availability = "available",
            },
        });

        snapshot.RunId.Should().Be("run-alpha");
        snapshot.ActorId.Should().Be("actor-alpha");
        snapshot.WorkflowId.Should().Be("wf-alpha");
        snapshot.CompletedAtUtc?.ToDateTimeOffset().Should().Be(completedAt);
        snapshot.DurationMs.Should().Be(62_000);
        snapshot.InputSummary.Should().Be("safe input summary");
        snapshot.CompilationError.Should().Be("workflow definition failed to compile");
        snapshot.ActivityInitiator.ExternalUserId.Should().Be("m-alpha");
        // Binding ids can be exchanged for short-lived NyxID credentials and
        // must never escape through activity/read-model projections, including
        // when legacy documents already contain one.
        snapshot.ActivityInitiator.BindingId.Should().BeEmpty();
        snapshot.ActivityCurrentStep.StepId.Should().Be("step-current");
        snapshot.ActivityFirstFailure.Message.Should().Be("first failure");
        snapshot.ActivityWaiting.WaitingKind.Should().Be("signal");
    }

    [Fact]
    public void WorkflowExecutionReadModelMapper_ShouldExposeTypedRecoveryCapability()
    {
        var mapper = new WorkflowExecutionReadModelMapper();
        var snapshot = mapper.ToActorSnapshot(new WorkflowExecutionCurrentStateDocument
        {
            RootActorId = "actor-recovery",
            RunId = "run-recovery",
            Status = "failed",
            RecoveryCapability = new WorkflowRunRecoveryCapabilityReadModel
            {
                WorkflowDefinitionRevisionId = "rev-recovery",
                WorkflowDefinitionVersion = 12,
                RetryFailedStep = new WorkflowRecoveryActionCapabilityReadModel
                {
                    Eligibility = WorkflowRecoveryEligibilityReadModel.Eligible,
                    UnavailableReasonCode = WorkflowRecoveryUnavailableReasonCodeReadModel.None,
                    StartingStepId = "step-failed",
                    ReusesPriorStepOutputs = true,
                    MayIncurModelOrToolCost = true,
                    RecommendedActions =
                    {
                        WorkflowRecoveryRecommendedActionReadModel.Retry,
                    },
                },
                RunAgain = new WorkflowRecoveryActionCapabilityReadModel
                {
                    Eligibility = WorkflowRecoveryEligibilityReadModel.Ineligible,
                    UnavailableReasonCode = WorkflowRecoveryUnavailableReasonCodeReadModel.ConfigurationFailure,
                    UnavailableReason = "Configuration must be changed before this run can be recovered.",
                    RecommendedActions =
                    {
                        WorkflowRecoveryRecommendedActionReadModel.ChangeConfiguration,
                    },
                },
            },
        });

        snapshot.RecoveryCapability.WorkflowDefinitionRevisionId.Should().Be("rev-recovery");
        snapshot.RecoveryCapability.WorkflowDefinitionVersion.Should().Be(12);
        snapshot.RecoveryCapability.RetryFailedStep.Eligibility.Should().Be(WorkflowRecoveryEligibility.Eligible);
        snapshot.RecoveryCapability.RetryFailedStep.UnavailableReasonCode.Should().Be(WorkflowRecoveryUnavailableReasonCode.None);
        snapshot.RecoveryCapability.RetryFailedStep.StartingStepId.Should().Be("step-failed");
        snapshot.RecoveryCapability.RetryFailedStep.ReusesPriorStepOutputs.Should().BeTrue();
        snapshot.RecoveryCapability.RetryFailedStep.MayIncurModelOrToolCost.Should().BeTrue();
        snapshot.RecoveryCapability.RetryFailedStep.RecommendedActions.Should().ContainSingle()
            .Which.Should().Be(WorkflowRecoveryRecommendedAction.Retry);
        snapshot.RecoveryCapability.RunAgain.Eligibility.Should().Be(WorkflowRecoveryEligibility.Ineligible);
        snapshot.RecoveryCapability.RunAgain.UnavailableReasonCode.Should().Be(WorkflowRecoveryUnavailableReasonCode.ConfigurationFailure);
        snapshot.RecoveryCapability.RunAgain.RecommendedActions.Should().ContainSingle()
            .Which.Should().Be(WorkflowRecoveryRecommendedAction.ChangeConfiguration);
    }

    [Fact]
    public void WorkflowExecutionReadModelMapper_ShouldLeaveDurationUnavailable_WhenDocumentHasNoDuration()
    {
        var mapper = new WorkflowExecutionReadModelMapper();
        var completedAt = DateTimeOffset.Parse("2026-08-07T02:10:00+00:00");
        var snapshot = mapper.ToActorSnapshot(new WorkflowExecutionCurrentStateDocument
        {
            RootActorId = "actor-legacy",
            RunId = "run-legacy",
            Status = "completed",
            CompletedAtUtcValue = Timestamp.FromDateTimeOffset(completedAt),
        });

        snapshot.CompletedAtUtc?.ToDateTimeOffset().Should().Be(completedAt);
        snapshot.HasDurationMs.Should().BeFalse();
    }

    [Fact]
    public async Task PageWorkflowActorCurrentStatesAsync_ShouldForwardActivityFiltersAndCursor()
    {
        var reader = new RecordingCurrentStateReader
        {
            Items =
            [
                new WorkflowExecutionCurrentStateDocument
                {
                    Id = "actor-alpha",
                    RootActorId = "actor-alpha",
                    RunId = "run-alpha",
                    WorkflowId = "wf-alpha",
                    WorkflowName = "workflow alpha",
                    ScopeId = "scope-alpha",
                    Status = "completed",
                    RunOrigin = "member-invoke",
                    StateVersion = 12,
                },
            ],
            NextCursor = "cursor-next",
            TotalCount = 37,
        };
        var port = new WorkflowExecutionCurrentStateQueryPort(
            reader,
            new WorkflowExecutionReadModelMapper(),
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                WorkflowActorCurrentStateQueryEnabled = true,
            });

        var page = await port.PageWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = 25,
                ScopeId = "scope-alpha",
                Status = "completed",
                WorkflowId = "wf-alpha",
                RunOrigins = ["member-invoke"],
                DefinitionActorIds = ["definition-alpha"],
                ScheduleIds = ["schedule-alpha"],
                UpdatedFromUtc = DateTimeOffset.Parse("2026-08-07T01:00:00+00:00"),
                UpdatedToUtc = DateTimeOffset.Parse("2026-08-07T02:00:00+00:00"),
                Cursor = "cursor-current",
                IncludeTotalCount = true,
            });

        page.Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-alpha");
        page.NextCursor.Should().Be("cursor-next");
        page.TotalCount.Should().Be(37);
        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Take.Should().Be(25);
        reader.LastQuery.Cursor.Should().Be("cursor-current");
        reader.LastQuery.IncludeTotalCount.Should().BeTrue();
        reader.LastQuery.Sorts.Should().HaveCount(2);
        reader.LastQuery.Sorts[0].FieldPath.Should().Be(nameof(WorkflowExecutionCurrentStateDocument.UpdatedAtUtcValue));
        reader.LastQuery.Sorts[1].FieldPath.Should().Be(nameof(WorkflowExecutionCurrentStateDocument.RootActorId));
        reader.LastQuery.Filters.Should().Contain(filter =>
            filter.FieldPath == nameof(WorkflowExecutionCurrentStateDocument.WorkflowId) &&
            filter.Operator == ProjectionDocumentFilterOperator.Eq &&
            Equals(filter.Value.RawValue, "wf-alpha"));
    }

    private sealed class RecordingCurrentStateReader : IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string>
    {
        public IReadOnlyList<WorkflowExecutionCurrentStateDocument> Items { get; init; } = [];
        public string? NextCursor { get; init; }
        public long? TotalCount { get; init; }
        public ProjectionDocumentQuery? LastQuery { get; private set; }

        public Task<WorkflowExecutionCurrentStateDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            _ = key;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowExecutionCurrentStateDocument?>(null);
        }

        public Task<ProjectionDocumentQueryResult<WorkflowExecutionCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastQuery = query;
            return Task.FromResult(new ProjectionDocumentQueryResult<WorkflowExecutionCurrentStateDocument>
            {
                Items = Items,
                NextCursor = NextCursor,
                TotalCount = TotalCount,
            });
        }
    }
}
