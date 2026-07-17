using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnExactRemoteSkillFetcherTests
{
    private const string SkillGuid = "11111111-1111-1111-1111-111111111111";
    private const string SkillsetGuid = "22222222-2222-2222-2222-222222222222";
    private const string MemberAGuid = "33333333-3333-3333-3333-333333333333";
    private const string MemberBGuid = "44444444-4444-4444-4444-444444444444";
    private const string DependencyGuid = "55555555-5555-5555-5555-555555555555";
    private const int Megabyte = 1024 * 1024;
    private static readonly string SkillHash = new('a', 64);

    [Fact]
    public async Task FetchExactSkillAsync_UsesThreeGuidScopedReadsAndReturnsReviewedEvidence()
    {
        var handler = ExactSkillHandler();
        var fetcher = CreateFetcher(handler);

        var release = await fetcher.FetchExactSkillAsync(
            "access-token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });

        release.Reference.Guid.Should().Be(SkillGuid);
        release.Reference.LiteralVersion.Should().Be("1.2");
        release.PublishedName.Should().Be("curated-skill");
        release.Provenance.Should().Be(new ExactRemoteVersionProvenance(
            "publisher-subject",
            "publisher@example.test",
            "Publisher Name",
            DateTimeOffset.Parse("2026-07-10T12:30:00Z")));
        release.Package.Files.Should().ContainKey("SKILL.md");
        release.Package.Shape.FileCount.Should().Be(2);
        release.DeclaredTools.Should().ContainSingle();
        release.DeclaredTools[0].Tool.Should().Be("workspace.read");
        release.DeclaredTools[0].McpServers.Should().ContainSingle()
            .Which.Should().Be(new ExactRemoteMcpServerDeclaration("workspace-mcp", "2.0"));
        release.Definition.Name.Should().Be("curated-skill");
        release.Definition.RemoteId.Should().Be(SkillGuid);

        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
        handler.Requests.Should().OnlyContain(request => request.Authorization!.Parameter == "access-token");
    }

    [Fact]
    public async Task FetchExactSkillsetAsync_UsesThreeGuidScopedReadsAndResolvesLiteralMemberVersions()
    {
        var handler = ExactSkillsetHandler();
        var fetcher = CreateFetcher(handler);

        var release = await fetcher.FetchExactSkillsetAsync(
            "access-token",
            new ExactRemoteSkillsetRef { Guid = SkillsetGuid, LiteralVersion = "2.0" });

        release.PublishedName.Should().Be("reviewed-set");
        release.Instructions.Should().Be("Use both reviewed skills.");
        release.Provenance.PublisherSubjectId.Should().Be("set-publisher");
        release.Provenance.PublisherEmailSnapshot.Should().BeNull();
        release.Provenance.PublisherDisplayNameSnapshot.Should().Be("Set Publisher");
        release.DirectMembers.Select(member => (member.Guid, member.LiteralVersion)).Should().Equal(
            (MemberAGuid, "1.0"),
            (MemberBGuid, "2.0"));
        release.FullClosure.Select(member => (member.Guid, member.LiteralVersion)).Should().Equal(
            (DependencyGuid, "3.0"),
            (MemberAGuid, "1.0"),
            (MemberBGuid, "2.0"));

        AssertOnlyExactRequests(handler, SkillsetGuid, "2.0", isSkillset: true);
    }

    [Theory]
    [InlineData(false, "not-a-guid", "1.2")]
    [InlineData(false, SkillGuid, "")]
    [InlineData(false, SkillGuid, "latest")]
    [InlineData(false, SkillGuid, "1")]
    [InlineData(false, SkillGuid, "1.2.3")]
    [InlineData(true, "not-a-guid", "2.0")]
    [InlineData(true, SkillsetGuid, "")]
    [InlineData(true, SkillsetGuid, "latest")]
    [InlineData(true, SkillsetGuid, "2")]
    [InlineData(true, SkillsetGuid, "2.0.1")]
    public async Task FetchExactAsync_WhenReferenceIsNotGuidPlusLiteralVersion_FailsBeforeHttp(
        bool isSkillset,
        string guid,
        string literalVersion)
    {
        var handler = OrnnTestHttpMessageHandler.Routing(
            _ => throw new InvalidOperationException("Invalid exact references must not reach HTTP."));
        var fetcher = CreateFetcher(handler);

        Func<Task> act = async () =>
        {
            if (isSkillset)
            {
                await fetcher.FetchExactSkillsetAsync(
                    "token",
                    new ExactRemoteSkillsetRef { Guid = guid, LiteralVersion = literalVersion });
            }
            else
            {
                await fetcher.FetchExactSkillAsync(
                    "token",
                    new ExactRemoteSkillRef { Guid = guid, LiteralVersion = literalVersion });
            }
        };

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchExactSkillAsync_WhenEvidenceMismatches_FailsClosedWithoutFallback()
    {
        var handler = ExactSkillHandler(detailJson: SkillDetail(name: "different-name"));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync(
            "token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("detail")]
    public async Task FetchExactSkillAsync_WhenPackageOrDetailVersionDiffers_FailsClosed(string source)
    {
        var handler = ExactSkillHandler(
            packageJson: source == "package" ? SkillPackage(version: "9.9") : null,
            detailJson: source == "detail" ? SkillDetail(version: "9.9") : null);
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync(
            "token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
        assertion.Which.Message.Should().Contain("version");
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchExactSkillAsync_WhenRequestedVersionRowIsMissingOrDuplicated_FailsClosed(
        bool duplicate)
    {
        var rows = duplicate
            ? $"{SkillVersionRow()},{SkillVersionRow()}"
            : SkillVersionRow(version: "9.9");
        var handler = ExactSkillHandler(versionsJson: SkillVersions(rows));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync(
            "token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        assertion.Which.Message.Should().Contain("exactly once");
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Fact]
    public async Task FetchExactSkillAsync_WhenDetailAndVersionHashesDiffer_FailsClosed()
    {
        var otherHash = new string('b', 64);
        var handler = ExactSkillHandler(
            versionsJson: SkillVersions(SkillVersionRow(
                skillHash: otherHash,
                integrity: Integrity(otherHash))));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync(
            "token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
        assertion.Which.Message.Should().Contain("hash");
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Theory]
    [InlineData("not-hex")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task FetchExactSkillAsync_WhenVersionHashIsNotSha256_FailsClosed(string malformedHash)
    {
        var handler = ExactSkillHandler(
            detailJson: SkillDetail(skillHash: malformedHash),
            versionsJson: SkillVersions(SkillVersionRow(
                skillHash: malformedHash,
                integrity: "sha256-invalid")));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync(
            "token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        assertion.Which.Message.Should().Contain("hash");
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Fact]
    public async Task FetchExactSkillAsync_WhenIntegrityDoesNotMatchHash_FailsClosed()
    {
        var handler = ExactSkillHandler(
            versionsJson: SkillVersions(SkillVersionRow(integrity: "sha256-invalid")));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync(
            "token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
        assertion.Which.Message.Should().Contain("integrity");
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Fact]
    public async Task FetchExactSkillAsync_WhenToolDeclarationsOnlyShareADelimiterKey_FailsClosed()
    {
        var handler = ExactSkillHandler(
            packageJson: SkillPackage(tool: "a", type: "b\u001fc"),
            detailJson: SkillDetail(tool: "a\u001fb", type: "c"));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync(
            "token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
        assertion.Which.Message.Should().Contain("tools");
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Fact]
    public async Task FetchExactSkillAsync_WhenOneResponseIsUnavailable_StillMakesOnlyTheFixedReads()
    {
        var handler = ExactSkillHandler(
            packageResponse: () => OrnnTestHttpMessageHandler.JsonResponse(
                """{"error":"missing"}""",
                HttpStatusCode.NotFound));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync(
            "token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.Unavailable);
        assertion.Which.HttpStatus.Should().Be(404);
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Theory]
    [InlineData(false, "{", "not valid JSON")]
    [InlineData(false, "{}", "omitted data")]
    [InlineData(true, "{", "not valid JSON")]
    [InlineData(true, "{}", "omitted data")]
    public async Task FetchExactAsync_WhenEnvelopeIsMalformedOrOmitsData_FailsClosedWithoutFallback(
        bool isSkillset,
        string responseJson,
        string expectedMessage)
    {
        var handler = isSkillset
            ? ExactSkillsetHandler(detailJson: responseJson)
            : ExactSkillHandler(packageJson: responseJson);
        var fetcher = CreateFetcher(handler);

        Func<Task> act = async () =>
        {
            if (isSkillset)
                await fetcher.FetchExactSkillsetAsync("token", SkillsetReference());
            else
                await fetcher.FetchExactSkillAsync("token", SkillReference());
        };

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        var exception = assertion.Which;
        exception.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        exception.ResourceKind.Should().Be(
            isSkillset ? ExactRemoteResourceKind.Skillset : ExactRemoteResourceKind.Skill);
        exception.Guid.Should().Be(isSkillset ? SkillsetGuid : SkillGuid);
        exception.LiteralVersion.Should().Be(isSkillset ? "2.0" : "1.2");
        exception.Message.Should().Contain(expectedMessage);
        if (responseJson == "{")
            exception.InnerException.Should().BeOfType<JsonException>();
        else
            exception.InnerException.Should().BeNull();
        AssertOnlyExactRequests(
            handler,
            isSkillset ? SkillsetGuid : SkillGuid,
            isSkillset ? "2.0" : "1.2",
            isSkillset);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("detail")]
    public async Task FetchExactSkillAsync_WhenPackageOrDetailMetadataIsMissing_FailsClosedWithoutFallback(
        string source)
    {
        var handler = ExactSkillHandler(
            packageJson: source == "package" ? SkillPackage(includeMetadata: false) : null,
            detailJson: source == "detail" ? SkillDetail(includeMetadata: false) : null);
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync("token", SkillReference());

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        assertion.Which.Message.Should().Contain("metadata");
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchExactSkillAsync_WhenPackageAndDetailToolListsAreMissingOrEmpty_ReturnsNoDeclarations(
        bool includeEmptyToolList)
    {
        var toolsJson = includeEmptyToolList ? "[]" : null;
        var handler = ExactSkillHandler(
            packageJson: SkillPackage(toolsJson: toolsJson, includeTools: includeEmptyToolList),
            detailJson: SkillDetail(toolsJson: toolsJson, includeTools: includeEmptyToolList));
        var fetcher = CreateFetcher(handler);

        var release = await fetcher.FetchExactSkillAsync("token", SkillReference());

        release.DeclaredTools.Should().BeEmpty();
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchExactSkillAsync_WhenToolMcpServersAreMissingOrEmpty_ReturnsToolWithoutServers(
        bool includeEmptyMcpServers)
    {
        var toolsJson = ToolWithoutMcpServersJson(includeEmptyMcpServers);
        var handler = ExactSkillHandler(
            packageJson: SkillPackage(toolsJson: toolsJson),
            detailJson: SkillDetail(toolsJson: toolsJson));
        var fetcher = CreateFetcher(handler);

        var release = await fetcher.FetchExactSkillAsync("token", SkillReference());

        release.DeclaredTools.Should().ContainSingle()
            .Which.McpServers.Should().BeEmpty();
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchExactAsync_WhenReturnedGuidDiffers_FailsClosedWithoutFallback(bool isSkillset)
    {
        var handler = isSkillset
            ? ExactSkillsetHandler(detailJson: SkillsetDetail(guid: SkillGuid))
            : ExactSkillHandler(detailJson: SkillDetail(guid: SkillsetGuid));
        var fetcher = CreateFetcher(handler);

        Func<Task> act = async () =>
        {
            if (isSkillset)
                await fetcher.FetchExactSkillsetAsync("token", SkillsetReference());
            else
                await fetcher.FetchExactSkillAsync("token", SkillReference());
        };

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
        assertion.Which.Message.Should().Contain("returned GUID");
        AssertOnlyExactRequests(
            handler,
            isSkillset ? SkillsetGuid : SkillGuid,
            isSkillset ? "2.0" : "1.2",
            isSkillset);
    }

    [Fact]
    public async Task FetchExactSkillAsync_UsesOneSharedTimeoutForAllThreeReads()
    {
        var timeProvider = new ManualTimeProvider();
        var handler = OrnnTestHttpMessageHandler.HangingUntilCanceled(expectedRequestCount: 3);
        var fetcher = CreateFetcher(handler, TimeSpan.FromSeconds(30), timeProvider);

        var fetchTask = fetcher.FetchExactSkillAsync(
            "token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });
        await handler.ExpectedRequestsStarted;
        timeProvider.ExpireTimer();
        var act = async () => await fetchTask;

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.Unavailable);
        handler.Requests.Should().HaveCount(3);
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Fact]
    public async Task FetchExactSkillsetAsync_UsesOneSharedTimeoutForAllThreeReads()
    {
        var timeProvider = new ManualTimeProvider();
        var handler = OrnnTestHttpMessageHandler.HangingUntilCanceled(expectedRequestCount: 3);
        var fetcher = CreateFetcher(handler, TimeSpan.FromSeconds(30), timeProvider);

        var fetchTask = fetcher.FetchExactSkillsetAsync("token", SkillsetReference());
        await handler.ExpectedRequestsStarted;
        timeProvider.ExpireTimer();
        var act = async () => await fetchTask;

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.Unavailable);
        AssertOnlyExactRequests(handler, SkillsetGuid, "2.0", isSkillset: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchExactAsync_WhenCallerCancels_PropagatesCancellation(bool isSkillset)
    {
        using var callerCts = new CancellationTokenSource();
        var handler = OrnnTestHttpMessageHandler.HangingUntilCanceled(expectedRequestCount: 3);
        var fetcher = CreateFetcher(handler);
        Task fetchTask = isSkillset
            ? fetcher.FetchExactSkillsetAsync("token", SkillsetReference(), callerCts.Token)
            : fetcher.FetchExactSkillAsync("token", SkillReference(), callerCts.Token);

        await handler.ExpectedRequestsStarted;
        callerCts.Cancel();
        var act = async () => await fetchTask;

        await act.Should().ThrowAsync<OperationCanceledException>();
        AssertOnlyExactRequests(
            handler,
            isSkillset ? SkillsetGuid : SkillGuid,
            isSkillset ? "2.0" : "1.2",
            isSkillset);
    }

    [Fact]
    public async Task FetchExactSkillAsync_RejectsDeclaredContentLengthBeforeDeserialization()
    {
        var handler = ExactSkillHandler(
            packageResponse: () => OrnnTestHttpMessageHandler.JsonResponse(
                SkillPackage(),
                contentLength: NyxIdToolOptions.HardProxyFileArtifactMaxBytes + 1));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync(
            "token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        assertion.Which.Message.Should().Contain("exceeded");
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Fact]
    public async Task FetchExactSkillAsync_RejectsOversizedEvidenceStreamWithoutContentLength()
    {
        var handler = ExactSkillHandler(
            detailResponse: () => OrnnTestHttpMessageHandler.OversizedStreamResponse(
                NyxIdToolOptions.DefaultProxyFileArtifactMaxBytes + 1));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync(
            "token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Fact]
    public async Task FetchExactSkillAsync_RejectsDecodedPackagePathBeyondAdapterBound()
    {
        var oversizedPath = new string('p', 513);
        var files = $$"""{"{{oversizedPath}}":"content"}""";
        var handler = ExactSkillHandler(packageJson: SkillPackage(filesJson: files));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync(
            "token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Theory]
    [InlineData("missing-files")]
    [InlineData("too-many-files")]
    [InlineData("duplicate-normalized-path")]
    [InlineData("unix-absolute-path")]
    [InlineData("windows-absolute-path")]
    [InlineData("traversal-path")]
    [InlineData("null-file-content")]
    [InlineData("blank-path")]
    [InlineData("nul-path")]
    [InlineData("single-file-too-large")]
    [InlineData("total-files-too-large")]
    public async Task FetchExactSkillAsync_WhenDecodedPackageViolatesAdapterBounds_FailsClosed(
        string invalidPackage)
    {
        var handler = ExactSkillHandler(
            packageJson: SkillPackage(filesJson: InvalidPackageFilesJson(invalidPackage)));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync("token", SkillReference());

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        var expectedMessage = invalidPackage switch
        {
            "null-file-content" => "null content",
            "blank-path" or "nul-path" => "normalized relative path",
            "total-files-too-large" => "total file bytes",
            _ => null,
        };
        if (expectedMessage is not null)
            assertion.Which.Message.Should().Contain(expectedMessage);
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Theory]
    [InlineData("too-many-tools")]
    [InlineData("blank-tool")]
    [InlineData("blank-type")]
    [InlineData("duplicate-tool")]
    [InlineData("blank-mcp-name")]
    [InlineData("blank-mcp-version")]
    [InlineData("duplicate-mcp")]
    [InlineData("null-tool")]
    [InlineData("null-mcp-server")]
    public async Task FetchExactSkillAsync_WhenDeclaredToolsAreInvalid_FailsClosed(string invalidTools)
    {
        var handler = ExactSkillHandler(
            packageJson: SkillPackage(toolsJson: InvalidToolsJson(invalidTools)));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync("token", SkillReference());

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        var exception = assertion.Which;
        exception.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        exception.ResourceKind.Should().Be(ExactRemoteResourceKind.Skill);
        exception.Guid.Should().Be(SkillGuid);
        exception.LiteralVersion.Should().Be("1.2");
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Theory]
    [InlineData("blank-subject")]
    [InlineData("blank-email")]
    [InlineData("blank-display-name")]
    [InlineData("invalid-published-at")]
    public async Task FetchExactSkillAsync_WhenPublisherProvenanceIsInvalid_FailsClosed(
        string invalidProvenance)
    {
        var versionRow = invalidProvenance switch
        {
            "blank-subject" => SkillVersionRow(createdBy: " "),
            "blank-email" => SkillVersionRow(createdByEmail: " "),
            "blank-display-name" => SkillVersionRow(createdByDisplayName: " "),
            "invalid-published-at" => SkillVersionRow(createdOn: "not-a-timestamp"),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidProvenance)),
        };
        var handler = ExactSkillHandler(versionsJson: SkillVersions(versionRow));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillAsync("token", SkillReference());

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
    }

    [Fact]
    public async Task FetchExactSkillsetAsync_WhenClosureContainsConflictingIdentity_FailsClosed()
    {
        var duplicateClosure = $$"""
            {
              "data": {
                "instructions": "Use both reviewed skills.",
                "items": [
                  { "ref": "member-a@1.0", "guid": "{{MemberAGuid}}", "name": "member-a", "version": "1.0", "depth": 0 },
                  { "ref": "member-a@2.0", "guid": "{{MemberAGuid}}", "name": "member-b", "version": "2.0", "depth": 0 }
                ]
              }
            }
            """;
        var handler = ExactSkillsetHandler(closureJson: duplicateClosure);
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillsetAsync(
            "token",
            new ExactRemoteSkillsetRef { Guid = SkillsetGuid, LiteralVersion = "2.0" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        AssertOnlyExactRequests(handler, SkillsetGuid, "2.0", isSkillset: true);
    }

    [Fact]
    public async Task FetchExactSkillsetAsync_WhenStringMemberUsesGuidAndLiteralVersion_ResolvesExactRoot()
    {
        var members = $$"""["{{MemberAGuid}}@1.0", "member-b@beta"]""";
        var handler = ExactSkillsetHandler(detailJson: SkillsetDetail(membersJson: members));
        var fetcher = CreateFetcher(handler);

        var release = await fetcher.FetchExactSkillsetAsync(
            "token",
            new ExactRemoteSkillsetRef { Guid = SkillsetGuid, LiteralVersion = "2.0" });

        release.DirectMembers.Select(member => (member.Guid, member.LiteralVersion)).Should().Equal(
            (MemberAGuid, "1.0"),
            (MemberBGuid, "2.0"));
        AssertOnlyExactRequests(handler, SkillsetGuid, "2.0", isSkillset: true);
    }

    [Fact]
    public async Task FetchExactSkillsetAsync_WhenObjectMemberGuidMatchesButLiteralVersionDiffers_FailsClosed()
    {
        var members = $$"""
            [
              { "guid": "{{MemberAGuid}}", "name": "member-a", "version": "9.9" },
              { "guid": "{{MemberBGuid}}", "name": "member-b", "version": "2.0" }
            ]
            """;
        var handler = ExactSkillsetHandler(detailJson: SkillsetDetail(membersJson: members));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillsetAsync(
            "token",
            new ExactRemoteSkillsetRef { Guid = SkillsetGuid, LiteralVersion = "2.0" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
        AssertOnlyExactRequests(handler, SkillsetGuid, "2.0", isSkillset: true);
    }

    [Fact]
    public async Task FetchExactSkillsetAsync_WhenDetailVersionDiffers_FailsClosed()
    {
        var handler = ExactSkillsetHandler(detailJson: SkillsetDetail(version: "9.9"));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillsetAsync(
            "token",
            new ExactRemoteSkillsetRef { Guid = SkillsetGuid, LiteralVersion = "2.0" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
        assertion.Which.Message.Should().Contain("version");
        AssertOnlyExactRequests(handler, SkillsetGuid, "2.0", isSkillset: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchExactSkillsetAsync_WhenRequestedVersionRowIsMissingOrDuplicated_FailsClosed(
        bool duplicate)
    {
        var rows = duplicate
            ? $"{SkillsetVersionRow()},{SkillsetVersionRow()}"
            : SkillsetVersionRow(version: "9.9");
        var handler = ExactSkillsetHandler(versionsJson: SkillsetVersions(rows));
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillsetAsync(
            "token",
            new ExactRemoteSkillsetRef { Guid = SkillsetGuid, LiteralVersion = "2.0" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        assertion.Which.Message.Should().Contain("exactly once");
        AssertOnlyExactRequests(handler, SkillsetGuid, "2.0", isSkillset: true);
    }

    [Theory]
    [InlineData("empty-members", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("null-members", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("null-member", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("too-many-members", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("missing-closure", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("null-closure-item", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("too-many-closure-nodes", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("missing-member-count", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("mismatched-member-count", ExactRemoteFetchFailureKind.IntegrityMismatch)]
    [InlineData("missing-depth", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("negative-depth", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("root-count-mismatch", ExactRemoteFetchFailureKind.IntegrityMismatch)]
    [InlineData("missing-member-identity", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("invalid-member-guid", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("missing-member-version", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("missing-closure-ref", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("missing-closure-name", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("invalid-closure-guid", ExactRemoteFetchFailureKind.InvalidResponse)]
    [InlineData("invalid-closure-version", ExactRemoteFetchFailureKind.InvalidResponse)]
    public async Task FetchExactSkillsetAsync_WhenMemberOrClosureEvidenceIsInvalid_FailsClosed(
        string invalidEvidence,
        ExactRemoteFetchFailureKind expectedFailureKind)
    {
        var (detailJson, closureJson, versionsJson) = InvalidSkillsetEvidence(invalidEvidence);
        var handler = ExactSkillsetHandler(detailJson, closureJson, versionsJson);
        var fetcher = CreateFetcher(handler);

        var act = async () => await fetcher.FetchExactSkillsetAsync("token", SkillsetReference());

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        var exception = assertion.Which;
        exception.FailureKind.Should().Be(expectedFailureKind);
        exception.ResourceKind.Should().Be(ExactRemoteResourceKind.Skillset);
        var expectedGuid = invalidEvidence == "invalid-closure-guid" ? "not-a-guid" :
            invalidEvidence == "invalid-closure-version" ? DependencyGuid : SkillsetGuid;
        var expectedVersion = invalidEvidence switch
        {
            "invalid-closure-guid" => "3.0",
            "invalid-closure-version" => "latest",
            _ => "2.0",
        };
        exception.Guid.Should().Be(expectedGuid);
        exception.LiteralVersion.Should().Be(expectedVersion);
        AssertOnlyExactRequests(handler, SkillsetGuid, "2.0", isSkillset: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FetchExactAsync_WhenVersionsItemsAreNullOrContainNull_FailsClosedWithoutFallback(
        bool isSkillset,
        bool containsNullElement)
    {
        var itemsJson = containsNullElement ? "[null]" : "null";
        var handler = isSkillset
            ? ExactSkillsetHandler(versionsJson: SkillsetVersionsWithItems(itemsJson))
            : ExactSkillHandler(versionsJson: SkillVersionsWithItems(itemsJson));
        var fetcher = CreateFetcher(handler);

        Func<Task> act = isSkillset
            ? async () => await fetcher.FetchExactSkillsetAsync("token", SkillsetReference())
            : async () => await fetcher.FetchExactSkillAsync("token", SkillReference());

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        var exception = assertion.Which;
        exception.FailureKind.Should().Be(ExactRemoteFetchFailureKind.InvalidResponse);
        exception.ResourceKind.Should().Be(
            isSkillset ? ExactRemoteResourceKind.Skillset : ExactRemoteResourceKind.Skill);
        exception.Guid.Should().Be(isSkillset ? SkillsetGuid : SkillGuid);
        exception.LiteralVersion.Should().Be(isSkillset ? "2.0" : "1.2");
        AssertOnlyExactRequests(
            handler,
            isSkillset ? SkillsetGuid : SkillGuid,
            isSkillset ? "2.0" : "1.2",
            isSkillset);
    }

    private static OrnnRemoteSkillFetcher CreateFetcher(
        OrnnTestHttpMessageHandler handler,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        var client = new OrnnSkillClient(
            new OrnnOptions { NyxIdSlug = "ornn-api" },
            nyxClient,
            timeout ?? TimeSpan.FromSeconds(30),
            timeProvider ?? TimeProvider.System);
        return new OrnnRemoteSkillFetcher(client);
    }

    private static OrnnTestHttpMessageHandler ExactSkillHandler(
        string? packageJson = null,
        string? detailJson = null,
        string? versionsJson = null,
        Func<HttpResponseMessage>? packageResponse = null,
        Func<HttpResponseMessage>? detailResponse = null)
    {
        return OrnnTestHttpMessageHandler.Routing(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith($"/skills/{SkillGuid}/json", StringComparison.Ordinal))
                return packageResponse?.Invoke() ?? OrnnTestHttpMessageHandler.JsonResponse(packageJson ?? SkillPackage());
            if (path.EndsWith($"/skills/{SkillGuid}/versions", StringComparison.Ordinal))
                return OrnnTestHttpMessageHandler.JsonResponse(versionsJson ?? SkillVersions());
            if (path.EndsWith($"/skills/{SkillGuid}", StringComparison.Ordinal))
                return detailResponse?.Invoke() ?? OrnnTestHttpMessageHandler.JsonResponse(detailJson ?? SkillDetail());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static OrnnTestHttpMessageHandler ExactSkillsetHandler(
        string? detailJson = null,
        string? closureJson = null,
        string? versionsJson = null)
    {
        return OrnnTestHttpMessageHandler.Routing(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith($"/skillsets/{SkillsetGuid}/closure", StringComparison.Ordinal))
                return OrnnTestHttpMessageHandler.JsonResponse(closureJson ?? SkillsetClosure());
            if (path.EndsWith($"/skillsets/{SkillsetGuid}/versions", StringComparison.Ordinal))
                return OrnnTestHttpMessageHandler.JsonResponse(versionsJson ?? SkillsetVersions());
            if (path.EndsWith($"/skillsets/{SkillsetGuid}", StringComparison.Ordinal))
                return OrnnTestHttpMessageHandler.JsonResponse(detailJson ?? SkillsetDetail());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static void AssertOnlyExactRequests(
        OrnnTestHttpMessageHandler handler,
        string guid,
        string version,
        bool isSkillset)
    {
        var resource = isSkillset ? "skillsets" : "skills";
        var expected = isSkillset
            ? new[]
            {
                $"/api/v1/{resource}/{guid}?version={version}",
                $"/api/v1/{resource}/{guid}/closure?version={version}",
                $"/api/v1/{resource}/{guid}/versions",
            }
            : new[]
            {
                $"/api/v1/{resource}/{guid}/json?version={version}",
                $"/api/v1/{resource}/{guid}?version={version}",
                $"/api/v1/{resource}/{guid}/versions",
            };

        handler.Requests.Should().HaveCount(3);
        handler.Requests.Select(request => request.RequestUri!.PathAndQuery)
            .Should().BeEquivalentTo(expected.Select(path => $"/api/v1/proxy/s/ornn-api{path}"));
        handler.Requests.Should().OnlyContain(request =>
            request.RequestUri!.AbsolutePath.Contains($"/{resource}/{guid}", StringComparison.Ordinal));
        handler.Requests.Select(request => request.RequestUri!.PathAndQuery)
            .Should().NotContain(path => path.Contains("latest", StringComparison.OrdinalIgnoreCase) ||
                                         path.Contains("search", StringComparison.OrdinalIgnoreCase));
    }

    private static string SkillPackage(
        string name = "curated-skill",
        string version = "1.2",
        string? filesJson = null,
        string tool = "workspace.read",
        string type = "mcp",
        string? toolsJson = null,
        bool includeMetadata = true,
        bool includeTools = true) => $$"""
        {
          "data": {
            "name": "{{name}}",
            "description": "A reviewed skill",
            "version": "{{version}}",
            "metadata": {{(includeMetadata ? SkillMetadataJson(tool, type, toolsJson, includeTools) : "null")}},
            "files": {{filesJson ?? "{\"SKILL.md\":\"---\\nname: curated-skill\\ndescription: Reviewed\\n---\\nRun it.\",\"docs/readme.md\":\"Reference\"}"}}
          }
        }
        """;

    private static string SkillDetail(
        string guid = SkillGuid,
        string name = "curated-skill",
        string version = "1.2",
        string? skillHash = null,
        string tool = "workspace.read",
        string type = "mcp",
        string? toolsJson = null,
        bool includeMetadata = true,
        bool includeTools = true) => $$"""
        {
          "data": {
            "guid": "{{guid}}",
            "name": "{{name}}",
            "version": "{{version}}",
            "skillHash": "{{skillHash ?? SkillHash}}",
            "metadata": {{(includeMetadata ? SkillMetadataJson(tool, type, toolsJson, includeTools) : "null")}}
          }
        }
        """;

    private static string SkillMetadataJson(
        string tool,
        string type,
        string? toolsJson,
        bool includeTools) => $$"""
        {
          {{(includeTools ? $"\"tools\": {toolsJson ?? SingleToolJson(tool, type)}" : string.Empty)}}
        }
        """;

    private static string SkillVersions(string? itemsJson = null) => $$"""
        {
          "data": {
            "items": [
              {{itemsJson ?? SkillVersionRow()}}
            ]
          }
        }
        """;

    private static string SkillVersionsWithItems(string itemsJson) => $$"""
        {
          "data": {
            "items": {{itemsJson}}
          }
        }
        """;

    private static string SkillVersionRow(
        string version = "1.2",
        string? skillHash = null,
        string? integrity = null,
        string createdBy = "publisher-subject",
        string createdByEmail = "publisher@example.test",
        string createdByDisplayName = "Publisher Name",
        string createdOn = "2026-07-10T12:30:00Z") => $$"""
        {
          "version": "{{version}}",
          "skillHash": "{{skillHash ?? SkillHash}}",
          "integrity": "{{integrity ?? Integrity(skillHash ?? SkillHash)}}",
          "createdBy": {{JsonSerializer.Serialize(createdBy)}},
          "createdByEmail": {{JsonSerializer.Serialize(createdByEmail)}},
          "createdByDisplayName": {{JsonSerializer.Serialize(createdByDisplayName)}},
          "createdOn": {{JsonSerializer.Serialize(createdOn)}}
        }
        """;

    private static string SkillsetDetail(
        string guid = SkillsetGuid,
        string version = "2.0",
        string? membersJson = null) => $$"""
        {
          "data": {
            "guid": "{{guid}}",
            "name": "reviewed-set",
            "version": "{{version}}",
            "instructions": "Use both reviewed skills.",
            "members": {{membersJson ?? "[\"member-a@1.0\", \"member-b@beta\"]"}}
          }
        }
        """;

    private static string SkillsetClosure(string? itemsJson = null) => $$"""
        {
          "data": {
            "instructions": "Use both reviewed skills.",
            "items": {{itemsJson ?? DefaultClosureItemsJson()}}
          }
        }
        """;

    private static string SkillsetVersions(string? itemsJson = null) => $$"""
        {
          "data": {
            "items": [
              {{itemsJson ?? SkillsetVersionRow()}}
            ]
          }
        }
        """;

    private static string SkillsetVersionsWithItems(string itemsJson) => $$"""
        {
          "data": {
            "items": {{itemsJson}}
          }
        }
        """;

    private static string SkillsetVersionRow(
        string version = "2.0",
        string? memberCountJson = "2") => $$"""
        {
          "version": "{{version}}",
          {{(memberCountJson is null ? string.Empty : $"\"memberCount\": {memberCountJson},")}}
          "createdBy": "set-publisher",
          "createdByDisplayName": "Set Publisher",
          "createdOn": "2026-07-11T08:00:00+00:00"
        }
        """;

    private static ExactRemoteSkillRef SkillReference() => new()
    {
        Guid = SkillGuid,
        LiteralVersion = "1.2",
    };

    private static ExactRemoteSkillsetRef SkillsetReference() => new()
    {
        Guid = SkillsetGuid,
        LiteralVersion = "2.0",
    };

    private static string InvalidPackageFilesJson(string invalidPackage) => invalidPackage switch
    {
        "missing-files" => "null",
        "too-many-files" => JsonSerializer.Serialize(
            Enumerable.Range(0, ExactRemotePackageBounds.AdapterMaximum.MaximumFileCount + 1)
                .ToDictionary(static index => $"files/{index}.txt", static _ => "x")),
        "duplicate-normalized-path" => """{"docs/readme.md":"a","docs//readme.md":"b"}""",
        "unix-absolute-path" => """{"/absolute.txt":"x"}""",
        "windows-absolute-path" => """{"C:/absolute.txt":"x"}""",
        "traversal-path" => """{"docs/../secret.txt":"x"}""",
        "null-file-content" => """{"empty.txt":null}""",
        "blank-path" => """{" ":"x"}""",
        "nul-path" => """{"bad\u0000path":"x"}""",
        "single-file-too-large" => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["large.txt"] = new('x', checked((int)ExactRemotePackageBounds.AdapterMaximum.MaximumFileUtf8Bytes + 1)),
        }),
        "total-files-too-large" => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["first.txt"] = new('x', 25 * Megabyte),
            ["second.txt"] = new('x', 25 * Megabyte),
            ["third.txt"] = "x",
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(invalidPackage)),
    };

    private static string InvalidToolsJson(string invalidTools) => invalidTools switch
    {
        "too-many-tools" => $"[{string.Join(',', Enumerable.Range(0, 51).Select(index => ToolJson($"tool-{index}")))}]",
        "blank-tool" => $"[{ToolJson(" ")}]",
        "blank-type" => $"[{ToolJson("workspace.read", type: " ")}]",
        "duplicate-tool" => $"[{ToolJson("workspace.read")},{ToolJson("workspace.read")}]",
        "blank-mcp-name" => $"[{ToolJson("workspace.read", mcpServersJson: "[{\"mcp\":\" \",\"version\":\"2.0\"}]")}]",
        "blank-mcp-version" => $"[{ToolJson("workspace.read", mcpServersJson: "[{\"mcp\":\"workspace-mcp\",\"version\":\" \"}]")}]",
        "duplicate-mcp" => $"[{ToolJson("workspace.read", mcpServersJson: "[{\"mcp\":\"workspace-mcp\",\"version\":\"2.0\"},{\"mcp\":\"workspace-mcp\",\"version\":\"2.0\"}]")}]",
        "null-tool" => "[null]",
        "null-mcp-server" => $"[{ToolJson("workspace.read", mcpServersJson: "[null]")}]",
        _ => throw new ArgumentOutOfRangeException(nameof(invalidTools)),
    };

    private static string SingleToolJson(string tool, string type) => $"[{ToolJson(tool, type)}]";

    private static string ToolWithoutMcpServersJson(bool includeEmptyMcpServers) => includeEmptyMcpServers
        ? $"[{ToolJson("workspace.read", mcpServersJson: "[]")}]"
        : """[{"tool":"workspace.read","type":"mcp"}]""";

    private static string ToolJson(
        string tool,
        string type = "mcp",
        string mcpServersJson = "[{\"mcp\":\"workspace-mcp\",\"version\":\"2.0\"}]") => $$"""
        {
          "tool": {{JsonSerializer.Serialize(tool)}},
          "type": {{JsonSerializer.Serialize(type)}},
          "mcp-servers": {{mcpServersJson}}
        }
        """;

    private static (string? DetailJson, string? ClosureJson, string? VersionsJson) InvalidSkillsetEvidence(
        string invalidEvidence)
    {
        var invalidClosureItem = invalidEvidence switch
        {
            "missing-depth" => $$"""{ "ref": "dependency@3.0", "guid": "{{DependencyGuid}}", "name": "dependency", "version": "3.0" }""",
            "negative-depth" => $$"""{ "ref": "dependency@3.0", "guid": "{{DependencyGuid}}", "name": "dependency", "version": "3.0", "depth": -1 }""",
            "missing-closure-ref" => $$"""{ "guid": "{{DependencyGuid}}", "name": "dependency", "version": "3.0", "depth": 1 }""",
            "missing-closure-name" => $$"""{ "ref": "dependency@3.0", "guid": "{{DependencyGuid}}", "version": "3.0", "depth": 1 }""",
            "invalid-closure-guid" => """{ "ref": "dependency@3.0", "guid": "not-a-guid", "name": "dependency", "version": "3.0", "depth": 1 }""",
            "invalid-closure-version" => $$"""{ "ref": "dependency@latest", "guid": "{{DependencyGuid}}", "name": "dependency", "version": "latest", "depth": 1 }""",
            _ => null,
        };

        return invalidEvidence switch
        {
            "empty-members" => (SkillsetDetail(membersJson: "[]"), null, null),
            "null-members" => (SkillsetDetail(membersJson: "null"), null, null),
            "null-member" => (SkillsetDetail(membersJson: "[null]"), null, null),
            "too-many-members" => (SkillsetDetail(membersJson: MemberReferencesJson(101)), null, null),
            "missing-closure" => (null, SkillsetClosure("null"), null),
            "null-closure-item" => (null, SkillsetClosure("[null]"), null),
            "too-many-closure-nodes" => (null, SkillsetClosure(ClosureItemsJson(501)), null),
            "missing-member-count" => (null, null, SkillsetVersions(SkillsetVersionRow(memberCountJson: null))),
            "mismatched-member-count" => (null, null, SkillsetVersions(SkillsetVersionRow(memberCountJson: "3"))),
            "root-count-mismatch" => (null, SkillsetClosure(RootCountMismatchClosureItemsJson()), null),
            "missing-member-identity" => (SkillsetDetail(membersJson: $$"""[{ "version": "1.0" }, "member-b@2.0"]"""), null, null),
            "invalid-member-guid" => (SkillsetDetail(membersJson: $$"""[{ "guid": "not-a-guid", "name": "member-a", "version": "1.0" }, "member-b@2.0"]"""), null, null),
            "missing-member-version" => (SkillsetDetail(membersJson: $$"""[{ "name": "member-a" }, "member-b@2.0"]"""), null, null),
            _ when invalidClosureItem is not null =>
                (null, SkillsetClosure($"[{invalidClosureItem},{DefaultClosureRootsJson()}]"), null),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidEvidence)),
        };
    }

    private static string MemberReferencesJson(int count) => JsonSerializer.Serialize(
        Enumerable.Range(0, count).Select(static index => $"member-{index}@1.0"));

    private static string ClosureItemsJson(int count) => $"[{string.Join(',', Enumerable.Range(1, count).Select(index => $$"""
        { "ref": "member-{{index}}@1.0", "guid": "00000000-0000-0000-0000-{{index.ToString("000000000000")}}", "name": "member-{{index}}", "version": "1.0", "depth": 1 }
        """))}]";

    private static string DefaultClosureItemsJson() => $"[{DefaultClosureDependencyJson()},{DefaultClosureRootsJson()}]";

    private static string DefaultClosureDependencyJson() => $$"""
        { "ref": "dependency@3.0", "guid": "{{DependencyGuid}}", "name": "dependency", "version": "3.0", "depth": 1 }
        """;

    private static string DefaultClosureRootsJson() => $$"""
        { "ref": "member-a@1.0", "guid": "{{MemberAGuid}}", "name": "member-a", "version": "1.0", "depth": 0 },
        { "ref": "member-b@2.0", "guid": "{{MemberBGuid}}", "name": "member-b", "version": "2.0", "depth": 0 }
        """;

    private static string RootCountMismatchClosureItemsJson() => $$"""
        [
          {{DefaultClosureDependencyJson()}},
          { "ref": "member-a@1.0", "guid": "{{MemberAGuid}}", "name": "member-a", "version": "1.0", "depth": 0 },
          { "ref": "member-b@2.0", "guid": "{{MemberBGuid}}", "name": "member-b", "version": "2.0", "depth": 1 }
        ]
        """;

    private static string Integrity(string hex) =>
        $"sha256-{Convert.ToBase64String(Convert.FromHexString(hex))}";

    private sealed class ManualTimeProvider : TimeProvider
    {
        private ManualTimer? _timer;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            Interlocked.Exchange(ref _timer, timer)?.Dispose();
            return timer;
        }

        public void ExpireTimer()
        {
            var timer = Volatile.Read(ref _timer)
                        ?? throw new InvalidOperationException("The timeout timer was not created.");
            timer.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private TimerCallback? _callback = callback;

            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                Volatile.Read(ref _callback) is not null;

            public void Dispose() => Interlocked.Exchange(ref _callback, null);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                Interlocked.Exchange(ref _callback, null)?.Invoke(state);
            }
        }
    }
}
