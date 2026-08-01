using System.Net;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnSkillClientTests
{
    [Fact]
    public async Task SearchSkillsAsync_RoutesThroughNyxIdProxyWithNormalizedQuery()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "total": 1,
                "totalPages": 1,
                "page": 1,
                "pageSize": 100,
                "items": [
                  {
                    "guid": "skill-1",
                    "name": "Translate",
                    "description": "Translate text",
                    "isPrivate": true,
                    "tags": ["language"],
                    "metadata": { "category": "text", "tag": ["fallback"] }
                  }
                ]
              }
            }
            """);
        var client = CreateClient(handler);

        var result = await client.SearchSkillsAsync(
            "access-token",
            "hello world",
            "invalid",
            page: 0,
            pageSize: 500,
            mode: "semantic");

        result.Total.Should().Be(1);
        result.Items.Should().ContainSingle();
        result.Items[0].Name.Should().Be("Translate");
        result.Items[0].Tags.Should().Equal("language");

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Authorization.Should().NotBeNull();
        request.Authorization!.Scheme.Should().Be("Bearer");
        request.Authorization.Parameter.Should().Be("access-token");
        request.RequestUri!.AbsoluteUri.Should().Be(
            "https://nyx.example/api/v1/proxy/s/ornn/api/v1/skill-search?query=hello%20world&mode=semantic&scope=mixed&page=1&pageSize=100");
    }

    [Fact]
    public async Task SearchSkillsAsync_HonorsCustomNyxIdSlug()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""{ "data": { "items": [] } }""");
        var client = CreateClient(handler, slug: "ornn-tenant-a");

        await client.SearchSkillsAsync("token", "anything");

        handler.Requests.Should().ContainSingle()
            .Which.RequestUri!.AbsoluteUri.Should().StartWith("https://nyx.example/api/v1/proxy/s/ornn-tenant-a/api/v1/skill-search");
    }

    [Fact]
    public async Task SearchSkillsAsync_SurfacesGenericNyxIdProxyErrorWithStatus()
    {
        // NyxIdApiClient wraps non-2xx responses as {"error":true,"status":N,"body":"..."}.
        // The client must surface a concise error rather than a confusing JsonException
        // about the wrapper shape.
        var handler = OrnnTestHttpMessageHandler.ReturningJson(
            """{ "error": "nope" }""",
            HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var result = await client.SearchSkillsAsync("token", "query");

        result.Items.Should().BeEmpty();
        result.Error.Should().Contain("status=500");
    }

    [Fact]
    public async Task SearchSkillsAsync_OnNyxIdProxy404_SurfacesSlugBindingHint()
    {
        // 404 from NyxID proxy means the slug isn't resolvable: the user hasn't bound an
        // Ornn service or the deployment's slug differs. The LLM-facing error must tell the
        // model exactly that so it can guide the user rather than retry mechanically (which
        // is what we observed in mainnet after the first NyxID-proxy refactor).
        var handler = OrnnTestHttpMessageHandler.ReturningJson(
            """{ "error": "missing" }""",
            HttpStatusCode.NotFound);
        var client = CreateClient(handler, slug: "ornn");

        var result = await client.SearchSkillsAsync("token", "query");

        result.Items.Should().BeEmpty();
        result.Error.Should().Contain("slug 'ornn'");
        result.Error.Should().Contain("nyxid_services action=create");
    }

    [Fact]
    public async Task GetSkillJsonAsync_RoutesThroughNyxIdProxyAndReturnsSkillFiles()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Translate",
                "description": "Translate text",
                "metadata": { "category": "text", "tag": ["language"] },
                "files": { "SKILL.md": "Use this skill." }
              }
            }
            """);
        var client = CreateClient(handler);

        var skill = await client.GetSkillJsonAsync("access-token", "Translate Skill");

        skill.Should().NotBeNull();
        skill!.Name.Should().Be("Translate");
        skill.Metadata!.Tags.Should().Equal("language");
        skill.Files.Should().ContainKey("SKILL.md");

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Authorization!.Parameter.Should().Be("access-token");
        request.RequestUri!.AbsoluteUri.Should().Be(
            "https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/Translate%20Skill/json");
    }

    [Fact]
    public async Task GetSkillSetAsync_RoutesThroughNyxIdProxyAndParsesBothMemberShapes()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "guid": "set-guid-1",
                "name": "aevatar-system",
                "instructions": "set master prompt",
                "members": [
                  { "guid": "m-1", "name": "aevatar-skill-loading", "version": "1.1" },
                  "aevatar-lark-provisioning@1.0"
                ]
              }
            }
            """);
        var client = CreateClient(handler, slug: "ornn-api");

        var set = await client.GetSkillSetAsync("access-token", "aevatar-system");

        set.Should().NotBeNull();
        set!.Guid.Should().Be("set-guid-1");
        set.Members.Should().HaveCount(2);
        set.Members[0].Reference.Should().Be("m-1");                       // object member → guid preferred
        set.Members[1].Reference.Should().Be("aevatar-lark-provisioning"); // string member → name without @version

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Authorization!.Parameter.Should().Be("access-token");
        request.RequestUri!.AbsoluteUri.Should().Be(
            "https://nyx.example/api/v1/proxy/s/ornn-api/api/v1/skillsets/aevatar-system");
    }

    [Fact]
    public async Task GetExactSkillJsonAsync_UsesGuidAndLiteralVersionAndParsesToolDeclarations()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "nyxid-service-call",
                "version": "1.2",
                "metadata": {
                  "tools": [
                    { "tool": "nyxid_service_inventory", "type": "mcp" },
                    { "tool": "nyxid_service_request", "type": "mcp" }
                  ]
                },
                "files": { "SKILL.md": "reviewed" }
              }
            }
            """);
        var client = CreateClient(handler, slug: "ornn-api");
        const string guid = "11111111-2222-3333-4444-555555555555";

        var result = await client.GetExactSkillJsonAsync("access-token", guid, "1.2");

        result.ProxyStatus.Should().BeNull();
        result.Value.Should().NotBeNull();
        result.Value!.Version.Should().Be("1.2");
        result.Value.Metadata!.Tools!.Select(static tool => tool.Tool).Should().Equal(
            "nyxid_service_inventory",
            "nyxid_service_request");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Authorization!.Parameter.Should().Be("access-token");
        request.RequestUri!.AbsoluteUri.Should().Be(
            $"https://nyx.example/api/v1/proxy/s/ornn-api/api/v1/skills/{guid}/json?version=1.2");
    }

    [Fact]
    public async Task GetExactSkillSetAsync_UsesGuidAndLiteralVersion()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "guid": "11111111-2222-3333-4444-555555555555",
                "name": "nyxid-chat-core",
                "version": "1.0",
                "createdBy": "publisher-id",
                "members": []
              }
            }
            """);
        var client = CreateClient(handler, slug: "ornn-api");
        const string guid = "11111111-2222-3333-4444-555555555555";

        var skillset = await client.GetExactSkillSetAsync("access-token", guid, "1.0");

        skillset.Should().NotBeNull();
        skillset!.Guid.Should().Be(guid);
        skillset.Version.Should().Be("1.0");
        skillset.CreatedBy.Should().Be("publisher-id");
        handler.Requests.Should().ContainSingle().Which.RequestUri!.AbsoluteUri.Should().Be(
            $"https://nyx.example/api/v1/proxy/s/ornn-api/api/v1/skillsets/{guid}?version=1.0");
    }

    [Fact]
    public async Task GetExactSkillSetClosureAsync_UsesExactClosurePath()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "items": [
                  {
                    "guid": "22222222-2222-2222-2222-222222222222",
                    "name": "nyxid-service-call",
                    "version": "1.2"
                  }
                ]
              }
            }
            """);
        var client = CreateClient(handler, slug: "ornn-api");
        const string guid = "11111111-2222-3333-4444-555555555555";

        var closure = await client.GetExactSkillSetClosureAsync("access-token", guid, "1.0");

        closure.Should().NotBeNull();
        closure!.Items.Should().ContainSingle().Which.Version.Should().Be("1.2");
        handler.Requests.Should().ContainSingle().Which.RequestUri!.AbsoluteUri.Should().Be(
            $"https://nyx.example/api/v1/proxy/s/ornn-api/api/v1/skillsets/{guid}/closure?version=1.0");
    }

    [Fact]
    public async Task CreateSkillSetAsync_PostsReviewedExactMembers()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "guid": "11111111-2222-3333-4444-555555555555",
                "name": "nyxid-chat-core",
                "version": "1.0",
                "members": []
              }
            }
            """);
        var client = CreateClient(handler, slug: "ornn-api");
        var requestModel = new OrnnSkillSetPublishRequest(
            "nyxid-chat-core",
            "reviewed",
            "select one member",
            "generic",
            ["aevatar", "reviewed-profile"],
            ["22222222-2222-2222-2222-222222222222@1.2"],
            "1.0");

        var result = await client.CreateSkillSetAsync("access-token", requestModel);

        result.Succeeded.Should().BeTrue();
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Authorization!.Parameter.Should().Be("access-token");
        request.ContentType.Should().Be("application/json");
        request.RequestUri!.AbsoluteUri.Should().Be(
            "https://nyx.example/api/v1/proxy/s/ornn-api/api/v1/skillsets");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("name").GetString().Should().Be("nyxid-chat-core");
        body.RootElement.GetProperty("version").GetString().Should().Be("1.0");
        body.RootElement.GetProperty("members").EnumerateArray()
            .Select(static member => member.GetString())
            .Should().Equal("22222222-2222-2222-2222-222222222222@1.2");
    }

    [Fact]
    public async Task GetSkillSetAsync_OnNyxIdProxy403_ThrowsAccessDenied()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson(
            """{ "error": "denied" }""",
            HttpStatusCode.Forbidden);
        var client = CreateClient(handler, slug: "ornn-api");

        var act = async () => await client.GetSkillSetAsync("token", "aevatar-system");

        await act.Should().ThrowAsync<RemoteSkillFetchException>();
    }

    [Fact]
    public async Task RemoteSkillFetcher_LiftsWorkflowYamlFilesIntoTypedDescriptorWithFrontmatterEntryOverride()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Workflow Skill",
                "description": "Runs a workflow",
                "files": {
                  "SKILL.md": "---\nname: wf-skill\ndescription: Use workflow skill\nworkflow: custom-entry\n---\nRun it.",
                  "workflows/z-child.yml": " name: z-child\nsteps: []\n ",
                  "workflows/a-main.yaml": "name: a-main\nsteps: []\n",
                  "workflows/empty.yaml": "   ",
                  "docs/workflows/ignored.yaml": "name: ignored\nsteps: []\n",
                  "assets/readme.md": "reference"
                }
              }
            }
            """);
        var fetcher = new OrnnRemoteSkillFetcher(CreateClient(handler));

        var skill = await fetcher.FetchSkillAsync("access-token", "Workflow Skill");

        skill.Should().NotBeNull();
        skill!.Workflows.Should().ContainSingle();
        var workflow = skill.Workflows.Single();
        workflow.WorkflowId.Should().Be("custom-entry");
        workflow.WorkflowYamls.Should().Equal(
            "name: a-main\nsteps: []",
            "name: z-child\nsteps: []");
        skill.AssociatedFiles.Should().NotBeNull();
        skill.AssociatedFiles.Should().NotContainKey("workflows/a-main.yaml");
        skill.AssociatedFiles.Should().ContainKey("docs/workflows/ignored.yaml");
        skill.AssociatedFiles.Should().ContainKey("assets/readme.md");
    }

    [Fact]
    public async Task RemoteSkillFetcher_WhenFrontmatterEntryMatchesWorkflowFile_ShouldPutEntryYamlFirst()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Workflow Skill",
                "description": "Runs a workflow",
                "files": {
                  "SKILL.md": "---\nname: wf-skill\nworkflow: z-entry\n---\nRun it.",
                  "workflows/a-helper.yaml": "name: a-helper\nsteps: []\n",
                  "workflows/m-middle.yaml": "name: m-middle\nsteps: []\n",
                  "workflows/z-entry.yaml": "name: z-entry\nsteps: []\n"
                }
              }
            }
            """);
        var fetcher = new OrnnRemoteSkillFetcher(CreateClient(handler));

        var skill = await fetcher.FetchSkillAsync("access-token", "Workflow Skill");

        skill.Should().NotBeNull();
        var workflow = skill!.Workflows.Should().ContainSingle().Subject;
        workflow.WorkflowId.Should().Be("z-entry");
        workflow.WorkflowYamls.Should().Equal(
            "name: z-entry\nsteps: []",
            "name: a-helper\nsteps: []",
            "name: m-middle\nsteps: []");
    }

    [Fact]
    public async Task RemoteSkillFetcher_LiftsScriptsIntoTypedDescriptorAndKeepsCallerToken()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Script Skill",
                "description": "Runs script",
                "files": {
                  "SKILL.md": "---\nname: script-skill\nscriptEntry: Zeta.EntryBehavior\n---\nRun it.",
                  "scripts/a-helper.cs": "public sealed class HelperBehavior {}",
                  "scripts/z-entry.cs": "public sealed class EntryBehavior {}",
                  "scripts/contract.proto": "syntax = \"proto3\";",
                  "assets/fallback.cs": "public sealed class FallbackBehavior {}",
                  "docs/readme.md": "reference"
                }
              }
            }
            """);
        var fetcher = new OrnnRemoteSkillFetcher(CreateClient(handler));

        var skill = await fetcher.FetchSkillAsync("access-token", "Script Skill");

        skill.Should().NotBeNull();
        var script = skill!.Scripts.Should().ContainSingle().Subject;
        script.ScriptId.Should().Be("script-skill-a-helper");
        script.SourceFiles.Keys.Should().Equal("scripts/a-helper.cs", "scripts/z-entry.cs");
        script.ProtoFiles.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>(
                "scripts/contract.proto",
                "syntax = \"proto3\";"));
        script.EntryBehaviorTypeName.Should().Be("Zeta.EntryBehavior");
        skill.AssociatedFiles.Should().ContainKeys("assets/fallback.cs", "docs/readme.md");
        skill.AssociatedFiles.Should().NotContainKey("scripts/a-helper.cs");
        skill.AssociatedFiles.Should().NotContainKey("scripts/z-entry.cs");
        skill.AssociatedFiles.Should().NotContainKey("scripts/contract.proto");

        handler.Requests.Should().ContainSingle()
            .Which.Authorization!.Parameter.Should().Be("access-token");
    }

    [Fact]
    public async Task RemoteSkillFetcher_DefaultsWorkflowIdToFirstSortedWorkflowFileNameWithoutFrontmatterEntry()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Workflow Skill",
                "description": "Runs a workflow",
                "files": {
                  "SKILL.md": "---\nname: wf-skill\ndescription: Use workflow skill\n---\nRun it.",
                  "workflows/z-child.yml": "name: z-child\nsteps: []\n",
                  "workflows/a-main.yaml": "name: a-main\nsteps: []\n"
                }
              }
            }
            """);
        var fetcher = new OrnnRemoteSkillFetcher(CreateClient(handler));

        var skill = await fetcher.FetchSkillAsync("access-token", "Workflow Skill");

        skill.Should().NotBeNull();
        var workflow = skill!.Workflows.Should().ContainSingle().Subject;
        workflow.WorkflowId.Should().Be("a-main");
        workflow.WorkflowYamls.Should().Equal(
            "name: a-main\nsteps: []",
            "name: z-child\nsteps: []");
    }

    [Fact]
    public async Task UseSkillTool_WhenNyxIdProxyReportsNotFound_ProducesNotFoundReceipt()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson(
            """{ "error": "missing" }""",
            HttpStatusCode.NotFound);
        var tool = CreateUseSkillTool(handler);
        const string arguments = """{"skill":"missing"}""";

        var result = await tool.ExecuteAsync(arguments);
        var receipt = tool.CreateResultReceipt("call-missing", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.ErrorCode.Should().Be("USE_SKILL_NOT_FOUND");
    }

    [Fact]
    public async Task UseSkillTool_WhenNyxIdProxyReportsServerError_ProducesLoadFailedReceipt()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson(
            """{ "error": "upstream failed" }""",
            HttpStatusCode.InternalServerError);
        var tool = CreateUseSkillTool(handler);
        const string arguments = """{"skill":"nyxid-service-connect"}""";

        var result = await tool.ExecuteAsync(arguments);
        var receipt = tool.CreateResultReceipt("call-server-error", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.ErrorCode.Should().Be("USE_SKILL_LOAD_FAILED");
    }

    [Fact]
    public async Task GetSkillJsonAsync_WhenNyxIdProxyForbidsAccess_ShouldThrowAccessDenied()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson(
            """{ "error": "forbidden" }""",
            HttpStatusCode.Forbidden);
        var client = CreateClient(handler, slug: "ornn-api");

        var act = async () => await client.GetSkillJsonAsync("scoped-agent-key", "daily-report");

        var assertion = await act.Should().ThrowAsync<RemoteSkillFetchException>();
        assertion.Which.FailureKind.Should().Be(RemoteSkillFetchFailureKind.AccessDenied);
        assertion.Which.HttpStatus.Should().Be(403);
        assertion.Which.Message.Should().Contain("missing proxy scope or service authorization");
        assertion.Which.Message.Should().Contain("ornn-api");
    }

    [Fact]
    public async Task GetSkillJsonAsync_WhenOrnnReturnsNestedPermissionError_ShouldSurfaceUpstreamReason()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson(
            """{ "error": { "code": "permission_denied", "message": "Missing ornn:skill:read permission" } }""",
            HttpStatusCode.Forbidden);
        var client = CreateClient(handler, slug: "ornn-api");

        var act = async () => await client.GetSkillJsonAsync("sender-token", "private-skill");

        var assertion = await act.Should().ThrowAsync<RemoteSkillFetchException>();
        assertion.Which.HttpStatus.Should().Be(403);
        assertion.Which.Message.Should().Contain("permission_denied: Missing ornn:skill:read permission");
        assertion.Which.Message.Should().NotContain("missing proxy scope");
    }

    [Fact]
    public async Task GetSkillJsonAsync_WhenOrnnReturnsProblemJsonPermissionError_ShouldSurfaceDetail()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson(
            """{ "status": 403, "code": "permission_denied", "detail": "You do not have permission to read this skill" }""",
            HttpStatusCode.Forbidden);
        var client = CreateClient(handler, slug: "ornn-api");

        var act = async () => await client.GetSkillJsonAsync("sender-token", "private-skill");

        var assertion = await act.Should().ThrowAsync<RemoteSkillFetchException>();
        assertion.Which.HttpStatus.Should().Be(403);
        assertion.Which.Message.Should().Contain("You do not have permission to read this skill");
        assertion.Which.Message.Should().NotContain("missing proxy scope");
    }

    [Fact]
    public async Task UseSkillTool_WhenPerCallTimeoutFires_ProducesLoadFailedReceipt()
    {
        // Regression for the 2026-05-13 lark-bot incident: a NyxID-proxied call to
        // `/api/v1/skills/project-summary/json` hung for 113 s, holding the Orleans grain turn.
        // OrnnSkillClient must surface a fast typed failure instead of letting one upstream request
        // stall the whole skill workflow.
        var handler = OrnnTestHttpMessageHandler.HangingUntilCanceled();
        var tool = CreateUseSkillTool(handler, perCallTimeout: TimeSpan.FromMilliseconds(150));
        const string arguments = """{"skill":"project-summary"}""";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await tool.ExecuteAsync(arguments);
        sw.Stop();
        var receipt = tool.CreateResultReceipt("call-timeout", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.ErrorCode.Should().Be("USE_SKILL_LOAD_FAILED");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "per-call timeout (150ms) must abort the stuck request");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchSkillsAsync_SurfacesTimeoutErrorWhenPerCallTimeoutFires()
    {
        // Same incident class as GetSkillJsonAsync: a stuck NyxID proxy must not hold the grain
        // turn. SearchSkillsAsync is exercised by `ornn_search_skills` when the LLM discovers
        // skills before invoking `use_skill`, so it has the same blast radius.
        var handler = OrnnTestHttpMessageHandler.HangingUntilCanceled();
        var client = CreateClient(handler, perCallTimeout: TimeSpan.FromMilliseconds(150));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await client.SearchSkillsAsync("token", "query");
        sw.Stop();

        result.Items.Should().BeEmpty();
        result.Error.Should().Contain("budget");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "per-call timeout (150ms) must abort the stuck request");
    }

    [Fact]
    public async Task GetSkillJsonAsync_DoesNotMaskCallerCancellationAsTimeoutError()
    {
        // If the caller cancels, we must NOT log the failure as "exceeded per-call budget";
        // that misroutes the diagnosis. Letting the OperationCanceledException propagate keeps
        // caller cancellation semantically distinct from our own per-call timeout fallback.
        var handler = OrnnTestHttpMessageHandler.HangingUntilCanceled();
        var client = CreateClient(handler, perCallTimeout: TimeSpan.FromSeconds(10));

        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () => await client.GetSkillJsonAsync("token", "project-summary", callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task UpdateSkillAsync_RoutesPutThroughNyxIdProxyWithEscapedIdAndZipContentType()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""{ "data": { "id": "skill-1" } }""");
        var client = CreateClient(handler);

        var result = await client.UpdateSkillAsync("access-token", "skill id/1", [1, 2, 3]);

        result.Succeeded.Should().BeTrue();
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Put);
        request.Authorization!.Parameter.Should().Be("access-token");
        request.ContentType.Should().Be("application/zip");
        request.RequestUri!.AbsoluteUri.Should().Be(
            "https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/skill%20id%2F1");
    }

    private static OrnnSkillClient CreateClient(
        OrnnTestHttpMessageHandler handler,
        string slug = "ornn",
        TimeSpan? perCallTimeout = null)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        var options = new OrnnOptions { NyxIdSlug = slug };
        return perCallTimeout is { } timeout
            ? new OrnnSkillClient(options, nyxClient, timeout)
            : new OrnnSkillClient(options, nyxClient);
    }

    private static UseSkillTool CreateUseSkillTool(
        OrnnTestHttpMessageHandler handler,
        TimeSpan? perCallTimeout = null) =>
        new(
            new LocalSkillCatalog(),
            new OrnnRemoteSkillFetcher(CreateClient(handler, perCallTimeout: perCallTimeout)),
            remoteAccessTokenResolver: new StaticRemoteSkillAccessTokenResolver());

    private sealed class StaticRemoteSkillAccessTokenResolver : IRemoteSkillAccessTokenResolver
    {
        public Task<string?> ResolveAsync(string skillName, CancellationToken ct = default) =>
            Task.FromResult<string?>("caller-token");
    }
}
