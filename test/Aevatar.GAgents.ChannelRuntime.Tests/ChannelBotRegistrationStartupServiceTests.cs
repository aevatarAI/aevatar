using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelBotRegistrationStartupServiceTests
{
    [Fact]
    public async Task StartAsync_ActivatesProjection_AndDispatchesRebuildCommand()
    {
        var activationService = Substitute.For<IProjectionScopeActivationService<ChannelBotRegistrationMaterializationRuntimeLease>>();
        activationService.EnsureAsync(Arg.Any<ProjectionScopeStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChannelBotRegistrationMaterializationRuntimeLease(
                new ChannelBotRegistrationMaterializationContext
                {
                    RootActorId = ChannelBotRegistrationGAgent.WellKnownId,
                    ProjectionKind = ChannelBotRegistrationProjectionPort.ProjectionKind,
                })));

        EventEnvelope? capturedEnvelope = null;
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var projectionPort = new ChannelBotRegistrationProjectionPort(activationService);
        var startupService = new ChannelBotRegistrationStartupService(
            projectionPort,
            actorRuntime,
            (IActorDispatchPort)actorRuntime,
            NullLogger<ChannelBotRegistrationStartupService>.Instance);

        await startupService.StartAsync(CancellationToken.None);

        await activationService.Received(1).EnsureAsync(
            Arg.Is<ProjectionScopeStartRequest>(request =>
                request.RootActorId == ChannelBotRegistrationGAgent.WellKnownId &&
                request.ProjectionKind == ChannelBotRegistrationProjectionPort.ProjectionKind &&
                request.Mode == ProjectionRuntimeMode.DurableMaterialization),
            Arg.Any<CancellationToken>());
        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Unpack<ChannelBotRebuildProjectionCommand>().Reason.Should().Be("startup_projection_rebuild");
    }
}
