using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionCurrentStateQueryPortFilterTests
{
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
            "CompensationDeadLetter");
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
            "CompensationDeadLetter");
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
        var sort = reader.LastQuery!.Sorts.Should().ContainSingle().Subject;
        sort.FieldPath.Should().Be(nameof(WorkflowExecutionCurrentStateDocument.UpdatedAtUtcValue));
        sort.Direction.Should().Be(ProjectionDocumentSortDirection.Desc);
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
        public ProjectionDocumentQuery? LastQuery { get; private set; }

        public Task<WorkflowExecutionCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
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
            return Task.FromResult(ProjectionDocumentQueryResult<WorkflowExecutionCurrentStateDocument>.Empty);
        }
    }
}
