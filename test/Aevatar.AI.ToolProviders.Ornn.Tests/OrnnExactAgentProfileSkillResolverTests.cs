using System.Net;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnExactAgentProfileSkillResolverTests
{
    private const string SkillGuid = "2d05bf2e-88ee-4f76-9998-728ba2f9db10";
    private const string LiteralVersion = "1.4";
    private const string SkillMarkdown = "---\nname: skill-alpha\n---\nbody";
    private const string HashHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private static readonly ByteString HashBytes =
        ByteString.CopyFrom(Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());

    [Theory]
    [InlineData(HashHex)]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")]
    public async Task ResolveAsync_ShouldReturnTypedEvidenceFromVersionPinnedReads(string upstreamHash)
    {
        var handler = SuccessHandler(hash: upstreamHash);
        var resolver = new OrnnExactAgentProfileSkillResolver(CreateClient(handler));

        var result = await resolver.ResolveAsync("token", ExactRef());

        result.IsSuccess.Should().BeTrue();
        result.Package.Should().BeEquivalentTo(new ResolvedOrnnSkillPackage
        {
            SkillGuid = SkillGuid,
            LiteralVersion = LiteralVersion,
            CanonicalName = "skill-alpha",
            PublisherId = "publisher-alpha",
            SkillSha256 = HashBytes,
            SkillMarkdownUtf8Bytes = System.Text.Encoding.UTF8.GetByteCount(SkillMarkdown),
            DeclaredToolNames = ["lookup", "search"],
        });
        handler.Requests.Select(request => request.RequestUri!.AbsoluteUri).Should().Equal(
            $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}?version={LiteralVersion}",
            $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}/json?version={LiteralVersion}");
    }

    [Fact]
    public async Task ResolveAsync_ShouldAcceptSkillMarkdownInsideSinglePackageDirectory()
    {
        var skillJson = SkillJson(skillMarkdownPath: "skill-alpha/SKILL.md");
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson()),
            _ => OrnnTestHttpMessageHandler.JsonResponse(skillJson));

        var result = await new OrnnExactAgentProfileSkillResolver(CreateClient(handler))
            .ResolveAsync("token", ExactRef());

        result.IsSuccess.Should().BeTrue();
        result.Package!.DeclaredToolNames.Should().Equal("lookup", "search");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectNonCanonicalReferenceOrMissingTokenBeforeHttp()
    {
        var handler = SuccessHandler();
        var resolver = new OrnnExactAgentProfileSkillResolver(CreateClient(handler));

        var invalidGuid = await resolver.ResolveAsync("token", new ExactRemoteSkillRef
        {
            Guid = SkillGuid.ToUpperInvariant(),
            LiteralVersion = LiteralVersion,
        });
        var invalidVersion = await resolver.ResolveAsync("token", new ExactRemoteSkillRef
        {
            Guid = SkillGuid,
            LiteralVersion = "latest",
        });
        var missingToken = await resolver.ResolveAsync(" ", ExactRef());

        invalidGuid.DiagnosticCode.Should().Be("ORNN_SKILL_INVALID_REFERENCE");
        invalidVersion.DiagnosticCode.Should().Be("ORNN_SKILL_INVALID_REFERENCE");
        missingToken.DiagnosticCode.Should().Be("ORNN_DEPENDENCY_UNAVAILABLE");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("guid")]
    [InlineData("version")]
    [InlineData("name")]
    public async Task ResolveAsync_ShouldRejectDetailAndJsonIdentityDisagreement(string mismatch)
    {
        var detail = DetailJson(
            guid: mismatch == "guid" ? "3d05bf2e-88ee-4f76-9998-728ba2f9db10" : SkillGuid,
            name: "skill-alpha");
        var skillJson = SkillJson(
            version: mismatch == "version" ? "1.5" : LiteralVersion,
            name: mismatch == "name" ? "skill-beta" : "skill-alpha");
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(detail),
            _ => OrnnTestHttpMessageHandler.JsonResponse(skillJson));

        var result = await new OrnnExactAgentProfileSkillResolver(CreateClient(handler))
            .ResolveAsync("token", ExactRef());

        result.DiagnosticCode.Should().Be("ORNN_SKILL_IDENTITY_MISMATCH");
    }

    [Theory]
    [InlineData("")]
    [InlineData("hash-alpha")]
    [InlineData("00010203")]
    [InlineData("AAECAwQ=")]
    public async Task ResolveAsync_ShouldRejectMissingOrMalformedSha256(string upstreamHash)
    {
        var handler = SuccessHandler(hash: upstreamHash);

        var result = await new OrnnExactAgentProfileSkillResolver(CreateClient(handler))
            .ResolveAsync("token", ExactRef());

        result.DiagnosticCode.Should().Be("ORNN_SKILL_INTEGRITY_EVIDENCE_MISSING");
    }

    [Theory]
    [InlineData(true, HttpStatusCode.Forbidden, "ORNN_SKILL_ACCESS_DENIED")]
    [InlineData(false, HttpStatusCode.Forbidden, "ORNN_SKILL_ACCESS_DENIED")]
    [InlineData(false, HttpStatusCode.NotFound, "ORNN_SKILL_NOT_FOUND")]
    [InlineData(true, HttpStatusCode.InternalServerError, "ORNN_DEPENDENCY_UNAVAILABLE")]
    [InlineData(false, HttpStatusCode.ServiceUnavailable, "ORNN_DEPENDENCY_UNAVAILABLE")]
    public async Task ResolveAsync_ShouldMapExactEndpointFailuresWithoutFallback(
        bool failDetail,
        HttpStatusCode status,
        string expectedCode)
    {
        var handler = failDetail
            ? new OrnnTestHttpMessageHandler(
                _ => OrnnTestHttpMessageHandler.JsonResponse("{\"error\":\"failed\"}", status))
            : new OrnnTestHttpMessageHandler(
                _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson()),
                _ => OrnnTestHttpMessageHandler.JsonResponse("{\"error\":\"failed\"}", status));

        var result = await new OrnnExactAgentProfileSkillResolver(CreateClient(handler))
            .ResolveAsync("token", ExactRef());

        result.DiagnosticCode.Should().Be(expectedCode);
        handler.Requests.Should().HaveCount(failDetail ? 1 : 2);
    }

    [Theory]
    [InlineData("not-json", "ORNN_DEPENDENCY_UNAVAILABLE", 1)]
    [InlineData("{}", "ORNN_SKILL_NOT_FOUND", 2)]
    public async Task ResolveAsync_ShouldFailClosedForMalformedOrMissingPayload(
        string payload,
        string expectedCode,
        int expectedRequestCount)
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(payload));

        var result = await new OrnnExactAgentProfileSkillResolver(CreateClient(handler))
            .ResolveAsync("token", ExactRef());

        result.DiagnosticCode.Should().Be(expectedCode);
        handler.Requests.Should().HaveCount(expectedRequestCount);
    }

    [Fact]
    public async Task ResolveAsync_InternalTimeoutShouldReturnTypedDependencyFailure()
    {
        var handler = OrnnTestHttpMessageHandler.HangingUntilCanceled();
        var timeProvider = new FakeTimeProvider();
        var resolver = new OrnnExactAgentProfileSkillResolver(
            CreateClient(handler, TimeSpan.FromSeconds(1), timeProvider));

        var resolving = resolver.ResolveAsync("token", ExactRef());
        await handler.RequestStarted;
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var result = await resolving;

        result.DiagnosticCode.Should().Be("ORNN_DEPENDENCY_UNAVAILABLE");
    }

    [Fact]
    public async Task ResolveAsync_CallerCancellationShouldPropagate()
    {
        var handler = OrnnTestHttpMessageHandler.HangingUntilCanceled();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await new OrnnExactAgentProfileSkillResolver(CreateClient(handler))
            .ResolveAsync("token", ExactRef(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ExactRemoteSkillRef ExactRef() => new()
    {
        Guid = SkillGuid,
        LiteralVersion = LiteralVersion,
    };

    private static OrnnTestHttpMessageHandler SuccessHandler(string hash = HashHex) =>
        new(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson(hash: hash)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(SkillJson()));

    private static OrnnSkillClient CreateClient(
        HttpMessageHandler handler,
        TimeSpan? perCallTimeout = null,
        TimeProvider? timeProvider = null)
    {
        var nyxIdClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        return perCallTimeout.HasValue
            ? new OrnnSkillClient(
                new OrnnOptions { NyxIdSlug = "ornn" },
                nyxIdClient,
                perCallTimeout.Value,
                timeProvider: timeProvider)
            : new OrnnSkillClient(new OrnnOptions { NyxIdSlug = "ornn" }, nyxIdClient);
    }

    private static string DetailJson(
        string guid = SkillGuid,
        string name = "skill-alpha",
        string hash = HashHex) =>
        "{\"data\":{\"guid\":\"" + guid +
        "\",\"name\":\"" + name + "\",\"skillHash\":\"" + hash +
        "\",\"createdBy\":\"publisher-alpha\"}}";

    private static string SkillJson(
        string version = LiteralVersion,
        string name = "skill-alpha",
        string skillMarkdownPath = "SKILL.md") =>
        "{\"data\":{\"name\":\"" + name + "\",\"version\":\"" + version +
        "\",\"files\":{\"" + skillMarkdownPath + "\":\"---\\nname: skill-alpha\\n---\\nbody\"}," +
        "\"metadata\":{\"tools\":[{\"tool\":\"search\"},{\"tool\":\"lookup\"}]}}}";
}
