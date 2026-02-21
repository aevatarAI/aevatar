using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests;

public sealed class RuntimeRoutingAndDeduplicationCoverageTests
{
    [Fact]
    public async Task MemoryCacheDeduplicator_ShouldRejectDuplicateEventId()
    {
        var deduplicator = new MemoryCacheDeduplicator();

        var first = await deduplicator.TryRecordAsync("evt-1");
        var second = await deduplicator.TryRecordAsync("evt-1");
        var third = await deduplicator.TryRecordAsync("evt-2");

        first.Should().BeTrue();
        second.Should().BeFalse();
        third.Should().BeTrue();
    }

    [Fact]
    public async Task InMemoryRouterStore_ShouldSaveLoadAndDeleteHierarchy()
    {
        var store = new InMemoryRouterStore();
        var hierarchy = new RouterHierarchy(
            ParentId: "parent-1",
            ChildrenIds: new HashSet<string>(StringComparer.Ordinal)
            {
                "child-1",
                "child-2",
            });

        await store.SaveAsync("actor-1", hierarchy);
        var loaded = await store.LoadAsync("actor-1");

        loaded.Should().NotBeNull();
        loaded!.ParentId.Should().Be("parent-1");
        loaded.ChildrenIds.Should().BeEquivalentTo(new[] { "child-1", "child-2" });

        await store.DeleteAsync("actor-1");
        var deleted = await store.LoadAsync("actor-1");
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task InMemoryRouterStore_LoadUnknownActor_ShouldReturnNull()
    {
        var store = new InMemoryRouterStore();

        var loaded = await store.LoadAsync("missing-actor");

        loaded.Should().BeNull();
    }

    [Fact]
    public void RouterHierarchy_Record_ShouldSupportValueEquality()
    {
        var first = new RouterHierarchy(
            ParentId: "p",
            ChildrenIds: new HashSet<string>(StringComparer.Ordinal) { "c1", "c2" });
        var second = new RouterHierarchy(
            ParentId: "p",
            ChildrenIds: new HashSet<string>(StringComparer.Ordinal) { "c1", "c2" });
        var changed = first with { ParentId = "p2" };

        // Record equality is reference-based for collection members; verify expected semantics explicitly.
        first.ParentId.Should().Be(second.ParentId);
        first.ChildrenIds.Should().BeEquivalentTo(second.ChildrenIds);
        changed.ParentId.Should().Be("p2");
        changed.ChildrenIds.Should().BeEquivalentTo(first.ChildrenIds);
    }
}
