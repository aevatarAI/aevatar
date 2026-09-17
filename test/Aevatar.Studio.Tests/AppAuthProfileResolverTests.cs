using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Studio.Hosting.Auth;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class AppAuthProfileResolverTests
{
    [Fact]
    public async Task ResolveAsync_ShouldUseProviderProfileWithoutScopeFallback()
    {
        var nyxId = new RecordingNyxIdUserReadApi(
            """
            {
              "id":"nyx-user-1",
              "name":"Abigail Deng",
              "email":"abigail@example.com",
              "email_verified":true,
              "picture":"https://example.com/avatar.png",
              "roles":["admin"],
              "groups":["studio"]
            }
            """);
        var resolver = new NyxIdAppAuthProfileResolver(
            nyxId,
            NullLogger<NyxIdAppAuthProfileResolver>.Instance);
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer local-token";
        var claimsProfile = new AppAuthProfileResponse(
            Subject: "scope-123",
            Name: null,
            Email: null,
            EmailVerified: null,
            Picture: null,
            Roles: [],
            Groups: []);

        var profile = await resolver.ResolveAsync(http, claimsProfile, CancellationToken.None);

        profile.Should().NotBeNull();
        profile!.Subject.Should().Be("nyx-user-1");
        profile.Name.Should().Be("Abigail Deng");
        profile.Email.Should().Be("abigail@example.com");
        profile.EmailVerified.Should().BeTrue();
        profile.Picture.Should().Be("https://example.com/avatar.png");
        profile.Roles.Should().Equal("admin");
        profile.Groups.Should().Equal("studio");
        nyxId.ObservedToken.Should().Be("local-token");
    }

    [Fact]
    public async Task ResolveAsync_WithDpopAuthorization_ShouldUseProviderProfile()
    {
        var nyxId = new RecordingNyxIdUserReadApi("""{"id":"nyx-user-1","name":"Abigail Deng"}""");
        var resolver = new NyxIdAppAuthProfileResolver(
            nyxId,
            NullLogger<NyxIdAppAuthProfileResolver>.Instance);
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "DPoP local-dpop-token";

        var profile = await resolver.ResolveAsync(http, null, CancellationToken.None);

        profile.Should().NotBeNull();
        profile!.Subject.Should().Be("nyx-user-1");
        profile.Name.Should().Be("Abigail Deng");
        nyxId.ObservedToken.Should().Be("local-dpop-token");
    }

    [Fact]
    public void ParseCurrentUser_WithMissingProviderFields_ShouldKeepMissingFieldsEmpty()
    {
        var profile = NyxIdAppAuthProfileResolver.ParseCurrentUser("""{"id":"nyx-user-1"}""");

        profile.Should().NotBeNull();
        profile!.Subject.Should().Be("nyx-user-1");
        profile.Name.Should().BeNull();
        profile.Email.Should().BeNull();
        profile.EmailVerified.Should().BeNull();
        profile.Picture.Should().BeNull();
        profile.Roles.Should().BeEmpty();
        profile.Groups.Should().BeEmpty();
    }

    private sealed class RecordingNyxIdUserReadApi(string currentUserJson) : INyxIdUserReadApi
    {
        public string? ObservedToken { get; private set; }

        public Task<string> GetCurrentUserAsync(string token, CancellationToken ct)
        {
            ObservedToken = token;
            return Task.FromResult(currentUserJson);
        }

        public Task<string> SearchAdminUsersAsync(string token, string email, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
