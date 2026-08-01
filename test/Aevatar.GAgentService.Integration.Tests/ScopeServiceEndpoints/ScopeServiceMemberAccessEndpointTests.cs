using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Aevatar.GAgentService.Integration.Tests;

[Collection(ScopeServiceEndpointCollection.Name)]
public sealed class ScopeServiceMemberAccessEndpointTests : ScopeServiceEndpointTestKit
{
    [Theory]
    [InlineData("GET", "/api/scopes/scope-alpha/members/m-alpha/published-service")]
    [InlineData("POST", "/api/scopes/scope-alpha/members/m-alpha/invoke/chat")]
    [InlineData("POST", "/api/scopes/scope-alpha/members/m-alpha/invoke/chat:stream")]
    [InlineData("GET", "/api/scopes/scope-alpha/members/m-alpha/runs?take=5")]
    [InlineData("GET", "/api/scopes/scope-alpha/members/m-alpha/runs/run-alpha")]
    [InlineData("GET", "/api/scopes/scope-alpha/members/m-alpha/runs/run-alpha/audit")]
    [InlineData("POST", "/api/scopes/scope-alpha/members/m-alpha/runs/run-alpha:resume")]
    [InlineData("POST", "/api/scopes/scope-alpha/members/m-alpha/runs/run-alpha:signal")]
    [InlineData("POST", "/api/scopes/scope-alpha/members/m-alpha/runs/run-alpha:stop")]
    [InlineData("POST", "/api/scopes/scope-alpha/members/m-alpha/runs/run-alpha:retry-compensation")]
    public async Task MemberFirstRoutes_ShouldRejectDifferentAuthenticatedMember(
        string method,
        string requestUri)
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), requestUri);
        if (string.Equals(method, "POST", StringComparison.Ordinal))
            request.Content = JsonContent.Create(new { });
        request.Headers.Add("X-Test-Scope-Id", "scope-alpha");
        request.Headers.Add("X-Test-Member-Id", "m-attacker");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.Should().Contain("MEMBER_ACCESS_DENIED");
        host.MemberPublishedServiceResolver.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task MemberFirstRoute_ShouldAllowScopeOwner()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/scopes/scope-alpha/members/m-alpha/published-service");
        request.Headers.Add("X-Test-Scope-Id", "scope-alpha");
        request.Headers.Add("X-Test-Member-Id", "m-attacker");
        request.Headers.Add("X-Test-Role", "owner");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.MemberPublishedServiceResolver.Calls.Should().ContainSingle();
    }
}
