using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Authentication.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Tests;

public sealed class NyxIdPlatformAdminAuthorizerTests
{
    [Theory]
    [InlineData("""{"id":"u1","email":"a@x.io","role":"admin"}""", true, "admin", "a@x.io", "u1")]
    [InlineData("""{"id":"u1","email":"A@X.IO","role":"operator"}""", true, "operator", "a@x.io", "u1")]
    [InlineData("""{"id":"u1","email":"a@x.io","role":"user"}""", true, "user", "a@x.io", "u1")]
    [InlineData("""{"id":"u1","role":"user"}""", true, "user", "", "u1")]
    [InlineData("""{"email":"a@x.io","role":"user"}""", true, "user", "a@x.io", "")]
    [InlineData("""{"error":true,"status":401,"body":"unauthorized"}""", false, "", "", "")]
    [InlineData("""{"error":true,"status":500,"body":"x","role":"admin"}""", false, "", "", "")]
    [InlineData("not-json", false, "", "", "")]
    [InlineData("""["admin"]""", false, "", "", "")]
    [InlineData("""{"role":"admin"}""", false, "", "", "")]
    [InlineData("", false, "", "", "")]
    [InlineData("   ", false, "", "", "")]
    public void ParseCurrentUser_IsFailClosed(string raw, bool expectedValid, string expectedRole, string expectedEmail, string expectedUserId)
    {
        var caller = NyxIdPlatformAdminAuthorizer.ParseCurrentUser(raw);

        caller.IsValid.Should().Be(expectedValid);
        caller.Role.Should().Be(expectedRole);
        caller.Email.Should().Be(expectedEmail);
        caller.UserId.Should().Be(expectedUserId);
        if (!expectedValid)
            caller.Should().BeEquivalentTo(NyxIdPlatformAdminAuthorizer.NyxIdCurrentUser.Invalid);
    }

    [Fact]
    public void ParseCurrentUser_NormalizesEmail()
    {
        var caller = NyxIdPlatformAdminAuthorizer.ParseCurrentUser(
            """{"id":"5d0d7b72","email":" EAN@X.IO ","role":"admin"}""");

        caller.IsValid.Should().BeTrue();
        caller.Email.Should().Be("ean@x.io");
        caller.UserId.Should().Be("5d0d7b72");
    }

    [Fact]
    public async Task ResolveCallerAsync_BlankToken_DeniesWithoutCallingNyxId()
    {
        var stub = new StubUserReadApi();
        var authorizer = CreateAuthorizer(stub);

        var caller = await authorizer.ResolveCallerAsync("   ", CancellationToken.None);

        caller.IsElevated.Should().BeFalse();
        stub.GetCurrentUserCalls.Should().Be(0);
    }

    [Fact]
    public async Task ResolveCallerAsync_AllowsConfiguredUserId()
    {
        var stub = new StubUserReadApi
        {
            OnGetCurrentUser = (_, _) => Task.FromResult("""{"id":"u-allow","email":"person@x.io","role":"user"}"""),
        };
        var authorizer = CreateAuthorizer(stub, allowedUserIds: ["u-allow"], trustNyxIdPlatformRole: false);

        var caller = await authorizer.ResolveCallerAsync("tok-1", CancellationToken.None);

        caller.IsElevated.Should().BeTrue();
        caller.UserId.Should().Be("u-allow");
        caller.GrantSource.Should().Be(PlatformAdminGrantSources.AllowedUserId);
    }

    [Fact]
    public async Task ResolveCallerAsync_AllowsConfiguredEmailWithNormalization()
    {
        var stub = new StubUserReadApi
        {
            OnGetCurrentUser = (_, _) => Task.FromResult("""{"id":"u1","email":" ADMIN@EXAMPLE.COM ","role":"user"}"""),
        };
        var authorizer = CreateAuthorizer(stub, allowedEmails: [" admin@example.com "], trustNyxIdPlatformRole: false);

        var caller = await authorizer.ResolveCallerAsync("tok-1", CancellationToken.None);

        caller.IsElevated.Should().BeTrue();
        caller.Email.Should().Be("admin@example.com");
        caller.GrantSource.Should().Be(PlatformAdminGrantSources.AllowedEmail);
    }

    [Fact]
    public async Task ResolveCallerAsync_TrustNyxIdPlatformRoleOn_AllowsAdminRoleAsTransitionalFallback()
    {
        var stub = new StubUserReadApi
        {
            OnGetCurrentUser = (_, _) => Task.FromResult("""{"id":"u1","email":"a@x.io","role":"operator"}"""),
        };
        var authorizer = CreateAuthorizer(stub, trustNyxIdPlatformRole: true);

        var caller = await authorizer.ResolveCallerAsync("tok-1", CancellationToken.None);

        caller.IsElevated.Should().BeTrue();
        caller.GrantSource.Should().Be(PlatformAdminGrantSources.NyxIdPlatformRole);
    }

