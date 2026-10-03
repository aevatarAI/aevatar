using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.CatalogSkills;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.AI.ToolProviders.Ornn.CatalogSkills;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.Authentication.Abstractions;
using Aevatar.GAgentService.Application.CatalogSkills;
using Aevatar.Mainnet.Host.Api.CatalogSkills;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Aevatar.Capabilities.Tests;

public sealed class CatalogRecommendedSkillAdminBearerTests
{
    private const string CatalogId = "aaaaaaaa-1111-1111-1111-111111111111";
    private const string SkillId = "bbbbbbbb-2222-2222-2222-222222222222";
    private const string SkillName = "aevatar-connected-service";
    private const string AdministratorId = "admin-current-user";
    private const string AdministratorBearer = "current-administrator-bearer";
    private const string PublisherToken = "configured-publisher-token";

    [Fact]
    public async Task Update_UsesRequestBearerWithoutAdministratorBinding()
    {
        using var handler = new DownstreamHandler();
        using var httpClient = new HttpClient(handler);
        using var api = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" }, httpClient);
        var options = new OrnnOptions { NyxIdSlug = "ornn" };
        var skills = new OrnnSkillClient(options, api);
        var publisher = new OrnnSkillPublishingService(new OrnnSkillPublishValidationPipeline(), new OrnnSkillPackageBuilder(),
            new OrnnSkillPackageFormatValidator(options, api), skills);
        var steps = new OrnnCatalogRecommendedSkillUpdateSteps(new PublisherCredentials(), new NyxIdCatalogSkillClient(api),
            new NyxIdRecommendedSkillGenerator(api), publisher, skills);
        var application = new CatalogRecommendedSkillUpdateApplicationService(steps);
        var authorizer = Substitute.For<IPlatformAdminAuthorizer>();
        authorizer.ResolveCallerAsync(AdministratorBearer, Arg.Any<CancellationToken>())
            .Returns(new PlatformCaller(true, "admin", "", AdministratorId, PlatformAdminGrantSources.AllowedUserId));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", AdministratorId)], "test")),
        };
        http.Request.Headers.Authorization = $"Bearer {AdministratorBearer}";

        var result = await CatalogRecommendedSkillAdminEndpoints.UpdateAsync(http, CatalogId, SkillId,
            new("1.0", "1.1"), authorizer, application, CancellationToken.None);

        var body = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200, "the current bearer is usable without a native binding: {0}", body);
        body.Should().Contain("\"catalogReferenceUpdated\":true").And.NotContain(AdministratorBearer).And.NotContain(PublisherToken);
        handler.Calls.Where(call => call.Token == AdministratorBearer).Select(call => call.Path).Should().Equal(
            "/api/v1/proxy/s/ornn/api/v1/me",
            $"/api/v1/proxy/s/ornn/api/v1/skills/{SkillId}?version=1.1",
            $"/api/v1/proxy/s/ornn/api/v1/skills/{SkillId}/json?version=1.1");
        handler.Calls.Where(call => call.Method != "GET").Should().OnlyContain(call => call.Token == PublisherToken);
        handler.Calls.Where(call => !call.Path.StartsWith("/api/v1/proxy/", StringComparison.Ordinal))
            .Should().OnlyContain(call => call.Token == PublisherToken);
    }

    private sealed class PublisherCredentials : INyxIdClientCredentialsTokenSource
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult<string?>(PublisherToken);
    }

    private sealed record Call(string Method, string Path, string? Token);

    private sealed class DownstreamHandler : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];
        private string _digest = new('a', 64);
        private string _version = "1.0";
        private int _revision = 1;
        private bool _private = true;
        private string _references = JsonSerializer.Serialize(new[]
        {
            new { source = "ornn", skill_id = SkillId, name = SkillName, version = "1.0", sha256 = new string('a', 64) },
        });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Authorization?.Parameter;
            Calls.Add(new(request.Method.Method, request.RequestUri.PathAndQuery, token));
            if (path == $"/api/v1/keys/{CatalogId}")
                return Json(new { resource_type = "catalog_service", id = CatalogId, catalog_service_id = CatalogId,
                    slug = "aevatar", catalog_service_slug = "aevatar", name = "Aevatar", description = "Agent execution API" });
            if (path == $"/api/v1/catalog-curation/services/{CatalogId}/openapi.json")
                return Raw("""{"openapi":"3.1.0","paths":{"/runs":{"get":{"operationId":"listRuns","responses":{"200":{"description":"Runs","content":{"application/json":{"schema":{"type":"array","items":{"type":"string"}}}}}}}}}}""");
            if (path.EndsWith("/skill-format/validate", StringComparison.Ordinal))
                return Json(new { data = new { valid = true, violations = Array.Empty<object>() } });
            if (path.EndsWith("/api/v1/me", StringComparison.Ordinal))
                return Json(new { data = new { userId = token == AdministratorBearer ? AdministratorId : "publisher-owner",
                    permissions = new[] { "ornn:skill:read", "ornn:skill:update" } } });
            if (path.EndsWith("/versions", StringComparison.Ordinal))
                return Json(new { data = new { items = new[] { new { version = _version, skillHash = _digest } } } });
            if (path.EndsWith("/permissions", StringComparison.Ordinal))
            {
                _private = false;
                return Json(new { data = new { skill = new { guid = SkillId, isPrivate = false } } });
            }
            if (path.EndsWith("/json", StringComparison.Ordinal))
                return Json(new { data = new { name = SkillName, version = _version,
                    files = new Dictionary<string, string> { ["SKILL.md"] = "Published complete skill body" } } });
            if (path == $"/api/v1/proxy/s/ornn/api/v1/skills/{SkillId}")
            {
                if (request.Method == HttpMethod.Put)
                {
                    _digest = Convert.ToHexStringLower(SHA256.HashData(await request.Content!.ReadAsByteArrayAsync(ct)));
                    _version = "1.1";
                }
                return Json(new { data = new { guid = SkillId, name = SkillName, version = _version,
                    skillHash = _digest, createdBy = "publisher-owner", isPrivate = _private } });
            }
            if (path == $"/api/v1/catalog-curation/services/{CatalogId}/skills")
            {
                if (request.Method == HttpMethod.Put)
                {
                    using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    document.RootElement.GetProperty("base_revision").GetInt32().Should().Be(_revision);
                    _references = document.RootElement.GetProperty("recommended_skill_refs").GetRawText();
                    _revision++;
                }
                return Raw($"{{\"service_id\":\"{CatalogId}\",\"skills_revision\":{_revision},\"recommended_skill_refs\":{_references}}}");
            }
            throw new InvalidOperationException($"Unexpected test route {request.Method} {path}");
        }

        private static HttpResponseMessage Json(object value) => Raw(JsonSerializer.Serialize(value));
        private static HttpResponseMessage Raw(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }
}
