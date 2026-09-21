using System.Net;
using System.Net.Http.Headers;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnRecommendedSkillRefCreatorTests
{
    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_MatchingTemplate_PublishesPrivateSkillWithServerToken()
    {
        var handler = new CapturingHandler();
        var creator = CreateCreator(handler);
        var instance = ReadyInstance();

        var refs = await creator.CreateRecommendedSkillRefsAsync(instance, CancellationToken.None);
        var refsAgain = await creator.CreateRecommendedSkillRefsAsync(instance, CancellationToken.None);

        refsAgain.Should().BeSameAs(refs);
        var skillRef = refs.Should().ContainSingle().Subject;
        skillRef.Source.Should().Be(NyxIdRecommendedSkillSource.Ornn);
        skillRef.SkillId.Should().Be("33333333-3333-3333-3333-333333333333");
        skillRef.LiteralVersion.Should().Be("3.0");
        skillRef.ManifestDigest.Should().Be(new string('a', 64));
        skillRef.DisplayName.Should().Be("GitHub Operator");
        skillRef.RecommendationName.Should().Be("github-service-default");

        handler.Requests.Should().HaveCount(4);
        handler.Requests.Select(request => request.Path).Should().Equal(
            "/api/v1/proxy/s/ornn/api/v1/skill-format/validate",
            "/api/v1/proxy/s/ornn/api/v1/skills",
            "/api/v1/keys/us-personal",
            "/api/v1/keys/us-personal");
        handler.Requests.Select(request => request.Authorization?.Parameter).Should().OnlyContain(token => token == "server-token");
        handler.Requests[1].ContentType.Should().Be("application/zip");
        handler.Requests[3].Method.Should().Be(HttpMethod.Put);
        handler.Requests[3].BodyText.Should().Contain("recommended_skill_refs");
        handler.Requests[3].BodyText.Should().Contain("33333333-3333-3333-3333-333333333333");
    }

    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_WhenNyxIdUpdateFails_ReturnsEmptyRefList()
    {
        var handler = new CapturingHandler { FailUpdate = true };
        var creator = CreateCreator(handler);

        var refs = await creator.CreateRecommendedSkillRefsAsync(ReadyInstance(), CancellationToken.None);
        var refsAgain = await creator.CreateRecommendedSkillRefsAsync(ReadyInstance(), CancellationToken.None);

        refs.Should().BeEmpty();
        refsAgain.Should().BeEmpty();
        handler.Requests.Should().HaveCount(8);
        handler.Requests.Where(request => request.Method == HttpMethod.Put)
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateRecommendedSkillRefsAsync_UnmatchedService_ReturnsEmptyWithoutPublishing()
    {
        var handler = new CapturingHandler();
        var creator = CreateCreator(handler);
        var instance = ReadyInstance();
        instance.CatalogServiceSlug = "api-calendar";

        var refs = await creator.CreateRecommendedSkillRefsAsync(instance, CancellationToken.None);

        refs.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    private static OrnnRecommendedSkillRefCreator CreateCreator(CapturingHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        options.RecommendedSkillCreationTemplates.Add(new NyxIdRecommendedSkillCreationTemplate
        {
            CatalogServiceSlug = "api-github",
            SkillName = "github-service-default",
            Description = "GitHub service default skill",
            Version = "3.0",
            Category = "tool-based",
            InstructionsMarkdown = "Use exact GitHub service tools.",
            DisplayName = "GitHub Operator",
            RecommendationName = "github-service-default",
            Tags = ["github"],
            ToolList = ["nyxop_list_repositories"],
        });
        var nyxClient = new NyxIdApiClient(options, new HttpClient(handler));
        var ornnOptions = new OrnnOptions { NyxIdSlug = "ornn" };
        var skillClient = new OrnnSkillClient(ornnOptions, nyxClient);
        var publishingService = new OrnnSkillPublishingService(
            new OrnnSkillPublishValidationPipeline(),
            new OrnnSkillPackageBuilder(),
            new OrnnSkillPackageFormatValidator(ornnOptions, nyxClient),
            skillClient);
        return new OrnnRecommendedSkillRefCreator(
            options,
            new StaticTokenSource("server-token"),
            publishingService,
            new NyxIdRecommendedSkillRefPersistenceService(nyxClient));
    }

    private static NyxIdServiceInstance ReadyInstance() => new()
    {
        UserServiceId = "us-personal",
        DisplaySlug = "github",
        CatalogServiceSlug = "api-github",
        Label = "GitHub",
        EndpointUrl = "https://api.github.test",
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
        public bool FailUpdate { get; init; }

        public List<CapturedRequest> Requests { get; } = [];

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

            var response = (request.Method.Method, request.RequestUri.AbsolutePath) switch
            {
                ("POST", "/api/v1/proxy/s/ornn/api/v1/skill-format/validate") => new CapturingResponse("""{"data":{"valid":true,"violations":[]}}"""),
                ("POST", "/api/v1/proxy/s/ornn/api/v1/skills") => new CapturingResponse("""{"data":{"guid":"33333333-3333-3333-3333-333333333333","version":"3.0","skillHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}"""),
                ("GET", "/api/v1/keys/us-personal") => new CapturingResponse("""
                    {"key":{"id":"us-personal","slug":"github","catalog_service_id":"catalog-github",
                    "catalog_service_slug":"api-github","is_active":true,"connected":true,"status":"active",
                    "credential_source":{"type":"personal"},"recommended_skill_refs":[]}}
                    """),
                ("PUT", "/api/v1/keys/us-personal") when FailUpdate => new CapturingResponse("""{"code":"permission_denied"}""", HttpStatusCode.Forbidden),
                ("PUT", "/api/v1/keys/us-personal") => new CapturingResponse("""{"key":{"id":"us-personal"}}"""),
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
