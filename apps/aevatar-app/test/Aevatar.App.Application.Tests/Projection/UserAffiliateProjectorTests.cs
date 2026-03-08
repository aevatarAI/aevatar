using FluentAssertions;
using Aevatar.App.Application.Projection;
using Aevatar.App.Application.Projection.Projectors;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Reducers;
using Aevatar.App.Application.Projection.Stores;
using Aevatar.App.GAgents;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Google.Protobuf.WellKnownTypes;
using static Aevatar.App.Application.Tests.Projection.ProjectionTestHelpers;

namespace Aevatar.App.Application.Tests.Projection;

public sealed class UserAffiliateProjectorTests
{
    private readonly AppInMemoryDocumentStore<AppUserAffiliateReadModel, string> _store = new(m => m.Id);

    private AppUserAffiliateProjector CreateProjector(
        params IProjectionEventReducer<AppUserAffiliateReadModel, AppProjectionContext>[] reducers) =>
        new(_store, reducers);

    [Fact]
    public async Task InitializeAsync_CreatesReadModel_When_PrefixMatches()
    {
        var projector = CreateProjector();
        var ctx = CreateContext("useraffiliate:user1");

        await projector.InitializeAsync(ctx);

        var model = await _store.GetAsync("useraffiliate:user1");
        model.Should().NotBeNull();
        model!.Id.Should().Be("useraffiliate:user1");
    }

    [Fact]
    public async Task InitializeAsync_Skips_When_PrefixDoesNotMatch()
    {
        var projector = CreateProjector();
        var ctx = CreateContext("paymenttransaction:tx1");

        await projector.InitializeAsync(ctx);

        var model = await _store.GetAsync("paymenttransaction:tx1");
        model.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_AppliesCreatedReducer()
    {
        var reducer = new UserAffiliateCreatedEventReducer();
        var projector = CreateProjector(reducer);
        var ctx = CreateContext("useraffiliate:user1");
        await projector.InitializeAsync(ctx);

        var evt = new UserAffiliateCreatedEvent
        {
            UserId = "user1",
            CustomerId = "cust-abc",
            Platform = "tolt"
        };
        await projector.ProjectAsync(ctx, PackEnvelope(evt));

        var model = await _store.GetAsync("useraffiliate:user1");
        model.Should().NotBeNull();
        model!.UserId.Should().Be("user1");
        model.CustomerId.Should().Be("cust-abc");
        model.Platform.Should().Be("tolt");
    }

    [Fact]
    public async Task ProjectAsync_Skips_When_PrefixDoesNotMatch()
    {
        var reducer = new UserAffiliateCreatedEventReducer();
        var projector = CreateProjector(reducer);
        var ctx = CreateContext("paymenttransaction:tx1");
        var evt = new UserAffiliateCreatedEvent { UserId = "u1", CustomerId = "c1", Platform = "tolt" };

        await projector.ProjectAsync(ctx, PackEnvelope(evt));

        var model = await _store.GetAsync("paymenttransaction:tx1");
        model.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_Deduplicates_By_EventId()
    {
        var reducer = new UserAffiliateCreatedEventReducer();
        var projector = CreateProjector(reducer);
        var ctx = CreateContext("useraffiliate:user2");
        await projector.InitializeAsync(ctx);

        var evt1 = new UserAffiliateCreatedEvent { UserId = "u2", CustomerId = "first", Platform = "tolt" };
        await projector.ProjectAsync(ctx, PackEnvelope(evt1, eventId: "same-id"));

        var evt2 = new UserAffiliateCreatedEvent { UserId = "u2", CustomerId = "second", Platform = "tolt" };
        await projector.ProjectAsync(ctx, PackEnvelope(evt2, eventId: "same-id"));

        var model = await _store.GetAsync("useraffiliate:user2");
        model!.CustomerId.Should().Be("first");
    }
}
