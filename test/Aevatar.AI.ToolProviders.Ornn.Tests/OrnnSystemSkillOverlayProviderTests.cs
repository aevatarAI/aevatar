using System.Net;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn.SystemSkillOverlay;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnSystemSkillOverlayProviderTests
{
    private const string GlobalBody = "GLOBAL OVERLAY BODY";
    private const string LarkBody = "LARK OVERLAY BODY";

    [Fact]
    public async Task GetCurrent_InjectsGlobalAlwaysAndPlatformScopedOnlyWhenPlatformMatches()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => Json(SetJson("set-guid-1", "m-global", "m-lark")),
            _ => Json(MemberJson("aevatar-skill-loading", "overlay-scope-global", GlobalBody)),
            _ => Json(MemberJson("aevatar-lark-provisioning", "overlay-scope-lark", LarkBody)));
        var provider = CreateProvider(CreateClient(handler));

        await provider.RefreshAsync("token");

        var lark = provider.GetCurrent(new SystemSkillOverlayRequest("lark", null))!.Content;
        lark.Should().Contain(GlobalBody).And.Contain(LarkBody);

        var telegram = provider.GetCurrent(new SystemSkillOverlayRequest("telegram", null))!.Content;
        telegram.Should().Contain(GlobalBody);
        telegram.Should().NotContain(LarkBody);

        var dm = provider.GetCurrent(new SystemSkillOverlayRequest("dm", null))!.Content;
        dm.Should().Contain(GlobalBody);
        dm.Should().NotContain(LarkBody);
        provider.GetCurrent(new SystemSkillOverlayRequest("dm", null))!.Provenance.SourceWatermark
            .Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetCurrent_ReturnsNull_WhenSetUnreachableAndNoLastKnownGoodExists()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""{ "error": "boom" }""", HttpStatusCode.InternalServerError);
        var provider = CreateProvider(CreateClient(handler));

        await provider.RefreshAsync("token");

        provider.GetCurrent(new SystemSkillOverlayRequest("lark", null)).Should().BeNull();
    }

    [Fact]
    public async Task GetCurrent_ReturnsNullForEmptyVariant_WhenOnlyOtherPlatformScopedMembersExist()
    {
        // The global provider owns only its optional slot; the separate built-in floor remains present.
        var handler = new OrnnTestHttpMessageHandler(
            _ => Json(SetJson("set-guid-1", "m-lark")),
            _ => Json(MemberJson("aevatar-lark-provisioning", "overlay-scope-lark", LarkBody)));
        var provider = CreateProvider(CreateClient(handler));

        await provider.RefreshAsync("token");

        provider.GetCurrent(new SystemSkillOverlayRequest("dm", null)).Should().BeNull();
        provider.GetCurrent(new SystemSkillOverlayRequest("telegram", null)).Should().BeNull();
        // The lark turn still gets its scoped member.
        provider.GetCurrent(new SystemSkillOverlayRequest("lark", null))!
            .Content.Should().Contain(LarkBody);
    }

    [Fact]
    public async Task GetCurrent_ReturnsNull_WhenNoMemberHasOverlayScopeTag()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => Json(SetJson("set-guid-1", "m-untagged")),
            _ => Json(MemberJson("some-skill", "not-an-overlay-scope", "SOME BODY")));
        var provider = CreateProvider(CreateClient(handler));

        await provider.RefreshAsync("token");

        provider.GetCurrent(new SystemSkillOverlayRequest("dm", null)).Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_ResolvesSetByNameThenPinsGuidAgainstSquatting()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => Json(SetJson("set-guid-1", "m-global")),
            _ => Json(MemberJson("aevatar-skill-loading", "overlay-scope-global", GlobalBody)),
            _ => Json(SetJson("set-guid-1", "m-global")),
            _ => Json(MemberJson("aevatar-skill-loading", "overlay-scope-global", GlobalBody)));
        var provider = CreateProvider(CreateClient(handler));

        await provider.RefreshAsync("token");
        await provider.RefreshAsync("token");

        var setRequests = handler.Requests
            .Where(request => request.RequestUri!.AbsolutePath.Contains("/skillsets/", StringComparison.Ordinal))
            .ToList();
        setRequests.Should().HaveCount(2);
        setRequests[0].RequestUri!.AbsoluteUri.Should().Contain("/skillsets/aevatar-system");
        setRequests[1].RequestUri!.AbsoluteUri.Should().Contain("/skillsets/set-guid-1");
    }

    [Fact]
    public async Task RefreshAsync_AcceptsStringMembersAndFetchesByNameWithoutVersion()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => Json("""{ "data": { "guid": "sg", "name": "aevatar-system", "members": ["aevatar-skill-loading@1.1"] } }"""),
            _ => Json(MemberJson("aevatar-skill-loading", "overlay-scope-global", GlobalBody)));
        var provider = CreateProvider(CreateClient(handler));

        await provider.RefreshAsync("token");

        provider.GetCurrent(new SystemSkillOverlayRequest("dm", null))!.Content.Should().Contain(GlobalBody);
        handler.Requests
            .Single(request => request.RequestUri!.AbsolutePath.Contains("/skills/", StringComparison.Ordinal))
            .RequestUri!.AbsoluteUri.Should().Contain("/skills/aevatar-skill-loading/json");
    }

    [Fact]
    public async Task RefreshAsync_SkipsUnrecognizedMemberShapesInsteadOfFailingTheSnapshot()
    {
        // The member converter yields null for JSON shapes that are neither a string nor an object
        // (e.g. a bare number); the snapshot build must skip those and keep the valid members.
        var handler = new OrnnTestHttpMessageHandler(
            _ => Json("""{ "data": { "guid": "sg", "name": "aevatar-system", "members": [ 123, { "guid": "m-global" } ] } }"""),
            _ => Json(MemberJson("aevatar-skill-loading", "overlay-scope-global", GlobalBody)));
        var provider = CreateProvider(CreateClient(handler));

        await provider.RefreshAsync("token");

        provider.GetCurrent(new SystemSkillOverlayRequest("dm", null))!.Content.Should().Contain(GlobalBody);
    }

    [Fact]
    public async Task RefreshAsync_DoesNotLeakTheAccessTokenIntoTheOverlayOrWatermark()
    {
        const string token = "super-secret-nyxid-token-xyz";
        var handler = new OrnnTestHttpMessageHandler(
            _ => Json(SetJson("set-guid-1", "m-global")),
            _ => Json(MemberJson("aevatar-skill-loading", "overlay-scope-global", GlobalBody)));
        var provider = CreateProvider(CreateClient(handler));

        await provider.RefreshAsync(token);

        var overlay = provider.GetCurrent(new SystemSkillOverlayRequest("dm", null))!;
        overlay.Content.Should().NotContain(token);
        overlay.Provenance.SourceWatermark.Should().NotContain(token);
        // The token is still what authenticated the proxy call — proving it is used, just never stored.
        handler.Requests.Should().Contain(request => request.Authorization!.Parameter == token);
    }

    [Fact]
    public async Task RefreshAsync_WatermarkIsDeterministicForSameContent_AndChangesWhenContentChanges()
    {
        // The watermark is the provenance handle for the injected set: identical member content must
        // hash identically across independent refreshes, and a body change must produce a new hash
        // (otherwise refresh silently skips the swap and serves stale content forever).
        static OrnnTestHttpMessageHandler HandlerFor(string body) => new(
            _ => Json(SetJson("set-guid-1", "m-global")),
            _ => Json(MemberJson("aevatar-skill-loading", "overlay-scope-global", body)));

        var first = CreateProvider(CreateClient(HandlerFor(GlobalBody)));
        var second = CreateProvider(CreateClient(HandlerFor(GlobalBody)));
        var changed = CreateProvider(CreateClient(HandlerFor(GlobalBody + " V2")));
        await first.RefreshAsync("token");
        await second.RefreshAsync("token");
        await changed.RefreshAsync("token");

        var firstWatermark = first.GetCurrent(new SystemSkillOverlayRequest("dm", null))!.Provenance.SourceWatermark;
        var secondWatermark = second.GetCurrent(new SystemSkillOverlayRequest("dm", null))!.Provenance.SourceWatermark;
        var changedWatermark = changed.GetCurrent(new SystemSkillOverlayRequest("dm", null))!.Provenance.SourceWatermark;

        firstWatermark.Should().NotBeNullOrEmpty();
        secondWatermark.Should().Be(firstWatermark);
        changedWatermark.Should().NotBe(firstWatermark);
    }

    [Fact]
    public async Task RefreshAsync_KeepsLastKnownGoodGlobalLayer_WhenLaterRefreshFails()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => Json(SetJson("set-guid-1", "m-global")),
            _ => Json(MemberJson("aevatar-skill-loading", "overlay-scope-global", GlobalBody)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(
                """{ "error": "boom" }""",
                HttpStatusCode.InternalServerError));
        var provider = CreateProvider(CreateClient(handler));

        await provider.RefreshAsync("token");
        var beforeFailure = provider.GetCurrent(new SystemSkillOverlayRequest("dm", null));
        await provider.RefreshAsync("token");
        var afterFailure = provider.GetCurrent(new SystemSkillOverlayRequest("dm", null));

        afterFailure.Should().BeSameAs(beforeFailure);
        afterFailure!.Content.Should().Contain(GlobalBody);
    }

    [Fact]
    public async Task RefreshAsync_OverBudgetMember_DegradesToCatalogLineWithinMaxBytes()
    {
        var bigBody = new string('x', 8 * 1024);
        var handler = new OrnnTestHttpMessageHandler(
            _ => Json(SetJson("set-guid-1", "m-global")),
            _ => Json(MemberJson("aevatar-skill-loading", "overlay-scope-global", bigBody)));
        const int maxBytes = 512;
        var provider = new OrnnSystemSkillOverlayProvider(
            new SystemSkillOverlayOptions
            {
                Enabled = true,
                SetName = "aevatar-system",
                MaxSkills = 32,
                MaxBytes = maxBytes,
            },
            CreateClient(handler));

        await provider.RefreshAsync("token");

        var layer = provider.GetCurrent(new SystemSkillOverlayRequest("dm", null))!;
        var markdown = layer.Content;
        System.Text.Encoding.UTF8.GetByteCount(markdown).Should().BeLessThanOrEqualTo(maxBytes);
        layer.Bounds.Should().Be(new PromptLayerBounds(maxBytes, (maxBytes + 3) / 4));
        markdown.Should().NotContain(bigBody);
        markdown.Should().Contain("- aevatar-skill-loading:", "the over-budget member must degrade to a catalog line");
    }

    [Fact]
    public void GetCurrent_WithNoTokenAndNoSnapshot_ReturnsNullWithoutFetching()
    {
        var handler = new OrnnTestHttpMessageHandler();
        var provider = CreateProvider(CreateClient(handler));

        var overlay = provider.GetCurrent(new SystemSkillOverlayRequest("lark", null));

        overlay.Should().BeNull();
        handler.Requests.Should().BeEmpty();
    }

    private static string SetJson(string guid, params string[] memberGuids)
    {
        var members = string.Join(",", memberGuids.Select(guid => $$"""{ "guid": "{{guid}}" }"""));
        return $$"""{ "data": { "guid": "{{guid}}", "name": "aevatar-system", "members": [ {{members}} ] } }""";
    }

    private static string MemberJson(string name, string scopeTag, string body) =>
        $$"""
        { "data": { "name": "{{name}}", "description": "{{name}} description",
          "metadata": { "category": "plain", "tag": ["{{scopeTag}}"] },
          "files": { "SKILL.md": "---\nname: {{name}}\nversion: 1.0\n---\n{{body}}" } } }
        """;

    private static HttpResponseMessage Json(string json) => OrnnTestHttpMessageHandler.JsonResponse(json);

    private static OrnnSkillClient CreateClient(OrnnTestHttpMessageHandler handler, string slug = "ornn-api")
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        return new OrnnSkillClient(new OrnnOptions { NyxIdSlug = slug }, nyxClient);
    }

    private static OrnnSystemSkillOverlayProvider CreateProvider(OrnnSkillClient client) =>
        new(
            new SystemSkillOverlayOptions
            {
                Enabled = true,
                SetName = "aevatar-system",
                MaxSkills = 32,
                MaxBytes = 32 * 1024,
            },
            client);
}
