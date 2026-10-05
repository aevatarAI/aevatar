using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using FluentAssertions;
using FluentAssertions.Execution;

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

    [Fact]
    public async Task ExecuteAsync_AllFiles_WithNamedPackageRoot_ShouldReturnPackageRelativeFileMap()
    {
        const string packageName = "dinner-booking-c45cbf15-merchant-assistant";
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12, name: packageName)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2", Hash12, false))),
            _ => OrnnTestHttpMessageHandler.JsonResponse(PackageJson(
                packageName,
                "1.2",
                $$"""
                {"{{packageName}}/SKILL.md":"# Skill Alpha","{{packageName}}/references/rules.md":"Rules"}
                """)));
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync($$"""
            {"skill_id":"{{SkillId}}","version":"1.2","package_content":"all_files"}
            """);

        using var result = JsonDocument.Parse(resultJson);
        result.RootElement.GetProperty("status").GetString().Should().Be("success");
        var files = result.RootElement.GetProperty("package").GetProperty("files");
        files.GetProperty("SKILL.md").GetString().Should().Be("# Skill Alpha");
        files.GetProperty("references/rules.md").GetString().Should().Be("Rules");
        files.EnumerateObject().Select(static property => property.Name)
            .Should().Equal("SKILL.md", "references/rules.md");
    }

    [Theory]
    [InlineData("{\"/SKILL.md\":\"absolute\"}")]
    [InlineData("{\"SKILL.md\":\"root\",\"skill-alpha/references/guide.md\":\"mixed\"}")]
    public async Task ExecuteAsync_UnsafeOrMixedPackageRoot_ShouldFailClosed(string filesJson)
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2", Hash12, false))),
            _ => OrnnTestHttpMessageHandler.JsonResponse(PackageJson("skill-alpha", "1.2", filesJson)));
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync($$"""
            {"skill_id":"{{SkillId}}","version":"1.2","package_content":"all_files"}
            """);

        ReadErrorCode(resultJson).Should().Be("invalid_package");
    }

    [Fact]
    public async Task CreateResultReceipt_VerifiedSuccess_ShouldReturnReadOnlySkillEvidence()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2", Hash12, false))));
        var tool = CreateTool(handler);
        var argumentsJson = $$"""{"skill_id":"{{SkillId}}","version":"1.2"}""";

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync(argumentsJson);
        var receipt = ((IAgentTool)tool).CreateResultReceipt(
            "call-read",
            tool.Name,
            argumentsJson,
            resultJson);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.NeverRequire);
        receipt.Effect.Should().Be(AgentToolReceiptEffect.ReadOnly);
        receipt.IsDestructive.Should().BeFalse();
        receipt.SideEffectKind.Should().BeEmpty();
        receipt.SubjectKind.Should().Be("ornn.skill");
        receipt.SubjectId.Should().Be(SkillId);
        receipt.SubjectVersion.Should().Be("1.2");
        receipt.SubjectHash.Should().Be(Hash12);
        receipt.ResultJson.Should().Be(resultJson);
    }

    [Fact]
    public async Task CreateResultReceipt_StructuredError_ShouldPreserveSafeFailureEvidence()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson(
            "{\"data\":null,\"error\":{\"code\":\"UPSTREAM\",\"message\":\"denied\"}}",
            HttpStatusCode.Forbidden);
        var tool = CreateTool(handler);
        var argumentsJson = $$"""{"skill_id":"{{SkillId}}"}""";

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync(argumentsJson);
        var receipt = ((IAgentTool)tool).CreateResultReceipt(
            "call-read",
            tool.Name,
            argumentsJson,
            resultJson);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.Effect.Should().Be(AgentToolReceiptEffect.ReadOnly);
        receipt.SubjectKind.Should().Be("ornn.skill");
        receipt.SubjectId.Should().Be(SkillId);
        receipt.ErrorCode.Should().Be("permission_denied");
        receipt.ErrorMessage.Should().Be("The current NyxID caller cannot read the requested Ornn data.");
        receipt.FailureOutcome.Should().Be(AgentToolFailureOutcome.CalleeConfirmed);
        receipt.ResultJson.Should().Be(resultJson);
        receipt.ResultJson.Should().Contain("\"http_status\":403");
    }

    [Fact]
    public void CreateResultReceipt_MalformedOrInconsistentResult_ShouldRemainUnverified()
    {
        var tool = CreateTool(OrnnTestHttpMessageHandler.ReturningJson("{}"));
        var argumentsJson = $$"""{"skill_id":"{{SkillId}}","version":"1.2"}""";
        var inconsistentResult = $$"""
            {
              "result_type": "ornn_read_skill",
              "status": "success",
              "content_scope": "none",
              "resolved_version": "1.2",
              "detail": {
                "skill_id": "{{SkillId}}",
                "version": "1.2",
                "skill_hash": "{{Hash12}}"
              },
              "versions": [
                { "version": "1.2", "skill_hash": "{{Hash13}}" }
              ],
              "package": null,
              "package_diagnostics": null
            }
            """;
        var staleLatestResult = $$"""
            {
              "result_type": "ornn_read_skill",
              "status": "success",
              "content_scope": "none",
              "resolved_version": "1.2",
              "detail": {
                "skill_id": "{{SkillId}}",
                "version": "1.2",
                "skill_hash": "{{Hash12}}"
              },
              "versions": [
                { "version": "1.2", "skill_hash": "{{Hash12}}" },
                { "version": "1.3", "skill_hash": "{{Hash13}}" }
              ],
              "package": null,
              "package_diagnostics": null
            }
            """;

        ((IAgentTool)tool).CreateResultReceipt(
                "call-read",
                tool.Name,
                argumentsJson,
                "not-json")
            .Should().BeNull();
        ((IAgentTool)tool).CreateResultReceipt(
                "call-read",
                tool.Name,
                argumentsJson,
                inconsistentResult)
            .Should().BeNull();
        ((IAgentTool)tool).CreateResultReceipt(
                "call-read",
                tool.Name,
                $$"""{"skill_id":"{{SkillId}}"}""",
                staleLatestResult)
            .Should().BeNull();
    }

    [Fact]
    public void CreateResultReceipt_InvalidSkillHashFacts_ShouldRemainUnverified()
    {
        var tool = CreateTool(OrnnTestHttpMessageHandler.ReturningJson("{}"));
        var argumentsJson = $$"""{"skill_id":"{{SkillId}}","version":"1.2"}""";
        var invalidResolvedHash = $$"""
            {
              "result_type": "ornn_read_skill",
              "status": "success",
              "content_scope": "none",
              "resolved_version": "1.2",
              "detail": {
                "skill_id": "{{SkillId}}",
                "version": "1.2",
                "skill_hash": "not-a-hash"
              },
              "versions": [
                { "version": "1.2", "skill_hash": "not-a-hash" }
              ],
              "package": null,
              "package_diagnostics": null
            }
            """;
        var invalidHistoricalHash = $$"""
            {
              "result_type": "ornn_read_skill",
              "status": "success",
              "content_scope": "none",
              "resolved_version": "1.2",
              "detail": {
                "skill_id": "{{SkillId}}",
                "version": "1.2",
                "skill_hash": "{{Hash12}}"
              },
              "versions": [
                { "version": "1.0", "skill_hash": "not-a-hash" },
                { "version": "1.2", "skill_hash": "{{Hash12}}" }
              ],
              "package": null,
              "package_diagnostics": null
            }
            """;

        ((IAgentTool)tool).CreateResultReceipt(
                "call-read",
                tool.Name,
                argumentsJson,
                invalidResolvedHash)
            .Should().BeNull();
        ((IAgentTool)tool).CreateResultReceipt(
                "call-read",
                tool.Name,
                argumentsJson,
                invalidHistoricalHash)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("skill_markdown")]
    [InlineData("all_files")]
    public async Task CreateResultReceipt_VerifiedPackage_ShouldReturnReadOnlySkillEvidence(
        string packageContent)
    {
        var (tool, argumentsJson, resultJson) = await ExecutePackageReadAsync(packageContent);

        var receipt = ((IAgentTool)tool).CreateResultReceipt(
            "call-read",
            tool.Name,
            argumentsJson,
            resultJson);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.SubjectId.Should().Be(SkillId);
        receipt.SubjectVersion.Should().Be("1.2");
        receipt.SubjectHash.Should().Be(Hash12);
    }

    [Fact]
    public async Task CreateResultReceipt_AllFilesWithNonStringFile_ShouldRemainUnverified()
    {
        var (tool, argumentsJson, resultJson) = await ExecutePackageReadAsync("all_files");
        var forgedResult = JsonNode.Parse(resultJson)!;
        forgedResult["package"]!["files"]!["references/rules.md"] = 42;

        ((IAgentTool)tool).CreateResultReceipt(
                "call-read",
                tool.Name,
                argumentsJson,
                forgedResult.ToJsonString())
            .Should().BeNull();
    }

    [Fact]
    public async Task CreateResultReceipt_AllFilesWithMismatchedDiagnostics_ShouldRemainUnverified()
    {
        var (tool, argumentsJson, resultJson) = await ExecutePackageReadAsync("all_files");
        var forgedResult = JsonNode.Parse(resultJson)!;
        forgedResult["package_diagnostics"]!["total_file_bytes"] = 1;

        ((IAgentTool)tool).CreateResultReceipt(
                "call-read",
                tool.Name,
                argumentsJson,
                forgedResult.ToJsonString())
            .Should().BeNull();
    }

    [Fact]
    public async Task CreateResultReceipt_SkillMarkdownWithIncompleteDiagnostics_ShouldRemainUnverified()
    {
        var (tool, argumentsJson, resultJson) = await ExecutePackageReadAsync("skill_markdown");
        var forgedResult = JsonNode.Parse(resultJson)!;
        forgedResult["package_diagnostics"] = new JsonObject();

        ((IAgentTool)tool).CreateResultReceipt(
                "call-read",
                tool.Name,
                argumentsJson,
                forgedResult.ToJsonString())
            .Should().BeNull();
    }

    [Fact]
    public async Task CreateResultReceipt_SkillMarkdownWithMismatchedRootBytes_ShouldRemainUnverified()
    {
        var (tool, argumentsJson, resultJson) = await ExecutePackageReadAsync("skill_markdown");
        var forgedResult = JsonNode.Parse(resultJson)!;
        forgedResult["package_diagnostics"]!["root_skill_bytes"] = 0;

        ((IAgentTool)tool).CreateResultReceipt(
                "call-read",
                tool.Name,
                argumentsJson,
                forgedResult.ToJsonString())
            .Should().BeNull();
    }

    [Fact]
    public async Task CreateResultReceipt_SkillMarkdownWithImpossibleDiagnostics_ShouldRemainUnverified()
    {
        var (tool, argumentsJson, resultJson) = await ExecutePackageReadAsync("skill_markdown");
        var result = JsonNode.Parse(resultJson)!;
        var rootSkillBytes = result["package_diagnostics"]!["root_skill_bytes"]!.GetValue<int>();

        var missingNonEmptyFileBytes = result.DeepClone();
        missingNonEmptyFileBytes["package_diagnostics"]!["total_file_bytes"] = rootSkillBytes;

        var rootClaimedLargerThanItIs = result.DeepClone();
        rootClaimedLargerThanItIs["package_diagnostics"]!["largest_file_bytes"] = rootSkillBytes + 1;

        var totalExceedsLargestFileBound = result.DeepClone();
        totalExceedsLargestFileBound["package_diagnostics"]!["total_file_bytes"] = 100;

        using var scope = new AssertionScope();
        foreach (var forgedResult in new[]
                 {
                     missingNonEmptyFileBytes,
                     rootClaimedLargerThanItIs,
                     totalExceedsLargestFileBound,
                 })
        {
            ((IAgentTool)tool).CreateResultReceipt(
                    "call-read",
                    tool.Name,
                    argumentsJson,
                    forgedResult.ToJsonString())
                .Should().BeNull();
        }
    }

    [Fact]
    public async Task CreateResultReceipt_SkillMarkdownAboveKnownRootBound_ShouldRemainUnverified()
    {
        var (tool, argumentsJson, resultJson) = await ExecutePackageReadAsync("skill_markdown");
        var forgedResult = JsonNode.Parse(resultJson)!;
        var diagnostics = forgedResult["package_diagnostics"]!;
        var rootSkillBytes = diagnostics["root_skill_bytes"]!.GetValue<int>();
        var largestFileBytes = rootSkillBytes + 7;
        diagnostics["largest_file_path"] = "references/rules.md";
        diagnostics["largest_file_bytes"] = largestFileBytes;
        diagnostics["total_file_bytes"] = rootSkillBytes + largestFileBytes + 1;

        ((IAgentTool)tool).CreateResultReceipt(
                "call-read",
                tool.Name,
                argumentsJson,
                forgedResult.ToJsonString())
            .Should().BeNull();
    }

    [Fact]
    public void CreateResultReceipt_UnknownErrorCode_ShouldRemainUnverified()
    {
        var tool = CreateTool(OrnnTestHttpMessageHandler.ReturningJson("{}"));
        var argumentsJson = $$"""{"skill_id":"{{SkillId}}"}""";
        const string resultJson = """
            {
              "result_type": "ornn_read_skill",
              "status": "error",
              "error": {
                "code": "invented",
                "message": "invented",
                "http_status": null
              }
            }
            """;

        ((IAgentTool)tool).CreateResultReceipt(
                "call-read",
                tool.Name,
                argumentsJson,
                resultJson)
            .Should().BeNull();
    }

    [Fact]
    public void CreateResultReceipt_KnownErrorWithWrongMessage_ShouldRemainUnverified()
    {
        var tool = CreateTool(OrnnTestHttpMessageHandler.ReturningJson("{}"));
        var argumentsJson = $$"""{"skill_id":"{{SkillId}}"}""";
        const string resultJson = """
            {
              "result_type": "ornn_read_skill",
              "status": "error",
              "error": {
                "code": "permission_denied",
                "message": "invented",
                "http_status": 403
              }
            }
            """;

        ((IAgentTool)tool).CreateResultReceipt(
                "call-read",
                tool.Name,
                argumentsJson,
                resultJson)
            .Should().BeNull();
    }

    [Fact]
    public void CreateResultReceipt_KnownErrorWithSuccessHttpStatus_ShouldRemainUnverified()
    {
        var tool = CreateTool(OrnnTestHttpMessageHandler.ReturningJson("{}"));
        var argumentsJson = $$"""{"skill_id":"{{SkillId}}"}""";
        const string resultJson = """
            {
              "result_type": "ornn_read_skill",
              "status": "error",
              "error": {
                "code": "upstream_failure",
                "message": "The Ornn skill snapshot request failed.",
                "http_status": 200
              }
            }
            """;

        ((IAgentTool)tool).CreateResultReceipt(
                "call-read",
                tool.Name,
                argumentsJson,
                resultJson)
            .Should().BeNull();
    }

    [Fact]
    public void CreateResultReceipt_FailureIncompatibleWithArguments_ShouldRemainUnverified()
    {
        var tool = CreateTool(OrnnTestHttpMessageHandler.ReturningJson("{}"));
        var metadataOnlyArguments = $$"""{"skill_id":"{{SkillId}}"}""";
        var exactArguments = $$"""{"skill_id":"{{SkillId}}","version":"1.2"}""";

        var incompatibleFailures = new[]
        {
            (
                metadataOnlyArguments,
                FailureResultJson(
                    "invalid_package",
                    "The pinned Ornn package does not contain files.")),
            (
                metadataOnlyArguments,
                FailureResultJson(
                    "package_download_failed",
                    "The pinned Ornn package could not be downloaded.",
                    500)),
            (
                metadataOnlyArguments,
                FailureResultJson(
                    "invalid_response",
                    "Ornn returned no pinned package data.")),
            (
                metadataOnlyArguments,
                FailureResultJson(
                    "identity_mismatch",
                    "The pinned Ornn package identity does not match the resolved detail.")),
            (
                exactArguments,
                FailureResultJson(
                    "snapshot_changed",
                    "The latest Ornn skill changed while the snapshot was being read.")),
            (
                metadataOnlyArguments,
                FailureResultJson(
                    "integrity_mismatch",
                    "The exact Ornn detail hash does not match the version listing.")),
        };

        using var scope = new AssertionScope();
        foreach (var (argumentsJson, resultJson) in incompatibleFailures)
        {
            ((IAgentTool)tool).CreateResultReceipt(
                    "call-read",
                    tool.Name,
                    argumentsJson,
                    resultJson)
                .Should().BeNull();
        }
    }

    [Fact]
    public async Task CreateResultReceipt_KnownValidationError_ShouldReturnErrorReceipt()
    {
        var tool = CreateTool(OrnnTestHttpMessageHandler.ReturningJson("{}"));
        const string argumentsJson = "{}";
        var resultJson = await tool.ExecuteAsync(argumentsJson);

        var receipt = ((IAgentTool)tool).CreateResultReceipt(
            "call-read",
            tool.Name,
            argumentsJson,
            resultJson);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.SubjectKind.Should().BeEmpty();
        receipt.ErrorCode.Should().Be("invalid_arguments");
        receipt.ErrorMessage.Should().Be("skill_id must be a non-empty canonical GUID.");
    }

    [Fact]
    public async Task AdmittedExecutor_VerifiedRead_ShouldPreserveSuccessfulToolResult()
    {
        const string packageName = "dinner-booking-c45cbf15-merchant-assistant";
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12, name: packageName)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2", Hash12, false))),
            _ => OrnnTestHttpMessageHandler.JsonResponse(PackageJson(
                packageName,
                "1.2",
                $$"""
                {"{{packageName}}/SKILL.md":"# Skill Alpha","{{packageName}}/references/rules.md":"Rules"}
                """)));
        var tool = CreateTool(handler);
        var argumentsJson = $$"""
            {"skill_id":"{{SkillId}}","version":"1.2","package_content":"all_files"}
            """;
        var executor = new AdmittedAgentToolExecutor(
            new StartedAdmissionLedger(),
            new AppendedAuditTrail(),
            new StableIdentityHasher());
        var context = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-read", "call-read"),
            Credentials = new AgentToolCredentials("caller-token", null, null),
            ExecutionOwner = AgentToolExecutionOwners.Actor("actor-read"),
        };

        var outcome = await executor.ExecuteAsync(new AgentToolExecutionRequest(
            tool,
            argumentsJson,
            context,
            AgentToolApprovalContinuationMode.None,
            null));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        outcome.Receipt.SubjectId.Should().Be(SkillId);
        outcome.ResultJson.Should().Contain("\"status\":\"success\"");
        outcome.ResultJson.Should().Contain("references/rules.md");
        outcome.ResultJson.Should().NotContain("The tool outcome could not be verified.");
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

    private static string FailureResultJson(
        string code,
        string message,
        int? httpStatus = null) =>
        JsonSerializer.Serialize(new
        {
            result_type = "ornn_read_skill",
            status = "error",
            error = new
            {
                code,
                message,
                http_status = httpStatus,
            },
        });

    private static async Task<(OrnnReadSkillTool Tool, string ArgumentsJson, string ResultJson)>
        ExecutePackageReadAsync(string packageContent)
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson("1.2", Hash12)),
            _ => OrnnTestHttpMessageHandler.JsonResponse(VersionsJson(("1.2", Hash12, false))),
            _ => OrnnTestHttpMessageHandler.JsonResponse(PackageJson(
                "skill-alpha",
                "1.2",
                "{\"SKILL.md\":\"# Skill Alpha\",\"references/rules.md\":\"Rules\"}")));
        var tool = CreateTool(handler);
        var argumentsJson = $$"""
            {"skill_id":"{{SkillId}}","version":"1.2","package_content":"{{packageContent}}"}
            """;

        using var _ = BeginTokenScope();
        var resultJson = await tool.ExecuteAsync(argumentsJson);
        return (tool, argumentsJson, resultJson);
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

    private sealed class StartedAdmissionLedger : IAgentToolAdmissionLedger
    {
        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentToolAdmissionResult(AgentToolAdmissionStatus.Started));
        }
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
        }
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }
}
