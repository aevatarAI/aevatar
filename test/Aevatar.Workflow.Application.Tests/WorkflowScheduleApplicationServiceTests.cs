using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Schedules;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Schedules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowScheduleApplicationServiceTests
{
    [Fact]
    public async Task CreateAsync_ShouldNormalizeConfigurationAndDispatchConfigure()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            ResolveActorId = string.Empty,
        };
        var preparation = new FakeScheduledDispatchPreparationService();
        var service = CreateService(actorPort, new FakeWorkflowScheduleQueryPort(), preparation);

        var receipt = await service.CreateAsync(new WorkflowScheduleConfiguration(
            ScheduleId: " daily-report ",
            DisplayName: " Daily report ",
            WorkflowName: " direct ",
            Prompt: " summarize status ",
            CronExpression: "*/15 * * * *",
            Timezone: " UTC ",
            Enabled: true,
            Headers: new Dictionary<string, string>
            {
                [" trace "] = " enabled ",
                [" "] = "ignored",
            },
            ScopeId: " scope-1 ",
            Auth: CreateDefaultAuth()));

        receipt.ScheduleId.Should().Be("daily-report");
        receipt.ScheduleActorId.Should().Be("actor:daily-report");
        actorPort.EnsureScheduleIds.Should().Equal("daily-report");
        actorPort.Created.Should().ContainSingle();
        var configured = actorPort.Created.Single();
        configured.ActorId.Should().Be("actor:daily-report");
        configured.Configuration.ScheduleId.Should().Be("daily-report");
        configured.Configuration.DisplayName.Should().Be("Daily report");
        configured.Configuration.Target.Kind.Should().Be(ScheduledDispatchTargetKind.ServiceInvocation);
        configured.Configuration.Target.ServiceInvocation.Should().NotBeNull();
        configured.Configuration.Target.ServiceInvocation!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-1",
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = "direct",
        });
        configured.Configuration.Target.ServiceInvocation.EndpointId.Should().Be("chat");
        configured.Configuration.Target.ServiceInvocation.Payload.Unpack<ChatRequestEvent>().Prompt.Should().Be("summarize status");
        configured.Configuration.CredentialRequirementTargetKind.Should()
            .Be(ScheduledDispatchCredentialRequirementTargetKind.WorkflowService);
        configured.Configuration.Target.ServiceInvocation.Auth.Should().NotBeNull();
        configured.Configuration.Target.ServiceInvocation.Auth!.SenderNyxId.Should().NotBeNull();
        configured.Configuration.Timezone.Should().Be("UTC");
        configured.Configuration.Headers.Should().Contain(
            new KeyValuePair<string, string>("trace", "enabled"));
        configured.Configuration.Headers.Should().NotContainKey("workflow.schedule.workflow_name");
        configured.Configuration.Headers.Should().NotContainKey("workflow.schedule.prompt");
        configured.Configuration.Headers.Should().NotContainKey("workflow.schedule.scope_id");
        configured.Configuration.Headers.Should().NotContainKey("workflow.schedule.source_actor_id");
        configured.Dispatch.TargetActorId.Should().Be("target:daily-report");
        configured.Dispatch.Descriptor.Kind.Should().Be(ScheduledDispatchTargetKind.ServiceInvocation);
        preparation.Configurations.Should().ContainSingle()
            .Which.ScheduleId.Should().Be("daily-report");
    }

    [Fact]
    public async Task CreateAsync_ShouldMapOneShotWorkflowSchedule()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            ResolveActorId = string.Empty,
        };
        var service = CreateService(actorPort);
        var fireAt = DateTimeOffset.UtcNow.AddHours(1);

        await service.CreateAsync(CreateConfiguration("one-shot") with
        {
            CronExpression = "0 9 * * *",
            ScheduleMode = WorkflowScheduleMode.OneShotAtUtc,
            OneShotFireAt = fireAt,
        });

        var configuration = actorPort.Created.Should().ContainSingle().Which.Configuration;
        configuration.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Workflow);
        configuration.ScheduleMode.Should().Be(ScheduledDispatchScheduleMode.OneShotAtUtc);
        configuration.CronExpression.Should().BeEmpty();
        configuration.OneShotFireAt.Should().Be(fireAt.ToUniversalTime());
        configuration.Target.Kind.Should().Be(ScheduledDispatchTargetKind.ServiceInvocation);
        configuration.Target.ServiceInvocation!.EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task WorkflowScheduleCommandPort_EnsureAsync_ShouldMapWorkflowScheduleAndDispatchEnsure()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var queryPort = new FakeWorkflowScheduleQueryPort();
        var scheduledDispatches = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            new FakeScheduledDispatchPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort());
        var port = new WorkflowScheduleCommandPort(scheduledDispatches);

        var receipt = await port.EnsureAsync(new WorkflowScheduleConfiguration(
            ScheduleId: " daily-report ",
            DisplayName: " Daily report ",
            WorkflowName: " direct ",
            Prompt: " summarize status ",
            CronExpression: "*/15 * * * *",
            Timezone: " UTC ",
            Enabled: true,
            Headers: new Dictionary<string, string> { [" trace "] = " enabled " },
            ScopeId: " scope-1 ",
            Auth: CreateDefaultAuth()));

        receipt.ScheduleId.Should().Be("daily-report");
        actorPort.EnsureScheduleIds.Should().Equal("daily-report");
        actorPort.Ensured.Should().ContainSingle();
        actorPort.Created.Should().BeEmpty();
        actorPort.Updated.Should().BeEmpty();
        // Ensure performs exactly one readmodel read: the delete-tombstone guard
        // (a deleted schedule surfaces as typed not-found instead of a silently
        // swallowed reconfigure of the tombstoned actor).
        queryPort.GetScheduleIds.Should().Equal("daily-report");
        var ensured = actorPort.Ensured.Single();
        ensured.Configuration.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Workflow);
        ensured.Configuration.Target.Kind.Should().Be(ScheduledDispatchTargetKind.ServiceInvocation);
        ensured.Configuration.Target.ServiceInvocation.Should().NotBeNull();
        var invocation = ensured.Configuration.Target.ServiceInvocation!;
        invocation.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-1",
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = "direct",
        });
        invocation.EndpointId.Should().Be("chat");
        invocation.Payload.Unpack<ChatRequestEvent>().Prompt.Should().Be("summarize status");
        invocation.Payload.Unpack<ChatRequestEvent>().Metadata.Should().Contain("trace", "enabled");
        invocation.Auth.Should().NotBeNull();
        invocation.Auth!.SenderNyxId.Should().NotBeNull();
    }

    [Fact]
    public async Task WorkflowScheduleCommandPort_EnsureAsync_ShouldForwardScopeOwnerMutationContextToAdmission()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var admissionPort = new RecordingScheduledDispatchCredentialAdmissionPort();
        var scheduledDispatches = new ScheduledDispatchApplicationService(
            actorPort,
            new FakeWorkflowScheduleQueryPort(),
            new FakeScheduledDispatchPreparationService(),
            admissionPort);
        var port = new WorkflowScheduleCommandPort(scheduledDispatches);

        await port.EnsureAsync(CreateScopeOwnerWorkflowConfiguration("owner-ensure"));

        AssertScopeOwnerAdmissionRequest(admissionPort.Requests.Should().ContainSingle().Which);
        actorPort.Ensured.Should().ContainSingle()
            .Which.Configuration.Target.ServiceInvocation!.Auth!.ScopeOwnerNyxId!.OwnerSubject
            .Should().BeEquivalentTo(new ScheduledServiceInvocationNyxIdSubjectRef("nyx", "tenant-1", "owner-user-1"));
    }

    [Fact]
    public async Task CreateAsync_ShouldMapWorkflowScheduleAuthToServiceInvocationAuth()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            ResolveActorId = string.Empty,
        };
        var service = CreateService(actorPort);

        await service.CreateAsync(new WorkflowScheduleConfiguration(
            ScheduleId: "auth-schedule",
            DisplayName: "Auth schedule",
            WorkflowName: "direct",
            Prompt: "summarize status",
            CronExpression: "*/15 * * * *",
            Timezone: "UTC",
            Enabled: true,
            Headers: new Dictionary<string, string>(),
            ScopeId: "scope-1",
            Auth: new WorkflowScheduleAuth(new WorkflowScheduleNyxIdCredentialSource(
                new WorkflowScheduleNyxIdSubjectRef(" lark ", " tenant-1 ", " ou-user-1 "),
                " proxy "))));

        var invocation = actorPort.Created.Single().Configuration.Target.ServiceInvocation!;
        invocation.Auth.Should().NotBeNull();
        invocation.Auth!.SenderNyxId.Should().NotBeNull();
        invocation.Auth.SenderNyxId!.Subject.Platform.Should().Be("lark");
        invocation.Auth.SenderNyxId.Subject.Tenant.Should().Be("tenant-1");
        invocation.Auth.SenderNyxId.Subject.ExternalUserId.Should().Be("ou-user-1");
        invocation.Auth.SenderNyxId.Scope.Should().Be("proxy");
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectWorkflowScheduleWithoutCredentialSource()
    {
        var service = CreateService(new FakeWorkflowScheduleActorPort
        {
            ResolveActorId = string.Empty,
        });

        var act = () => service.CreateAsync(CreateConfiguration("no-auth-schedule") with
        {
            Auth = null,
        });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*requires a typed service invocation credential source*");
    }

    [Fact]
    public async Task CreateAsync_ShouldMapTenantlessWorkflowScheduleAuthToEmptyTenant()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            ResolveActorId = string.Empty,
        };
        var service = CreateService(actorPort);

        await service.CreateAsync(CreateConfiguration("auth-schedule") with
        {
            Auth = new WorkflowScheduleAuth(new WorkflowScheduleNyxIdCredentialSource(
                new WorkflowScheduleNyxIdSubjectRef("lark", " ", "ou-user-1"),
                "proxy")),
        });

        var invocation = actorPort.Created.Single().Configuration.Target.ServiceInvocation!;
        invocation.Auth!.SenderNyxId!.Subject.Tenant.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ShouldMapWorkflowScheduleScopeOwnerAuthToServiceInvocationAuth()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            ResolveActorId = string.Empty,
        };
        var admissionPort = new RecordingScheduledDispatchCredentialAdmissionPort();
        var service = CreateService(actorPort, admissionPort: admissionPort);

        await service.CreateAsync(CreateConfiguration("owner-auth-schedule") with
        {
            Auth = new WorkflowScheduleAuth(
                ScopeOwnerNyxId: new WorkflowScheduleScopeOwnerNyxIdCredentialSource(
                    " owner-proxy ",
                    new WorkflowScheduleNyxIdSubjectRef(" nyx ", " tenant-1 ", " owner-user-1 "))),
            MutationContext = new WorkflowScheduleMutationContext(
                "scope-1",
                new WorkflowScheduleNyxIdSubjectRef(" nyx ", " tenant-1 ", " owner-user-1 ")),
        });

        var invocation = actorPort.Created.Single().Configuration.Target.ServiceInvocation!;
        invocation.Auth.Should().NotBeNull();
        invocation.Auth!.SenderNyxId.Should().BeNull();
        invocation.Auth.ScopeOwnerNyxId!.Scope.Should().Be("owner-proxy");
        invocation.Auth.ScopeOwnerNyxId.OwnerSubject.Should().BeEquivalentTo(
            new ScheduledServiceInvocationNyxIdSubjectRef("nyx", "tenant-1", "owner-user-1"));
        admissionPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectWorkflowScheduleAuthWithMultipleCredentialSources()
    {
        var service = CreateService();

        var act = () => service.CreateAsync(CreateConfiguration("auth-schedule") with
        {
            Auth = new WorkflowScheduleAuth(
                new WorkflowScheduleNyxIdCredentialSource(
                    new WorkflowScheduleNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
                    "proxy"),
                new WorkflowScheduleScopeOwnerNyxIdCredentialSource(
                    "owner-proxy",
                    new WorkflowScheduleNyxIdSubjectRef("nyx", string.Empty, "owner-user-1"))),
        });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Exactly one workflow schedule credential source*");
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectEmptyWorkflowScheduleScopeOwnerAuth()
    {
        var service = CreateService();

        var act = () => service.CreateAsync(CreateConfiguration("auth-schedule") with
        {
            Auth = new WorkflowScheduleAuth(
                ScopeOwnerNyxId: new WorkflowScheduleScopeOwnerNyxIdCredentialSource(
                    " ",
                    new WorkflowScheduleNyxIdSubjectRef("nyx", string.Empty, "owner-user-1"))),
        });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Scope*required*");
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectEmptyWorkflowScheduleAuth()
    {
        var service = CreateService();

        var act = () => service.CreateAsync(CreateConfiguration("auth-schedule") with
        {
            Auth = new WorkflowScheduleAuth(),
        });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectWorkflowScheduleAuthWithoutSubject()
    {
        var service = CreateService();

        var act = () => service.CreateAsync(CreateConfiguration("auth-schedule") with
        {
            Auth = new WorkflowScheduleAuth(new WorkflowScheduleNyxIdCredentialSource(null!, "proxy")),
        });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*subject is required*");
    }

    [Theory]
    [InlineData("", "tenant-1", "ou-user-1", "proxy")]
    [InlineData("lark", "tenant-1", "", "proxy")]
    [InlineData("lark", "tenant-1", "ou-user-1", "")]
    public async Task CreateAsync_ShouldRejectInvalidWorkflowScheduleAuth(
        string platform,
        string tenant,
        string externalUserId,
        string scope)
    {
        var service = CreateService();

        var act = () => service.CreateAsync(CreateConfiguration("auth-schedule") with
        {
            Auth = new WorkflowScheduleAuth(new WorkflowScheduleNyxIdCredentialSource(
                new WorkflowScheduleNyxIdSubjectRef(platform, tenant, externalUserId),
                scope)),
        });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectInvalidCron()
    {
        var service = CreateService();

        var act = () => service.CreateAsync(new WorkflowScheduleConfiguration(
            ScheduleId: "invalid",
            DisplayName: string.Empty,
            WorkflowName: "direct",
            Prompt: "hello",
            CronExpression: "not cron",
            Timezone: "UTC",
            Enabled: true,
            Headers: new Dictionary<string, string>()));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("tenant/report")]
    [InlineData("tenant?report")]
    [InlineData("tenant%report")]
    public async Task CreateAsync_ShouldRejectRouteUnsafeScheduleId(string scheduleId)
    {
        var service = CreateService();

        var act = () => service.CreateAsync(CreateConfiguration(scheduleId));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*letters, digits, '.', '_', and '-'*");
    }

    [Fact]
    public async Task PreviewAsync_ShouldReturnBoundedUtcOccurrences()
    {
        var service = CreateService();

        var preview = await service.PreviewAsync(
            "0 9 * * *",
            "UTC",
            3,
            new DateTimeOffset(2026, 5, 29, 8, 30, 0, TimeSpan.Zero));

        preview.CronExpression.Should().Be("0 9 * * *");
        preview.Timezone.Should().Be("UTC");
        preview.NextFireTimes.Should().Equal(
            new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 30, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 31, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task RunNowAsync_ShouldDispatchManualFireWithStableIdempotencyKey()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1");
        var service = CreateService(actorPort, queryPort);

        var receipt = await service.RunNowAsync("schedule-1");

        receipt.ScheduleId.Should().Be("schedule-1");
        receipt.ScheduleActorId.Should().Be("actor:schedule-1");
        receipt.Accepted.Should().BeTrue();
        receipt.IdempotencyKey.Should().Be(
            ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", receipt.ScheduledFireAt));
        receipt.IdempotencyKey.Should().StartWith("schedule:schedule-1:fire:");
        actorPort.RunNowRequests.Should().ContainSingle()
            .Which.ActorId.Should().Be("actor:schedule-1");
        actorPort.EnsureScheduleIds.Should().BeEmpty();
        actorPort.ResolveScheduleIds.Should().Equal("schedule-1");
    }

    [Fact]
    public async Task RunNowAsync_WhenDispatchAdmissionIsRejected_ShouldReturnRejectedReceipt()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            AdmissionFactory = (actorId, envelope) => new DispatchAdmission(
                Accepted: false,
                CommandId: envelope.Id,
                AckedAt: DateTimeOffset.UtcNow,
                ActorId: actorId,
                CorrelationId: envelope.Propagation?.CorrelationId ?? envelope.Id),
        };
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1");
        var service = CreateService(actorPort, queryPort);

        var receipt = await service.RunNowAsync("schedule-1");

        receipt.Accepted.Should().BeFalse();
        actorPort.RunNowRequests.Should().ContainSingle()
            .Which.ActorId.Should().Be("actor:schedule-1");
    }

    [Fact]
    public async Task EnableAsync_WhenDispatchAdmissionIsRejected_ShouldReturnRejectedReceipt()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            AdmissionFactory = (actorId, envelope) => new DispatchAdmission(
                Accepted: false,
                CommandId: envelope.Id,
                AckedAt: DateTimeOffset.UtcNow,
                ActorId: actorId,
                CorrelationId: envelope.Propagation?.CorrelationId ?? envelope.Id),
        };
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1");
        var service = CreateService(actorPort, queryPort);

        var receipt = await service.EnableAsync("schedule-1", "resume");

        receipt.Should().BeEquivalentTo(new
        {
            ScheduleId = "schedule-1",
            ScheduleActorId = "actor:schedule-1",
            Accepted = false,
            AckStage = "accepted",
        });
        receipt.CommandId.Should().NotBeNullOrWhiteSpace();
        receipt.CorrelationId.Should().NotBeNullOrWhiteSpace();
        receipt.AckedAt.Should().NotBe(default);
        actorPort.Enabled.Should().ContainSingle()
            .Which.Should().Be(("actor:schedule-1", "resume"));
    }

    [Fact]
    public async Task CreateAsync_WhenDispatchAdmissionIsRejected_ShouldReturnRejectedReceipt()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            ResolveActorId = string.Empty,
            AdmissionFactory = (actorId, envelope) => new DispatchAdmission(
                Accepted: false,
                CommandId: envelope.Id,
                AckedAt: DateTimeOffset.UtcNow,
                ActorId: actorId,
                CorrelationId: envelope.Propagation?.CorrelationId ?? envelope.Id),
        };
        var service = CreateService(actorPort);

        var receipt = await service.CreateAsync(CreateConfiguration("schedule-1"));

        receipt.Should().BeEquivalentTo(new
        {
            ScheduleId = "schedule-1",
            ScheduleActorId = "actor:schedule-1",
            Accepted = false,
            AckStage = "accepted",
        });
        receipt.CommandId.Should().NotBeNullOrWhiteSpace();
        receipt.CorrelationId.Should().NotBeNullOrWhiteSpace();
        receipt.AckedAt.Should().NotBe(default);
        actorPort.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task DisableAsync_ShouldReturnNotFound_WhenScheduleActorDoesNotExist()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            ResolveActorId = string.Empty,
        };
        var service = CreateService(actorPort, new FakeWorkflowScheduleQueryPort());

        var act = () => service.DisableAsync("missing", string.Empty);

        await act.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        actorPort.EnsureScheduleIds.Should().BeEmpty();
        actorPort.Disabled.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_ShouldClampTakeBeforeQueryingReadModel()
    {
        var queryPort = new FakeWorkflowScheduleQueryPort();
        var service = CreateService(new FakeWorkflowScheduleActorPort(), queryPort);

        await service.ListAsync(500, "cursor", includeTotalCount: true);

        queryPort.LastTake.Should().Be(200);
        queryPort.LastCursor.Should().Be("cursor");
        queryPort.LastIncludeTotalCount.Should().BeTrue();
    }

    [Fact]
    public async Task EnableDisableAndRunNow_ShouldRejectMissingScheduleActor()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            ResolveActorId = string.Empty,
        };
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1");
        var service = CreateService(actorPort, queryPort);

        var enable = () => service.EnableAsync(" schedule-1 ", " resume ");
        var runNow = () => service.RunNowAsync("schedule-1");

        await enable.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await runNow.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        actorPort.ResolveScheduleIds.Should().Equal("schedule-1", "schedule-1");
        actorPort.Enabled.Should().BeEmpty();
        actorPort.RunNowRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task EnableDisable_ShouldNormalizeReasonsAndDispatchToResolvedActor()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1");
        var service = CreateService(actorPort, queryPort);

        var enabled = await service.EnableAsync(" schedule-1 ", " resume ");
        var disabled = await service.DisableAsync("schedule-1", " ");

        enabled.Should().BeEquivalentTo(new
        {
            ScheduleId = "schedule-1",
            ScheduleActorId = "actor:schedule-1",
            Accepted = true,
            AckStage = "accepted",
        });
        enabled.CommandId.Should().NotBeNullOrWhiteSpace();
        enabled.CorrelationId.Should().NotBeNullOrWhiteSpace();
        enabled.AckedAt.Should().NotBe(default);
        disabled.Should().BeEquivalentTo(new
        {
            ScheduleId = "schedule-1",
            ScheduleActorId = "actor:schedule-1",
            Accepted = true,
            AckStage = "accepted",
        });
        disabled.CommandId.Should().NotBeNullOrWhiteSpace();
        disabled.CorrelationId.Should().NotBeNullOrWhiteSpace();
        disabled.AckedAt.Should().NotBe(default);
        actorPort.Enabled.Should().ContainSingle()
            .Which.Should().Be(("actor:schedule-1", "resume"));
        actorPort.Disabled.Should().ContainSingle()
            .Which.Should().Be(("actor:schedule-1", string.Empty));
    }

    [Fact]
    public async Task DeleteAsync_ShouldNormalizeReasonAndDispatchToResolvedActor()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1");
        var service = CreateService(actorPort, queryPort);

        var receipt = await service.DeleteAsync(" schedule-1 ", " retire ");

        receipt.Should().BeEquivalentTo(new
        {
            ScheduleId = "schedule-1",
            ScheduleActorId = "actor:schedule-1",
            Accepted = true,
            AckStage = "accepted",
        });
        receipt.CommandId.Should().NotBeNullOrWhiteSpace();
        receipt.CorrelationId.Should().NotBeNullOrWhiteSpace();
        receipt.AckedAt.Should().NotBe(default);
        actorPort.Deleted.Should().ContainSingle()
            .Which.Should().Be(("actor:schedule-1", "retire"));
    }

    [Fact]
    public async Task UpdateAsync_ShouldUseRouteScheduleIdAndScrubOptionalAdapterHeaders()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["route-schedule"] = CreateDetail("route-schedule");
        var service = CreateService(actorPort, queryPort);

        var receipt = await service.UpdateAsync(
            " route-schedule ",
            new WorkflowScheduleConfiguration(
                ScheduleId: "body-schedule",
                DisplayName: " ",
                WorkflowName: " direct ",
                Prompt: " hello ",
                CronExpression: "*/20 * * * *",
                Timezone: " ",
                Enabled: false,
                Headers: new Dictionary<string, string>
                {
                    [" x "] = " y ",
                    ["empty"] = " ",
                    ["workflow.schedule.scope_id"] = "caller-extension",
                    ["workflow.schedule.source_actor_id"] = "caller-extension",
                },
                ScopeId: " ",
                TenantId: "tenant-1",
                Auth: CreateDefaultAuth()));

        receipt.Should().BeEquivalentTo(new
        {
            ScheduleId = "route-schedule",
            ScheduleActorId = "actor:route-schedule",
            Accepted = true,
            AckStage = "accepted",
        });
        receipt.CommandId.Should().NotBeNullOrWhiteSpace();
        receipt.CorrelationId.Should().NotBeNullOrWhiteSpace();
        receipt.AckedAt.Should().NotBe(default);
        actorPort.Updated.Should().ContainSingle();
        var configuration = actorPort.Updated.Single().Configuration;
        configuration.ScheduleId.Should().Be("route-schedule");
        configuration.DisplayName.Should().BeEmpty();
        configuration.Timezone.Should().Be("UTC");
        configuration.Target.ServiceInvocation.Should().NotBeNull();
        configuration.Target.ServiceInvocation!.Identity.TenantId.Should().Be("tenant-1");
        configuration.Headers.Should().Contain("x", "y");
        configuration.Headers.Should().NotContainKey("empty");
        configuration.Headers.Should().Contain("workflow.schedule.scope_id", "caller-extension");
        configuration.Headers.Should().Contain("workflow.schedule.source_actor_id", "caller-extension");
    }

    [Fact]
    public async Task UpdateAsync_ShouldForwardScopeOwnerMutationContextToAdmission()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["route-schedule"] = CreateDetail("route-schedule");
        var admissionPort = new RecordingScheduledDispatchCredentialAdmissionPort();
        var service = CreateService(actorPort, queryPort, admissionPort: admissionPort);

        await service.UpdateAsync(
            " route-schedule ",
            CreateScopeOwnerWorkflowConfiguration("body-schedule"));

        AssertScopeOwnerAdmissionRequest(admissionPort.Requests.Should().ContainSingle().Which);
        actorPort.Updated.Should().ContainSingle()
            .Which.Configuration.ScheduleId.Should().Be("route-schedule");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not cron")]
    public void ScheduledDispatchCalculator_Validate_ShouldReturnError_ForInvalidCron(string cronExpression)
    {
        var result = ScheduledDispatchCalculator.Validate(
            cronExpression,
            "UTC",
            new DateTimeOffset(2026, 5, 29, 8, 30, 0, TimeSpan.Zero));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ScheduledDispatchCalculator_TryResolveTimeZone_ShouldRejectInvalidTimezone()
    {
        var resolved = ScheduledDispatchCalculator.TryResolveTimeZone(
            "Invalid/Zone",
            out var timeZone,
            out var error);

        resolved.Should().BeFalse();
        timeZone.Should().Be(TimeZoneInfo.Utc);
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ScheduledDispatchCalculator_GetNextOccurrences_ShouldClampCountAndUseDefaultTimezone()
    {
        var occurrences = ScheduledDispatchCalculator.GetNextOccurrences(
            "*/1 * * * *",
            " ",
            new DateTimeOffset(2026, 5, 29, 8, 30, 0, TimeSpan.Zero),
            150);

        occurrences.Should().HaveCount(100);
        occurrences.First().Should().Be(
            new DateTimeOffset(2026, 5, 29, 8, 31, 0, TimeSpan.Zero));
        occurrences.Should().BeInAscendingOrder();
    }

    [Fact]
    public void ScheduledDispatchCalculator_ComputeDueTime_ShouldFloorPastOrCurrentValues()
    {
        var now = new DateTimeOffset(2026, 5, 29, 8, 30, 0, TimeSpan.Zero);

        ScheduledDispatchCalculator.ComputeDueTime(now, now)
            .Should().Be(TimeSpan.FromSeconds(1));
        ScheduledDispatchCalculator.ComputeDueTime(now.AddSeconds(-5), now)
            .Should().Be(TimeSpan.FromSeconds(1));
        ScheduledDispatchCalculator.ComputeDueTime(now.AddMinutes(3), now)
            .Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void ScheduledDispatchCalculator_BuildIdempotencyKey_ShouldUseUtcRoundTripInstant()
    {
        var scheduledFireAt = new DateTimeOffset(
            2026,
            5,
            29,
            17,
            30,
            0,
            TimeSpan.FromHours(8));

        var key = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);

        key.Should().Be("schedule:schedule-1:fire:2026-05-29T09:30:00.0000000+00:00");
    }

    [Fact]
    public void ScheduledDispatchCalculator_ShouldAdvanceCursorExclusivelyAndThrowForInvalidInputs()
    {
        var boundary = new DateTimeOffset(2026, 5, 29, 8, 30, 0, TimeSpan.Zero);

        var occurrences = ScheduledDispatchCalculator.GetNextOccurrences(
            "*/30 * * * *",
            "UTC",
            boundary,
            -3);

        occurrences.Should().ContainSingle()
            .Which.Should().Be(new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero));
        occurrences.Should().NotContain(boundary);

        var invalidTimezone = () => ScheduledDispatchCalculator.GetNextOccurrences(
            "* * * * *",
            "Invalid/Zone",
            boundary,
            1);
        invalidTimezone.Should().Throw<ArgumentException>()
            .WithParameterName("timeZoneId");

        var invalidCron = () => ScheduledDispatchCalculator.GetNextOccurrences(
            "not cron",
            "UTC",
            boundary,
            1);
        invalidCron.Should().Throw<ArgumentException>()
            .WithParameterName("cronExpression");

        ScheduledDispatchValidationResult.Failed(" ")
            .Error.Should().Be("Schedule is invalid.");
    }

    [Fact]
    public async Task PreviewAsync_ShouldNormalizeBlankTimezoneAndClampLowCount()
    {
        var service = CreateService();

        var preview = await service.PreviewAsync(
            " */30 * * * * ",
            " ",
            0,
            new DateTimeOffset(2026, 5, 29, 8, 31, 0, TimeSpan.Zero));

        preview.CronExpression.Should().Be("*/30 * * * *");
        preview.Timezone.Should().Be("UTC");
        preview.NextFireTimes.Should().ContainSingle()
            .Which.Should().Be(new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task GetAsync_ShouldNormalizeScheduleIdBeforeQuerying()
    {
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1");
        var service = CreateService(new FakeWorkflowScheduleActorPort(), queryPort);

        var detail = await service.GetAsync(" schedule-1 ");

        detail.Should().NotBeNull();
        detail!.Schedule.Prompt.Should().Be("saved prompt");
        queryPort.GetScheduleIds.Should().Equal("schedule-1");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNullForNonWorkflowScheduledDispatch()
    {
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["generic"] = CreateDetail("generic") with
        {
            Schedule = CreateDetail("generic").Schedule with
            {
                TargetKind = ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceEndpointId = "chat",
                ServiceId = "generic-chat",
                ScheduleKind = ScheduledDispatchScheduleKind.Generic,
            },
        };
        var service = CreateService(new FakeWorkflowScheduleActorPort(), queryPort);

        var detail = await service.GetAsync("generic");

        detail.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldRequestWorkflowFilteredReadModelPage()
    {
        var queryPort = new FakeWorkflowScheduleQueryPort();
        var fireAt = new DateTimeOffset(2026, 7, 14, 1, 30, 0, TimeSpan.Zero);
        queryPort.ListResult = new ScheduledDispatchListResult(
            [CreateDetail(
                "workflow-1",
                cronExpression: string.Empty,
                scheduleMode: ScheduledDispatchScheduleMode.OneShotAtUtc,
                oneShotFireAt: fireAt,
                completed: true).Schedule],
            "next-workflow",
            1);
        var service = CreateService(new FakeWorkflowScheduleActorPort(), queryPort);

        var result = await service.ListAsync(25, "cursor", includeTotalCount: true);

        var summary = result.Items.Should().ContainSingle().Subject;
        summary.ScheduleId.Should().Be("workflow-1");
        summary.Prompt.Should().Be("saved prompt");
        summary.ScheduleMode.Should().Be(WorkflowScheduleMode.OneShotAtUtc);
        summary.OneShotFireAt.Should().Be(fireAt);
        summary.Completed.Should().BeTrue();
        result.NextCursor.Should().Be("next-workflow");
        result.TotalCount.Should().Be(1);
        queryPort.LastQuery.Should().Be(new ScheduledDispatchListQuery(
            25,
            "cursor",
            true,
            ScheduleKind: ScheduledDispatchScheduleKind.Workflow,
            TargetKind: ScheduledDispatchTargetKind.ServiceInvocation,
            ExcludeTeamOwned: true));
    }

    [Fact]
    public async Task Mutations_ShouldRejectExistingNonWorkflowSchedulesWithoutDispatching()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["generic"] = CreateDetail("generic") with
        {
            Schedule = CreateDetail("generic").Schedule with
            {
                ScheduleKind = ScheduledDispatchScheduleKind.Generic,
            },
        };
        var service = CreateService(actorPort, queryPort);

        var update = () => service.UpdateAsync("generic", CreateConfiguration("generic"));
        var enable = () => service.EnableAsync("generic", "resume");
        var disable = () => service.DisableAsync("generic", "pause");
        var delete = () => service.DeleteAsync("generic", "retire");
        var runNow = () => service.RunNowAsync("generic");

        await update.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await enable.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await disable.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await delete.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await runNow.Should().ThrowAsync<ScheduledDispatchNotFoundException>();

        actorPort.Updated.Should().BeEmpty();
        actorPort.Enabled.Should().BeEmpty();
        actorPort.Disabled.Should().BeEmpty();
        actorPort.Deleted.Should().BeEmpty();
        actorPort.RunNowRequests.Should().BeEmpty();
        actorPort.ResolveScheduleIds.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ShouldKeepHeadersAsDispatchExtensionsOnly()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            ResolveActorId = string.Empty,
        };
        var preparation = new FakeScheduledDispatchPreparationService();
        var service = CreateService(actorPort, new FakeWorkflowScheduleQueryPort(), preparation);

        await service.CreateAsync(new WorkflowScheduleConfiguration(
            ScheduleId: "schedule-1",
            DisplayName: string.Empty,
            WorkflowName: "direct",
            Prompt: "hello",
            CronExpression: "*/15 * * * *",
            Timezone: "UTC",
            Enabled: true,
            Headers: new Dictionary<string, string>
            {
                ["caller"] = "kept",
            },
            ScopeId: "scope-1",
            Auth: CreateDefaultAuth()));

        var created = actorPort.Created.Single();
        created.Configuration.Headers.Should().Contain(new KeyValuePair<string, string>("caller", "kept"));
        created.Configuration.Headers.Should().NotContainKey("workflow.schedule.workflow_name");
        created.Configuration.Headers.Should().NotContainKey("workflow.schedule.scope_id");
        created.Configuration.Headers.Should().NotContainKey("workflow.schedule.source_actor_id");
        created.Dispatch.Descriptor.ServiceInvocation.Should().NotBeNull();
        var payload = created.Dispatch.Descriptor.ServiceInvocation!.Payload.Unpack<ChatRequestEvent>();
        payload.Metadata.Should().Contain("caller", "kept");
        payload.Metadata.Should().NotContainKey("workflow.schedule.workflow_name");
    }

    private sealed class FakeWorkflowScheduleActorPort : IScheduledDispatchActorPort
    {
        public List<string> EnsureScheduleIds { get; } = [];
        public List<string> ResolveScheduleIds { get; } = [];
        public List<(string ActorId, ScheduledDispatchConfiguration Configuration, PreparedScheduledDispatchTarget Dispatch)> Created { get; } = [];
        public List<(string ActorId, ScheduledDispatchConfiguration Configuration, PreparedScheduledDispatchTarget Dispatch)> Updated { get; } = [];
        public List<(string ActorId, ScheduledDispatchConfiguration Configuration, PreparedScheduledDispatchTarget Dispatch)> Ensured { get; } = [];
        public List<(string ActorId, string Reason)> Enabled { get; } = [];
        public List<(string ActorId, string Reason)> Disabled { get; } = [];
        public List<(string ActorId, string Reason)> Deleted { get; } = [];
        public List<(string ActorId, DateTimeOffset ScheduledFireAt)> RunNowRequests { get; } = [];
        public string? ResolveActorId { get; set; }
        public Func<string, EventEnvelope, DispatchAdmission> AdmissionFactory { get; set; } =
            DispatchAdmissionFactory.Create;

        public Task<string> EnsureScheduleActorAsync(string scheduleId, CancellationToken ct = default)
        {
            EnsureScheduleIds.Add(scheduleId);
            return Task.FromResult($"actor:{scheduleId}");
        }

        public Task<string?> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct = default)
        {
            ResolveScheduleIds.Add(scheduleId);
            return Task.FromResult<string?>(ResolveActorId ?? $"actor:{scheduleId}");
        }

        public Task<DispatchAdmission> DispatchCreateAsync(
            string actorId,
            ScheduledDispatchConfiguration configuration,
            PreparedScheduledDispatchTarget dispatch,
            CancellationToken ct = default)
        {
            Created.Add((actorId, configuration, dispatch));
            return Task.FromResult(AdmissionFactory(actorId, dispatch.TriggerEnvelope));
        }

        public Task<DispatchAdmission> DispatchUpdateAsync(
            string actorId,
            ScheduledDispatchConfiguration configuration,
            PreparedScheduledDispatchTarget dispatch,
            CancellationToken ct = default)
        {
            Updated.Add((actorId, configuration, dispatch));
            return Task.FromResult(AdmissionFactory(actorId, dispatch.TriggerEnvelope));
        }

        public Task<DispatchAdmission> DispatchEnsureAsync(
            string actorId,
            ScheduledDispatchConfiguration configuration,
            PreparedScheduledDispatchTarget dispatch,
            CancellationToken ct = default)
        {
            Ensured.Add((actorId, configuration, dispatch));
            return Task.FromResult(AdmissionFactory(actorId, dispatch.TriggerEnvelope));
        }

        public Task<DispatchAdmission> DispatchEnableAsync(
            string actorId,
            string reason,
            CancellationToken ct = default)
        {
            Enabled.Add((actorId, reason));
            return Task.FromResult(AdmissionFactory(actorId, CreateAdmissionEnvelope()));
        }

        public Task<DispatchAdmission> DispatchDisableAsync(
            string actorId,
            string reason,
            CancellationToken ct = default)
        {
            Disabled.Add((actorId, reason));
            return Task.FromResult(AdmissionFactory(actorId, CreateAdmissionEnvelope()));
        }

        public Task<DispatchAdmission> DispatchDeleteAsync(
            string actorId,
            string reason,
            CancellationToken ct = default)
        {
            Deleted.Add((actorId, reason));
            return Task.FromResult(AdmissionFactory(actorId, CreateAdmissionEnvelope()));
        }

        public Task<DispatchAdmission> DispatchRunNowAsync(
            string actorId,
            DateTimeOffset scheduledFireAt,
            CancellationToken ct = default)
        {
            RunNowRequests.Add((actorId, scheduledFireAt));
            return Task.FromResult(AdmissionFactory(actorId, CreateAdmissionEnvelope()));
        }

        private static EventEnvelope CreateAdmissionEnvelope() =>
            new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = Guid.NewGuid().ToString("N"),
                },
            };
    }

    private sealed class FakeScheduledDispatchPreparationService : IScheduledDispatchTargetPreparationService
    {
        public List<ScheduledDispatchConfiguration> Configurations { get; } = [];

        public Task<PreparedScheduledDispatchTarget> PrepareAsync(
            ScheduledDispatchConfiguration configuration,
            string commandId,
            string correlationId,
            CancellationToken ct = default)
        {
            Configurations.Add(configuration);
            var targetActorId = $"target:{configuration.ScheduleId}";
            return Task.FromResult(new PreparedScheduledDispatchTarget(
                targetActorId,
                new EventEnvelope
                {
                    Id = commandId,
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Payload = Any.Pack(new Empty()),
                    Route = EnvelopeRouteSemantics.CreateDirect("test", targetActorId),
                    Propagation = new EnvelopePropagation
                    {
                        CorrelationId = correlationId,
                    },
                },
                Any.Pack(new Empty()).TypeUrl,
                configuration.Target));
        }
    }

    private static WorkflowScheduleApplicationService CreateService(
        FakeWorkflowScheduleActorPort? actorPort = null,
        FakeWorkflowScheduleQueryPort? queryPort = null,
        FakeScheduledDispatchPreparationService? preparation = null,
        IScheduledDispatchCredentialAdmissionPort? admissionPort = null) =>
        new(new ScheduledDispatchApplicationService(
            actorPort ?? new FakeWorkflowScheduleActorPort(),
            queryPort ?? new FakeWorkflowScheduleQueryPort(),
            preparation ?? new FakeScheduledDispatchPreparationService(),
            admissionPort ?? new NoopScheduledDispatchCredentialAdmissionPort()));

    private sealed class FakeWorkflowScheduleQueryPort : IScheduledDispatchQueryPort
    {
        public Dictionary<string, ScheduledDispatchDetail> Details { get; } = new(StringComparer.Ordinal);
        public List<string> GetScheduleIds { get; } = [];
        public int? LastTake { get; private set; }
        public string? LastCursor { get; private set; }
        public bool? LastIncludeTotalCount { get; private set; }
        public ScheduledDispatchListQuery? LastQuery { get; private set; }
        public ScheduledDispatchListResult ListResult { get; set; } = new([], null, null);

        public Task<ScheduledDispatchDetail?> GetAsync(string scheduleId, CancellationToken ct = default)
        {
            GetScheduleIds.Add(scheduleId);
            return Task.FromResult(Details.GetValueOrDefault(scheduleId));
        }

        public Task<ScheduledDispatchListResult> ListAsync(
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default) =>
            ListAsync(new ScheduledDispatchListQuery(take, cursor, includeTotalCount), ct);

        public Task<ScheduledDispatchListResult> ListAsync(
            ScheduledDispatchListQuery query,
            CancellationToken ct = default)
        {
            LastTake = query.Take;
            LastCursor = query.Cursor;
            LastIncludeTotalCount = query.IncludeTotalCount;
            LastQuery = query;
            return Task.FromResult(ListResult);
        }
    }

    private sealed class RecordingScheduledDispatchCredentialAdmissionPort : IScheduledDispatchCredentialAdmissionPort
    {
        public List<ScheduledDispatchCredentialAdmissionRequest> Requests { get; } = [];

        public Task<ScheduledDispatchCredentialAdmissionResult> AdmitAsync(
            ScheduledDispatchCredentialAdmissionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(ScheduledDispatchCredentialAdmissionResult.Allowed());
        }
    }

    private sealed class FakeWorkflowRunActorResolver : IWorkflowRunActorResolver
    {
        public List<WorkflowChatRunRequest> Requests { get; } = [];

        public WorkflowActorResolutionResult Result { get; set; } =
            new(
                new WorkflowRunCreationReceipt("run-actor-1", "definition-actor-1", []),
                "daily-workflow",
                WorkflowChatRunStartError.None);

        public Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
            WorkflowChatRunRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingWorkflowChatEnvelopeFactory : ICommandEnvelopeFactory<WorkflowChatRunRequest>
    {
        public List<WorkflowChatRunRequest> Commands { get; } = [];
        public List<CommandContext> Contexts { get; } = [];

        public EventEnvelope CreateEnvelope(WorkflowChatRunRequest command, CommandContext context)
        {
            Commands.Add(command);
            Contexts.Add(context);

            var chatRequest = new ChatRequestEvent
            {
                Prompt = command.Prompt,
                SessionId = command.SessionId ?? context.CorrelationId,
                ScopeId = command.ScopeId ?? string.Empty,
            };
            foreach (var (key, value) in command.Metadata ?? new Dictionary<string, string>())
                chatRequest.Metadata[key] = value;

            return new EventEnvelope
            {
                Id = context.CommandId,
                Payload = Any.Pack(chatRequest),
                Route = EnvelopeRouteSemantics.CreateDirect("test", context.TargetId),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = context.CorrelationId,
                },
            };
        }
    }

    private static WorkflowScheduleConfiguration CreateConfiguration(string scheduleId) =>
        new(
            ScheduleId: scheduleId,
            DisplayName: string.Empty,
            WorkflowName: "direct",
            Prompt: "hello",
            CronExpression: "*/15 * * * *",
            Timezone: "UTC",
            Enabled: true,
            Headers: new Dictionary<string, string>(),
            ScopeId: "scope-1",
            Auth: CreateDefaultAuth());

    private static WorkflowScheduleAuth CreateDefaultAuth() =>
        new(new WorkflowScheduleNyxIdCredentialSource(
            new WorkflowScheduleNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
            "proxy"));

    private static WorkflowScheduleConfiguration CreateScopeOwnerWorkflowConfiguration(string scheduleId) =>
        CreateConfiguration(scheduleId) with
        {
            Auth = new WorkflowScheduleAuth(
                ScopeOwnerNyxId: new WorkflowScheduleScopeOwnerNyxIdCredentialSource(
                    " owner-proxy ",
                    new WorkflowScheduleNyxIdSubjectRef(" nyx ", " tenant-1 ", " owner-user-1 "))),
            MutationContext = new WorkflowScheduleMutationContext(
                " scope-1 ",
                new WorkflowScheduleNyxIdSubjectRef(" nyx ", " tenant-1 ", " owner-user-1 ")),
        };

    private static void AssertScopeOwnerAdmissionRequest(ScheduledDispatchCredentialAdmissionRequest request)
    {
        request.Context.AuthenticatedScopeId.Should().Be("scope-1");
        request.Context.AuthenticatedNyxIdOwnerSubject.Should().BeEquivalentTo(
            new ScheduledServiceInvocationNyxIdSubjectRef("nyx", "tenant-1", "owner-user-1"));
        request.ScopeOwnerNyxId.Scope.Should().Be("owner-proxy");
        request.ScopeOwnerNyxId.OwnerSubject.Should().BeEquivalentTo(
            new ScheduledServiceInvocationNyxIdSubjectRef("nyx", "tenant-1", "owner-user-1"));
        request.ServiceIdentity.TenantId.Should().Be("scope-1");
        request.ServiceIdentity.ServiceId.Should().Be("direct");
    }

    private static ScheduledDispatchDetail CreateDetail(
        string scheduleId,
        string workflowName = "direct",
        string cronExpression = "*/15 * * * *",
        ScheduledDispatchScheduleMode scheduleMode = ScheduledDispatchScheduleMode.RecurringCron,
        DateTimeOffset? oneShotFireAt = null,
        bool completed = false) =>
        new(
            new ScheduledDispatchSummary(
                ScheduleId: scheduleId,
                DisplayName: string.Empty,
                TargetKind: ScheduledDispatchTargetKind.ServiceInvocation,
                TargetActorId: string.Empty,
                PayloadTypeUrl: string.Empty,
                ServiceKey: ServiceKeys.Build(
                    "scope-1",
                    ScopeServiceIdentityDefaults.ServiceAppId,
                    ScopeServiceIdentityDefaults.ServiceNamespace,
                    workflowName),
                ServiceId: workflowName,
                ServiceEndpointId: "chat",
                CronExpression: cronExpression,
                Timezone: "UTC",
                Enabled: true,
                CreatedAt: DateTimeOffset.UnixEpoch,
                UpdatedAt: DateTimeOffset.UnixEpoch,
                NextFireAt: null,
                LastFireAt: null,
                LastTargetActorId: string.Empty,
                LastCommandId: string.Empty,
                LastCorrelationId: string.Empty,
                LastError: string.Empty,
                LastErrorCode: string.Empty,
                FireCount: 0,
                FailureCount: 0,
                Headers: new Dictionary<string, string>(),
                ScheduleActorId: string.Empty,
                Prompt: "saved prompt",
                ScheduleKind: ScheduledDispatchScheduleKind.Workflow,
                CredentialRequirementTargetKind: ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
                CredentialSourceKind: ScheduledDispatchCredentialSourceKind.SenderNyxId,
                ScheduleMode: scheduleMode,
                OneShotFireAt: oneShotFireAt,
                Completed: completed),
            []);
}
