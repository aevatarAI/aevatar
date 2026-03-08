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

public sealed class PaymentTransactionProjectorTests
{
    private readonly AppInMemoryDocumentStore<AppPaymentTransactionReadModel, string> _store = new(m => m.Id);

    private AppPaymentTransactionProjector CreateProjector(
        params IProjectionEventReducer<AppPaymentTransactionReadModel, AppProjectionContext>[] reducers) =>
        new(_store, reducers);

    [Fact]
    public async Task InitializeAsync_CreatesReadModel_When_PrefixMatches()
    {
        var projector = CreateProjector();
        var ctx = CreateContext("paymenttransaction:tx1");

        await projector.InitializeAsync(ctx);

        var model = await _store.GetAsync("paymenttransaction:tx1");
        model.Should().NotBeNull();
        model!.Id.Should().Be("paymenttransaction:tx1");
    }

    [Fact]
    public async Task InitializeAsync_Skips_When_PrefixDoesNotMatch()
    {
        var projector = CreateProjector();
        var ctx = CreateContext("useraffiliate:user1");

        await projector.InitializeAsync(ctx);

        var model = await _store.GetAsync("useraffiliate:user1");
        model.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_AppliesCreatedReducer()
    {
        var reducer = new PaymentTransactionCreatedEventReducer();
        var projector = CreateProjector(reducer);
        var ctx = CreateContext("paymenttransaction:tx1");
        await projector.InitializeAsync(ctx);

        var evt = new PaymentTransactionCreatedEvent
        {
            TransactionId = "tx1",
            UserId = "user-1",
            Amount = 1999,
            Currency = "USD",
            AmountUsd = 1999,
            BillingType = "subscription",
            ProductId = "pro_monthly",
            Store = "APP_STORE"
        };
        await projector.ProjectAsync(ctx, PackEnvelope(evt));

        var model = await _store.GetAsync("paymenttransaction:tx1");
        model.Should().NotBeNull();
        model!.TransactionId.Should().Be("tx1");
        model.UserId.Should().Be("user-1");
        model.Amount.Should().Be(1999);
    }

    [Fact]
    public async Task ProjectAsync_ChainsAllReducers()
    {
        var created = new PaymentTransactionCreatedEventReducer();
        var tracked = new PaymentTransactionAffiliateTrackedEventReducer();
        var refunded = new PaymentTransactionRefundedEventReducer();
        var projector = CreateProjector(created, tracked, refunded);
        var ctx = CreateContext("paymenttransaction:tx2");
        await projector.InitializeAsync(ctx);

        var createEvt = new PaymentTransactionCreatedEvent
        {
            TransactionId = "tx2",
            UserId = "user-2",
            Amount = 500,
            Currency = "EUR",
            AmountUsd = 550,
            BillingType = "one-time",
            ProductId = "credits_pack",
            Store = "PLAY_STORE"
        };
        await projector.ProjectAsync(ctx, PackEnvelope(createEvt));

        var trackEvt = new PaymentTransactionAffiliateTrackedEvent
        {
            AffiliateTransactionId = "aff-123",
            AffiliatePlatform = "tolt"
        };
        await projector.ProjectAsync(ctx, PackEnvelope(trackEvt));

        var refundEvt = new PaymentTransactionRefundedEvent();
        await projector.ProjectAsync(ctx, PackEnvelope(refundEvt));

        var model = await _store.GetAsync("paymenttransaction:tx2");
        model.Should().NotBeNull();
        model!.TransactionId.Should().Be("tx2");
        model.AffiliateTransactionId.Should().Be("aff-123");
        model.Refunded.Should().BeTrue();
    }

    [Fact]
    public async Task ProjectAsync_Skips_When_PrefixDoesNotMatch()
    {
        var reducer = new PaymentTransactionCreatedEventReducer();
        var projector = CreateProjector(reducer);
        var ctx = CreateContext("useraffiliate:user1");
        var evt = new PaymentTransactionCreatedEvent { TransactionId = "tx1", UserId = "u1" };

        await projector.ProjectAsync(ctx, PackEnvelope(evt));

        var model = await _store.GetAsync("useraffiliate:user1");
        model.Should().BeNull();
    }
}
