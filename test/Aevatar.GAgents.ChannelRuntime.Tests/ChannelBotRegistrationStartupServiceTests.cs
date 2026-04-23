using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

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
        var actor = Substitute.For<IActor>();
        actor.Id.Returns(ChannelBotRegistrationGAgent.WellKnownId);
        actor.HandleEventAsync(Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(actor));

        var projectionPort = new ChannelBotRegistrationProjectionPort(activationService);
        var startupService = new ChannelBotRegistrationStartupService(
            projectionPort,
            actorRuntime,
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
