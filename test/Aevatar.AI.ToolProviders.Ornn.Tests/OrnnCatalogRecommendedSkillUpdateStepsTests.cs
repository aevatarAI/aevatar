using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.CatalogSkills;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.Ornn.CatalogSkills;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.GAgentService.Abstractions.CatalogSkills;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnCatalogRecommendedSkillUpdateStepsTests
{
    private const string CatalogId = "aaaaaaaa-1111-1111-1111-111111111111";
    private const string SkillId = "bbbbbbbb-2222-2222-2222-222222222222";
    private const string OtherId = "cccccccc-3333-3333-3333-333333333333";
    private const string SkillName = "aevatar-connected-service";

    [Fact]
    public async Task Update_ShouldPublishSameIdThenVerifyPublicConsumerAndConditionalReference()
    {
        var handler = new DownstreamHandler { IsPrivate = true };
        var steps = Create(handler);
        var request = Request();
        var target = await steps.ResolveTargetAsync(request);
        var package = await steps.PreparePackageAsync(request, target);
        var published = await steps.PublishVersionAsync(request, package);
        published.IsPublic.Should().BeFalse();
        var verified = await steps.VerifyPublicationAsync(request, published, Credential());
        var written = await steps.PersistReferenceAsync(request, target, verified);
        var observed = await steps.VerifyReferenceAsync(request, verified, written);

        observed.Confirmed.Should().BeTrue();
        observed.SkillsRevision.Should().Be(8);
        observed.ManifestDigest.Should().Be(Convert.ToHexStringLower(SHA256.HashData(package.ZipBytes)));
        handler.Requests.Should().ContainSingle(item => item.Method == "PUT" && item.Path == $"/api/v1/proxy/s/ornn/api/v1/skills/{SkillId}");
        handler.Requests.Should().NotContain(item => item.Method == "POST" && item.Path.EndsWith("/skills", StringComparison.Ordinal));
        handler.Requests.Should().ContainSingle(item => item.Path.EndsWith("/permissions", StringComparison.Ordinal));
        handler.Requests.Should().Contain(item => item.Token == "consumer" && item.Path.EndsWith("/json", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(handler.ReferenceWrite!);
        body.RootElement.GetProperty("request_id").GetString().Should().Be(request.OperationId);
        body.RootElement.GetProperty("base_revision").GetInt64().Should().Be(7);
        var references = body.RootElement.GetProperty("recommended_skill_refs");
        references[0].GetProperty("skill_id").GetString().Should().Be(SkillId);
        references[0].GetProperty("version").GetString().Should().Be("1.1");
        references[0].GetProperty("dependencies").GetArrayLength().Should().Be(1);
        references[1].GetProperty("skill_id").GetString().Should().Be(OtherId);
        references[1].GetProperty("version").GetString().Should().Be("3.0");
    }

    [Fact]
    public async Task Resolve_ShouldRejectStaleExpectedVersionBeforeOrnnCalls()
    {
        var handler = new DownstreamHandler();
        var request = Request();
        request.ExpectedVersion = "0.9";
        var error = await FluentActions.Awaiting(() => Create(handler).ResolveTargetAsync(request))
            .Should().ThrowAsync<CatalogRecommendedSkillUpdateException>();
        error.Which.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.ExpectedVersionConflict);
        handler.Requests.Should().NotContain(item => item.Path.Contains("/proxy/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolve_ShouldRequireActualManagementPermission()
    {
        var handler = new DownstreamHandler { PublisherUserId = "not-owner" };
        var error = await FluentActions.Awaiting(() => Create(handler).ResolveTargetAsync(Request()))
            .Should().ThrowAsync<CatalogRecommendedSkillUpdateException>();
        error.Which.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.Forbidden);
        handler.Requests.Should().NotContain(item => item.Method == "PUT" || item.Method == "POST");
    }

    [Fact]
    public async Task Resolve_ShouldRejectVersionBehindOrnnLatestEvenWhenCatalogIsOlder()
    {
        var handler = new DownstreamHandler { LatestVersion = "2.0" };
        var error = await FluentActions.Awaiting(() => Create(handler).ResolveTargetAsync(Request()))
            .Should().ThrowAsync<CatalogRecommendedSkillUpdateException>();
        error.Which.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.VersionOccupied);
        handler.Requests.Should().NotContain(item => item.Method == "PUT" || item.Method == "POST");
    }

    [Fact]
    public async Task Publish_ShouldMarkMalformedSuccessUncertain()
    {
        var handler = new DownstreamHandler { InvalidPublicationReceipt = true };
        var error = await FluentActions.Awaiting(() => Create(handler).PublishVersionAsync(Request(), new([1, 2, 3], SkillName)))
            .Should().ThrowAsync<CatalogRecommendedSkillUpdateException>();
        error.Which.OutcomeUncertain.Should().BeTrue();
        handler.ReferenceWrite.Should().BeNull();
    }

    [Fact]
    public async Task Verify_ShouldUseAdministratorBearerWhenNoConsumerBindingExists()
    {
        var handler = new DownstreamHandler();
        var steps = Create(handler);
        var published = await steps.PublishVersionAsync(Request(), new([1, 2, 3], SkillName));
        var verified = await steps.VerifyPublicationAsync(Request(), published, Credential());
        verified.ConsumerReadable.Should().BeTrue();
        handler.Requests.Should().Contain(item => item.Token == "consumer" && item.Path.EndsWith("/api/v1/me", StringComparison.Ordinal));
        handler.Requests.Should().Contain(item => item.Token == "consumer" && item.Path.EndsWith("/json", StringComparison.Ordinal));
        handler.ReferenceWrite.Should().BeNull();
        handler.PublishCount.Should().Be(1);
    }

    [Fact]
    public async Task Verify_ShouldRejectBearerWhenItsOrnnIdentityDoesNotMatchAdministrator()
    {
        var handler = new DownstreamHandler();
        var steps = Create(handler);
        var request = Request();
        request.AdministratorId = "different-administrator";
        var published = await steps.PublishVersionAsync(request, new([1, 2, 3], SkillName));

        var error = await FluentActions.Awaiting(() => steps.VerifyPublicationAsync(request, published, Credential()))
            .Should().ThrowAsync<CatalogRecommendedSkillUpdateException>();

        error.Which.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.Forbidden);
        error.Which.DownstreamStatusCode.Should().Be(403);
        handler.Requests.Should().NotContain(item => item.Path.EndsWith("/json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_ShouldReportDeniedAdministratorBearerRead()
    {
        var handler = new DownstreamHandler { ConsumerReadDenied = true };
        var steps = Create(handler);
        var request = Request();
        var published = await steps.PublishVersionAsync(request, new([1, 2, 3], SkillName));

        var error = await FluentActions.Awaiting(() => steps.VerifyPublicationAsync(request, published, Credential()))
            .Should().ThrowAsync<CatalogRecommendedSkillUpdateException>();

        error.Which.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.Forbidden);
        error.Which.DownstreamStatusCode.Should().Be(403);
        handler.Requests.Should().NotContain(item => item.Path.EndsWith("/json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_ShouldRejectIncorrectConsumerVersion()
    {
        var handler = new DownstreamHandler { ConsumerVersionMismatch = true };
        var steps = Create(handler);
        var published = await steps.PublishVersionAsync(Request(), new([1, 2, 3], SkillName));
        var error = await FluentActions.Awaiting(() => steps.VerifyPublicationAsync(Request(), published, Credential()))
            .Should().ThrowAsync<CatalogRecommendedSkillUpdateException>();
        error.Which.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.PublicationInvalid);
        handler.ReferenceWrite.Should().BeNull();
    }

    [Fact]
    public async Task Persist_ShouldRefuseConcurrentCatalogChangeWithoutOverwritingOtherPins()
    {
        var handler = new DownstreamHandler();
        var steps = Create(handler);
        var request = Request();
        var target = await steps.ResolveTargetAsync(request);
        var publication = await steps.PublishVersionAsync(request, new([1, 2, 3], SkillName));
        publication = await steps.VerifyPublicationAsync(request, publication, Credential());
        handler.Revision = 9;
        var error = await FluentActions.Awaiting(() => steps.PersistReferenceAsync(request, target, publication))
            .Should().ThrowAsync<CatalogRecommendedSkillUpdateException>();
        error.Which.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.ReferenceConflict);
        handler.ReferenceWrite.Should().BeNull();
    }

    [Fact]
    public async Task Persist_ShouldSurfaceServerCasConflict()
    {
        var handler = new DownstreamHandler { ConflictOnReferenceWrite = true };
        var steps = Create(handler);
        var request = Request();
        var target = await steps.ResolveTargetAsync(request);
        var publication = await steps.PublishVersionAsync(request, new([1, 2, 3], SkillName));
        publication = await steps.VerifyPublicationAsync(request, publication, Credential());
        var error = await FluentActions.Awaiting(() => steps.PersistReferenceAsync(request, target, publication))
            .Should().ThrowAsync<CatalogRecommendedSkillUpdateException>();
        error.Which.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.ReferenceConflict);
        handler.Revision.Should().Be(7);
    }

    private static CatalogRecommendedSkillUpdateRequest Request() => new()
    {
        OperationId = "eeeeeeee-5555-5555-5555-555555555555", AdministratorId = "admin-consumer", CatalogServiceId = CatalogId,
        SkillId = SkillId, ExpectedVersion = "1.0", NewVersion = "1.1",
    };

    private static OrnnCatalogRecommendedSkillUpdateSteps Create(DownstreamHandler handler)
    {
        var api = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" }, new HttpClient(handler));
        var options = new OrnnOptions { NyxIdSlug = "ornn" };
        var client = new OrnnSkillClient(options, api);
        var publishing = new OrnnSkillPublishingService(new OrnnSkillPublishValidationPipeline(), new OrnnSkillPackageBuilder(),
            new OrnnSkillPackageFormatValidator(options, api), client);
        return new(new PublisherCredentials(), new NyxIdCatalogSkillClient(api), new NyxIdRecommendedSkillGenerator(api),
            publishing, client);
    }

    private static CatalogRecommendedSkillUpdateExecutionCredential Credential() =>
        new("consumer");

    private sealed class PublisherCredentials : INyxIdClientCredentialsTokenSource
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult<string?>("publisher");
    }

    private sealed record Call(string Method, string Path, string? Token);

    private sealed class DownstreamHandler : HttpMessageHandler
    {
        public string PublisherUserId { get; init; } = "publisher-owner";
        public string LatestVersion { get; init; } = "1.0";
        public bool IsPrivate { get; set; }
        public bool InvalidPublicationReceipt { get; init; }
        public bool ConsumerVersionMismatch { get; init; }
        public bool ConsumerReadDenied { get; init; }
        public bool ConflictOnReferenceWrite { get; init; }
        public long Revision { get; set; } = 7;
        public int PublishCount { get; private set; }
        public string? ReferenceWrite { get; private set; }
        public List<Call> Requests { get; } = [];
        private string? _publicationDigest;
        private string? _references;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Authorization?.Parameter;
            Requests.Add(new(request.Method.Method, path, token));
            if (path == $"/api/v1/keys/{CatalogId}")
                return Json(new { resource_type = "catalog_service", id = CatalogId, catalog_service_id = CatalogId,
                    slug = "aevatar", catalog_service_slug = "aevatar", name = "Aevatar", description = "Agent execution API" });
            if (path == $"/api/v1/catalog-curation/services/{CatalogId}/openapi.json")
                return Raw("""{"openapi":"3.1.0","paths":{"/runs":{"get":{"operationId":"listRuns","responses":{"200":{"description":"Runs","content":{"application/json":{"schema":{"type":"array","items":{"type":"string"}}}}}}}}}}""");
            if (path.EndsWith("/skill-format/validate", StringComparison.Ordinal))
                return Json(new { data = new { valid = true, violations = Array.Empty<object>() } });
            if (path.EndsWith("/api/v1/me", StringComparison.Ordinal))
                return Json(new { data = new { userId = token == "consumer" ? "admin-consumer" : PublisherUserId,
                    permissions = new[] { "ornn:skill:read", "ornn:skill:update" } } });
            if (ConsumerReadDenied && token == "consumer" && path.Contains($"/skills/{SkillId}", StringComparison.Ordinal))
                return Raw("{\"detail\":\"forbidden\"}", HttpStatusCode.Forbidden);
            if (path.EndsWith("/versions", StringComparison.Ordinal))
                return Json(new { data = new { items = new[] { new { version = LatestVersion, skillHash = new string('a', 64) } } } });
            if (path.EndsWith("/permissions", StringComparison.Ordinal))
            {
                IsPrivate = false;
                return Json(new { data = new { skill = new { guid = SkillId, isPrivate = false } } });
            }
            if (path.EndsWith("/json", StringComparison.Ordinal))
                return Json(new { data = new { name = SkillName, version = ConsumerVersionMismatch ? "1.0" : "1.1",
                    files = new Dictionary<string, string> { ["SKILL.md"] = "Published complete skill body" } } });
            if (path == $"/api/v1/proxy/s/ornn/api/v1/skills/{SkillId}")
            {
                if (request.Method == HttpMethod.Put)
                {
                    PublishCount++;
                    _publicationDigest = Convert.ToHexStringLower(SHA256.HashData(await request.Content!.ReadAsByteArrayAsync(ct)));
                    return Json(new { data = new { guid = SkillId, version = "1.1", skillHash = InvalidPublicationReceipt ? "wrong" : _publicationDigest } });
                }
                return Json(new { data = new { guid = SkillId, name = SkillName, version = _publicationDigest is null ? LatestVersion : "1.1",
                    skillHash = _publicationDigest ?? new string('a', 64), createdBy = "publisher-owner", isPrivate = IsPrivate } });
            }
            if (path == $"/api/v1/catalog-curation/services/{CatalogId}/skills")
            {
                if (request.Method == HttpMethod.Put)
                {
                    ReferenceWrite = await request.Content!.ReadAsStringAsync(ct);
                    if (ConflictOnReferenceWrite)
                        return Raw("""{"detail":"Skills revision changed"}""", HttpStatusCode.Conflict);
                    using var body = JsonDocument.Parse(ReferenceWrite);
                    body.RootElement.GetProperty("base_revision").GetInt64().Should().Be(Revision);
                    _references = body.RootElement.GetProperty("recommended_skill_refs").GetRawText();
                    Revision++;
                }
                _references ??= JsonSerializer.Serialize(new[]
                {
                    new { source = "ornn", skill_id = SkillId, name = SkillName, version = "1.0", sha256 = new string('a', 64),
                        dependencies = new[] { new { source = "ornn", skill_id = OtherId, name = "shared-helper", version = "3.0", sha256 = new string('b', 64) } } },
                    new { source = "ornn", skill_id = OtherId, name = "other-guide", version = "3.0", sha256 = new string('c', 64),
                        dependencies = Array.Empty<object>().Select(_ => new { source = "", skill_id = "", name = "", version = "", sha256 = "" }).ToArray() },
                });
                return Raw($"{{\"service_id\":\"{CatalogId}\",\"skills_revision\":{Revision},\"recommended_skill_refs\":{_references},\"skills_manifest_digest\":\"v1:{new string('d', 64)}\"}}");
            }
            throw new InvalidOperationException($"Unexpected test route {request.Method} {path}");
        }

        private static HttpResponseMessage Json(object value) => Raw(JsonSerializer.Serialize(value));
        private static HttpResponseMessage Raw(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }
}
