using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Authentication.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Tests;

// 06-20-observatory-admin-cross-scope (G1/G8): the fail-closed parse matrix is the security crux of the feature.
public sealed class NyxIdPlatformAdminAuthorizerTests
{
    [Theory]
    [InlineData("""{"id":"u1","email":"a@x.io","role":"admin"}""", true, "admin")]
    [InlineData("""{"id":"u1","email":"a@x.io","role":"operator"}""", true, "operator")]
    [InlineData("""{"id":"u1","email":"a@x.io","role":"Admin"}""", true, "Admin")] // case-insensitive by design
    [InlineData("""{"id":"u1","email":"a@x.io","role":"user"}""", false, "")]
    [InlineData("""{"id":"u1","email":"a@x.io","role":"superadmin"}""", false, "")] // unknown role denied
    [InlineData("""{"id":"u1","email":"a@x.io"}""", false, "")] // missing role denied
    [InlineData("""{"id":"u1","email":"a@x.io","role":""}""", false, "")] // empty role denied
    [InlineData("""{"error":true,"status":401,"body":"unauthorized"}""", false, "")] // error envelope denied
    [InlineData("""{"error":true,"status":500,"body":"x","role":"admin"}""", false, "")] // error wins over role
    [InlineData("not-json", false, "")]
    [InlineData("""["admin"]""", false, "")] // non-object denied
    [InlineData("", false, "")]
    [InlineData("   ", false, "")]
    public void ParseCaller_IsFailClosed(string raw, bool expectedElevated, string expectedRole)
    {
        var caller = NyxIdPlatformAdminAuthorizer.ParseCaller(raw);

        caller.IsElevated.Should().Be(expectedElevated);
        caller.Role.Should().Be(expectedRole);
        if (!expectedElevated)
            caller.Should().BeEquivalentTo(PlatformCaller.NotElevated);
    }

    [Fact]
    public void ParseCaller_CapturesEmailAndUserId_WhenElevated()
    {
        var caller = NyxIdPlatformAdminAuthorizer.ParseCaller(
            """{"id":"5d0d7b72","email":"ean@x.io","role":"admin"}""");

        caller.IsElevated.Should().BeTrue();
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
        stub.GetCurrentUserCalls.Should().Be(1); // second served from cache
    }

    [Fact]
    public async Task ResolveCallerAsync_DoesNotCacheNonElevated()
    {
        var responses = new Queue<string>(
        [
            """{"id":"u1","email":"a@x.io","role":"user"}""",
            """{"id":"u1","email":"a@x.io","role":"admin"}""",
        ]);
        var stub = new StubUserReadApi { OnGetCurrentUser = (_, _) => Task.FromResult(responses.Dequeue()) };
        var authorizer = CreateAuthorizer(stub);

        var first = await authorizer.ResolveCallerAsync("tok-1", CancellationToken.None);
        var second = await authorizer.ResolveCallerAsync("tok-1", CancellationToken.None);

        first.IsElevated.Should().BeFalse();
        second.IsElevated.Should().BeTrue(); // a denial was NOT pinned; a freshly-granted admin is seen
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

    private static NyxIdPlatformAdminAuthorizer CreateAuthorizer(StubUserReadApi stub, bool crossScopeEnabled = true) =>
        new(
            stub,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new ObservatoryAdminAuthorizationOptions
            {
                AdminRoleCacheTtlSeconds = 60,
                CrossScopeEnabled = crossScopeEnabled,
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
