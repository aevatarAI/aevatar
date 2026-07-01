using System.Net;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn.SystemSkillOverlay;
using FluentAssertions;
using OverlayMessage = Aevatar.AI.Abstractions.SystemSkillOverlay;

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

        var lark = provider.GetCurrent(new SystemSkillOverlayRequest("lark", null))!.OverlayMarkdown;
        lark.Should().Contain(GlobalBody).And.Contain(LarkBody);

        var telegram = provider.GetCurrent(new SystemSkillOverlayRequest("telegram", null))!.OverlayMarkdown;
        telegram.Should().Contain(GlobalBody);
        telegram.Should().NotContain(LarkBody);

        var dm = provider.GetCurrent(new SystemSkillOverlayRequest("dm", null))!.OverlayMarkdown;
        dm.Should().Contain(GlobalBody);
        dm.Should().NotContain(LarkBody);
    }

    [Fact]
    public async Task GetCurrent_FallsBackToBuiltInDefault_WhenSetUnreachable()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""{ "error": "boom" }""", HttpStatusCode.InternalServerError);
        var provider = CreateProvider(CreateClient(handler), new StubFallback("BUILT-IN DEFAULT"));

        await provider.RefreshAsync("token");

        provider.GetCurrent(new SystemSkillOverlayRequest("lark", null))!
            .OverlayMarkdown.Should().Be("BUILT-IN DEFAULT");
    }

    [Fact]
    public async Task GetCurrent_FallsBackToBuiltInDefault_WhenOnlyOtherPlatformScopedMembersExist()
    {
        // Set has only a lark member: a dm/telegram turn's global-only variant is empty and must fall
        // back to the built-in default, not serve an empty overlay (per-variant no-regression floor).
        var handler = new OrnnTestHttpMessageHandler(
            _ => Json(SetJson("set-guid-1", "m-lark")),
            _ => Json(MemberJson("aevatar-lark-provisioning", "overlay-scope-lark", LarkBody)));
        var provider = CreateProvider(CreateClient(handler), new StubFallback("BUILT-IN DEFAULT"));

        await provider.RefreshAsync("token");

        provider.GetCurrent(new SystemSkillOverlayRequest("dm", null))!
            .OverlayMarkdown.Should().Be("BUILT-IN DEFAULT");
        provider.GetCurrent(new SystemSkillOverlayRequest("telegram", null))!
            .OverlayMarkdown.Should().Be("BUILT-IN DEFAULT");
        // The lark turn still gets its scoped member.
        provider.GetCurrent(new SystemSkillOverlayRequest("lark", null))!
            .OverlayMarkdown.Should().Contain(LarkBody);
    }

    [Fact]
    public async Task GetCurrent_FallsBackToBuiltInDefault_WhenNoMemberHasOverlayScopeTag()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => Json(SetJson("set-guid-1", "m-untagged")),
            _ => Json(MemberJson("some-skill", "not-an-overlay-scope", "SOME BODY")));
        var provider = CreateProvider(CreateClient(handler), new StubFallback("BUILT-IN DEFAULT"));

        await provider.RefreshAsync("token");

        provider.GetCurrent(new SystemSkillOverlayRequest("dm", null))!
            .OverlayMarkdown.Should().Be("BUILT-IN DEFAULT");
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

        provider.GetCurrent(new SystemSkillOverlayRequest("dm", null))!.OverlayMarkdown.Should().Contain(GlobalBody);
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

        provider.GetCurrent(new SystemSkillOverlayRequest("dm", null))!.OverlayMarkdown.Should().Contain(GlobalBody);
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
        overlay.OverlayMarkdown.Should().NotContain(token);
        overlay.SourceWatermark.Should().NotContain(token);
        // The token is still what authenticated the proxy call — proving it is used, just never stored.
        handler.Requests.Should().Contain(request => request.Authorization!.Parameter == token);
    }

    [Fact]
    public void GetCurrent_WithNoTokenAndNoSnapshot_ReturnsFallbackWithoutFetching()
    {
        var handler = new OrnnTestHttpMessageHandler();
        var provider = CreateProvider(CreateClient(handler), new StubFallback("BUILT-IN DEFAULT"));

        var overlay = provider.GetCurrent(new SystemSkillOverlayRequest("lark", null));

        overlay!.OverlayMarkdown.Should().Be("BUILT-IN DEFAULT");
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

    private static OrnnSystemSkillOverlayProvider CreateProvider(
        OrnnSkillClient client,
        ISystemSkillOverlayFallback? fallback = null) =>
        new(
            new SystemSkillOverlayOptions
            {
                Enabled = true,
                SetName = "aevatar-system",
                MaxSkills = 32,
                MaxBytes = 32 * 1024,
            },
            client,
            fallback);

    private sealed class StubFallback(string markdown) : ISystemSkillOverlayFallback
    {
        public OverlayMessage GetFallback() =>
            new() { OverlayMarkdown = markdown, SourceWatermark = "builtin-default" };
    }
}
