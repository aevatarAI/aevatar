using FluentAssertions;
using Aevatar.App.Application.Projection;
using Aevatar.App.Application.Projection.Projectors;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Reducers;
using Aevatar.App.Application.Projection.Stores;
using Aevatar.App.GAgents;
using Aevatar.CQRS.Projection.Core.Abstractions;
using static Aevatar.App.Application.Tests.Projection.ProjectionTestHelpers;

namespace Aevatar.App.Application.Tests.Projection;

public sealed class AppProjectorBaseTests
{
    private readonly AppInMemoryDocumentStore<AppAuthLookupReadModel, string> _store = new(m => m.Id);

    private AppAuthLookupProjector CreateProjector(
        params IProjectionEventReducer<AppAuthLookupReadModel, AppProjectionContext>[] reducers) =>
        new(_store, reducers);

    [Fact]
    public async Task InitializeAsync_CreatesReadModel_When_PrefixMatches()
    {
        var projector = CreateProjector();
        var ctx = CreateContext("authlookup:user1");

        await projector.InitializeAsync(ctx);

        var model = await _store.GetAsync("authlookup:user1");
        model.Should().NotBeNull();
        model!.Id.Should().Be("authlookup:user1");
    }

    [Fact]
    public async Task InitializeAsync_Skips_When_PrefixDoesNotMatch()
    {
        var projector = CreateProjector();
        var ctx = CreateContext("useraccount:user1");

        await projector.InitializeAsync(ctx);

        var model = await _store.GetAsync("useraccount:user1");
        model.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_AppliesReducer_When_PrefixAndTypeMatch()
    {
        var reducer = new AuthLookupSetEventReducer();
        var projector = CreateProjector(reducer);
        var ctx = CreateContext("authlookup:user1");

        await projector.InitializeAsync(ctx);

        var evt = new AuthLookupSetEvent { LookupKey = "fb:uid1", UserId = "u1" };
        var envelope = PackEnvelope(evt);

        await projector.ProjectAsync(ctx, envelope);

        var model = await _store.GetAsync("authlookup:user1");
        model.Should().NotBeNull();
        model!.LookupKey.Should().Be("fb:uid1");
        model.UserId.Should().Be("u1");
    }

    [Fact]
    public async Task ProjectAsync_Skips_When_PrefixDoesNotMatch()
    {
        var reducer = new AuthLookupSetEventReducer();
        var projector = CreateProjector(reducer);
        var ctx = CreateContext("useraccount:user1");
        var evt = new AuthLookupSetEvent { LookupKey = "fb:uid1", UserId = "u1" };

        await projector.ProjectAsync(ctx, PackEnvelope(evt));

        var model = await _store.GetAsync("useraccount:user1");
        model.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_Skips_When_NoMatchingReducer()
    {
        var projector = CreateProjector();
        var ctx = CreateContext("authlookup:user1");
        await projector.InitializeAsync(ctx);

        var evt = new AuthLookupSetEvent { LookupKey = "fb:uid1", UserId = "u1" };
        await projector.ProjectAsync(ctx, PackEnvelope(evt));

        var model = await _store.GetAsync("authlookup:user1");
        model.Should().NotBeNull();
        model!.LookupKey.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_Deduplicates_By_EventId()
    {
        var reducer = new AuthLookupSetEventReducer();
        var projector = CreateProjector(reducer);
        var ctx = CreateContext("authlookup:user1");
        await projector.InitializeAsync(ctx);

        var evt1 = new AuthLookupSetEvent { LookupKey = "first", UserId = "u1" };
        var envelope = PackEnvelope(evt1, eventId: "same-id");
        await projector.ProjectAsync(ctx, envelope);

        var evt2 = new AuthLookupSetEvent { LookupKey = "second", UserId = "u2" };
        var envelope2 = PackEnvelope(evt2, eventId: "same-id");
        await projector.ProjectAsync(ctx, envelope2);

        var model = await _store.GetAsync("authlookup:user1");
        model!.LookupKey.Should().Be("first");
        model.UserId.Should().Be("u1");
    }

    [Fact]
    public async Task ProjectAsync_Allows_Different_EventIds()
    {
        var reducer = new AuthLookupSetEventReducer();
        var projector = CreateProjector(reducer);
        var ctx = CreateContext("authlookup:user1");
        await projector.InitializeAsync(ctx);

        var evt1 = new AuthLookupSetEvent { LookupKey = "first", UserId = "u1" };
        await projector.ProjectAsync(ctx, PackEnvelope(evt1, eventId: "id-1"));

        var evt2 = new AuthLookupSetEvent { LookupKey = "second", UserId = "u2" };
        await projector.ProjectAsync(ctx, PackEnvelope(evt2, eventId: "id-2"));

        var model = await _store.GetAsync("authlookup:user1");
        model!.LookupKey.Should().Be("second");
        model.UserId.Should().Be("u2");
    }

    [Fact]
    public async Task ProjectAsync_Chains_Multiple_Reducers()
    {
        var setReducer = new AuthLookupSetEventReducer();
        var clearReducer = new AuthLookupClearedEventReducer();
        var projector = CreateProjector(setReducer, clearReducer);
        var ctx = CreateContext("authlookup:user1");
        await projector.InitializeAsync(ctx);

        var setEvt = new AuthLookupSetEvent { LookupKey = "fb:uid1", UserId = "u1" };
        await projector.ProjectAsync(ctx, PackEnvelope(setEvt));

        var model = await _store.GetAsync("authlookup:user1");
        model!.UserId.Should().Be("u1");

        var clearEvt = new AuthLookupClearedEvent();
        await projector.ProjectAsync(ctx, PackEnvelope(clearEvt));

        model = await _store.GetAsync("authlookup:user1");
        model!.UserId.Should().BeEmpty();
        model.LookupKey.Should().Be("fb:uid1");
    }

    [Fact]
    public async Task CompleteAsync_Returns_CompletedTask()
    {
        var projector = CreateProjector();
        var ctx = CreateContext("authlookup:user1");

        var task = projector.CompleteAsync(ctx, null);

        task.IsCompletedSuccessfully.Should().BeTrue();
    }
}
