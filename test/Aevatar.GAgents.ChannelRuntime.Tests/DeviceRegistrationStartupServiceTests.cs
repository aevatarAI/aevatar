using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgents.Device;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class DeviceRegistrationStartupServiceTests
{
    [Fact]
    public async Task StartAsync_ActivatesProjection()
    {
        var activationService = Substitute.For<IProjectionScopeActivationService<DeviceRegistrationMaterializationRuntimeLease>>();
        activationService.EnsureAsync(Arg.Any<ProjectionScopeStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeviceRegistrationMaterializationRuntimeLease(
                new DeviceRegistrationMaterializationContext
                {
                    RootActorId = DeviceRegistrationGAgent.WellKnownId,
                    ProjectionKind = DeviceRegistrationProjectionBootstrapActivator.ProjectionKind,
                })));

        var projectionActivator = new DeviceRegistrationProjectionBootstrapActivator(activationService);
        var startupService = new DeviceRegistrationStartupService(
            projectionActivator,
            NullLogger<DeviceRegistrationStartupService>.Instance);

        await startupService.StartAsync(CancellationToken.None);

        await activationService.Received(1).EnsureAsync(
            Arg.Is<ProjectionScopeStartRequest>(request =>
                request.RootActorId == DeviceRegistrationGAgent.WellKnownId &&
                request.ProjectionKind == DeviceRegistrationProjectionBootstrapActivator.ProjectionKind &&
                request.Mode == ProjectionRuntimeMode.DurableMaterialization),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WhenActivationFails_DispatchesOnlyOneActivationAttempt()
    {
        var activationService = Substitute.For<IProjectionScopeActivationService<DeviceRegistrationMaterializationRuntimeLease>>();
        activationService.EnsureAsync(Arg.Any<ProjectionScopeStartRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeviceRegistrationMaterializationRuntimeLease>>(_ => throw new InvalidOperationException("boom"));

        var projectionActivator = new DeviceRegistrationProjectionBootstrapActivator(activationService);
        var startupService = new DeviceRegistrationStartupService(
            projectionActivator,
            NullLogger<DeviceRegistrationStartupService>.Instance);

        await startupService.StartAsync(CancellationToken.None);

        await activationService.Received(1).EnsureAsync(
            Arg.Is<ProjectionScopeStartRequest>(request =>
                request.RootActorId == DeviceRegistrationGAgent.WellKnownId &&
                request.ProjectionKind == DeviceRegistrationProjectionBootstrapActivator.ProjectionKind &&
                request.Mode == ProjectionRuntimeMode.DurableMaterialization),
            Arg.Any<CancellationToken>());
    }
}
