using System.Net;
using Aevatar.AI.Abstractions.Skills;
using Aevatar.AI.Core.Skills;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class SkillServiceRecommendationTests
{
    private const string Detail = """
        {"data":{"guid":"guid+support","name":"support","description":"Use Slack for updates.",
        "nyxidServiceSlug":"api-github","nyxidServiceId":"catalog-not-a-user-service"}}
        """;
    private const string Package = """
        {"data":{"name":"support","files":{"SKILL.md":"Use Linear. gmail and mail-helper are different. TEST_ONLY_PRIVATE",
        "private.txt":"TEST_ONLY_SECRET"}}}
        """;
    private const string Catalog = """
        {"entries":[
          {"slug":"api-github","name":"GitHub"},
          {"slug":"slack","name":"Slack"},
          {"slug":"linear","name":"Linear"},
          {"slug":"drive","name":"Drive","recommended_skills":["support"]},
          {"slug":"mail","name":"Mail"},
          {"slug":"unused","name":"Unused","recommended_skills":["support-extra"]}
        ]}
        """;
    private const string Inventory = """
        {"services":[
          {"id":"us-personal","slug":"api-github","label":"Personal GitHub","is_active":true,
           "credential_source":{"type":"personal"},"api_key":"TEST_ONLY_SECRET"},
          {"id":"us-org","slug":"api-github","label":"Team GitHub","is_active":false,
           "credential_source":{"type":"org","org_id":"org-1","org_name":"Acme","role":"viewer","allowed":false}}
        ]}
        """;

    [Fact]
    public async Task RegisteredService_DiscoversEvidenceAndExactInstances_WithoutLeakingInputsOrMutatingGrants()
    {
        var handler = Responses(Detail, Package, Catalog, Inventory);
        using var http = new HttpClient(handler);
        var nyx = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" }, http);
        var services = new ServiceCollection();
        services.AddSingleton(nyx);
        services.AddSingleton<ILogger<OrnnSkillServiceDiscoverySource>>(NullLogger<OrnnSkillServiceDiscoverySource>.Instance);
        services.AddOrnnSkillClient();
        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ISkillServiceRecommendationService>();

        var result = await service.RecommendAsync("caller-token", "support");

        result.SkillName.Should().Be("support");
        result.Suggestions.Select(item => (item.Slug, item.Evidence)).Should().Equal(
            ("api-github", SkillServiceEvidence.Linked),
            ("slack", SkillServiceEvidence.Mention),
            ("linear", SkillServiceEvidence.Mention),
            ("drive", SkillServiceEvidence.Catalog));
        var instances = result.Suggestions[0].Instances;
        instances.Select(item => item.UserServiceId).Should().Equal("us-personal", "us-org");
        instances[0].CredentialSource.Should().Be(SkillServiceCredentialSource.Personal);
        instances[1].CredentialSource.Should().Be(SkillServiceCredentialSource.Organization);
        instances[1].Active.Should().BeFalse();
        instances[1].AccountAccessAllowed.Should().BeFalse();
        result.Suggestions[1].Instances.Should().BeEmpty();
        result.ToString().Should().NotContain("TEST_ONLY").And.NotContain("catalog-not-a-user-service");
        handler.Requests.Should().OnlyContain(request =>
            request.Method == HttpMethod.Get && request.Authorization!.Parameter == "caller-token");
        handler.Requests.Select(request => request.RequestUri!.PathAndQuery).Should().Equal(
            "/api/v1/proxy/s/ornn-api/api/v1/skills/support",
            "/api/v1/proxy/s/ornn-api/api/v1/skills/guid%2Bsupport/json",
            "/api/v1/catalog?include_all=true",
            "/api/v1/user-services");
    }

    [Theory]
    [InlineData("""{"data":{"name":"another-skill","files":{"SKILL.md":"Slack"}}}""")]
    [InlineData("""{"data":{"name":"support","files":{}}}""")]
    public async Task Discovery_RejectsMismatchedOrMissingSkillContents(string package)
    {
        var handler = Responses(Detail, package);
        var service = Create(handler);
        var action = () => service.RecommendAsync("caller-token", "support");
        await action.Should().ThrowAsync<SkillServiceDiscoveryException>()
            .WithMessage("Skill service discovery is unavailable.");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Discovery_FailsClosedForUnavailableCatalog_AndDoesNotReturnPartialSuccess()
    {
        var handler = Responses(Detail, Package, """{"error":true,"status":403,"body":"TEST_ONLY_SECRET"}""");
        var action = () => Create(handler).RecommendAsync("caller-token", "support");
        await action.Should().ThrowAsync<SkillServiceDiscoveryException>()
            .WithMessage("Skill service discovery is unavailable.");
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Discovery_PropagatesCancellation_AndDoesNotContinueReading()
    {
        var handler = OrnnTestHttpMessageHandler.HangingUntilCanceled();
        using var cancellation = new CancellationTokenSource();
        var task = Create(handler).RecommendAsync("caller-token", "support", cancellation.Token);
        await handler.RequestStarted;
        await cancellation.CancelAsync();
        var action = async () => await task;
        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Discovery_IsolatesCallers_AndRefreshesExternalFactsWithoutSharedState()
    {
        var handler = Responses(Detail, Package, Catalog, Inventory,
            Detail, Package, """{"entries":[]}""", """{"services":[]}""");
        var service = Create(handler);
        var first = await service.RecommendAsync("first-caller", "support");
        var second = await service.RecommendAsync("second-caller", "support");
        first.Suggestions[0].Instances.Should().HaveCount(2);
        second.Suggestions.Should().ContainSingle().Which.Instances.Should().BeEmpty();
        handler.Requests.Skip(4).Should().OnlyContain(request =>
            request.Authorization!.Parameter == "second-caller");
    }

    private static OrnnTestHttpMessageHandler Responses(params string[] responses) =>
        new(responses.Select<string, Func<HttpRequestMessage, HttpResponseMessage>>(
            response => _ => OrnnTestHttpMessageHandler.JsonResponse(response, HttpStatusCode.OK)).ToArray());

    private static SkillServiceRecommendationService Create(OrnnTestHttpMessageHandler handler)
    {
        var nyx = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" }, new HttpClient(handler));
        var options = new OrnnOptions();
        return new SkillServiceRecommendationService(new OrnnSkillServiceDiscoverySource(
            new OrnnSkillClient(options, nyx), nyx, options,
            NullLogger<OrnnSkillServiceDiscoverySource>.Instance));
    }
}
