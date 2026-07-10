using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Orchestration;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class GAgentDraftRunObservationScopeLeasePreparationPortTests
{
    [Fact]
    public async Task PrepareAsync_ShouldStartDraftRunSessionAndTerminalMaterializationScopes()
    {
        var sessionActivation = new RecordingActivationService<GAgentDraftRunRuntimeLease>(
            request => new GAgentDraftRunRuntimeLease(new GAgentDraftRunProjectionContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
                SessionId = request.SessionId,
            }));
        var terminalActivation = new RecordingActivationService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>>(
            request => new ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>(
                request.RootActorId,
                new GAgentRunTerminalProjectionContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                    CorrelationId = request.SessionId,
                    InteractionKind = GAgentRunTerminalInteractionKind.DraftRun,
                }));
        var port = new GAgentDraftRunObservationScopeLeasePreparationPort(
            sessionActivation,
            new RecordingReleaseService<GAgentDraftRunRuntimeLease>(),
            terminalActivation,
            new RecordingReleaseService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>>());

        var activation = await port.PrepareAsync("actor-1", "cmd-1", "corr-1", CancellationToken.None);

        activation.Should().NotBeNull();
        activation!.ActorId.Should().Be("actor-1");
        activation.CommandId.Should().Be("cmd-1");
        activation.CorrelationId.Should().Be("corr-1");
        sessionActivation.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(new ProjectionScopeStartRequest
        {
            RootActorId = "actor-1",
            ProjectionKind = "service-draft-run-session",
            Mode = ProjectionRuntimeMode.SessionObservation,
            SessionId = "cmd-1",
        });
        terminalActivation.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(new ProjectionScopeStartRequest
        {
            RootActorId = "actor-1",
            ProjectionKind = "gagent-run-terminal-draft-run",
            Mode = ProjectionRuntimeMode.DurableMaterialization,
            SessionId = "corr-1",
        });
    }

    [Fact]
    public async Task PrepareAsync_ShouldCompensateSessionScope_WhenTerminalPreparationFails()
    {
        var sessionRelease = new RecordingReleaseService<GAgentDraftRunRuntimeLease>();
        var sessionActivation = new RecordingActivationService<GAgentDraftRunRuntimeLease>(
            request => new GAgentDraftRunRuntimeLease(new GAgentDraftRunProjectionContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
                SessionId = request.SessionId,
            }));
        var terminalActivation = new RecordingActivationService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>>(
            _ => throw new InvalidOperationException("terminal unavailable"));
        var port = new GAgentDraftRunObservationScopeLeasePreparationPort(
            sessionActivation,
            sessionRelease,
            terminalActivation,
            new RecordingReleaseService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>>());

        var activation = await port.PrepareAsync("actor-1", "cmd-1", "corr-1", CancellationToken.None);

        activation.Should().BeNull();
        sessionRelease.Leases.Should().ContainSingle();
        sessionRelease.Leases[0].ActorId.Should().Be("actor-1");
        sessionRelease.Leases[0].CommandId.Should().Be("cmd-1");
    }

    [Fact]
    public async Task ReleaseAsync_ShouldReconstructTerminalAndSessionLeaseContractsInDefinedOrder()
    {
        var operations = new List<string>();
        var sessionRelease = new RecordingReleaseService<GAgentDraftRunRuntimeLease>("session", operations);
        var terminalRelease = new RecordingReleaseService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>>(
            "terminal",
            operations);
        var port = new GAgentDraftRunObservationScopeLeasePreparationPort(
            new RecordingActivationService<GAgentDraftRunRuntimeLease>(
                request => new GAgentDraftRunRuntimeLease(new GAgentDraftRunProjectionContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                    SessionId = request.SessionId,
                })),
            sessionRelease,
            new RecordingActivationService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>>(
                request => new ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>(
                    request.RootActorId,
                    new GAgentRunTerminalProjectionContext
                    {
                        RootActorId = request.RootActorId,
                        ProjectionKind = request.ProjectionKind,
                        CorrelationId = request.SessionId,
                        InteractionKind = GAgentRunTerminalInteractionKind.DraftRun,
                    })),
            terminalRelease);

        await port.ReleaseAsync(
            new GAgentDraftRunObservationScopeLeasePreparation("actor-1", "cmd-1", "corr-1"),
            CancellationToken.None);

        terminalRelease.Leases.Should().ContainSingle();
        var terminalLease = terminalRelease.Leases[0];
        terminalLease.RootEntityId.Should().Be("actor-1");
        terminalLease.Context.RootActorId.Should().Be("actor-1");
        terminalLease.Context.ProjectionKind.Should().Be("gagent-run-terminal-draft-run");
        terminalLease.Context.CorrelationId.Should().Be("corr-1");
        terminalLease.Context.InteractionKind.Should().Be(GAgentRunTerminalInteractionKind.DraftRun);

        sessionRelease.Leases.Should().ContainSingle();
        var sessionLease = sessionRelease.Leases[0];
        sessionLease.ActorId.Should().Be("actor-1");
        sessionLease.Context.RootActorId.Should().Be("actor-1");
        sessionLease.Context.ProjectionKind.Should().Be("service-draft-run-session");
        sessionLease.Context.SessionId.Should().Be("cmd-1");
        sessionLease.CommandId.Should().Be("cmd-1");

        operations.Should().Equal("terminal", "session");
    }

    private sealed class RecordingActivationService<TLease>(Func<ProjectionScopeStartRequest, TLease> factory)
        : IProjectionScopeActivationService<TLease>
        where TLease : class, IProjectionRuntimeLease
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<TLease> EnsureAsync(ProjectionScopeStartRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(factory(request));
        }
    }

    private sealed class RecordingReleaseService<TLease>(
        string? operation = null,
        List<string>? operations = null) : IProjectionScopeReleaseService<TLease>
        where TLease : class, IProjectionRuntimeLease
    {
        public List<TLease> Leases { get; } = [];

        public Task ReleaseIfIdleAsync(TLease lease, CancellationToken ct = default)
        {
            if (operation != null)
                operations?.Add(operation);
            Leases.Add(lease);
            return Task.CompletedTask;
        }
    }
}
