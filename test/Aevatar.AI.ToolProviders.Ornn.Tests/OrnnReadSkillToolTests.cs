using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnReadSkillToolTests
{
    private const string SkillId = "11111111-2222-3333-4444-555555555555";
    private const string Hash10 = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private const string Hash12 = "101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f";
    private const string Hash13 = "202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f";

    [Fact]
    public void Contract_ShouldExposeOneGenericReadDiscriminator()
    {
        var tool = CreateTool(OrnnTestHttpMessageHandler.ReturningJson("{}"));

        tool.Name.Should().Be("ornn_read_skill");
        tool.IsReadOnly.Should().BeTrue();
        tool.IsDestructive.Should().BeFalse();
        tool.SideEffectKind.Should().BeEmpty();
        tool.Description.Should().Contain("authoritative Ornn skill detail");
        tool.Description.Should().Contain("untrusted inspection data");
        tool.Description.Should().NotContain("merchant");
        tool.Description.Should().NotContain("onboarding");

        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        var root = schema.RootElement;
        root.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        root.GetProperty("required").EnumerateArray()
            .Select(static item => item.GetString()).Should().Equal("skill_id");
        root.GetProperty("properties").EnumerateObject().Select(static property => property.Name)
            .Should().Equal("skill_id", "version", "package_content");
        root.GetProperty("properties").GetProperty("package_content")
            .GetProperty("enum").EnumerateArray().Select(static item => item.GetString())
            .Should().Equal("none", "skill_markdown", "all_files");
        root.GetProperty("properties").EnumerateObject().Select(static property => property.Name)
            .Should().NotContain(["skill_name", "include_package", "only_skill_md", "metadata"]);
    }

    [Fact]
    public async Task ExecuteAsync_LatestMetadataOnly_ShouldReturnCompleteVersionFactsWithoutDownloadingPackage()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(
                ("1.0", Hash10, true),
                ("1.2", Hash12, false))),
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)));
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync($$"""{"skill_id":"{{SkillId}}"}""");

        using var result = JsonDocument.Parse(resultJson);
        var root = result.RootElement;
        root.GetProperty("result_type").GetString().Should().Be("ornn_read_skill");
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("content_scope").GetString().Should().Be("none");
        root.GetProperty("resolved_version").GetString().Should().Be("1.2");
        root.GetProperty("package").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("package_diagnostics").ValueKind.Should().Be(JsonValueKind.Null);

        var detail = root.GetProperty("detail");
        detail.GetProperty("skill_id").GetString().Should().Be(SkillId);
        detail.GetProperty("name").GetString().Should().Be("skill-alpha");
        detail.GetProperty("description").GetString().Should().Be("Authoritative skill alpha.");
        detail.GetProperty("version").GetString().Should().Be("1.2");
        detail.GetProperty("skill_hash").GetString().Should().Be(Hash12);
        detail.GetProperty("category").GetString().Should().Be("plain");
        detail.GetProperty("tags").EnumerateArray().Select(static item => item.GetString())
            .Should().Equal("alpha", "read");

        var versions = root.GetProperty("versions").EnumerateArray().ToArray();
        versions.Should().HaveCount(2);
        versions.Select(static item => item.GetProperty("version").GetString())
            .Should().Equal("1.0", "1.2");
        versions[0].GetProperty("is_deprecated").GetBoolean().Should().BeTrue();
        versions[0].GetProperty("release_notes").GetString().Should().Be("release-1.0");

        handler.Requests.Select(static request => request.RequestUri!.AbsoluteUri).Should().Equal(
            SkillUri(),
            $"{SkillUri()}/versions",
            SkillUri());
        handler.Requests.Should().OnlyContain(static request =>
            request.Method == HttpMethod.Get && request.Authorization!.Parameter == "caller-token");
    }

    [Fact]
    public async Task ExecuteAsync_SkillMarkdown_ShouldPinExactPackageAndReturnOnlyRootMarkdown()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2", Hash12, false))),
            _ => OrnnTestHttpMessageHandler.JsonResponse(PackageJson(
                "skill-alpha",
                "1.2",
                "{\"SKILL.md\":\"# Skill Alpha\\n\\nInspect only.\",\"references/guide.md\":\"Guide\"}")));
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync($$"""
            {"skill_id":"{{SkillId}}","version":"1.2","package_content":"skill_markdown"}
            """);

        using var result = JsonDocument.Parse(resultJson);
        var root = result.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("content_scope").GetString().Should().Be("skill_markdown");
        var package = root.GetProperty("package");
        package.EnumerateObject().Select(static property => property.Name)
            .Should().Equal("skill_markdown");
        package.GetProperty("skill_markdown").GetString().Should().Be("# Skill Alpha\n\nInspect only.");
        var diagnostics = root.GetProperty("package_diagnostics");
        diagnostics.GetProperty("file_count").GetInt32().Should().Be(2);
        diagnostics.GetProperty("total_file_bytes").GetInt64().Should().BeGreaterThan(0);
        diagnostics.GetProperty("file_tree_sha256").GetString().Should().MatchRegex("^[0-9a-f]{64}$");

        handler.Requests.Select(static request => request.RequestUri!.AbsoluteUri).Should().Equal(
            $"{SkillUri()}?version=1.2",
            $"{SkillUri()}/versions",
            $"{SkillUri()}/json?version=1.2");
    }

    [Fact]
    public async Task ExecuteAsync_AllFiles_ShouldReturnCompletePinnedFileMap()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2", Hash12, false))),
            _ => OrnnTestHttpMessageHandler.JsonResponse(PackageJson(
                "skill-alpha",
                "1.2",
                "{\"SKILL.md\":\"# Skill Alpha\",\"scripts/run.sh\":\"echo ok\"}")));
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync($$"""
            {"skill_id":"{{SkillId}}","version":"1.2","package_content":"all_files"}
            """);

        using var result = JsonDocument.Parse(resultJson);
        var files = result.RootElement.GetProperty("package").GetProperty("files");
        files.GetProperty("SKILL.md").GetString().Should().Be("# Skill Alpha");
        files.GetProperty("scripts/run.sh").GetString().Should().Be("echo ok");
        files.EnumerateObject().Should().HaveCount(2);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"skill_id\":\"not-a-guid\"}")]
    [InlineData("{\"skill_id\":\"AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA\"}")]
    [InlineData("{\"skill_id\":\"11111111-2222-3333-4444-555555555555\",\"version\":\"1.2.0\"}")]
    [InlineData("{\"skill_id\":\"11111111-2222-3333-4444-555555555555\",\"package_content\":\"package\"}")]
    [InlineData("{\"skill_id\":\"11111111-2222-3333-4444-555555555555\",\"include_package\":true}")]
    public async Task ExecuteAsync_InvalidArguments_ShouldFailBeforeHttp(string argumentsJson)
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("{}");
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync(argumentsJson);

        ReadErrorCode(resultJson).Should().Be("invalid_arguments");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WithoutCredential_ShouldReturnAuthenticationRequired()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("{}");
        var tool = CreateTool(handler);
        AgentToolRequestContext.Current = null;

        var resultJson = await tool.ExecuteAsync($$"""{"skill_id":"{{SkillId}}"}""");

        ReadErrorCode(resultJson).Should().Be("authentication_required");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WithRemoteResolver_ShouldUseResolvedChannelCredential()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2", Hash12, false))),
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)));
        var resolver = new RecordingTokenResolver("channel-token");
        var tool = CreateTool(handler, resolver);
        AgentToolRequestContext.Current = null;

        var resultJson = await tool.ExecuteAsync($$"""{"skill_id":"{{SkillId}}"}""");

        JsonDocument.Parse(resultJson).RootElement.GetProperty("status").GetString().Should().Be("success");
        resolver.RequestedSkill.Should().Be(SkillId);
        handler.Requests.Should().OnlyContain(static request => request.Authorization!.Parameter == "channel-token");
    }

    [Fact]
    public async Task ExecuteAsync_FromChannelDefaultSkill_ShouldPreserveRegistrationCredentialSubject()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2", Hash12, false))),
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)));
        var resolver = new RecordingTokenResolver("registration-agent-key");
        var tool = CreateTool(handler, resolver);
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            SkillRecovery = new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: false,
                RequireOrnnSearchOnBlocker: false,
                CommandName: "merchant-booking",
                OriginalCommand: "merchant-booking",
                PrimarySkillName: "merchant-booking",
                MaxOrnnSearchAttempts: 0,
                FromChannelDefaultSkillBinding: true),
        });

        var resultJson = await tool.ExecuteAsync($$"""{"skill_id":"{{SkillId}}"}""");

        JsonDocument.Parse(resultJson).RootElement.GetProperty("status").GetString().Should().Be("success");
        resolver.RequestedSkill.Should().Be("merchant-booking");
        handler.Requests.Should().OnlyContain(static request =>
            request.Authorization!.Parameter == "registration-agent-key");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "permission_denied")]
    [InlineData(HttpStatusCode.NotFound, "skill_not_found")]
    public async Task ExecuteAsync_VisibleReadFailure_ShouldPreserveTypedCode(
        HttpStatusCode statusCode,
        string expectedCode)
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson(
            "{\"data\":null,\"error\":{\"code\":\"UPSTREAM\",\"message\":\"denied\"}}",
            statusCode);
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync($$"""{"skill_id":"{{SkillId}}"}""");

        ReadErrorCode(resultJson).Should().Be(expectedCode);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_MalformedVersionRow_ShouldFailClosed()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2.0", Hash12, false))));
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync($$"""{"skill_id":"{{SkillId}}"}""");

        ReadErrorCode(resultJson).Should().Be("invalid_response");
    }

    [Fact]
    public async Task ExecuteAsync_DetailAndVersionHashMismatch_ShouldFailClosed()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2", Hash13, false))));
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync($$"""{"skill_id":"{{SkillId}}","version":"1.2"}""");

        ReadErrorCode(resultJson).Should().Be("integrity_mismatch");
    }

    [Fact]
    public async Task ExecuteAsync_PackageIdentityMismatch_ShouldFailClosed()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2", Hash12, false))),
            _ => OrnnTestHttpMessageHandler.JsonResponse(PackageJson(
                "skill-beta",
                "1.2",
                "{\"SKILL.md\":\"# Skill Beta\"}")));
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync($$"""
            {"skill_id":"{{SkillId}}","version":"1.2","package_content":"skill_markdown"}
            """);

        ReadErrorCode(resultJson).Should().Be("identity_mismatch");
    }

    [Theory]
    [InlineData("{\"README.md\":\"missing\"}")]
    [InlineData("{\"SKILL.md\":\"one\",\"skill.md\":\"two\"}")]
    [InlineData("{\"nested/SKILL.md\":\"not root\"}")]
    public async Task ExecuteAsync_InvalidRootSkillMarkdown_ShouldFailClosed(string filesJson)
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2", Hash12, false))),
            _ => OrnnTestHttpMessageHandler.JsonResponse(PackageJson("skill-alpha", "1.2", filesJson)));
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync($$"""
            {"skill_id":"{{SkillId}}","version":"1.2","package_content":"skill_markdown"}
            """);

        ReadErrorCode(resultJson).Should().Be("invalid_package");
    }

    [Fact]
    public async Task ExecuteAsync_LatestSnapshotChangesDuringRead_ShouldReturnRetryableFailure()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2", Hash12, false))),
            _ => OrnnTestHttpMessageHandler.JsonResponse(PackageJson(
                "skill-alpha",
                "1.2",
                "{\"SKILL.md\":\"# Skill Alpha\"}")),
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.3", Hash13)));
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync($$"""
            {"skill_id":"{{SkillId}}","package_content":"skill_markdown"}
            """);

        ReadErrorCode(resultJson).Should().Be("snapshot_changed");
        handler.Requests.Select(static request => request.RequestUri!.AbsoluteUri).Should().Equal(
            SkillUri(),
            $"{SkillUri()}/versions",
            $"{SkillUri()}/json?version=1.2",
            SkillUri());
    }

    private static OrnnReadSkillTool CreateTool(
        OrnnTestHttpMessageHandler handler,
        IRemoteSkillAccessTokenResolver? resolver = null)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        var client = new OrnnSkillClient(
            new OrnnOptions { NyxIdSlug = "ornn-api" },
            nyxClient);
        return new OrnnReadSkillTool(client, resolver);
    }

    private static AgentToolContextScope BeginTokenScope() =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials("caller-token", null, null),
        });

    private static string SkillUri() =>
        $"https://nyx.example/api/v1/proxy/s/ornn-api/api/v1/skills/{SkillId}";

    private static string DetailJson(
        string version,
        string hash,
        string guid = SkillId,
        string name = "skill-alpha") =>
        $$"""
        {
          "data": {
            "guid": "{{guid}}",
            "name": "{{name}}",
            "description": "Authoritative skill alpha.",
            "metadata": { "category": "plain", "tag": ["alpha", "read"] },
            "tags": ["alpha", "read"],
            "skillHash": "{{hash}}",
            "ownerId": "owner-alpha",
            "createdBy": "publisher-alpha",
            "createdByEmail": "publisher@example.com",
            "createdByDisplayName": "Publisher Alpha",
            "createdOn": "2026-10-01T00:00:00Z",
            "updatedOn": "2026-10-02T00:00:00Z",
            "version": "{{version}}",
            "isPrivate": true,
            "sharedWithUsers": ["user-alpha"],
            "sharedWithOrgs": ["org-alpha"],
            "isDeprecated": false,
            "deprecationNote": null
          },
          "error": null
        }
        """;

    private static string VersionsJson(params (string Version, string Hash, bool Deprecated)[] versions)
    {
        var items = versions.Select(item => new
        {
            version = item.Version,
            skillHash = item.Hash,
            createdBy = $"publisher-{item.Version}",
            createdByEmail = $"publisher-{item.Version}@example.com",
            createdByDisplayName = $"Publisher {item.Version}",
            createdOn = "2026-10-01T00:00:00Z",
            isDeprecated = item.Deprecated,
            deprecationNote = item.Deprecated ? $"deprecated-{item.Version}" : null,
            releaseNotes = $"release-{item.Version}",
        });
        return JsonSerializer.Serialize(new { data = new { items }, error = (object?)null });
    }

    private static string PackageJson(string name, string version, string filesJson) =>
        $$"""
        {
          "data": {
            "name": "{{name}}",
            "description": "Authoritative skill alpha.",
            "version": "{{version}}",
            "metadata": { "category": "plain", "tag": ["alpha", "read"] },
            "files": {{filesJson}}
          },
          "error": null
        }
        """;

    private static string ReadErrorCode(string resultJson)
    {
        using var result = JsonDocument.Parse(resultJson);
        result.RootElement.GetProperty("status").GetString().Should().Be("error");
        return result.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private sealed class RecordingTokenResolver(string token) : IRemoteSkillAccessTokenResolver
    {
        public string? RequestedSkill { get; private set; }

        public Task<RemoteSkillAccessTokenResolution> ResolveAsync(
            string skillName,
            CancellationToken ct = default)
        {
            RequestedSkill = skillName;
            return Task.FromResult(RemoteSkillAccessTokenResolution.Resolved(token));
        }
    }
}
