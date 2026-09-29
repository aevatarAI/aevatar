using System.Security.Claims;
using Aevatar.AI.Abstractions.Skills;
using Aevatar.AI.Core.Skills;
using Aevatar.Mainnet.Host.Api.Skills;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aevatar.Capabilities.Tests;

public sealed class SkillServiceRecommendationEndpointTests
{
    [Fact]
    public async Task Endpoint_MapsTypedRecommendations_AndForwardsCallerAndCancellation()
    {
        var source = new RecordingSource();
        var http = Context();
        using var cancellation = new CancellationTokenSource();
        var response = await SkillServiceRecommendationEndpoints.GetRecommendations(
            http, new SkillServiceRecommendationService(source), "support", cancellation.Token);

        var body = response.Should().BeOfType<JsonHttpResult<SkillServiceRecommendationsResponse>>()
            .Which.Value!;
        body.SkillName.Should().Be("support");
        body.Suggestions.Should().ContainSingle().Which.Evidence.Should().Be("linked");
        body.Suggestions[0].Instances.Should().BeEmpty();
        source.Token.Should().Be("caller-token");
        source.Cancellation.Should().Be(cancellation.Token);
        http.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Fact]
    public async Task Endpoint_RejectsUnauthenticatedOrInvalidInput_BeforeExternalReads()
    {
        var source = new RecordingSource();
        var service = new SkillServiceRecommendationService(source);
        var http = Context();
        http.User = new ClaimsPrincipal();
        (await SkillServiceRecommendationEndpoints.GetRecommendations(http, service, "support"))
            .Should().BeOfType<UnauthorizedHttpResult>();
        (await SkillServiceRecommendationEndpoints.GetRecommendations(Context(), service, ".."))
            .Should().BeOfType<BadRequest<SkillServiceRecommendationError>>();
        source.Token.Should().BeNull();
    }

    [Fact]
    public async Task Endpoint_ReturnsRetryableFailure_WithoutPretendingNoDependencies()
    {
        var source = new RecordingSource { Fail = true };
        var response = await SkillServiceRecommendationEndpoints.GetRecommendations(
            Context(), new SkillServiceRecommendationService(source), "support");
        var result = response.Should().BeOfType<JsonHttpResult<SkillServiceRecommendationError>>().Subject;
        result.StatusCode.Should().Be(502);
        result.Value!.Code.Should().Be("skill_service_discovery_unavailable");
    }

    private static DefaultHttpContext Context()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer caller-token";
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-alpha")], "test"));
        return http;
    }

    private sealed class RecordingSource : ISkillServiceDiscoverySource
    {
        public bool Fail { get; init; }
        public string? Token { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public Task<SkillServiceDiscoveryInput> ReadAsync(string accessToken, string skillName, CancellationToken ct)
        {
            Token = accessToken;
            Cancellation = ct;
            if (Fail) throw new SkillServiceDiscoveryException();
            return Task.FromResult(new SkillServiceDiscoveryInput
            {
                SkillName = skillName,
                LinkedServiceSlug = "api-github",
            });
        }
    }
}