    [Fact]
    public async Task ResolveCallerAsync_TrustNyxIdPlatformRoleOff_DeniesPlatformRoleWithoutAllowlist()
    {
        var stub = new StubUserReadApi
        {
            OnGetCurrentUser = (_, _) => Task.FromResult("""{"id":"u1","email":"a@x.io","role":"admin"}"""),
        };
        var authorizer = CreateAuthorizer(stub, trustNyxIdPlatformRole: false);

        var caller = await authorizer.ResolveCallerAsync("tok-1", CancellationToken.None);

        caller.IsElevated.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveCallerAsync_CachesPositiveDecisionPerToken()
    {
        var stub = new StubUserReadApi
        {
            OnGetCurrentUser = (_, _) => Task.FromResult("""{"id":"u1","email":"a@x.io","role":"admin"}"""),
        };
        var authorizer = CreateAuthorizer(stub);

        var first = await authorizer.ResolveCallerAsync("tok-1", CancellationToken.None);
        var second = await authorizer.ResolveCallerAsync("tok-1", CancellationToken.None);

        first.IsElevated.Should().BeTrue();
        second.IsElevated.Should().BeTrue();
        stub.GetCurrentUserCalls.Should().Be(1);
    }

    [Fact]
    public async Task ResolveCallerAsync_DoesNotCacheNonElevated()
    {
        var responses = new Queue<string>(
        [
            """{"id":"u1","email":"a@x.io","role":"user"}""",
            """{"id":"u1","email":"admin@x.io","role":"user"}""",
        ]);
        var stub = new StubUserReadApi { OnGetCurrentUser = (_, _) => Task.FromResult(responses.Dequeue()) };
        var authorizer = CreateAuthorizer(stub, allowedEmails: ["admin@x.io"], trustNyxIdPlatformRole: false);

        var first = await authorizer.ResolveCallerAsync("tok-1", CancellationToken.None);
        var second = await authorizer.ResolveCallerAsync("tok-1", CancellationToken.None);

        first.IsElevated.Should().BeFalse();
        second.IsElevated.Should().BeTrue();
        stub.GetCurrentUserCalls.Should().Be(2);
    }

    [Fact]
    public async Task ResolveCallerAsync_PropagatesCancellation()
    {
        var stub = new StubUserReadApi
        {
            OnGetCurrentUser = (_, _) => throw new OperationCanceledException(),
        };
        var authorizer = CreateAuthorizer(stub);

        await FluentActions
            .Awaiting(() => authorizer.ResolveCallerAsync("tok-1", CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ResolveCallerAsync_FailsClosed_OnProviderException()
    {
        var stub = new StubUserReadApi
        {
            OnGetCurrentUser = (_, _) => throw new HttpRequestException("network down"),
        };
        var authorizer = CreateAuthorizer(stub);

        var caller = await authorizer.ResolveCallerAsync("tok-1", CancellationToken.None);

        caller.IsElevated.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveCallerAsync_KillSwitchOff_DeniesWithoutCallingNyxId()
    {
        var stub = new StubUserReadApi
        {
            OnGetCurrentUser = (_, _) => Task.FromResult("""{"id":"u1","email":"a@x.io","role":"admin"}"""),
        };
        var authorizer = CreateAuthorizer(stub, crossScopeEnabled: false);

        var caller = await authorizer.ResolveCallerAsync("tok-1", CancellationToken.None);

        caller.IsElevated.Should().BeFalse();
        stub.GetCurrentUserCalls.Should().Be(0);
    }

    private static NyxIdPlatformAdminAuthorizer CreateAuthorizer(
        StubUserReadApi stub,
        bool crossScopeEnabled = true,
        IReadOnlyList<string>? allowedUserIds = null,
        IReadOnlyList<string>? allowedEmails = null,
        bool trustNyxIdPlatformRole = true) =>
        new(
            stub,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new ObservatoryAdminAuthorizationOptions
            {
                AdminRoleCacheTtlSeconds = 60,
                CrossScopeEnabled = crossScopeEnabled,
                AllowedUserIds = allowedUserIds?.ToArray() ?? [],
                AllowedEmails = allowedEmails?.ToArray() ?? [],
                TrustNyxIdPlatformRole = trustNyxIdPlatformRole,
            }),
            NullLogger<NyxIdPlatformAdminAuthorizer>.Instance);

    private sealed class StubUserReadApi : INyxIdUserReadApi
    {
        public Func<string, CancellationToken, Task<string>>? OnGetCurrentUser { get; init; }

        public Func<string, string, CancellationToken, Task<string>>? OnSearch { get; init; }

        public int GetCurrentUserCalls { get; private set; }

        public Task<string> GetCurrentUserAsync(string token, CancellationToken ct)
        {
            GetCurrentUserCalls++;
            return OnGetCurrentUser is null ? Task.FromResult("{}") : OnGetCurrentUser(token, ct);
        }

        public Task<string> SearchAdminUsersAsync(string token, string email, CancellationToken ct) =>
            OnSearch is null ? Task.FromResult("""{"users":[]}""") : OnSearch(token, email, ct);
    }
}
