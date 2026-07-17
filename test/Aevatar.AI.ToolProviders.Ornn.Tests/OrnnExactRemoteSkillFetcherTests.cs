using System.Net;
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

    [Fact]
    public async Task FetchExactSkillAsync_UsesOneSharedTimeoutForAllThreeReads()
    {
        var handler = OrnnTestHttpMessageHandler.HangingUntilCanceled();
        var fetcher = CreateFetcher(handler, TimeSpan.FromMilliseconds(100));

        var act = async () => await fetcher.FetchExactSkillAsync(
            "token",
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.2" });

        var assertion = await act.Should().ThrowAsync<ExactRemoteFetchException>();
        assertion.Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.Unavailable);
        handler.Requests.Should().HaveCount(3);
        AssertOnlyExactRequests(handler, SkillGuid, "1.2", isSkillset: false);
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

    private static OrnnRemoteSkillFetcher CreateFetcher(
        OrnnTestHttpMessageHandler handler,
        TimeSpan? timeout = null)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        var client = new OrnnSkillClient(
            new OrnnOptions { NyxIdSlug = "ornn-api" },
            nyxClient,
            timeout ?? TimeSpan.FromSeconds(30));
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
        string? filesJson = null) => $$"""
        {
          "data": {
            "name": "{{name}}",
            "description": "A reviewed skill",
            "version": "{{version}}",
            "metadata": {
              "tools": [
                {
                  "tool": "workspace.read",
                  "type": "mcp",
                  "mcp-servers": [{ "mcp": "workspace-mcp", "version": "2.0" }]
                }
              ]
            },
            "files": {{filesJson ?? "{\"SKILL.md\":\"---\\nname: curated-skill\\ndescription: Reviewed\\n---\\nRun it.\",\"docs/readme.md\":\"Reference\"}"}}
          }
        }
        """;

    private static string SkillDetail(string name = "curated-skill") => $$"""
        {
          "data": {
            "guid": "{{SkillGuid}}",
            "name": "{{name}}",
            "version": "1.2",
            "skillHash": "{{SkillHash}}",
            "metadata": {
              "tools": [
                {
                  "tool": "workspace.read",
                  "type": "mcp",
                  "mcp-servers": [{ "mcp": "workspace-mcp", "version": "2.0" }]
                }
              ]
            }
          }
        }
        """;

    private static string SkillVersions() => $$"""
        {
          "data": {
            "items": [
              {
                "version": "1.2",
                "skillHash": "{{SkillHash}}",
                "integrity": "{{Integrity(SkillHash)}}",
                "createdBy": "publisher-subject",
                "createdByEmail": "publisher@example.test",
                "createdByDisplayName": "Publisher Name",
                "createdOn": "2026-07-10T12:30:00Z"
              }
            ]
          }
        }
        """;

    private static string SkillsetDetail() => $$"""
        {
          "data": {
            "guid": "{{SkillsetGuid}}",
            "name": "reviewed-set",
            "version": "2.0",
            "instructions": "Use both reviewed skills.",
            "members": ["member-a@1.0", "member-b@beta"]
          }
        }
        """;

    private static string SkillsetClosure() => $$"""
        {
          "data": {
            "instructions": "Use both reviewed skills.",
            "items": [
              { "ref": "dependency@3.0", "guid": "{{DependencyGuid}}", "name": "dependency", "version": "3.0", "depth": 1 },
              { "ref": "member-a@1.0", "guid": "{{MemberAGuid}}", "name": "member-a", "version": "1.0", "depth": 0 },
              { "ref": "member-b@2.0", "guid": "{{MemberBGuid}}", "name": "member-b", "version": "2.0", "depth": 0 }
            ]
          }
        }
        """;

    private static string SkillsetVersions() => """
        {
          "data": {
            "items": [
              {
                "version": "2.0",
                "memberCount": 2,
                "createdBy": "set-publisher",
                "createdByDisplayName": "Set Publisher",
                "createdOn": "2026-07-11T08:00:00+00:00"
              }
            ]
          }
        }
        """;

    private static string Integrity(string hex) =>
        $"sha256-{Convert.ToBase64String(Convert.FromHexString(hex))}";
}
