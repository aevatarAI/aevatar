using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionCurrentStateQueryPortFilterTests
{
    [Fact]
    public async Task GetWorkflowRunCurrentStateAsync_ShouldQueryTypedRunIdWithoutActorKeyLookup()
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
                    ScopeId = "scope-alpha",
                },
            ],
        };
        IWorkflowExecutionCurrentStateQueryPort port = CreatePort(reader);

        var snapshot = await port.GetWorkflowRunCurrentStateAsync(" run-alpha ");

        snapshot.Should().NotBeNull();
        snapshot!.RunId.Should().Be("run-alpha");
        snapshot.ActorId.Should().Be("actor-alpha");
        reader.GetKeys.Should().BeEmpty();
        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Take.Should().Be(2);
        ShouldContainStringFilter(
            reader.LastQuery.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.RunId),
            ProjectionDocumentFilterOperator.Eq,
            "run-alpha");
    }

    [Fact]
    public async Task GetWorkflowRunCurrentStateForScopeAsync_ShouldFilterScopeAndTypedRunId()
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
                    ScopeId = "scope-alpha",
                },
            ],
        };
        IWorkflowExecutionCurrentStateQueryPort port = CreatePort(reader);

        var snapshot = await port.GetWorkflowRunCurrentStateForScopeAsync(
            " scope-alpha ",
            " run-alpha ");

        snapshot.Should().NotBeNull();
        snapshot!.RunId.Should().Be("run-alpha");
        snapshot.ScopeId.Should().Be("scope-alpha");
        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Take.Should().Be(2);
        ShouldContainStringFilter(
            reader.LastQuery.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.ScopeId),
            ProjectionDocumentFilterOperator.Eq,
            "scope-alpha");
        ShouldContainStringFilter(
            reader.LastQuery.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.RunId),
            ProjectionDocumentFilterOperator.Eq,
            "run-alpha");
    }

    [Fact]
    public async Task GetWorkflowRunCurrentStateAsync_ShouldNotFallBackToActorId()
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
                },
            ],
        };
        IWorkflowExecutionCurrentStateQueryPort port = CreatePort(reader);

        var snapshot = await port.GetWorkflowRunCurrentStateAsync("actor-alpha");

        snapshot.Should().BeNull();
        reader.GetKeys.Should().BeEmpty();
        ShouldContainStringFilter(
            reader.LastQuery!.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.RunId),
            ProjectionDocumentFilterOperator.Eq,
            "actor-alpha");
    }

    [Fact]
    public async Task ListWorkflowActorCurrentStatesAsync_ShouldEmitDeadLetterFilters_ForSingleDefinitionActorId()
    {
        var reader = new RecordingCurrentStateReader();
        var port = CreatePort(reader);

        await port.ListWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = 17,
                SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
                ScopeId = " scope-a ",
                DefinitionActorIds = [" def-a "],
            });

        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Take.Should().Be(17);
        reader.LastQuery.Filters.Should().HaveCount(3);
        ShouldContainStringFilter(
            reader.LastQuery.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.SagaStatus),
            ProjectionDocumentFilterOperator.Eq,
            "WORKFLOW_SAGA_STATUS_COMPENSATION_DEAD_LETTER");
        ShouldContainStringFilter(
            reader.LastQuery.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.ScopeId),
            ProjectionDocumentFilterOperator.Eq,
            "scope-a");
        ShouldContainStringFilter(
            reader.LastQuery.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.DefinitionActorId),
            ProjectionDocumentFilterOperator.Eq,
            "def-a");
    }

    [Fact]
    public async Task ListWorkflowActorCurrentStatesAsync_ShouldEmitDeadLetterFilters_ForMultipleDefinitionActorIds()
    {
        var reader = new RecordingCurrentStateReader();
        var port = CreatePort(reader);

        await port.ListWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = 2500,
                SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
                ScopeId = "scope-a",
                DefinitionActorIds = [" def-a ", "", "def-b", "def-a", "   "],
            });

        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Take.Should().Be(1000);
        reader.LastQuery.Filters.Should().HaveCount(3);
        ShouldContainStringFilter(
            reader.LastQuery.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.SagaStatus),
            ProjectionDocumentFilterOperator.Eq,
            "WORKFLOW_SAGA_STATUS_COMPENSATION_DEAD_LETTER");
        ShouldContainStringFilter(
            reader.LastQuery.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.ScopeId),
            ProjectionDocumentFilterOperator.Eq,
            "scope-a");
        ShouldContainStringListFilter(
            reader.LastQuery.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.DefinitionActorId),
            ProjectionDocumentFilterOperator.In,
            ["def-a", "def-b"]);
    }

    [Fact]
    public async Task ListWorkflowActorCurrentStatesAsync_ShouldEmitRecencyDescendingSort()
    {
        var reader = new RecordingCurrentStateReader();
        var port = CreatePort(reader);

        await port.ListWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery { Take = 100, ScopeId = "scope-a" });

        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Sorts.Should().HaveCount(2);
        reader.LastQuery.Sorts[0].FieldPath.Should().Be(nameof(WorkflowExecutionCurrentStateDocument.UpdatedAtUtcValue));
        reader.LastQuery.Sorts[0].Direction.Should().Be(ProjectionDocumentSortDirection.Desc);
        reader.LastQuery.Sorts[1].FieldPath.Should().Be(nameof(WorkflowExecutionCurrentStateDocument.RootActorId));
        reader.LastQuery.Sorts[1].Direction.Should().Be(ProjectionDocumentSortDirection.Asc);
    }

    [Fact]
    public async Task ListWorkflowActorCurrentStatesAsync_ShouldEmitOriginStatusAndTimeRangeFilters()
    {
        var reader = new RecordingCurrentStateReader();
        var port = CreatePort(reader);
        var from = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero);

        await port.ListWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = 50,
                Status = " completed ",
                RunOrigins = [" draft ", "member-invoke", "draft"],
                UpdatedFromUtc = from,
                UpdatedToUtc = to,
            });

        reader.LastQuery.Should().NotBeNull();
        ShouldContainStringFilter(
            reader.LastQuery!.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.Status),
            ProjectionDocumentFilterOperator.Eq,
            "completed");
        ShouldContainStringListFilter(
            reader.LastQuery.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.RunOrigin),
            ProjectionDocumentFilterOperator.In,
            ["draft", "member-invoke"]);
        var rangeFilters = reader.LastQuery.Filters
            .Where(filter => filter.FieldPath == nameof(WorkflowExecutionCurrentStateDocument.UpdatedAtUtcValue))
            .ToList();
        rangeFilters.Should().HaveCount(2);
        rangeFilters.Should().Contain(filter => filter.Operator == ProjectionDocumentFilterOperator.Gte);
        rangeFilters.Should().Contain(filter => filter.Operator == ProjectionDocumentFilterOperator.Lte);
    }

    [Fact]
    public async Task ListWorkflowActorCurrentStatesAsync_ShouldEmitActivitySearchAnyOfFilters()
    {
        var reader = new RecordingCurrentStateReader();
        var port = CreatePort(reader);

        await port.ListWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = 50,
                SearchText = "  Test Member  ",
                Status = "completed",
            });

        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Filters.Should().ContainSingle(filter =>
            filter.FieldPath == nameof(WorkflowExecutionCurrentStateDocument.Status) &&
            filter.Operator == ProjectionDocumentFilterOperator.Eq);
        reader.LastQuery.AnyOfFilters.Should().HaveCount(5);
        reader.LastQuery.AnyOfFilters.Should().OnlyContain(filter =>
            filter.Operator == ProjectionDocumentFilterOperator.ContainsText &&
            filter.Value.Kind == ProjectionDocumentValueKind.String &&
            Equals(filter.Value.RawValue, "Test Member"));
        reader.LastQuery.AnyOfFilters.Select(filter => filter.FieldPath).Should().BeEquivalentTo(
            [
                nameof(WorkflowExecutionCurrentStateDocument.WorkflowName),
                nameof(WorkflowExecutionCurrentStateDocument.RunId),
                nameof(WorkflowExecutionCurrentStateDocument.Status),
                nameof(WorkflowExecutionCurrentStateDocument.InputSummary),
                nameof(WorkflowExecutionCurrentStateDocument.ActivityInitiator) + "." + nameof(WorkflowRunActivityInitiatorSnapshot.DisplayValue),
            ]);
    }

    [Fact]
    public async Task ListWorkflowActorCurrentStatesAsync_ShouldEmitScheduleIdEqFilter_ForSingleScheduleId()
    {
        var reader = new RecordingCurrentStateReader();
        var port = CreatePort(reader);

        await port.ListWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = 50,
                ScheduleIds = [" schedule-a "],
            });

        reader.LastQuery.Should().NotBeNull();
        ShouldContainStringFilter(
            reader.LastQuery!.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.ScheduleId),
            ProjectionDocumentFilterOperator.Eq,
            "schedule-a");
    }

    [Fact]
    public async Task ListWorkflowActorCurrentStatesAsync_ShouldEmitScheduleIdInFilter_ForMultipleScheduleIds()
    {
        var reader = new RecordingCurrentStateReader();
        var port = CreatePort(reader);

        await port.ListWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = 50,
                ScheduleIds = [" schedule-a ", "", "schedule-b", "schedule-a", "   "],
            });

        reader.LastQuery.Should().NotBeNull();
        ShouldContainStringListFilter(
            reader.LastQuery!.Filters,
            nameof(WorkflowExecutionCurrentStateDocument.ScheduleId),
            ProjectionDocumentFilterOperator.In,
            ["schedule-a", "schedule-b"]);
    }

    private static WorkflowExecutionCurrentStateQueryPort CreatePort(RecordingCurrentStateReader reader) =>
        new(
            reader,
            new WorkflowExecutionReadModelMapper(),
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                WorkflowActorCurrentStateQueryEnabled = true,
            });

    private static void ShouldContainStringFilter(
        IReadOnlyList<ProjectionDocumentFilter> filters,
        string fieldPath,
        ProjectionDocumentFilterOperator op,
        string expectedValue)
    {
        var filter = filters.Should().ContainSingle(x => x.FieldPath == fieldPath).Subject;
        filter.Operator.Should().Be(op);
        filter.Value.Kind.Should().Be(ProjectionDocumentValueKind.String);
        filter.Value.RawValue.Should().Be(expectedValue);
    }

    private static void ShouldContainStringListFilter(
        IReadOnlyList<ProjectionDocumentFilter> filters,
        string fieldPath,
        ProjectionDocumentFilterOperator op,
        IReadOnlyList<string> expectedValues)
    {
        var filter = filters.Should().ContainSingle(x => x.FieldPath == fieldPath).Subject;
        filter.Operator.Should().Be(op);
        filter.Value.Kind.Should().Be(ProjectionDocumentValueKind.StringList);
        filter.Value.RawValue.Should().BeEquivalentTo(expectedValues, options => options.WithStrictOrdering());
    }

    private sealed class RecordingCurrentStateReader
        : IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string>
    {
        public IReadOnlyList<WorkflowExecutionCurrentStateDocument> Items { get; init; } = [];
        public ProjectionDocumentQuery? LastQuery { get; private set; }
        public List<string> GetKeys { get; } = [];

        public Task<WorkflowExecutionCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            GetKeys.Add(key);
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
            });
        }
    }
}
