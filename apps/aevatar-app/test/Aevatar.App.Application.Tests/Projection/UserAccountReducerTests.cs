using FluentAssertions;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Reducers;
using Aevatar.App.GAgents;
using Google.Protobuf.WellKnownTypes;
using static Aevatar.App.Application.Tests.Projection.ProjectionTestHelpers;

namespace Aevatar.App.Application.Tests.Projection;

public sealed class UserAccountReducerTests
{
    private readonly DateTimeOffset _now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UserRegistered_Populates_AllFields()
    {
        var reducer = new UserRegisteredEventReducer();
        var model = new AppUserAccountReadModel { Id = "useraccount:u1" };
        var registeredAt = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var evt = new UserRegisteredEvent
        {
            UserId = "u1",
            AuthProvider = "firebase",
            AuthProviderId = "fb-123",
            Email = "test@example.com",
            EmailVerified = true,
            RegisteredAt = Timestamp.FromDateTime(registeredAt),
        };
        var envelope = PackEnvelope(evt);
        var ctx = CreateContext("useraccount:u1");

        reducer.Reduce(model, ctx, envelope, _now).Should().BeTrue();

        model.UserId.Should().Be("u1");
        model.AuthProvider.Should().Be("firebase");
        model.AuthProviderId.Should().Be("fb-123");
        model.Email.Should().Be("test@example.com");
        model.EmailVerified.Should().BeTrue();
        model.CreatedAt.Should().Be(new DateTimeOffset(registeredAt));
        model.LastLoginAt.Should().Be(model.CreatedAt);
        model.Deleted.Should().BeFalse();
    }

    [Fact]
    public void UserRegistered_Uses_Now_WhenTimestamp_Missing()
    {
        var reducer = new UserRegisteredEventReducer();
        var model = new AppUserAccountReadModel { Id = "useraccount:u2" };
        var evt = new UserRegisteredEvent { UserId = "u2", AuthProvider = "google" };
        var envelope = PackEnvelope(evt);

        reducer.Reduce(model, CreateContext("useraccount:u2"), envelope, _now);

        model.CreatedAt.Should().Be(_now);
    }

    [Fact]
    public void ProviderLinked_Updates_Provider_And_Login()
    {
        var reducer = new UserProviderLinkedEventReducer();
        var model = new AppUserAccountReadModel
        {
            Id = "useraccount:u1",
            AuthProvider = "firebase",
            AuthProviderId = "old",
        };
        var linkedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var evt = new UserProviderLinkedEvent
        {
            AuthProvider = "google",
            AuthProviderId = "g-456",
            EmailVerified = true,
            LinkedAt = Timestamp.FromDateTime(linkedAt),
        };

        reducer.Reduce(model, CreateContext("useraccount:u1"), PackEnvelope(evt), _now).Should().BeTrue();

        model.AuthProvider.Should().Be("google");
        model.AuthProviderId.Should().Be("g-456");
        model.EmailVerified.Should().BeTrue();
        model.LastLoginAt.Should().Be(new DateTimeOffset(linkedAt));
    }

    [Fact]
    public void LoginUpdated_Updates_Email_And_LastLogin()
    {
        var reducer = new UserLoginUpdatedEventReducer();
        var model = new AppUserAccountReadModel { Id = "useraccount:u1", Email = "old@test.com" };
        var loginAt = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        var evt = new UserLoginUpdatedEvent
        {
            Email = "new@test.com",
            EmailVerified = true,
            LoginAt = Timestamp.FromDateTime(loginAt),
        };

        reducer.Reduce(model, CreateContext("useraccount:u1"), PackEnvelope(evt), _now).Should().BeTrue();

        model.Email.Should().Be("new@test.com");
        model.EmailVerified.Should().BeTrue();
        model.LastLoginAt.Should().Be(new DateTimeOffset(loginAt));
    }

    [Fact]
    public void AccountDeleted_Sets_Deleted_Flag()
    {
        var reducer = new AccountDeletedEventUserReducer();
        var model = new AppUserAccountReadModel { Id = "useraccount:u1", Deleted = false };
        var evt = new AccountDeletedEvent { UserId = "u1", Mode = "soft" };

        reducer.Reduce(model, CreateContext("useraccount:u1"), PackEnvelope(evt), _now).Should().BeTrue();

        model.Deleted.Should().BeTrue();
    }
}
