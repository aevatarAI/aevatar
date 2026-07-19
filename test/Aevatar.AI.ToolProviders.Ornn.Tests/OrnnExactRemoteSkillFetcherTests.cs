using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnExactRemoteSkillFetcherTests
{
    private const string SkillGuid = "11111111-1111-1111-1111-111111111111";
    private const string LiteralVersion = "1.2";

    [Fact]
    public async Task FetchAsync_ShouldReadOnlyVersionPinnedGuidDetailAndJson()
    {
        var handler = SuccessHandler();
        var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchAsync("token", ExactRef());

        result.IsSuccess.Should().BeTrue();
        result.Should().BeEquivalentTo(ExactRemoteSkillFetchResult.Success(
            SkillGuid,
            LiteralVersion,
            "skill-alpha",
            "publisher-alpha",
            "hash-alpha",
            "# Skill Alpha\n\nInstructions."));
        handler.Requests.Select(request => request.RequestUri!.AbsoluteUri).Should().Equal(
            $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}?version=1.2",
            $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}/json?version=1.2");
        handler.Requests.Should().OnlyContain(request =>
            request.Method == HttpMethod.Get && request.Authorization!.Parameter == "token");
    }

    [Fact]
    public async Task FetchAsync_InvalidReferenceOrMissingToken_ShouldFailBeforeHttp()
    {
        var handler = SuccessHandler();
        var fetcher = CreateFetcher(handler);

        var missingToken = await fetcher.FetchAsync(" ", ExactRef());
        var invalidGuid = await fetcher.FetchAsync("token", new ExactRemoteSkillRef
        {
            Guid = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
            LiteralVersion = LiteralVersion,
        });
        var emptyGuid = await fetcher.FetchAsync("token", new ExactRemoteSkillRef
        {
            Guid = Guid.Empty.ToString("D"),
            LiteralVersion = LiteralVersion,
        });
        var invalidVersion = await fetcher.FetchAsync("token", new ExactRemoteSkillRef
        {
            Guid = SkillGuid,
            LiteralVersion = "latest",
        });

        missingToken.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.AccessTokenMissing);
        invalidGuid.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.InvalidReference);
        emptyGuid.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.InvalidReference);
        invalidVersion.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.InvalidReference);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_IdentityMismatch_ShouldFailWithoutFallback()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson()),
            _ => OrnnTestHttpMessageHandler.JsonResponse(SkillJson(version: "1.3")));
        var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchAsync("token", ExactRef());

        result.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.IdentityMismatch);
        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(request =>
            request.RequestUri!.Query == "?version=1.2" &&
            !request.RequestUri.AbsoluteUri.Contains("latest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FetchAsync_MissingPublisherOrHashEvidence_ShouldFailClosed()
    {
        var cases = new[]
        {
            DetailJson(publisher: ""),
            DetailJson(hash: ""),
        };

        foreach (var detailJson in cases)
        {
            var handler = new OrnnTestHttpMessageHandler(
                _ => OrnnTestHttpMessageHandler.JsonResponse(detailJson),
                _ => OrnnTestHttpMessageHandler.JsonResponse(SkillJson()));

            var result = await CreateFetcher(handler).FetchAsync("token", ExactRef());

            result.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.IntegrityEvidenceMissing);
            handler.Requests.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task FetchAsync_MissingOrDuplicateSkillMarkdown_ShouldFailWithoutAlternateRead()
    {
        var skillJsonCases = new[]
        {
            SkillJson(filesJson: "{\"README.md\":\"readme\"}"),
            SkillJson(filesJson: "{\"SKILL.md\":\"one\",\"skill.md\":\"two\"}"),
        };

        foreach (var skillJson in skillJsonCases)
        {
            var handler = new OrnnTestHttpMessageHandler(
                _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson()),
                _ => OrnnTestHttpMessageHandler.JsonResponse(skillJson));

            var result = await CreateFetcher(handler).FetchAsync("token", ExactRef());

            result.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.InvalidResponse);
            result.FailureDetail.Should().Be("unique_skill_markdown_required");
            handler.Requests.Should().HaveCount(2);
        }
    }

    private static OrnnTestHttpMessageHandler SuccessHandler() =>
        new(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson()),
            _ => OrnnTestHttpMessageHandler.JsonResponse(SkillJson()));

    private static OrnnExactRemoteSkillFetcher CreateFetcher(OrnnTestHttpMessageHandler handler)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        return new OrnnExactRemoteSkillFetcher(new OrnnSkillClient(
            new OrnnOptions { NyxIdSlug = "ornn" },
            nyxClient));
    }

    private static ExactRemoteSkillRef ExactRef() => new()
    {
        Guid = SkillGuid,
        LiteralVersion = LiteralVersion,
    };

    private static string DetailJson(
        string publisher = "publisher-alpha",
        string hash = "hash-alpha") =>
        "{\"data\":{\"guid\":\"" + SkillGuid +
        "\",\"name\":\"skill-alpha\",\"skillHash\":\"" + hash +
        "\",\"createdBy\":\"" + publisher + "\"}}";

    private static string SkillJson(
        string version = LiteralVersion,
        string filesJson = "{\"SKILL.md\":\"# Skill Alpha\\n\\nInstructions.\"}") =>
        "{\"data\":{\"name\":\"skill-alpha\",\"version\":\"" + version +
        "\",\"files\":" + filesJson + "}}";
}
