using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Hosting.Maintenance;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Hosting.Tests;

public sealed class RetiredActorCleanupCoordinatorGAgentTests
{
    [Fact]
    public async Task TryAcquireLeaseAsync_ShouldGrantFirstAcquire()
    {
        var agent = CreateAgent();

        var lease = await agent.TryAcquireLeaseAsync(Acquire("spec-a", "owner-a", Now(), Now().AddMinutes(5)));

        lease.Should().NotBeNull();
        lease!.SpecId.Should().Be("spec-a");
        lease.Epoch.Should().Be(1);
        agent.State.Leases["spec-a"].Status.Should().Be(RetiredActorCleanupLeaseStatus.Active);
    }

    [Fact]
    public async Task TryAcquireLeaseAsync_ShouldDenyCompetingAcquire_WhenLeaseActive()
    {
        var now = Now();
        var agent = CreateAgent();
        await agent.TryAcquireLeaseAsync(Acquire("spec-a", "owner-a", now, now.AddMinutes(5)));

        var competing = await agent.TryAcquireLeaseAsync(
            Acquire("spec-a", "owner-b", now.AddSeconds(1), now.AddMinutes(6)));

        competing.Should().BeNull();
        agent.State.Leases["spec-a"].OwnerId.Should().Be("owner-a");
        agent.State.Leases["spec-a"].Epoch.Should().Be(1);
    }

    [Fact]
    public async Task TryAcquireLeaseAsync_ShouldTakeOverStaleLease_AndIncrementEpoch()
    {
        var now = Now();
        var agent = CreateAgent();
        await agent.TryAcquireLeaseAsync(Acquire("spec-a", "owner-a", now, now.AddMinutes(1)));

        var takeover = await agent.TryAcquireLeaseAsync(
            Acquire("spec-a", "owner-b", now.AddMinutes(2), now.AddMinutes(7)));

        takeover.Should().NotBeNull();
        takeover!.Epoch.Should().Be(2);
        takeover.OwnerId.Should().Be("owner-b");
        agent.State.Leases["spec-a"].Epoch.Should().Be(2);
    }

    [Fact]
    public async Task CheckLeaseAsync_ShouldReturnFalse_ForWrongTokenOrEpoch()
    {
        var now = Now();
        var agent = CreateAgent();
        var lease = await agent.TryAcquireLeaseAsync(Acquire("spec-a", "owner-a", now, now.AddMinutes(5)));

        var wrongToken = await agent.CheckLeaseAsync(new RetiredActorCleanupCheckCommand
        {
            SpecId = "spec-a",
            Epoch = lease!.Epoch,
            Token = "wrong-token",
            OwnerId = lease.OwnerId,
            CheckedAt = Timestamp.FromDateTimeOffset(now),
        });
        var wrongEpoch = await agent.CheckLeaseAsync(new RetiredActorCleanupCheckCommand
        {
            SpecId = "spec-a",
            Epoch = lease.Epoch + 1,
            Token = lease.Token,
            OwnerId = lease.OwnerId,
            CheckedAt = Timestamp.FromDateTimeOffset(now),
        });

        wrongToken.Should().BeFalse();
        wrongEpoch.Should().BeFalse();
    }

    [Fact]
    public async Task ReleaseLeaseAsync_ShouldReleaseByOwner()
    {
        var now = Now();
        var agent = CreateAgent();
        var lease = await agent.TryAcquireLeaseAsync(Acquire("spec-a", "owner-a", now, now.AddMinutes(5)));

        await agent.ReleaseLeaseAsync(new RetiredActorCleanupReleaseCommand
        {
            SpecId = "spec-a",
            Epoch = lease!.Epoch,
            Token = lease.Token,
            OwnerId = lease.OwnerId,
            ReleasedAt = Timestamp.FromDateTimeOffset(now.AddMinutes(1)),
        });

        agent.State.Leases["spec-a"].Status.Should().Be(RetiredActorCleanupLeaseStatus.Released);
        agent.State.Leases["spec-a"].ReleasedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ReleaseLeaseAsync_ShouldIgnoreStaleRelease()
    {
        var now = Now();
        var agent = CreateAgent();
        var first = await agent.TryAcquireLeaseAsync(Acquire("spec-a", "owner-a", now, now.AddMinutes(1)));
        var second = await agent.TryAcquireLeaseAsync(Acquire("spec-a", "owner-b", now.AddMinutes(2), now.AddMinutes(7)));

        await agent.ReleaseLeaseAsync(new RetiredActorCleanupReleaseCommand
        {
            SpecId = "spec-a",
            Epoch = first!.Epoch,
            Token = first.Token,
            OwnerId = first.OwnerId,
            ReleasedAt = Timestamp.FromDateTimeOffset(now.AddMinutes(3)),
        });

        agent.State.Leases["spec-a"].Epoch.Should().Be(second!.Epoch);
        agent.State.Leases["spec-a"].OwnerId.Should().Be(second.OwnerId);
        agent.State.Leases["spec-a"].Status.Should().Be(RetiredActorCleanupLeaseStatus.Active);
    }

    private static RetiredActorCleanupCoordinatorGAgent CreateAgent()
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore, InMemoryEventStore>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var agent = new RetiredActorCleanupCoordinatorGAgent
        {
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<RetiredActorCleanupCoordinatorState>>(),
            Services = services,
        };
        SetId(agent, RetiredActorCleanupCoordinatorEnvelopeFactory.CoordinatorActorId);
        return agent;
    }

    private static RetiredActorCleanupAcquireCommand Acquire(
        string specId,
        string ownerId,
        DateTimeOffset requestedAt,
        DateTimeOffset expiresAt) =>
        new()
        {
            SpecId = specId,
            OwnerId = ownerId,
            RequestedToken = Guid.NewGuid().ToString("N"),
            RequestedAt = Timestamp.FromDateTimeOffset(requestedAt),
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt),
        };

    private static DateTimeOffset Now() => new(2026, 05, 29, 12, 00, 00, TimeSpan.Zero);

    private static void SetId(RetiredActorCleanupCoordinatorGAgent agent, string id)
    {
        var field = typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetProperty(nameof(Aevatar.Foundation.Core.GAgentBase.Id))!
            .GetBackingField();
        field.SetValue(agent, id);
    }
}

internal static class ReflectionExtensions
{
    public static System.Reflection.FieldInfo GetBackingField(
        this System.Reflection.PropertyInfo property) =>
        property.DeclaringType!.GetField(
            $"<{property.Name}>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing backing field for {property.Name}.");
}
