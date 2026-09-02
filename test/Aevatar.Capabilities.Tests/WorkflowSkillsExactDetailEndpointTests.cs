using System.Security.Claims;
using Aevatar.Mainnet.Host.Api.Skills;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aevatar.Capabilities.Tests;

public sealed class WorkflowSkillsExactDetailEndpointTests
{
    [Fact]
    public async Task GetExactSkill_ShouldReturnPinnedAuthorityFields()
    {
        var catalog = new RecordingCatalog
        {
            ExactResult = new UserExactSkillReadResult(
                new UserExactSkillDetail(
                    "11111111-2222-3333-4444-555555555555",
                    "research",
                    "1.2",
                    "publisher-alpha",
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    ["lookup", "search"]),
                null),
        };
        var http = Context("Bearer nyx-token");

        var result = await WorkflowSkillsEndpoints.GetExactSkill(
            http,
            "11111111-2222-3333-4444-555555555555",
            catalog,
            "1.2",
            CancellationToken.None);

        var json = result.Should().BeOfType<JsonHttpResult<UserExactSkillDetail>>().Subject;
        json.Value!.LiteralVersion.Should().Be("1.2");
        json.Value.Publisher.Should().Be("publisher-alpha");
        json.Value.SkillHash.Should().HaveLength(64);
        json.Value.DeclaredToolNames.Should().Equal("lookup", "search");
        catalog.Requests.Should().Equal(("nyx-token", "11111111-2222-3333-4444-555555555555", "1.2"));
    }

    [Fact]
    public async Task GetExactSkill_ShouldRejectMalformedLiteralVersionBeforeQuery()
    {
        var catalog = new RecordingCatalog();

        var result = await WorkflowSkillsEndpoints.GetExactSkill(
            Context("Bearer nyx-token"),
            "11111111-2222-3333-4444-555555555555",
            catalog,
            "latest",
            CancellationToken.None);

        result.Should().BeOfType<BadRequest<AgentProfileExactSkillError>>()
            .Which.Value!.Code.Should().Be("invalid_literal_version");
        catalog.Requests.Should().BeEmpty();
    }

    private static DefaultHttpContext Context(string authorization)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = authorization;
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-alpha")], "test"));
        return http;
    }

    private sealed class RecordingCatalog : IUserSkillCatalogQueryService
    {
        public List<(string Token, string Guid, string? LiteralVersion)> Requests { get; } = [];

        public UserExactSkillReadResult ExactResult { get; init; } = new(null, "not_found");

        public Task<UserSkillListResult> ListVisibleSkillsAsync(
            string accessToken,
            string query,
            int page,
            int pageSize,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<UserSkillDetail?> GetSkillAsync(
            string accessToken,
            string guid,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<UserExactSkillReadResult> GetExactSkillAsync(
            string accessToken,
            string guid,
            string? literalVersion,
            CancellationToken ct = default)
        {
            Requests.Add((accessToken, guid, literalVersion));
            return Task.FromResult(ExactResult);
        }
    }
}
