using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceRunGAgentTests
{
    [Fact]
    public async Task HandleRegisterAsync_ShouldPersistRecord_AndDefaultStatusToAccepted()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        await actor.ActivateAsync();

        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        actor.State.Record.Should().NotBeNull();
        actor.State.Record!.RunId.Should().Be("run-1");
        actor.State.Record.Status.Should().Be(ServiceRunStatus.Accepted);
        actor.State.LastAppliedEventVersion.Should().Be(1);
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldBeIdempotent_WhenRunIdAlreadyBound()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());

        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        actor.State.LastAppliedEventVersion.Should().Be(1);
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldRejectMismatchedRunId()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        var act = () => actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-2"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*run-1*cannot register run 'run-2'*");
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldRejectScopeMismatchOnReRegister()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:tenant-1:svc-1:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        var foreign = BuildRecord("run-1");
        foreign.ScopeId = "tenant-2";
        var act = () => actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = foreign });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tenant-1*cannot re-register under scope 'tenant-2'*");
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldRejectServiceMismatchOnReRegister()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:tenant-1:svc-1:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        var foreign = BuildRecord("run-1");
        foreign.ServiceId = "svc-2";
        var act = () => actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = foreign });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*svc-1*cannot re-register under service 'svc-2'*");
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldRejectTargetMismatchOnReRegister()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:tenant-1:svc-1:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        var foreign = BuildRecord("run-1");
        foreign.TargetActorId = "different-target";
        var act = () => actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = foreign });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*target-run-1*cannot re-register against target 'different-target'*");
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldRejectMissingRequiredFields()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:bad",
            static () => new ServiceRunGAgent());

        var noRunId = () => actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = new ServiceRunRecord { ScopeId = "t", ServiceId = "s", CommandId = "c" },
        });
        await noRunId.Should().ThrowAsync<InvalidOperationException>().WithMessage("run_id*");
    }

    [Fact]
    public async Task HandleUpdateStatusAsync_ShouldAdvanceStatusAndStamp()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        await actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-1",
            Status = ServiceRunStatus.Completed,
        });

        actor.State.Record!.Status.Should().Be(ServiceRunStatus.Completed);
        actor.State.LastAppliedEventVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleUpdateStatusAsync_ShouldNoOp_WhenStatusUnchanged()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        await actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-1",
            Status = ServiceRunStatus.Accepted,
        });

        actor.State.LastAppliedEventVersion.Should().Be(1);
    }

    [Fact]
    public async Task HandleUpdateStatusAsync_ShouldRejectWhenNotRegistered()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());

        var act = () => actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-1",
            Status = ServiceRunStatus.Completed,
        });
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has no registered run*");
    }

    private static ServiceRunRecord BuildRecord(string runId) =>
        new()
        {
            ScopeId = "tenant-1",
            ServiceId = "svc-1",
            ServiceKey = "tenant-1:svc-1",
            RunId = runId,
            CommandId = $"cmd-{runId}",
            CorrelationId = $"corr-{runId}",
            EndpointId = "run",
            ImplementationKind = ServiceImplementationKind.Static,
            TargetActorId = $"target-{runId}",
            RevisionId = "r1",
            DeploymentId = "dep-1",
            Status = ServiceRunStatus.Unspecified,
            CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };
}
