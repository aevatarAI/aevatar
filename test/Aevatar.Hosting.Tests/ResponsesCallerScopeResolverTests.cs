using Aevatar.GAgents.Scheduled;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Mainnet.Host.Api.Responses;
using FluentAssertions;

namespace Aevatar.Hosting.Tests;

public sealed class ResponsesCallerScopeResolverTests
{
    [Fact]
    public void Constructor_ShouldRejectNullResolver()
    {
        ((Action)(() => new NyxIdResponsesCallerScopeResolver(null!)))
            .Should().Throw<ArgumentNullException>()
            .WithMessage("*currentUserResolver*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenAccessTokenMissing()
    {
        var resolver = new NyxIdResponsesCallerScopeResolver(new StubUserResolver(returnUserId: "user-1"));

        var act = () => resolver.ResolveAsync(CreateContext(""));

        await act.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>()
            .WithMessage("*access token is required*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenAccessTokenWhitespace()
    {
        var resolver = new NyxIdResponsesCallerScopeResolver(new StubUserResolver(returnUserId: "user-1"));

        var act = () => resolver.ResolveAsync(CreateContext("   "));

        await act.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenUpstreamReturnsNull()
    {
        var resolver = new NyxIdResponsesCallerScopeResolver(new StubUserResolver(returnUserId: null));

        var act = () => resolver.ResolveAsync(CreateContext("some-token"));

        await act.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>()
            .WithMessage("*Could not resolve current NyxID user id*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenUpstreamReturnsBlank()
    {
        var resolver = new NyxIdResponsesCallerScopeResolver(new StubUserResolver(returnUserId: "   "));

        var act = () => resolver.ResolveAsync(CreateContext("some-token"));

        await act.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnTrimmedScope_WithApiKeyOrigin()
    {
        var resolver = new NyxIdResponsesCallerScopeResolver(new StubUserResolver(returnUserId: "  alice-1  "));

        var scope = await resolver.ResolveAsync(CreateContext("token"));

        scope.ScopeId.Should().Be("alice-1");
        scope.OwnerSubject.Should().Be("alice-1");
        scope.OriginKind.Should().Be(LlmSessionOriginKind.ApiKey);
    }

    [Fact]
    public async Task ResolveAsync_ShouldPropagateCancellation()
    {
        var resolver = new NyxIdResponsesCallerScopeResolver(new StubUserResolver(returnUserId: "alice"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => resolver.ResolveAsync(CreateContext("token"), cts.Token);

        // Stub honors cancellation token; ensures the resolver passes ct through.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ResolveAsync_ShouldContinueUsingInboundBearerForMeFallback()
    {
        var currentUserResolver = new StubUserResolver(returnUserId: "alice");
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver);

        _ = await resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: "identity-token",
            delegationToken: "delegation-token"));

        currentUserResolver.LastAccessToken.Should().Be("bearer-token");
    }

    private static ResponsesCallerScopeResolutionContext CreateContext(
        string bearerToken,
        string? identityToken = null,
        string? delegationToken = null) =>
        new(bearerToken, identityToken, delegationToken);

    private sealed class StubUserResolver : INyxIdCurrentUserResolver
    {
        private readonly string? _returnUserId;

        public StubUserResolver(string? returnUserId) => _returnUserId = returnUserId;

        public string? LastAccessToken { get; private set; }

        public Task<string?> ResolveCurrentUserIdAsync(string nyxIdAccessToken, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastAccessToken = nyxIdAccessToken;
            return Task.FromResult(_returnUserId);
        }
    }
}
