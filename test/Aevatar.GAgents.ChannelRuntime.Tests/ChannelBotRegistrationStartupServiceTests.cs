using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelBotRegistrationStartupServiceTests
{
    [Fact]
    public async Task StartAsync_ActivatesProjection_WithoutDispatchingSyntheticRebuild()
    {
        var activationService = Substitute.For<IProjectionScopeActivationService<ChannelBotRegistrationMaterializationRuntimeLease>>();
        activationService.EnsureAsync(Arg.Any<ProjectionScopeStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChannelBotRegistrationMaterializationRuntimeLease(
                new ChannelBotRegistrationMaterializationContext
                {
                    RootActorId = ChannelBotRegistrationGAgent.WellKnownId,
                    ProjectionKind = ChannelBotRegistrationProjectionBootstrapActivator.ProjectionKind,
                })));

        var projectionActivator = new ChannelBotRegistrationProjectionBootstrapActivator(activationService);
        var startupService = new ChannelBotRegistrationStartupService(
            projectionActivator,
            NullLogger<ChannelBotRegistrationStartupService>.Instance);

        await startupService.StartAsync(CancellationToken.None);

        await activationService.Received(1).EnsureAsync(
            Arg.Is<ProjectionScopeStartRequest>(request =>
                request.RootActorId == ChannelBotRegistrationGAgent.WellKnownId &&
                request.ProjectionKind == ChannelBotRegistrationProjectionBootstrapActivator.ProjectionKind &&
                request.Mode == ProjectionRuntimeMode.DurableMaterialization),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WhenActivationFails_PropagatesFailure_AndAttemptsOnce()
    {
        var activationService = Substitute.For<IProjectionScopeActivationService<ChannelBotRegistrationMaterializationRuntimeLease>>();
        activationService.EnsureAsync(Arg.Any<ProjectionScopeStartRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChannelBotRegistrationMaterializationRuntimeLease>>(_ => throw new InvalidOperationException("boom"));

        var projectionActivator = new ChannelBotRegistrationProjectionBootstrapActivator(activationService);
        var startupService = new ChannelBotRegistrationStartupService(
            projectionActivator,
            NullLogger<ChannelBotRegistrationStartupService>.Instance);

        await startupService.Invoking(service => service.StartAsync(CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("boom");

        await activationService.Received(1).EnsureAsync(
            Arg.Is<ProjectionScopeStartRequest>(request =>
                request.RootActorId == ChannelBotRegistrationGAgent.WellKnownId &&
                request.ProjectionKind == ChannelBotRegistrationProjectionBootstrapActivator.ProjectionKind &&
                request.Mode == ProjectionRuntimeMode.DurableMaterialization),
            Arg.Any<CancellationToken>());
    }
}
