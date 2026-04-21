using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRuntimeTombstoneCompactorTests
{
    [Fact]
    public async Task RunOnceAsync_DispatchesCompactionCommandsUsingProjectionWatermarks()
    {
        var watermarkQueryPort = Substitute.For<IProjectionScopeWatermarkQueryPort>();
        watermarkQueryPort.GetLastSuccessfulVersionAsync(
                Arg.Is<ProjectionRuntimeScopeKey>(key =>
                    key.RootActorId == ChannelBotRegistrationGAgent.WellKnownId &&
                    key.ProjectionKind == ChannelBotRegistrationProjectionPort.ProjectionKind),
                Arg.Any<CancellationToken>())
            .Returns(12L);
        watermarkQueryPort.GetLastSuccessfulVersionAsync(
                Arg.Is<ProjectionRuntimeScopeKey>(key =>
                    key.RootActorId == DeviceRegistrationGAgent.WellKnownId &&
                    key.ProjectionKind == DeviceRegistrationProjectionPort.ProjectionKind),
                Arg.Any<CancellationToken>())
            .Returns(22L);
        watermarkQueryPort.GetLastSuccessfulVersionAsync(
                Arg.Is<ProjectionRuntimeScopeKey>(key =>
                    key.RootActorId == AgentRegistryGAgent.WellKnownId &&
                    key.ProjectionKind == AgentRegistryProjectionPort.ProjectionKind),
                Arg.Any<CancellationToken>())
            .Returns(32L);

        var channelActor = Substitute.For<IActor>();
        channelActor.Id.Returns(ChannelBotRegistrationGAgent.WellKnownId);
        var deviceActor = Substitute.For<IActor>();
        deviceActor.Id.Returns(DeviceRegistrationGAgent.WellKnownId);
        var registryActor = Substitute.For<IActor>();
        registryActor.Id.Returns(AgentRegistryGAgent.WellKnownId);

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId).Returns(Task.FromResult<IActor?>(channelActor));
        actorRuntime.GetAsync(DeviceRegistrationGAgent.WellKnownId).Returns(Task.FromResult<IActor?>(deviceActor));
        actorRuntime.GetAsync(AgentRegistryGAgent.WellKnownId).Returns(Task.FromResult<IActor?>(registryActor));

        var sut = new ChannelRuntimeTombstoneCompactor(
            watermarkQueryPort,
            actorRuntime,
            NullLogger<ChannelRuntimeTombstoneCompactor>.Instance);

        await sut.RunOnceAsync();

        await channelActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(env => IsChannelBotCompaction(env, 12)),
            Arg.Any<CancellationToken>());
        await deviceActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(env => IsDeviceCompaction(env, 22)),
            Arg.Any<CancellationToken>());
        await registryActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(env => IsAgentRegistryCompaction(env, 32)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_SkipsTargetsWithoutWatermark()
    {
        var watermarkQueryPort = Substitute.For<IProjectionScopeWatermarkQueryPort>();
        watermarkQueryPort.GetLastSuccessfulVersionAsync(Arg.Any<ProjectionRuntimeScopeKey>(), Arg.Any<CancellationToken>())
            .Returns((long?)null);
        var actorRuntime = Substitute.For<IActorRuntime>();

        var sut = new ChannelRuntimeTombstoneCompactor(
            watermarkQueryPort,
            actorRuntime,
            NullLogger<ChannelRuntimeTombstoneCompactor>.Instance);

        await sut.RunOnceAsync();

        actorRuntime.ReceivedCalls().Should().BeEmpty();
    }

    private static bool IsChannelBotCompaction(EventEnvelope envelope, long safeStateVersion)
    {
        if (envelope.Payload?.Is(ChannelBotCompactTombstonesCommand.Descriptor) != true)
            return false;

        return envelope.Payload.Unpack<ChannelBotCompactTombstonesCommand>().SafeStateVersion == safeStateVersion;
    }

    private static bool IsDeviceCompaction(EventEnvelope envelope, long safeStateVersion)
    {
        if (envelope.Payload?.Is(DeviceCompactTombstonesCommand.Descriptor) != true)
            return false;

        return envelope.Payload.Unpack<DeviceCompactTombstonesCommand>().SafeStateVersion == safeStateVersion;
    }

    private static bool IsAgentRegistryCompaction(EventEnvelope envelope, long safeStateVersion)
    {
        if (envelope.Payload?.Is(AgentRegistryCompactTombstonesCommand.Descriptor) != true)
            return false;

        return envelope.Payload.Unpack<AgentRegistryCompactTombstonesCommand>().SafeStateVersion == safeStateVersion;
    }
}
