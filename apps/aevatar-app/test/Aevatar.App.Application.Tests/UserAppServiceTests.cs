using Aevatar.App.Application.Errors;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Stores;
using Aevatar.App.Application.Services;
using Aevatar.App.GAgents;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.App.Application.Tests;

public sealed class UserAppServiceTests
{
    private static async Task<(UserAppService Svc, TestActorFactory Factory, IProjectionDocumentStore<AppUserAccountReadModel, string> AccountStore, IProjectionDocumentStore<AppUserProfileReadModel, string> ProfileStore)> CreateWithUser()
    {
        var factory = new TestActorFactory();
        var accountStore = new AppInMemoryDocumentStore<AppUserAccountReadModel, string>(m => m.Id);
        var profileStore = new AppInMemoryDocumentStore<AppUserProfileReadModel, string>(m => m.Id);

        await factory.SendCommandAsync<UserAccountGAgent>("user-1", new UserRegisteredEvent
        {
            UserId = "user-1",
            AuthProvider = "trial",
            AuthProviderId = "trial_user-1",
            Email = "test@test.com",
            EmailVerified = false,
        });
        var accountKey = factory.ResolveActorId<UserAccountGAgent>("user-1");
        await accountStore.UpsertAsync(new AppUserAccountReadModel
        {
            Id = accountKey,
            UserId = "user-1",
            AuthProvider = "trial",
            AuthProviderId = "trial_user-1",
            Email = "test@test.com",
            EmailVerified = false,
            CreatedAt = DateTimeOffset.UtcNow,
            LastLoginAt = DateTimeOffset.UtcNow,
        });

        return (new UserAppService(factory, accountStore, profileStore), factory, accountStore, profileStore);
    }

    [Fact]
    public async Task GetUserInfo_UserExists_ReturnsInfo()
    {
        var (svc, _, _, _) = await CreateWithUser();

        var userInfo = await svc.GetUserInfoAsync("user-1");

        userInfo.User.Should().NotBeNull();
        userInfo.User.Email.Should().Be("test@test.com");
        userInfo.Profile.Should().BeNull();
    }

    [Fact]
    public async Task GetUserInfo_UserNotFound_ThrowsNotFound()
    {
        var svc = new UserAppService(
            new TestActorFactory(),
            new AppInMemoryDocumentStore<AppUserAccountReadModel, string>(m => m.Id),
            new AppInMemoryDocumentStore<AppUserProfileReadModel, string>(m => m.Id));

        var act = () => svc.GetUserInfoAsync("nonexistent");

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task CreateProfile_New_ReturnsProfile()
    {
        var (svc, _, _, _) = await CreateWithUser();

        var profile = await svc.CreateProfileAsync(
            "user-1", "Alice", "Smith", "female", null, "UTC", ["meditation"], "growth", false, null);

        profile.FirstName.Should().Be("Alice");
        profile.LastName.Should().Be("Smith");
        profile.Timezone.Should().Be("UTC");
        profile.Purpose.Should().Be("growth");
    }

    [Fact]
    public async Task CreateProfile_AlreadyExists_ThrowsConflict()
    {
        var (svc, factory, _, profileStore) = await CreateWithUser();
        var profileKey = factory.ResolveActorId<UserProfileGAgent>("user-1");
        await profileStore.MutateAsync(profileKey, m => m.HasProfile = true);

        var act = () => svc.CreateProfileAsync("user-1", "C", "D", null, null, null, null, null, null, null);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task UpdateProfile_Exists_ReturnsUpdated()
    {
        var (svc, factory, _, profileStore) = await CreateWithUser();
        await svc.CreateProfileAsync("user-1", "A", "B", null, null, "UTC", null, null, null, null);
        var profileKey = factory.ResolveActorId<UserProfileGAgent>("user-1");
        await profileStore.MutateAsync(profileKey, m =>
        {
            m.HasProfile = true;
            m.FirstName = "A";
            m.LastName = "B";
            m.Timezone = "UTC";
        });

        var updated = await svc.UpdateProfileAsync(
            "user-1", "Updated", null, null, null, null, null, "America/New_York", null, null);

        updated.FirstName.Should().Be("Updated");
        updated.Timezone.Should().Be("America/New_York");
    }

    [Fact]
    public async Task UpdateProfile_NotExists_ThrowsNotFound()
    {
        var (svc, _, _, _) = await CreateWithUser();

        var act = () => svc.UpdateProfileAsync("user-1", "A", null, null, null, null, null, null, null, null);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task CreateProfile_WithInterests_PersistsInterests()
    {
        var (svc, _, _, _) = await CreateWithUser();

        var profile = await svc.CreateProfileAsync(
            "user-1", "A", "B", null, null, "UTC", ["yoga", "art"], "intent", null, null);

        profile.Interests.Should().Contain("yoga");
        profile.Interests.Should().Contain("art");
    }

    [Fact]
    public async Task DeleteAccount_Soft_DoesNotThrow()
    {
        var (svc, _, _, _) = await CreateWithUser();

        var act = () => svc.DeleteAccountAsync("user-1", hard: false);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAccount_Hard_DoesNotThrow()
    {
        var (svc, _, _, _) = await CreateWithUser();

        var act = () => svc.DeleteAccountAsync("user-1", hard: true);

        await act.Should().NotThrowAsync();
    }

}
