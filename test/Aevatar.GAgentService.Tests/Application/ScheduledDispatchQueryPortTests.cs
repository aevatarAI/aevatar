using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScheduledDispatchQueryPortTests
{
    [Fact]
    public async Task GetAsync_ShouldPreserveProjectedServiceIdentity()
    {
        var identity = new ServiceIdentity
        {
            TenantId = "scope-alpha",
            AppId = "app-alpha",
            Namespace = "workflows",
            ServiceId = "svc-alpha",
        };
        var reader = new RecordingScheduleDocumentReader
        {
            Document = new ScheduledDispatchDocument
            {
                ScheduleId = "schedule-alpha",
                ServiceIdentity = identity.Clone(),
            },
        };
        var port = new ScheduledDispatchQueryPort(reader);

        var detail = await port.GetAsync("schedule-alpha");

        detail.Should().NotBeNull();
        detail!.Schedule.ServiceIdentity.Should().BeEquivalentTo(identity);
    }

    [Fact]
    public async Task ListAsync_ShouldApplyServiceIdentityFilters()
    {
        var reader = new RecordingScheduleDocumentReader();
        var port = new ScheduledDispatchQueryPort(reader);

        await port.ListAsync(new ScheduledDispatchListQuery(
            Take: 25,
            Cursor: "cursor-alpha",
            IncludeTotalCount: true,
            TargetKind: ScheduledDispatchTargetKind.ServiceInvocation,
            ServiceEndpointId: " chat ",
            ServiceKey: " svc-key-alpha ",
            ServiceId: " svc-alpha ",
            ServiceRevisionId: " rev-alpha ",
            ScheduleKind: ScheduledDispatchScheduleKind.Workflow), CancellationToken.None);

        reader.Query.Should().NotBeNull();
        reader.Query!.Take.Should().Be(25);
        reader.Query.Cursor.Should().Be("cursor-alpha");
        reader.Query.IncludeTotalCount.Should().BeTrue();
        reader.Query.Filters.ContainFilter(
            nameof(ScheduledDispatchDocument.ServiceEndpointId),
            ProjectionDocumentFilterOperator.Eq,
            "chat");
        reader.Query.Filters.ContainFilter(
            nameof(ScheduledDispatchDocument.ServiceKey),
            ProjectionDocumentFilterOperator.Eq,
            "svc-key-alpha");
        reader.Query.Filters.ContainFilter(
            nameof(ScheduledDispatchDocument.ServiceId),
            ProjectionDocumentFilterOperator.Eq,
            "svc-alpha");
        reader.Query.Filters.ContainFilter(
            nameof(ScheduledDispatchDocument.ServiceRevisionId),
            ProjectionDocumentFilterOperator.Eq,
            "rev-alpha");
    }

    private sealed class RecordingScheduleDocumentReader : IProjectionDocumentReader<ScheduledDispatchDocument, string>
    {
        public ProjectionDocumentQuery? Query { get; private set; }
        public ScheduledDispatchDocument? Document { get; init; }

        public Task<ScheduledDispatchDocument?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Document);

        public Task<ProjectionDocumentQueryResult<ScheduledDispatchDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            Query = query;
            return Task.FromResult(ProjectionDocumentQueryResult<ScheduledDispatchDocument>.Empty);
        }
    }
}

internal static class ProjectionDocumentFilterAssertions
{
    public static void ContainFilter(
        this IEnumerable<ProjectionDocumentFilter> filters,
        string fieldPath,
        ProjectionDocumentFilterOperator filterOperator,
        string value)
    {
        var filter = filters.SingleOrDefault(filter => filter.FieldPath == fieldPath);
        filter.Should().NotBeNull();
        filter!.Operator.Should().Be(filterOperator);
        filter.Value.RawValue.Should().Be(value);
    }
}
