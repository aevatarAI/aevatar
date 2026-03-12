// ─── EventRouter tests ───

using Aevatar.Foundation.Runtime.Routing;
using Shouldly;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Tests;

public class EventRouterTests
{
    private static EventEnvelope MakeEnvelope(TopologyAudience direction) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Route = EnvelopeRouteSemantics.CreateTopologyPublication("origin", direction),
    };

    [Fact]
    public async Task Self_OnlyHandlesSelf()
    {
        var router = new EventRouter("actor-1");
        router.AddChild("child-1");

        var handled = false;
        var sent = new List<string>();

        await router.RouteAsync(
            MakeEnvelope(TopologyAudience.Self),
            _ => { handled = true; return Task.CompletedTask; },
            (id, _) => { sent.Add(id); return Task.CompletedTask; });

        handled.ShouldBeTrue();
        sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Down_SendsToChildren()
    {
        var router = new EventRouter("parent");
        router.AddChild("child-a");
        router.AddChild("child-b");

        var sent = new List<string>();

        await router.RouteAsync(
            MakeEnvelope(TopologyAudience.Children),
            _ => Task.CompletedTask,
            (id, _) => { sent.Add(id); return Task.CompletedTask; });

        sent.ShouldContain("child-a");
        sent.ShouldContain("child-b");
    }

    [Fact]
    public async Task Up_SendsToParent()
    {
        var router = new EventRouter("child");
        router.SetParent("parent");

        var sent = new List<string>();

        await router.RouteAsync(
            MakeEnvelope(TopologyAudience.Parent),
            _ => Task.CompletedTask,
            (id, _) => { sent.Add(id); return Task.CompletedTask; });

        sent.ShouldHaveSingleItem().ShouldBe("parent");
    }

    [Fact]
    public async Task Both_SendsToParentAndChildren()
    {
        var router = new EventRouter("middle");
        router.SetParent("parent");
        router.AddChild("child-1");

        var sent = new List<string>();

        await router.RouteAsync(
            MakeEnvelope(TopologyAudience.ParentAndChildren),
            _ => Task.CompletedTask,
            (id, _) => { sent.Add(id); return Task.CompletedTask; });

        sent.ShouldContain("parent");
        sent.ShouldContain("child-1");
    }

    [Fact]
    public async Task CycleDetection_PreventsInfiniteLoop()
    {
        var router = new EventRouter("actor-a");
        router.SetParent("actor-b");

        // Simulate that actor-a has already processed this event
        var envelope = MakeEnvelope(TopologyAudience.Parent);
        envelope.EnsureRuntime().VisitedActorIds.Add("actor-a");

        var handled = false;
        await router.RouteAsync(
            envelope,
            _ => { handled = true; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask);

        handled.ShouldBeFalse(); // Cycle detection skipped
    }

    [Fact]
    public async Task Observe_DoesNotHandleSelfOrForward()
    {
        var router = new EventRouter("actor-observe");
        router.SetParent("parent");
        router.AddChild("child-1");

        var handled = false;
        var sent = new List<string>();

        await router.RouteAsync(
            new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Route = EnvelopeRouteSemantics.CreateObserverPublication("origin"),
            },
            _ => { handled = true; return Task.CompletedTask; },
            (id, _) => { sent.Add(id); return Task.CompletedTask; });

        handled.ShouldBeFalse();
        sent.ShouldBeEmpty();
    }

    [Fact]
    public void Hierarchy_AddRemoveChild()
    {
        var router = new EventRouter("p");
        router.AddChild("c1");
        router.AddChild("c2");
        router.ChildrenIds.Count.ShouldBe(2);

        router.RemoveChild("c1");
        router.ChildrenIds.ShouldHaveSingleItem().ShouldBe("c2");
    }

    [Fact]
    public void Hierarchy_SetClearParent()
    {
        var router = new EventRouter("c");
        router.ParentId.ShouldBeNull();

        router.SetParent("p");
        router.ParentId.ShouldBe("p");

        router.ClearParent();
        router.ParentId.ShouldBeNull();
    }
}
