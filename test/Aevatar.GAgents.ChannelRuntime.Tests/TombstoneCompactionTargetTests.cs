using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Smoke tests for the three <see cref="ITombstoneCompactionTarget"/>
/// implementations. The behaviour they share — Get-or-Create lifecycle and
/// versioned command construction — is also exercised end-to-end through
/// <see cref="ChannelRuntimeTombstoneCompactorTests"/>; these focused tests
/// keep direct coverage on the small leaf classes in the patch.
/// </summary>
public sealed class TombstoneCompactionTargetTests
{
    [Fact]
    public async Task UserAgentCatalog_EnsureActorAsync_CreatesActor_WhenMissing()
    {
        var target = new UserAgentCatalogTombstoneCompactionTarget();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(target.ActorId).Returns(Task.FromResult<IActor?>(null));
        runtime.CreateAsync<UserAgentCatalogGAgent>(target.ActorId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IActor>()));

        await target.EnsureActorAsync(runtime, CancellationToken.None);

        await runtime.Received(1).GetAsync(target.ActorId);
        await runtime.Received(1).CreateAsync<UserAgentCatalogGAgent>(target.ActorId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UserAgentCatalog_EnsureActorAsync_DoesNotCreate_WhenActorExists()
    {
        var target = new UserAgentCatalogTombstoneCompactionTarget();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(target.ActorId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        await target.EnsureActorAsync(runtime, CancellationToken.None);

        await runtime.DidNotReceive().CreateAsync<UserAgentCatalogGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UserAgentCatalog_EnsureActorAsync_NullRuntime_Throws()
    {
        var target = new UserAgentCatalogTombstoneCompactionTarget();
        var act = () => target.EnsureActorAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void UserAgentCatalog_CreateCommand_CarriesSafeStateVersion()
    {
        var target = new UserAgentCatalogTombstoneCompactionTarget();
        var command = target.CreateCommand(42);

        command.Should().BeOfType<UserAgentCatalogCompactTombstonesCommand>()
            .Which.SafeStateVersion.Should().Be(42);
    }

    [Fact]
    public void UserAgentCatalog_Properties_DescribeTarget()
    {
        var target = new UserAgentCatalogTombstoneCompactionTarget();
        target.ActorId.Should().Be(UserAgentCatalogGAgent.WellKnownId);
        target.ProjectionKind.Should().Be(UserAgentCatalogProjectionPort.ProjectionKind);
        target.TargetName.Should().Be("user agent catalog");
    }

    [Fact]
    public async Task ChannelBotRegistration_EnsureActorAsync_CreatesActor_WhenMissing()
    {
        ITombstoneCompactionTarget target = ResolveChannelBotTarget();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(target.ActorId).Returns(Task.FromResult<IActor?>(null));
        runtime.CreateAsync<ChannelBotRegistrationGAgent>(target.ActorId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IActor>()));

        await target.EnsureActorAsync(runtime, CancellationToken.None);

        await runtime.Received(1).CreateAsync<ChannelBotRegistrationGAgent>(target.ActorId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ChannelBotRegistration_CreateCommand_CarriesSafeStateVersion()
    {
        ITombstoneCompactionTarget target = ResolveChannelBotTarget();
        var command = target.CreateCommand(99);
        command.Should().BeOfType<ChannelBotCompactTombstonesCommand>()
            .Which.SafeStateVersion.Should().Be(99);
    }

    [Fact]
    public async Task Device_EnsureActorAsync_CreatesActor_WhenMissing()
    {
        ITombstoneCompactionTarget target = ResolveDeviceTarget();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(target.ActorId).Returns(Task.FromResult<IActor?>(null));
        runtime.CreateAsync<DeviceRegistrationGAgent>(target.ActorId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IActor>()));

        await target.EnsureActorAsync(runtime, CancellationToken.None);

        await runtime.Received(1).CreateAsync<DeviceRegistrationGAgent>(target.ActorId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Device_CreateCommand_CarriesSafeStateVersion()
    {
        ITombstoneCompactionTarget target = ResolveDeviceTarget();
        var command = target.CreateCommand(77);
        command.Should().BeOfType<DeviceCompactTombstonesCommand>()
            .Which.SafeStateVersion.Should().Be(77);
    }

    private static ITombstoneCompactionTarget ResolveChannelBotTarget()
    {
        // Internal type — instantiate via reflection, kept stable for the patch's smoke coverage.
        var t = typeof(ChannelBotRegistrationGAgent).Assembly
            .GetType("Aevatar.GAgents.Channel.Runtime.ChannelBotRegistrationTombstoneCompactionTarget", throwOnError: true)!;
        return (ITombstoneCompactionTarget)Activator.CreateInstance(t)!;
    }

    private static ITombstoneCompactionTarget ResolveDeviceTarget()
    {
        var t = typeof(DeviceRegistrationGAgent).Assembly
            .GetType("Aevatar.GAgents.Device.DeviceTombstoneCompactionTarget", throwOnError: true)!;
        return (ITombstoneCompactionTarget)Activator.CreateInstance(t)!;
    }
}
