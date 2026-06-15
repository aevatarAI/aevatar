using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Hosting.Endpoints.Schedules;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScheduledDispatchEndpointsTests
{
    [Fact]
    public async Task Create_ShouldAcceptEnvelopeTargetAndForwardConfiguration()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var request = CreateEnvelopeRequest(scheduleId: "schedule-1");

        var result = await ScheduledDispatchEndpoints.Create(request, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        service.Created.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ScheduledDispatchConfiguration(
                "schedule-1",
                "Daily",
                new ScheduledDispatchTargetDescriptor(
                    ScheduledDispatchTargetKind.Envelope,
                    ActorId: "actor-1",
                    Envelope: request.Envelope!.Envelope),
                "0 9 * * *",
                "UTC",
                true,
                new Dictionary<string, string> { ["trace"] = "1" }));
    }

    [Fact]
    public async Task Create_ShouldRejectRequestsWithoutExactlyOneTarget()
    {
        var result = await ScheduledDispatchEndpoints.Create(
            new ScheduledDispatchConfigurationHttpRequest
            {
                ScheduleId = "schedule-1",
                CronExpression = "0 9 * * *",
            },
            new RecordingScheduledDispatchApplicationService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_ShouldMapConflict()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            CreateException = new ScheduledDispatchConflictException("schedule-1", "Schedule target cannot be prepared."),
        };

        var result = await ScheduledDispatchEndpoints.Create(CreateEnvelopeRequest(scheduleId: "schedule-1"), service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Update_ShouldUseRouteScheduleIdAsFallbackAndMapBadRequest()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            UpdateException = new ArgumentException("invalid update"),
        };

        var result = await ScheduledDispatchEndpoints.Update(
            "route-schedule",
            CreateEnvelopeRequest(scheduleId: null),
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.Updated.Should().ContainSingle()
            .Which.Configuration.ScheduleId.Should().Be("route-schedule");
    }

    [Fact]
    public async Task Update_ShouldAcceptServiceInvocationTarget()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var payload = Any.Pack(new StringValue { Value = "run" });
        var request = new ScheduledDispatchConfigurationHttpRequest
        {
            DisplayName = "Run service",
            CronExpression = "0 10 * * *",
            Timezone = "UTC",
            Enabled = false,
            ServiceInvocation = new ScheduledDispatchServiceInvocationTargetHttpRequest
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "tenant",
                    AppId = "app",
                    Namespace = "default",
                    ServiceId = "svc",
                },
                EndpointId = "run",
                Payload = payload,
                RevisionId = "rev-1",
                Caller = new ServiceInvocationCaller
                {
                    ServiceKey = "caller-service",
                    TenantId = "tenant",
                    AppId = "app",
                },
            },
        };

        var result = await ScheduledDispatchEndpoints.Update("schedule-1", request, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var configuration = service.Updated.Should().ContainSingle().Which.Configuration;
        configuration.ScheduleId.Should().Be("schedule-1");
        configuration.Target.Kind.Should().Be(ScheduledDispatchTargetKind.ServiceInvocation);
        configuration.Target.ServiceInvocation.Should().NotBeNull();
        configuration.Target.ServiceInvocation!.Identity.ServiceId.Should().Be("svc");
        configuration.Target.ServiceInvocation.EndpointId.Should().Be("run");
        configuration.Target.ServiceInvocation.Payload.Should().Be(payload);
        configuration.Target.ServiceInvocation.RevisionId.Should().Be("rev-1");
        configuration.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldReturnBadRequest_WhenServiceInvocationAuthIsEmpty()
    {
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest());

        var result = await ScheduledDispatchEndpoints.Create(
            request,
            new RecordingScheduledDispatchApplicationService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_ShouldAcceptTenantlessServiceInvocationNyxIdSubject()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest
        {
            SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceHttpRequest
            {
                Subject = new ScheduledServiceInvocationNyxIdSubjectRefHttpRequest
                {
                    Platform = "GitHub",
                    ExternalUserId = "ou-user-1",
                },
                Scope = " proxy ",
            },
        });

        var result = await ScheduledDispatchEndpoints.Create(request, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var auth = service.Created.Should().ContainSingle().Which.Target.ServiceInvocation!.Auth;
        auth.Should().NotBeNull();
        auth!.SenderNyxId.Should().NotBeNull();
        var subject = auth.SenderNyxId.Subject;
        subject.Platform.Should().Be("github");
        subject.Tenant.Should().BeEmpty();
        subject.ExternalUserId.Should().Be("ou-user-1");
    }

    [Fact]
    public async Task Create_ShouldReturnBadRequest_WhenServiceInvocationAuthSubjectIsNull()
    {
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest
        {
            SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceHttpRequest
            {
                Subject = null!,
                Scope = "proxy",
            },
        });

        var result = await ScheduledDispatchEndpoints.Create(
            request,
            new RecordingScheduledDispatchApplicationService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_ShouldReturnBadRequest_WhenServiceInvocationAuthFieldsAreBlank()
    {
        var request = CreateServiceInvocationRequestWithAuth(new ScheduledServiceInvocationAuthHttpRequest
        {
            SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceHttpRequest
            {
                Subject = new ScheduledServiceInvocationNyxIdSubjectRefHttpRequest
                {
                    Platform = " ",
                    Tenant = "tenant-1",
                    ExternalUserId = "ou-user-1",
                },
                Scope = "",
            },
        });

        var result = await ScheduledDispatchEndpoints.Create(
            request,
            new RecordingScheduledDispatchApplicationService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Enable_ShouldMapNotFound()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            EnableException = new ScheduledDispatchNotFoundException("missing"),
        };

        var result = await ScheduledDispatchEndpoints.Enable(
            "missing",
            new ScheduledDispatchStateChangeHttpRequest { Reason = "resume" },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        service.Enabled.Should().ContainSingle().Which.Should().Be(("missing", "resume"));
    }

    [Fact]
    public async Task Enable_ShouldDefaultEmptyReasonAndMapBadRequest()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            EnableException = new ArgumentException("invalid id"),
        };

        var result = await ScheduledDispatchEndpoints.Enable("invalid/id", null, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.Enabled.Should().ContainSingle().Which.Should().Be(("invalid/id", string.Empty));
    }

    [Fact]
    public async Task Disable_ShouldMapConflict()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            DisableException = new ScheduledDispatchConflictException("schedule-1", "Schedule cannot be disabled."),
        };

        var result = await ScheduledDispatchEndpoints.Disable(
            "schedule-1",
            new ScheduledDispatchStateChangeHttpRequest { Reason = "pause" },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        service.Disabled.Should().ContainSingle().Which.Should().Be(("schedule-1", "pause"));
    }

    [Fact]
    public async Task Disable_ShouldAccept()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.Disable("schedule-1", null, service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        service.Disabled.Should().ContainSingle().Which.Should().Be(("schedule-1", string.Empty));
    }

    [Fact]
    public async Task List_ShouldForwardQueryParameters()
    {
        var service = new RecordingScheduledDispatchApplicationService();

        var result = await ScheduledDispatchEndpoints.List(
            service,
            take: 25,
            cursor: "cursor-1",
            includeTotalCount: true);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.LastListTake.Should().Be(25);
        service.LastListCursor.Should().Be("cursor-1");
        service.LastListIncludeTotalCount.Should().BeTrue();
    }

    [Fact]
    public async Task Get_ShouldReturnOkAndNotFound()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            Detail = CreateDetail("schedule-1"),
        };

        var ok = await ScheduledDispatchEndpoints.Get("schedule-1", service);
        var notFound = await ScheduledDispatchEndpoints.Get("missing", new RecordingScheduledDispatchApplicationService());

        var okHttp = CreateHttpContext();
        await ok.ExecuteAsync(okHttp);
        var notFoundHttp = CreateHttpContext();
        await notFound.ExecuteAsync(notFoundHttp);

        okHttp.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        notFoundHttp.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Get_ShouldMapBadRequest()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            GetException = new ArgumentException("invalid id"),
        };

        var result = await ScheduledDispatchEndpoints.Get("invalid/id", service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Preview_ShouldForwardDefaultsAndMapBadRequest()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            PreviewException = new ArgumentException("invalid cron"),
        };

        var result = await ScheduledDispatchEndpoints.Preview(
            new ScheduledDispatchPreviewHttpRequest
            {
                CronExpression = "invalid",
                Timezone = "UTC",
                Count = 0,
            },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.LastPreviewCount.Should().Be(5);
    }

    [Fact]
    public async Task Preview_ShouldReturnOccurrences()
    {
        var service = new RecordingScheduledDispatchApplicationService();
        var fromUtc = new DateTimeOffset(2026, 5, 29, 0, 0, 0, TimeSpan.Zero);

        var result = await ScheduledDispatchEndpoints.Preview(
            new ScheduledDispatchPreviewHttpRequest
            {
                CronExpression = "0 9 * * *",
                Timezone = "UTC",
                Count = 2,
                FromUtc = fromUtc,
            },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.LastPreviewCount.Should().Be(2);
        service.LastPreviewFromUtc.Should().Be(fromUtc);
    }

    [Fact]
    public async Task RunNow_ShouldAcceptAndMapNotFound()
    {
        var accepted = await ScheduledDispatchEndpoints.RunNow(
            "schedule-1",
            new RecordingScheduledDispatchApplicationService());
        var notFound = await ScheduledDispatchEndpoints.RunNow(
            "missing",
            new RecordingScheduledDispatchApplicationService
            {
                RunNowException = new ScheduledDispatchNotFoundException("missing"),
            });

        var acceptedHttp = CreateHttpContext();
        await accepted.ExecuteAsync(acceptedHttp);
        var notFoundHttp = CreateHttpContext();
        await notFound.ExecuteAsync(notFoundHttp);

        acceptedHttp.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        notFoundHttp.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task RunNow_ShouldMapConflict()
    {
        var service = new RecordingScheduledDispatchApplicationService
        {
            RunNowException = new ScheduledDispatchConflictException("schedule-1", "Schedule is disabled."),
        };

        var result = await ScheduledDispatchEndpoints.RunNow("schedule-1", service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    private static ScheduledDispatchConfigurationHttpRequest CreateEnvelopeRequest(string? scheduleId) =>
        new()
        {
            ScheduleId = scheduleId,
            DisplayName = "Daily",
            CronExpression = "0 9 * * *",
            Timezone = "UTC",
            Enabled = true,
            Headers = new Dictionary<string, string> { ["trace"] = "1" },
            Envelope = new ScheduledDispatchEnvelopeTargetHttpRequest
            {
                ActorId = "actor-1",
                Envelope = new EventEnvelope
                {
                    Id = "template",
                    Payload = Any.Pack(new StringValue { Value = "run" }),
                },
            },
        };

    private static ScheduledDispatchConfigurationHttpRequest CreateServiceInvocationRequestWithAuth(
        ScheduledServiceInvocationAuthHttpRequest auth) =>
        new()
        {
            ScheduleId = "schedule-1",
            DisplayName = "Run service",
            CronExpression = "0 10 * * *",
            Timezone = "UTC",
            Enabled = false,
            ServiceInvocation = new ScheduledDispatchServiceInvocationTargetHttpRequest
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "tenant",
                    AppId = "app",
                    Namespace = "default",
                    ServiceId = "svc",
                },
                EndpointId = "run",
                Payload = Any.Pack(new StringValue { Value = "run" }),
                Auth = auth,
            },
        };

    private static ScheduledDispatchDetail CreateDetail(string scheduleId) =>
        new(
            new ScheduledDispatchSummary(
                scheduleId,
                "Daily",
                ScheduledDispatchTargetKind.Envelope,
                "actor-1",
                Any.Pack(new StringValue { Value = "run" }).TypeUrl,
                string.Empty,
                string.Empty,
                string.Empty,
                "0 9 * * *",
                "UTC",
                true,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                null,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                0,
                new Dictionary<string, string>(),
                "actor:schedule-1"),
            []);

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

    private sealed class RecordingScheduledDispatchApplicationService : IScheduledDispatchApplicationService
    {
        public List<ScheduledDispatchConfiguration> Created { get; } = [];
        public List<ScheduledDispatchConfiguration> Ensured { get; } = [];
        public List<(string ScheduleId, ScheduledDispatchConfiguration Configuration)> Updated { get; } = [];
        public List<(string ScheduleId, string Reason)> Enabled { get; } = [];
        public List<(string ScheduleId, string Reason)> Disabled { get; } = [];
        public int? LastListTake { get; private set; }
        public string? LastListCursor { get; private set; }
        public bool? LastListIncludeTotalCount { get; private set; }
        public int? LastPreviewCount { get; private set; }
        public DateTimeOffset? LastPreviewFromUtc { get; private set; }
        public ScheduledDispatchDetail? Detail { get; set; }
        public Exception? CreateException { get; set; }
        public Exception? UpdateException { get; set; }
        public Exception? EnableException { get; set; }
        public Exception? DisableException { get; set; }
        public Exception? GetException { get; set; }
        public Exception? PreviewException { get; set; }
        public Exception? RunNowException { get; set; }

        public Task<ScheduledDispatchMutationReceipt> CreateAsync(
            ScheduledDispatchConfiguration configuration,
            CancellationToken ct = default)
        {
            Created.Add(configuration);
            if (CreateException != null)
                throw CreateException;

            return Task.FromResult(new ScheduledDispatchMutationReceipt(
                configuration.ScheduleId,
                $"actor:{configuration.ScheduleId}",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        public Task<ScheduledDispatchMutationReceipt> EnsureAsync(
            ScheduledDispatchConfiguration configuration,
            CancellationToken ct = default)
        {
            Ensured.Add(configuration);
            return Task.FromResult(new ScheduledDispatchMutationReceipt(
                configuration.ScheduleId,
                $"actor:{configuration.ScheduleId}",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        public Task<ScheduledDispatchMutationReceipt> UpdateAsync(
            string scheduleId,
            ScheduledDispatchConfiguration configuration,
            CancellationToken ct = default)
        {
            Updated.Add((scheduleId, configuration));
            if (UpdateException != null)
                throw UpdateException;

            return Task.FromResult(new ScheduledDispatchMutationReceipt(
                scheduleId,
                $"actor:{scheduleId}",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        public Task<ScheduledDispatchMutationReceipt> EnableAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default)
        {
            Enabled.Add((scheduleId, reason));
            if (EnableException != null)
                throw EnableException;

            return Task.FromResult(new ScheduledDispatchMutationReceipt(
                scheduleId,
                $"actor:{scheduleId}",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        public Task<ScheduledDispatchMutationReceipt> DisableAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default)
        {
            Disabled.Add((scheduleId, reason));
            if (DisableException != null)
                throw DisableException;

            return Task.FromResult(new ScheduledDispatchMutationReceipt(
                scheduleId,
                $"actor:{scheduleId}",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        public Task<ScheduledDispatchDetail?> GetAsync(string scheduleId, CancellationToken ct = default)
        {
            if (GetException != null)
                throw GetException;

            return Task.FromResult(Detail?.Schedule.ScheduleId == scheduleId ? Detail : null);
        }

        public Task<ScheduledDispatchListResult> ListAsync(
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default)
        {
            LastListTake = take;
            LastListCursor = cursor;
            LastListIncludeTotalCount = includeTotalCount;
            return Task.FromResult(new ScheduledDispatchListResult([], null, includeTotalCount ? 0 : null));
        }

        public Task<ScheduledDispatchListResult> ListAsync(
            ScheduledDispatchListQuery query,
            CancellationToken ct = default)
        {
            LastListTake = query.Take;
            LastListCursor = query.Cursor;
            LastListIncludeTotalCount = query.IncludeTotalCount;
            return Task.FromResult(new ScheduledDispatchListResult([], null, query.IncludeTotalCount ? 0 : null));
        }

        public Task<ScheduledDispatchPreview> PreviewAsync(
            string cronExpression,
            string? timezone,
            int count,
            DateTimeOffset? fromUtc = null,
            CancellationToken ct = default)
        {
            LastPreviewCount = count;
            LastPreviewFromUtc = fromUtc;
            if (PreviewException != null)
                throw PreviewException;

            return Task.FromResult(new ScheduledDispatchPreview(
                cronExpression,
                timezone ?? "UTC",
                [new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero)]));
        }

        public Task<ScheduledDispatchRunNowReceipt> RunNowAsync(string scheduleId, CancellationToken ct = default)
        {
            if (RunNowException != null)
                throw RunNowException;

            return Task.FromResult(new ScheduledDispatchRunNowReceipt(
                scheduleId,
                $"actor:{scheduleId}",
                DateTimeOffset.UtcNow,
                "run-now:schedule-1",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }
    }
}
