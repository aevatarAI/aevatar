using FluentAssertions;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Reducers;
using Aevatar.App.GAgents;
using Google.Protobuf.WellKnownTypes;
using static Aevatar.App.Application.Tests.Projection.ProjectionTestHelpers;

namespace Aevatar.App.Application.Tests.Projection;

public sealed class UserAffiliateReducerTests
{
    private readonly DateTimeOffset _now = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreatedEvent_Populates_AllFields()
    {
        var reducer = new UserAffiliateCreatedEventReducer();
        var model = new AppUserAffiliateReadModel { Id = "useraffiliate:user1" };
        var ts = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero));
        var evt = new UserAffiliateCreatedEvent
        {
            UserId = "user1",
            CustomerId = "cust-abc",
            Platform = "tolt",
            CreatedAt = ts
        };

        var changed = reducer.Reduce(model, CreateContext("useraffiliate:user1"), PackEnvelope(evt), _now);

        changed.Should().BeTrue();
        model.UserId.Should().Be("user1");
        model.CustomerId.Should().Be("cust-abc");
        model.Platform.Should().Be("tolt");
        model.CreatedAt.Should().Be(ts.ToDateTimeOffset());
    }

    [Fact]
    public void CreatedEvent_UsesNow_When_TimestampMissing()
    {
        var reducer = new UserAffiliateCreatedEventReducer();
        var model = new AppUserAffiliateReadModel { Id = "useraffiliate:user2" };
        var evt = new UserAffiliateCreatedEvent
        {
            UserId = "user2",
            CustomerId = "cust-def",
            Platform = "tolt"
        };

        var changed = reducer.Reduce(model, CreateContext("useraffiliate:user2"), PackEnvelope(evt), _now);

        changed.Should().BeTrue();
        model.CreatedAt.Should().Be(_now);
    }

    [Fact]
    public void CreatedEvent_Skips_When_AlreadyBound()
    {
        var reducer = new UserAffiliateCreatedEventReducer();
        var model = new AppUserAffiliateReadModel
        {
            Id = "useraffiliate:user3",
            CustomerId = "existing-cust",
            Platform = "tolt"
        };
        var evt = new UserAffiliateCreatedEvent
        {
            UserId = "user3",
            CustomerId = "new-cust",
            Platform = "tolt"
        };

        var changed = reducer.Reduce(model, CreateContext("useraffiliate:user3"), PackEnvelope(evt), _now);

        changed.Should().BeFalse();
        model.CustomerId.Should().Be("existing-cust");
    }

    [Fact]
    public void CreatedEvent_Ignores_WrongTypeUrl()
    {
        var reducer = new UserAffiliateCreatedEventReducer();
        var model = new AppUserAffiliateReadModel { Id = "useraffiliate:user4" };
        var wrongEvt = new PaymentTransactionCreatedEvent { TransactionId = "tx" };

        var changed = reducer.Reduce(model, CreateContext("useraffiliate:user4"), PackEnvelope(wrongEvt), _now);

        changed.Should().BeFalse();
    }

    [Fact]
    public void EventTypeUrl_Matches_Packed_TypeUrl()
    {
        new UserAffiliateCreatedEventReducer().EventTypeUrl
            .Should().Contain("UserAffiliateCreatedEvent");
    }
}
