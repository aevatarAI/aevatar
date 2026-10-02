using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnRecommendedSkillRefCreatorTests
{
    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_UsesCallerCredentialForDocumentAndServerCredentialForPublication()
    {
        var handler = new CapturingHandler();
        var creator = CreateCreator(handler);

        var result = await creator.CreateRecommendedSkillRefsAsync(ReadyInstance(), "caller-document-token", CancellationToken.None);

        result.PersistenceStatus.Should().Be(NyxIdRecommendedSkillRefPersistenceStatus.Succeeded);
        handler.Requests.Should().ContainSingle(request => request.Path == "/api/v1/proxy/s/github/openapi.json")
            .Which.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "caller-document-token"));
        handler.Requests.Where(request => request.Path != "/api/v1/proxy/s/github/openapi.json")
            .Select(request => request.Authorization?.Parameter)
            .Should().OnlyContain(token => token == "server-token");
        handler.Requests.Should().Contain(request =>
            request.Method == HttpMethod.Post && request.Path == "/api/v1/proxy/s/ornn/api/v1/skills");
        handler.Requests.Should().Contain(request =>
            request.Method == HttpMethod.Put && request.Path.EndsWith("/permissions", StringComparison.Ordinal));
        handler.Requests.Should().Contain(request =>
            request.Method == HttpMethod.Put && request.Path == "/api/v1/keys/catalog-github");
    }

    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_MatchingTemplate_PublishesPublicSkillWithServerToken()
    {
        var handler = new CapturingHandler();
        var creator = CreateCreator(handler);
        var instance = ReadyInstance();

        var result = await creator.CreateRecommendedSkillRefsAsync(instance, "caller-document-token", CancellationToken.None);
        var resultAgain = await creator.CreateRecommendedSkillRefsAsync(instance, "caller-document-token", CancellationToken.None);

        resultAgain.Refs.Should().BeEquivalentTo(result.Refs);
        result.PersistenceStatus.Should().Be(NyxIdRecommendedSkillRefPersistenceStatus.Succeeded);
        var skillRef = result.Refs.Should().ContainSingle().Subject;
        skillRef.Source.Should().Be(NyxIdRecommendedSkillSource.Ornn);
        skillRef.SkillId.Should().Be("33333333-3333-3333-3333-333333333333");
        skillRef.LiteralVersion.Should().Be("1.0");
        skillRef.ManifestDigest.Should().Be(new string('a', 64));
        skillRef.DisplayName.Should().Be("GitHub");
        skillRef.RecommendationName.Should().Be("api-github-connected-service");

        handler.Requests.Should().HaveCount(8);
        handler.Requests.Select(request => request.Path).Should().Equal(
            "/api/v1/proxy/s/github/openapi.json",
            "/api/v1/proxy/s/ornn/api/v1/skill-format/validate",
            "/api/v1/proxy/s/ornn/api/v1/skills",
            "/api/v1/proxy/s/ornn/api/v1/skills/33333333-3333-3333-3333-333333333333/permissions",
            "/api/v1/keys/catalog-github",
            "/api/v1/keys/catalog-github",
            "/api/v1/proxy/s/github/openapi.json",
            "/api/v1/keys/catalog-github");
        handler.Requests.Where(request => request.Path == "/api/v1/proxy/s/ornn/api/v1/skills")
            .Should().ContainSingle();
        handler.Requests.Where(request => request.Path.EndsWith("/permissions", StringComparison.Ordinal))
            .Should().ContainSingle()
            .Which.BodyText.Should().Contain("\"isPrivate\":false");
        handler.Requests.Where(request => request.Method == HttpMethod.Get && request.Path == "/api/v1/keys/catalog-github")
            .Should().HaveCount(2);
        handler.Requests.Where(request => request.Method == HttpMethod.Put && request.Path == "/api/v1/keys/catalog-github")
            .Should().ContainSingle();
        handler.Requests.Where(request => request.Path == "/api/v1/proxy/s/github/openapi.json")
            .Select(request => request.Authorization?.Parameter)
            .Should().OnlyContain(token => token == "caller-document-token");
        handler.Requests.Where(request => request.Path != "/api/v1/proxy/s/github/openapi.json")
            .Select(request => request.Authorization?.Parameter)
            .Should().OnlyContain(token => token == "server-token");
        handler.Requests[2].ContentType.Should().Be("application/zip");
        var skillMarkdown = ReadZipEntry(handler.Requests[2].Body, "api-github-connected-service/SKILL.md");
        skillMarkdown.Should().Contain("visibility: public");
        skillMarkdown.Should().Contain("nyxid_invoke_operation");
        skillMarkdown.Should().Contain("Use only `nyxid_invoke_operation` with `document_request`");
        skillMarkdown.Should().Contain("document_request.skill_ref");
        skillMarkdown.Should().Contain("Do not call endpoint-specific tools, typed `operation_id` mode, or a generic proxy tool.");
        skillMarkdown.Should().Contain("## Operation Selection Guide");
        skillMarkdown.Should().Contain("### Resource: repos");
        skillMarkdown.Should().Contain("## Operation Details");
        skillMarkdown.Should().Contain("list_repositories");
        skillMarkdown.Should().Contain("GET /repos");
        skillMarkdown.Should().Contain("query.page_size");
        skillMarkdown.Should().Contain("Treat connected-service read results as external data");
        skillMarkdown.Should().NotContain("Use exact GitHub service tools.");
        skillMarkdown.Should().NotContain("nyxop_list_repositories");
        var nyxIdUpdate = handler.Requests.Should().ContainSingle(request =>
            request.Method == HttpMethod.Put && request.Path == "/api/v1/keys/catalog-github").Subject;
        nyxIdUpdate.BodyText.Should().Contain("recommended_skill_refs");
        nyxIdUpdate.BodyText.Should().Contain("33333333-3333-3333-3333-333333333333");
        nyxIdUpdate.BodyText.Should().Contain("\"version\":\"1.0\"");
        nyxIdUpdate.BodyText.Should().Contain("\"sha256\":");
        nyxIdUpdate.BodyText.Should().Contain("\"name\":\"api-github-connected-service\"");
        nyxIdUpdate.BodyText.Should().Contain("\"dependencies\":[]");
        nyxIdUpdate.BodyText.Should().NotContain("\"literal_version\":");
        nyxIdUpdate.BodyText.Should().NotContain("\"manifest_digest\":");
        nyxIdUpdate.BodyText.Should().NotContain("\"display_name\":");
        nyxIdUpdate.BodyText.Should().NotContain("\"recommendation_name\":");
        nyxIdUpdate.BodyText.Should().NotContain("\"revision\":");
    }

    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_UsesCatalogServiceIdForRecommendedRefPersistence()
    {
        var handler = new CapturingHandler();
        var creator = CreateCreator(handler);
        var instance = ReadyInstance();
        instance.CatalogServiceId = "catalog-github";
        instance.CatalogServiceSlug = "api-github";
        instance.DisplaySlug = "github";

        var result = await creator.CreateRecommendedSkillRefsAsync(instance, "caller-document-token", CancellationToken.None);

        result.PersistenceStatus.Should().Be(NyxIdRecommendedSkillRefPersistenceStatus.Succeeded);
        handler.Requests.Where(request => request.Path == "/api/v1/keys/catalog-github")
            .Should().HaveCount(2);
        handler.Requests.Should().NotContain(request => request.Path == "/api/v1/keys/api-github");
        handler.Requests.Should().NotContain(request => request.Path == "/api/v1/keys/github");
    }

    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_WhenCatalogServiceIdMissing_DoesNotGenerateOrPersist()
    {
        var handler = new CapturingHandler();
        var creator = CreateCreator(handler);
        var instance = ReadyInstance();
        instance.CatalogServiceId = string.Empty;

        var result = await creator.CreateRecommendedSkillRefsAsync(instance, "caller-document-token", CancellationToken.None);

        result.Refs.Should().BeEmpty();
        result.CreatedSkills.Should().BeEmpty();
        result.PersistenceStatus.Should().Be(NyxIdRecommendedSkillRefPersistenceStatus.WriteUnavailable);
        result.PersistenceFailureCode.Should().Be("catalog_service_id_missing");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_WhenNyxIdUpdateValidationFails_ReturnsValidationDetail()
    {
        var handler = new CapturingHandler { FailUpdateValidation = true };
        var creator = CreateCreator(handler);

        var result = await creator.CreateRecommendedSkillRefsAsync(ReadyInstance(), "caller-document-token", CancellationToken.None);

        result.Refs.Should().ContainSingle();
        result.PersistenceStatus.Should().Be(NyxIdRecommendedSkillRefPersistenceStatus.WriteUnavailable);
        result.PersistenceFailureCode.Should().Contain("http_422");
        result.PersistenceFailureCode.Should().Contain("recommended_skill_refs");
    }

    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_WhenPublishConflicts_ReusesExistingSkill()
    {
        var handler = new CapturingHandler { ConflictOnPublish = true };
        var creator = CreateCreator(handler);

        var result = await creator.CreateRecommendedSkillRefsAsync(ReadyInstance(), "caller-document-token", CancellationToken.None);

        result.PersistenceStatus.Should().Be(NyxIdRecommendedSkillRefPersistenceStatus.Succeeded);
        var skillRef = result.Refs.Should().ContainSingle().Subject;
        skillRef.Source.Should().Be(NyxIdRecommendedSkillSource.Ornn);
        skillRef.SkillId.Should().Be("44444444-4444-4444-4444-444444444444");
        skillRef.LiteralVersion.Should().Be("1.0");
        skillRef.ManifestDigest.Should().Be(new string('b', 64));
        handler.Requests.Select(request => request.Path).Should().Equal(
            "/api/v1/proxy/s/github/openapi.json",
            "/api/v1/proxy/s/ornn/api/v1/skill-format/validate",
            "/api/v1/proxy/s/ornn/api/v1/skills",
            "/api/v1/proxy/s/ornn/api/v1/skill-search",
            "/api/v1/proxy/s/ornn/api/v1/skills/44444444-4444-4444-4444-444444444444",
            "/api/v1/proxy/s/ornn/api/v1/skills/44444444-4444-4444-4444-444444444444/permissions",
            "/api/v1/keys/catalog-github",
            "/api/v1/keys/catalog-github");
        handler.Requests.Where(request => request.Method == HttpMethod.Put && request.Path == "/api/v1/keys/catalog-github")
            .Should().ContainSingle();
        handler.Requests.Last().BodyText.Should().Contain("44444444-4444-4444-4444-444444444444");
    }

    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_WhenExistingRecommendationRefIsStale_ReplacesIt()
    {
        var handler = new CapturingHandler();
        handler.SetRecommendedSkillRefs(
            """
            [{"source":"ornn","skill_id":"22222222-2222-2222-2222-222222222222","name":"api-github-connected-service","version":"1.0","sha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","dependencies":[]}]
            """);
        var creator = CreateCreator(handler);

        var result = await creator.CreateRecommendedSkillRefsAsync(ReadyInstance(), "caller-document-token", CancellationToken.None);

        result.PersistenceStatus.Should().Be(NyxIdRecommendedSkillRefPersistenceStatus.Succeeded);
        var update = handler.Requests.Should().ContainSingle(request =>
            request.Method == HttpMethod.Put && request.Path == "/api/v1/keys/catalog-github").Subject;
        update.BodyText.Should().Contain("33333333-3333-3333-3333-333333333333");
        update.BodyText.Should().NotContain("22222222-2222-2222-2222-222222222222");
    }

    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_CacheHitWithNyxIdUpdateFailure_ReturnsCreatedRefsWithoutRepublishing()
    {
        var handler = new CapturingHandler();
        var creator = CreateCreator(handler);
        var instance = ReadyInstance();
        var result = await creator.CreateRecommendedSkillRefsAsync(instance, "caller-document-token", CancellationToken.None);
        handler.ClearRecommendedSkillRefs();
        handler.FailUpdate = true;

        var resultAgain = await creator.CreateRecommendedSkillRefsAsync(instance, "caller-document-token", CancellationToken.None);

        result.Refs.Should().ContainSingle();
        resultAgain.Refs.Should().ContainSingle();
        resultAgain.CreatedSkills.Should().ContainSingle()
            .Which.MainDocument.Should().Contain("nyxid_invoke_operation");
        resultAgain.PersistenceStatus.Should().Be(NyxIdRecommendedSkillRefPersistenceStatus.WriteDenied);
        handler.Requests.Where(request => request.Path == "/api/v1/proxy/s/ornn/api/v1/skills")
            .Should().ContainSingle();
        handler.Requests.Where(request => request.Method == HttpMethod.Get && request.Path == "/api/v1/keys/catalog-github")
            .Should().HaveCount(2);
        handler.Requests.Where(request => request.Method == HttpMethod.Put && request.Path == "/api/v1/keys/catalog-github")
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_WhenNyxIdReadFails_ReturnsCreatedRefsWithoutDirectWriteOrRepublishing()
    {
        var handler = new CapturingHandler { FailRead = true };
        var creator = CreateCreator(handler);

        var result = await creator.CreateRecommendedSkillRefsAsync(ReadyInstance(), "caller-document-token", CancellationToken.None);
        var resultAgain = await creator.CreateRecommendedSkillRefsAsync(ReadyInstance(), "caller-document-token", CancellationToken.None);

        result.Refs.Should().ContainSingle();
        result.CreatedSkills.Should().ContainSingle()
            .Which.MainDocument.Should().Contain("nyxid_invoke_operation");
        resultAgain.Refs.Should().ContainSingle();
        result.PersistenceStatus.Should().Be(NyxIdRecommendedSkillRefPersistenceStatus.ReadDenied);
        handler.Requests.Should().HaveCount(7);
        handler.Requests.Where(request => request.Path == "/api/v1/proxy/s/ornn/api/v1/skills")
            .Should().ContainSingle();
        handler.Requests.Where(request => request.Method == HttpMethod.Get && request.Path == "/api/v1/keys/catalog-github")
            .Should().HaveCount(2);
        handler.Requests.Where(request => request.Method == HttpMethod.Put && request.Path == "/api/v1/keys/catalog-github")
            .Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_WhenNyxIdUpdateFails_ReturnsCreatedRefsWithoutRepublishing()
    {
        var handler = new CapturingHandler { FailUpdate = true };
        var creator = CreateCreator(handler);

        var result = await creator.CreateRecommendedSkillRefsAsync(ReadyInstance(), "caller-document-token", CancellationToken.None);
        var resultAgain = await creator.CreateRecommendedSkillRefsAsync(ReadyInstance(), "caller-document-token", CancellationToken.None);

        result.Refs.Should().ContainSingle();
        result.CreatedSkills.Should().ContainSingle()
            .Which.MainDocument.Should().Contain("nyxid_invoke_operation");
        result.PersistenceStatus.Should().Be(NyxIdRecommendedSkillRefPersistenceStatus.WriteDenied);
        resultAgain.Refs.Should().ContainSingle();
        resultAgain.PersistenceStatus.Should().Be(NyxIdRecommendedSkillRefPersistenceStatus.WriteDenied);
        handler.Requests.Should().HaveCount(9);
        handler.Requests.Where(request => request.Path == "/api/v1/proxy/s/ornn/api/v1/skills")
            .Should().ContainSingle();
        handler.Requests.Where(request => request.Method == HttpMethod.Put)
            .Should().HaveCount(3);
    }

    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_WhenPublicPermissionUpdateFails_ReturnsWriteDeniedWithoutPersistingRef()
    {
        var handler = new CapturingHandler { FailPermissionUpdate = true };
        var creator = CreateCreator(handler);

        var result = await creator.CreateRecommendedSkillRefsAsync(ReadyInstance(), "caller-document-token", CancellationToken.None);

        result.Refs.Should().BeEmpty();
        result.PersistenceStatus.Should().Be(NyxIdRecommendedSkillRefPersistenceStatus.WriteDenied);
        result.PersistenceFailureCode.Should().Be("ornn_permission_update_forbidden");
        handler.Requests.Where(request => request.Path.EndsWith("/permissions", StringComparison.Ordinal))
            .Should().ContainSingle();
        handler.Requests.Where(request => request.Method == HttpMethod.Put && request.Path == "/api/v1/keys/catalog-github")
            .Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_WhenNoOperationContracts_ReturnsEmptyWithoutPublishing()
    {
        var handler = new CapturingHandler();
        var creator = CreateCreator(handler);
        var instance = ReadyInstance();
        instance.OpenapiSpecUrl = string.Empty;

        var result = await creator.CreateRecommendedSkillRefsAsync(instance, "caller-document-token", CancellationToken.None);

        result.Refs.Should().BeEmpty();
        handler.Requests.Where(request => request.Path == "/api/v1/proxy/s/ornn/api/v1/skills")
            .Should().BeEmpty();
    }

    private static OrnnRecommendedSkillRefCreator CreateCreator(CapturingHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var nyxClient = new NyxIdApiClient(options, new HttpClient(handler));
        var ornnOptions = new OrnnOptions { NyxIdSlug = "ornn" };
        var skillClient = new OrnnSkillClient(ornnOptions, nyxClient);
        var publishingService = new OrnnSkillPublishingService(
            new OrnnSkillPublishValidationPipeline(),
            new OrnnSkillPackageBuilder(),
            new OrnnSkillPackageFormatValidator(ornnOptions, nyxClient),
            skillClient);
        return new OrnnRecommendedSkillRefCreator(
            new StaticTokenSource("server-token"),
            publishingService,
            new NyxIdRecommendedSkillRefPersistenceService(nyxClient),
            new NyxIdRecommendedSkillGenerator(nyxClient));
    }

    private static string ReadZipEntry(byte[] zipBytes, string path)
    {
        using var stream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(path) ?? throw new InvalidOperationException("missing_zip_entry");
        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static NyxIdServiceInstance ReadyInstance() => new()
    {
        UserServiceId = "us-personal",
        DisplaySlug = "github",
        CatalogServiceId = "catalog-github",
        CatalogServiceSlug = "api-github",
        Label = "GitHub",
        EndpointUrl = "https://api.github.test",
        OpenapiSpecUrl = "https://api.github.test/openapi.json",
        IsActive = true,
        CredentialAllowed = true,
        CredentialSource = NyxIdServiceCredentialSource.Personal,
        AccessTokenSource = NyxIdServiceAccessTokenSource.User,
        CallerExecutionReadiness = new NyxIdServiceCallerExecutionReadiness
        {
            Connected = true,
            CredentialStatus = NyxIdServiceCredentialStatus.Active,
            NodeStatus = NyxIdServiceNodeStatus.NotBound,
        },
    };

    private sealed class StaticTokenSource(string token) : INyxIdClientCredentialsTokenSource
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult<string?>(token);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private const string OpenApiSpec =
            """
            {
              "openapi": "3.0.0",
              "paths": {
                "/repos": {
                  "get": {
                    "operationId": "list_repositories",
                    "summary": "List repositories",
                    "parameters": [
                      {
                        "name": "page_size",
                        "in": "query",
                        "required": false,
                        "description": "Maximum repositories to return.",
                        "schema": { "type": "integer" }
                      }
                    ],
                    "responses": {
                      "200": {
                        "content": {
                          "application/json": {
                            "schema": { "type": "object" }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        public bool FailRead { get; set; }

        public bool FailUpdate { get; set; }

        public bool FailUpdateValidation { get; set; }

        public bool ConflictOnPublish { get; set; }

        public bool FailPermissionUpdate { get; set; }

        private string _recommendedSkillRefsJson = "[]";

        public List<CapturedRequest> Requests { get; } = [];

        public void ClearRecommendedSkillRefs() => _recommendedSkillRefsJson = "[]";

        public void SetRecommendedSkillRefs(string recommendedSkillRefsJson) =>
            _recommendedSkillRefsJson = recommendedSkillRefsJson;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization,
                request.Content?.Headers.ContentType?.MediaType,
                body,
                System.Text.Encoding.UTF8.GetString(body)));

            if (request.Method == HttpMethod.Put &&
                request.RequestUri.AbsolutePath == "/api/v1/keys/catalog-github" &&
                !FailUpdate)
            {
                using var updateDocument = JsonDocument.Parse(body);
                _recommendedSkillRefsJson = updateDocument.RootElement
                    .GetProperty("recommended_skill_refs")
                    .GetRawText();
            }

            var response = (request.Method.Method, request.RequestUri.AbsolutePath) switch
            {
                ("GET", "/api/v1/proxy/s/github/openapi.json") => new CapturingResponse(OpenApiSpec),
                ("POST", "/api/v1/proxy/s/ornn/api/v1/skill-format/validate") => new CapturingResponse("""{"data":{"valid":true,"violations":[]}}"""),
                ("POST", "/api/v1/proxy/s/ornn/api/v1/skills") when ConflictOnPublish => new CapturingResponse("""{"error":{"code":"skill_conflict","message":"skill already exists"}}""", HttpStatusCode.Conflict),
                ("POST", "/api/v1/proxy/s/ornn/api/v1/skills") => new CapturingResponse("""{"data":{"guid":"33333333-3333-3333-3333-333333333333","version":"1.0","skillHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}"""),
                ("PUT", var path) when path.EndsWith("/permissions", StringComparison.Ordinal) && FailPermissionUpdate => new CapturingResponse("""{"error":{"code":"forbidden","message":"not allowed"}}""", HttpStatusCode.Forbidden),
                ("PUT", var path) when path.EndsWith("/permissions", StringComparison.Ordinal) => new CapturingResponse("""{"data":{"isPrivate":false}}"""),
                ("GET", "/api/v1/proxy/s/ornn/api/v1/skill-search") => new CapturingResponse("""{"data":{"total":1,"items":[{"guid":"44444444-4444-4444-4444-444444444444","name":"api-github-connected-service","description":"GitHub service default skill","isPrivate":false}]}}"""),
                ("GET", "/api/v1/proxy/s/ornn/api/v1/skills/44444444-4444-4444-4444-444444444444") => new CapturingResponse("""{"data":{"guid":"44444444-4444-4444-4444-444444444444","name":"api-github-connected-service","skillHash":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}"""),
                ("GET", "/api/v1/keys/catalog-github") when FailRead => new CapturingResponse("""{"code":"permission_denied"}""", HttpStatusCode.Forbidden),
                ("GET", "/api/v1/keys/catalog-github") => new CapturingResponse(
                    """
                    {"key":{"id":"us-personal","slug":"github","catalog_service_id":"catalog-github",
                    "catalog_service_slug":"api-github","is_active":true,"connected":true,"status":"active",
                    "credential_source":{"type":"personal"},"recommended_skill_refs":
                    """ + _recommendedSkillRefsJson + "}}"),
                ("PUT", "/api/v1/keys/catalog-github") when FailUpdate => new CapturingResponse("""{"code":"permission_denied"}""", HttpStatusCode.Forbidden),
                ("PUT", "/api/v1/keys/catalog-github") when FailUpdateValidation => new CapturingResponse("""{"detail":[{"loc":["body","recommended_skill_refs",0,"version"],"msg":"version is required"}]}""", HttpStatusCode.UnprocessableEntity),
                ("PUT", "/api/v1/keys/catalog-github") => new CapturingResponse("""{"key":{"id":"us-personal"}}"""),
                _ => throw new InvalidOperationException("unexpected_route"),
            };
            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body),
            };
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        AuthenticationHeaderValue? Authorization,
        string? ContentType,
        byte[] Body,
        string BodyText);

    private sealed record CapturingResponse(string Body, HttpStatusCode StatusCode = HttpStatusCode.OK);
}
