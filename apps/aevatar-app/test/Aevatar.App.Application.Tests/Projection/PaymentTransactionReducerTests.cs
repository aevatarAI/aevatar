using FluentAssertions;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Reducers;
using Aevatar.App.GAgents;
using Google.Protobuf.WellKnownTypes;
using static Aevatar.App.Application.Tests.Projection.ProjectionTestHelpers;

namespace Aevatar.App.Application.Tests.Projection;

public sealed class PaymentTransactionReducerTests
{
    private readonly DateTimeOffset _now = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreatedEvent_Populates_AllFields()
    {
        var reducer = new PaymentTransactionCreatedEventReducer();
        var model = new AppPaymentTransactionReadModel { Id = "paymenttransaction:tx1" };
        var ts = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 2, 15, 12, 0, 0, TimeSpan.Zero));
        var evt = new PaymentTransactionCreatedEvent
        {
            TransactionId = "tx1",
            UserId = "user-1",
            OriginalTransactionId = "orig-tx1",
            Amount = 999,
            Currency = "USD",
            AmountUsd = 999,
            BillingType = "subscription",
            ProductId = "pro_monthly",
            Store = "APP_STORE",
            CreatedAt = ts
        };
        var envelope = PackEnvelope(evt);
        var ctx = CreateContext("paymenttransaction:tx1");

        var changed = reducer.Reduce(model, ctx, envelope, _now);

        changed.Should().BeTrue();
        model.TransactionId.Should().Be("tx1");
        model.UserId.Should().Be("user-1");
        model.OriginalTransactionId.Should().Be("orig-tx1");
        model.Amount.Should().Be(999);
        model.Currency.Should().Be("USD");
        model.AmountUsd.Should().Be(999);
        model.BillingType.Should().Be("subscription");
        model.ProductId.Should().Be("pro_monthly");
        model.Store.Should().Be("APP_STORE");
        model.CreatedAt.Should().Be(ts.ToDateTimeOffset());
    }

    [Fact]
    public void CreatedEvent_UsesNow_When_TimestampMissing()
    {
        var reducer = new PaymentTransactionCreatedEventReducer();
        var model = new AppPaymentTransactionReadModel { Id = "paymenttransaction:tx2" };
        var evt = new PaymentTransactionCreatedEvent
        {
            TransactionId = "tx2",
            UserId = "user-2"
        };

        var changed = reducer.Reduce(model, CreateContext("paymenttransaction:tx2"), PackEnvelope(evt), _now);

        changed.Should().BeTrue();
        model.CreatedAt.Should().Be(_now);
    }

    [Fact]
    public void CreatedEvent_Skips_When_AlreadyCreated()
    {
        var reducer = new PaymentTransactionCreatedEventReducer();
        var model = new AppPaymentTransactionReadModel
        {
            Id = "paymenttransaction:tx3",
            TransactionId = "tx3",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var evt = new PaymentTransactionCreatedEvent
        {
            TransactionId = "tx3-new",
            UserId = "user-3"
        };

        var changed = reducer.Reduce(model, CreateContext("paymenttransaction:tx3"), PackEnvelope(evt), _now);

        changed.Should().BeFalse();
        model.TransactionId.Should().Be("tx3");
    }

    [Fact]
    public void CreatedEvent_Ignores_WrongTypeUrl()
    {
        var reducer = new PaymentTransactionCreatedEventReducer();
        var model = new AppPaymentTransactionReadModel { Id = "paymenttransaction:tx4" };
        var wrongEvt = new PaymentTransactionRefundedEvent();

        var changed = reducer.Reduce(model, CreateContext("paymenttransaction:tx4"), PackEnvelope(wrongEvt), _now);

        changed.Should().BeFalse();
    }

    [Fact]
    public void AffiliateTrackedEvent_Populates_AffiliateFields()
    {
        var reducer = new PaymentTransactionAffiliateTrackedEventReducer();
        var model = new AppPaymentTransactionReadModel { Id = "paymenttransaction:tx5" };
        var evt = new PaymentTransactionAffiliateTrackedEvent
        {
            AffiliateTransactionId = "aff-tx-1",
            AffiliatePlatform = "tolt"
        };

        var changed = reducer.Reduce(model, CreateContext("paymenttransaction:tx5"), PackEnvelope(evt), _now);

        changed.Should().BeTrue();
        model.AffiliateTransactionId.Should().Be("aff-tx-1");
        model.AffiliatePlatform.Should().Be("tolt");
    }

    [Fact]
    public void AffiliateTrackedEvent_Skips_When_AlreadyTracked()
    {
        var reducer = new PaymentTransactionAffiliateTrackedEventReducer();
        var model = new AppPaymentTransactionReadModel
        {
            Id = "paymenttransaction:tx6",
            AffiliateTransactionId = "existing-aff"
        };
        var evt = new PaymentTransactionAffiliateTrackedEvent
        {
            AffiliateTransactionId = "new-aff",
            AffiliatePlatform = "tolt"
        };

        var changed = reducer.Reduce(model, CreateContext("paymenttransaction:tx6"), PackEnvelope(evt), _now);

        changed.Should().BeFalse();
        model.AffiliateTransactionId.Should().Be("existing-aff");
    }

    [Fact]
    public void RefundedEvent_Sets_RefundedFlag_And_Timestamp()
    {
        var reducer = new PaymentTransactionRefundedEventReducer();
        var model = new AppPaymentTransactionReadModel { Id = "paymenttransaction:tx7" };
        var ts = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        var evt = new PaymentTransactionRefundedEvent { RefundedAt = ts };

        var changed = reducer.Reduce(model, CreateContext("paymenttransaction:tx7"), PackEnvelope(evt), _now);

        changed.Should().BeTrue();
        model.Refunded.Should().BeTrue();
        model.RefundedAt.Should().Be(ts.ToDateTimeOffset());
    }

    [Fact]
    public void RefundedEvent_UsesNow_When_TimestampMissing()
    {
        var reducer = new PaymentTransactionRefundedEventReducer();
        var model = new AppPaymentTransactionReadModel { Id = "paymenttransaction:tx8" };
        var evt = new PaymentTransactionRefundedEvent();

        var changed = reducer.Reduce(model, CreateContext("paymenttransaction:tx8"), PackEnvelope(evt), _now);

        changed.Should().BeTrue();
        model.RefundedAt.Should().Be(_now);
    }

    [Fact]
    public void RefundedEvent_Skips_When_AlreadyRefunded()
    {
        var reducer = new PaymentTransactionRefundedEventReducer();
        var model = new AppPaymentTransactionReadModel
        {
            Id = "paymenttransaction:tx9",
            Refunded = true,
            RefundedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var evt = new PaymentTransactionRefundedEvent();

        var changed = reducer.Reduce(model, CreateContext("paymenttransaction:tx9"), PackEnvelope(evt), _now);

        changed.Should().BeFalse();
    }

    [Fact]
    public void EventTypeUrl_Matches_Packed_TypeUrl()
    {
        new PaymentTransactionCreatedEventReducer().EventTypeUrl
            .Should().Contain("PaymentTransactionCreatedEvent");

        new PaymentTransactionAffiliateTrackedEventReducer().EventTypeUrl
            .Should().Contain("PaymentTransactionAffiliateTrackedEvent");

        new PaymentTransactionRefundedEventReducer().EventTypeUrl
            .Should().Contain("PaymentTransactionRefundedEvent");
    }
}
