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

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Select(request => request.Path).Should().Equal(
            "/api/v1/proxy/s/ornn/api/v1/skill-format/validate",
            "/api/v1/proxy/s/ornn/api/v1/skills");
        handler.Requests.Select(request => request.Authorization?.Parameter).Should().OnlyContain(token => token == "server-token");
        handler.Requests[1].ContentType.Should().Be("application/zip");
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
            publishingService);
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
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken)));

            var response = request.RequestUri.AbsolutePath switch
            {
                "/api/v1/proxy/s/ornn/api/v1/skill-format/validate" => """{"data":{"valid":true,"violations":[]}}""",
                "/api/v1/proxy/s/ornn/api/v1/skills" => """{"data":{"guid":"33333333-3333-3333-3333-333333333333","version":"3.0","skillHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}""",
                _ => throw new InvalidOperationException("unexpected_route"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            };
        }
    }

    private sealed record CapturedRequest(
        string Path,
        AuthenticationHeaderValue? Authorization,
        string? ContentType,
        byte[] Body);
}
