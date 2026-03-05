using FluentAssertions;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Reducers;
using Aevatar.App.GAgents;
using static Aevatar.App.Application.Tests.Projection.ProjectionTestHelpers;

namespace Aevatar.App.Application.Tests.Projection;

public sealed class AuthLookupReducerTests
{
    private readonly DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SetEvent_Populates_LookupKey_And_UserId()
    {
        var reducer = new AuthLookupSetEventReducer();
        var model = new AppAuthLookupReadModel { Id = "authlookup:test" };
        var evt = new AuthLookupSetEvent { LookupKey = "firebase:uid1", UserId = "user-42" };
        var envelope = PackEnvelope(evt);
        var ctx = CreateContext("authlookup:test");

        var changed = reducer.Reduce(model, ctx, envelope, _now);

        changed.Should().BeTrue();
        model.LookupKey.Should().Be("firebase:uid1");
        model.UserId.Should().Be("user-42");
    }

    [Fact]
    public void SetEvent_Ignores_WrongTypeUrl()
    {
        var reducer = new AuthLookupSetEventReducer();
        var model = new AppAuthLookupReadModel { Id = "authlookup:test", UserId = "original" };
        var wrongEvt = new AuthLookupClearedEvent();
        var envelope = PackEnvelope(wrongEvt);
        var ctx = CreateContext("authlookup:test");

        var changed = reducer.Reduce(model, ctx, envelope, _now);

        changed.Should().BeFalse();
        model.UserId.Should().Be("original");
    }

    [Fact]
    public void ClearedEvent_Clears_UserId()
    {
        var reducer = new AuthLookupClearedEventReducer();
        var model = new AppAuthLookupReadModel { Id = "authlookup:test", UserId = "user-42", LookupKey = "k" };
        var evt = new AuthLookupClearedEvent();
        var envelope = PackEnvelope(evt);
        var ctx = CreateContext("authlookup:test");

        var changed = reducer.Reduce(model, ctx, envelope, _now);

        changed.Should().BeTrue();
        model.UserId.Should().BeEmpty();
        model.LookupKey.Should().Be("k");
    }

    [Fact]
    public void EventTypeUrl_Matches_Packed_TypeUrl()
    {
        var setReducer = new AuthLookupSetEventReducer();
        var clearedReducer = new AuthLookupClearedEventReducer();

        setReducer.EventTypeUrl.Should().Contain("AuthLookupSetEvent");
        clearedReducer.EventTypeUrl.Should().Contain("AuthLookupClearedEvent");
    }
}
