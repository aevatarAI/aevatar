// ─────────────────────────────────────────────────────────────
// BDD: Event routing behavior
// Feature: EventRouter dispatches events by direction and detects cycles
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Runtime.Routing;
using Shouldly;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Tests.Bdd;

[Trait("Category", "BDD")]
[Trait("Feature", "EventRouting")]
public class EventRoutingBddTests
{
    private static EventEnvelope MakeEnvelope(BroadcastDirection direction) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Route = EnvelopeRouteSemantics.CreateBroadcast("origin", direction),
    };

    [Fact(DisplayName = "Given a parent Actor with two child Actors, when event direction is Down, event should be sent to all children")]
    public async Task Given_ParentWithChildren_When_Down_Then_AllChildrenReceive()
    {
        // Given
        var router = new EventRouter("parent");
        router.AddChild("child-a");
        router.AddChild("child-b");

        var received = new List<string>();

        // When
        await router.RouteAsync(
            MakeEnvelope(BroadcastDirection.Down),
            _ => Task.CompletedTask,
            (id, _) => { received.Add(id); return Task.CompletedTask; });

        // Then
        received.Count.ShouldBe(2);
        received.ShouldContain("child-a");
        received.ShouldContain("child-b");
    }

    [Fact(DisplayName = "Given a child Actor, when event direction is Up, event should be sent to parent")]
    public async Task Given_Child_When_Up_Then_ParentReceives()
    {
        // Given
        var router = new EventRouter("child");
        router.SetParent("parent");

        var received = new List<string>();

        // When
        await router.RouteAsync(
            MakeEnvelope(BroadcastDirection.Up),
            _ => Task.CompletedTask,
            (id, _) => { received.Add(id); return Task.CompletedTask; });

        // Then
        received.ShouldHaveSingleItem().ShouldBe("parent");
    }

    [Fact(DisplayName = "Given event direction is Self, event should not propagate to any other Actor")]
    public async Task Given_SelfDirection_Then_NoForwarding()
    {
        // Given
        var router = new EventRouter("me");
        router.SetParent("parent");
        router.AddChild("child");

        var selfHandled = false;
        var forwarded = new List<string>();

        // When
        await router.RouteAsync(
            MakeEnvelope(BroadcastDirection.Self),
            _ => { selfHandled = true; return Task.CompletedTask; },
            (id, _) => { forwarded.Add(id); return Task.CompletedTask; });

        // Then
        selfHandled.ShouldBeTrue();
        forwarded.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Given an event has already passed through current Actor, when it arrives again, it should be skipped (cycle detection)")]
    public async Task Given_AlreadyProcessed_Then_Skipped()
    {
        // Given
        var router = new EventRouter("actor-a");
        var envelope = MakeEnvelope(BroadcastDirection.Down);
        envelope.EnsureRuntime().VisitedActorIds.Add("actor-a"); // Already processed

        var handled = false;

        // When
        await router.RouteAsync(
            envelope,
            _ => { handled = true; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask);

        // Then
        handled.ShouldBeFalse();
    }

    [Fact(DisplayName = "Given direction is Both, event should be sent to both parent and children")]
    public async Task Given_BothDirection_Then_ParentAndChildrenReceive()
    {
        // Given
        var router = new EventRouter("middle");
        router.SetParent("parent");
        router.AddChild("child-1");
        router.AddChild("child-2");

        var received = new List<string>();

        // When
        await router.RouteAsync(
            MakeEnvelope(BroadcastDirection.Both),
            _ => Task.CompletedTask,
            (id, _) => { received.Add(id); return Task.CompletedTask; });

        // Then
        received.Count.ShouldBe(3);
        received.ShouldContain("parent");
        received.ShouldContain("child-1");
        received.ShouldContain("child-2");
    }
}
