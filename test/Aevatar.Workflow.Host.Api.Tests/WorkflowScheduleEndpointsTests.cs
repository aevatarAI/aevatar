using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowScheduleEndpointsTests
{
    [Fact]
    public async Task Preview_ShouldReturnBadRequest_WhenCronIsInvalid()
    {
        var result = await WorkflowScheduleEndpoints.Preview(
            new WorkflowSchedulePreviewHttpRequest
            {
                CronExpression = "invalid",
                Timezone = "UTC",
            },
            new ThrowingPreviewScheduleService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Preview_ShouldReturnOccurrences_WhenRequestIsValid()
    {
        var service = new StaticPreviewScheduleService();

        var result = await WorkflowScheduleEndpoints.Preview(
            new WorkflowSchedulePreviewHttpRequest
            {
                CronExpression = "0 9 * * *",
                Timezone = "UTC",
                Count = 2,
                FromUtc = new DateTimeOffset(2026, 5, 29, 0, 0, 0, TimeSpan.Zero),
            },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.LastCount.Should().Be(2);
        service.LastFromUtc.Should().Be(new DateTimeOffset(2026, 5, 29, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task RunNow_ShouldReturnNotFound_WhenScheduleDoesNotExist()
    {
        var result = await WorkflowScheduleEndpoints.RunNow(
            "missing",
            new NotFoundRunNowScheduleService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Enable_ShouldReturnBadRequest_WhenScheduleIdIsInvalid()
    {
        var result = await WorkflowScheduleEndpoints.Enable(
            "tenant/report",
            null,
            new InvalidEnableScheduleService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Get_ShouldReturnBadRequest_WhenScheduleIdIsInvalid()
    {
        var result = await WorkflowScheduleEndpoints.Get(
            "tenant/report",
            new InvalidGetScheduleService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddOptions()
                .BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        return http;
    }

    private sealed class ThrowingPreviewScheduleService : EmptyWorkflowScheduleApplicationService
    {
        public override Task<WorkflowSchedulePreview> PreviewAsync(
            string cronExpression,
            string? timezone,
            int count,
            DateTimeOffset? fromUtc = null,
            CancellationToken ct = default) =>
            throw new ArgumentException("Cron expression is invalid.");
    }

    private sealed class StaticPreviewScheduleService : EmptyWorkflowScheduleApplicationService
    {
        public int LastCount { get; private set; }
        public DateTimeOffset? LastFromUtc { get; private set; }

        public override Task<WorkflowSchedulePreview> PreviewAsync(
            string cronExpression,
            string? timezone,
            int count,
            DateTimeOffset? fromUtc = null,
            CancellationToken ct = default)
        {
            LastCount = count;
            LastFromUtc = fromUtc;
            return Task.FromResult(new WorkflowSchedulePreview(
                cronExpression,
                timezone ?? "UTC",
                [
                    new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 5, 30, 9, 0, 0, TimeSpan.Zero),
                ]));
        }
    }

    private sealed class NotFoundRunNowScheduleService : EmptyWorkflowScheduleApplicationService
    {
        public override Task<WorkflowScheduleRunNowReceipt> RunNowAsync(
            string scheduleId,
            CancellationToken ct = default) =>
            throw new WorkflowScheduleNotFoundException(scheduleId);
    }

    private sealed class InvalidEnableScheduleService : EmptyWorkflowScheduleApplicationService
    {
        public override Task<WorkflowScheduleMutationReceipt> EnableAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default) =>
            throw new ArgumentException("Schedule id may only contain letters, digits, '.', '_', ':', and '-'.");
    }

    private sealed class InvalidGetScheduleService : EmptyWorkflowScheduleApplicationService
    {
        public override Task<WorkflowScheduleDetail?> GetAsync(
            string scheduleId,
            CancellationToken ct = default) =>
            throw new ArgumentException("Schedule id may only contain letters, digits, '.', '_', ':', and '-'.");
    }

    private abstract class EmptyWorkflowScheduleApplicationService : IWorkflowScheduleApplicationService
    {
        public virtual Task<WorkflowScheduleMutationReceipt> CreateAsync(
            WorkflowScheduleConfiguration configuration,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowScheduleMutationReceipt> UpdateAsync(
            string scheduleId,
            WorkflowScheduleConfiguration configuration,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowScheduleMutationReceipt> EnableAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowScheduleMutationReceipt> DisableAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowScheduleDetail?> GetAsync(
            string scheduleId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowScheduleListResult> ListAsync(
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowSchedulePreview> PreviewAsync(
            string cronExpression,
            string? timezone,
            int count,
            DateTimeOffset? fromUtc = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowScheduleRunNowReceipt> RunNowAsync(
            string scheduleId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
