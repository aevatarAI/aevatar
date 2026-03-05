using System.IdentityModel.Tokens.Jwt;
using Aevatar.App.Application.Projection.Orchestration;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Stores;
using Aevatar.App.Application.Services;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.App.Application.Tests;

public sealed class AuthAppServiceTests
{
    private const string Secret = "super-secret-key-for-test-32chars!";

    private static (AuthAppService Svc, IProjectionDocumentStore<AppAuthLookupReadModel, string> Store) Create(TestActorFactory? factory = null)
    {
        factory ??= new TestActorFactory();
        var store = new AppInMemoryDocumentStore<AppAuthLookupReadModel, string>(m => m.Id);
        var svc = new AuthAppService(
            factory,
            store,
            new NoOpAuthProjectionManager());
        return (svc, store);
    }

    [Fact]
    public async Task RegisterTrial_NewUser_CreatesTokenAndTrialId()
    {
        var (svc, _) = Create();

        var result = await svc.RegisterTrialAsync("Alice@Example.COM", Secret);

        result.IsExisting.Should().BeFalse();
        result.TrialId.Should().StartWith("trial_");
        result.Token.Should().NotBeNullOrWhiteSpace();

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(result.Token);
        jwt.Claims.Should().Contain(c => c.Type == "email" && c.Value == "alice@example.com");
        jwt.Claims.Should().Contain(c => c.Type == "type" && c.Value == "trial");
    }

    [Fact]
    public async Task RegisterTrial_SameEmailTwice_ReturnsExisting()
    {
        var factory = new TestActorFactory();
        var (svc, store) = Create(factory);

        var first = await svc.RegisterTrialAsync("bob@test.com", Secret);
        await SimulateAuthLookupProjection(store, "email:bob@test.com", first.TrialId.Replace("trial_", ""));

        var second = await svc.RegisterTrialAsync("Bob@Test.COM", Secret);

        second.IsExisting.Should().BeTrue();
        second.TrialId.Should().Be(first.TrialId);
    }

    [Fact]
    public async Task RegisterTrial_DifferentEmails_CreatesDifferentUsers()
    {
        var factory = new TestActorFactory();
        var (svc, _) = Create(factory);

        var a = await svc.RegisterTrialAsync("a@test.com", Secret);
        var b = await svc.RegisterTrialAsync("b@test.com", Secret);

        a.TrialId.Should().NotBe(b.TrialId);
        a.IsExisting.Should().BeFalse();
        b.IsExisting.Should().BeFalse();
    }

    [Fact]
    public async Task RegisterTrial_NormalizesEmail_WhitespaceAndCase()
    {
        var factory = new TestActorFactory();
        var (svc, store) = Create(factory);

        var first = await svc.RegisterTrialAsync("  User@MAIL.com  ", Secret);
        await SimulateAuthLookupProjection(store, "email:user@mail.com", first.TrialId.Replace("trial_", ""));

        var second = await svc.RegisterTrialAsync("user@mail.com", Secret);

        second.IsExisting.Should().BeTrue();
        second.TrialId.Should().Be(first.TrialId);
    }

    private static Task SimulateAuthLookupProjection(
        IProjectionDocumentStore<AppAuthLookupReadModel, string> store, string lookupKey, string userId)
    {
        var actorKey = $"authlookup:{lookupKey}";
        return store.UpsertAsync(new AppAuthLookupReadModel { Id = actorKey, LookupKey = actorKey, UserId = userId });
    }

    private sealed class NoOpAuthProjectionManager : IAppProjectionManager
    {
        public Task EnsureSubscribedAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnsubscribeAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
