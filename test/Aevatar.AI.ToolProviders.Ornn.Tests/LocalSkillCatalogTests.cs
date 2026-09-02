using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

/// <summary>
/// Skill catalog and use_skill lookup semantics.
/// </summary>
// Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
//   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
//   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
public sealed class LocalSkillCatalogTests
{
    [Fact]
    public void LocalCatalog_ReturnsRegisteredLocalSkill()
    {
        var catalog = new LocalSkillCatalog();

        catalog.Register(MakeSkill("nyxid", instructions: "v1"));

        catalog.TryGet("nyxid", out var skill).Should().BeTrue();
        skill!.Instructions.Should().Be("v1");
    }

    [Fact]
    public void LocalCatalog_IgnoresRemoteSkillRegistration()
    {
        var catalog = new LocalSkillCatalog();

        catalog.Register(MakeSkill("nyxid", instructions: "v1", remoteId: "skill-nyxid"));

        catalog.TryGet("nyxid", out var skill).Should().BeFalse();
        skill.Should().BeNull();
        catalog.Count.Should().Be(0);
    }

    [Fact]
    public void RegisterRange_MixedSources_KeepsOnlyLocalModelInvocableSkills()
    {
        var catalog = new LocalSkillCatalog();

        catalog.RegisterRange([
            MakeSkill("local", instructions: "local-body"),
            MakeSkill("remote", instructions: "remote-body", remoteId: "skill-remote")
        ]);

        catalog.TryGet("local", out var localSkill).Should().BeTrue();
        localSkill!.Instructions.Should().Be("local-body");
        catalog.TryGet("remote", out var remoteSkill).Should().BeFalse();
        remoteSkill.Should().BeNull();
        catalog.Count.Should().Be(1);
        catalog.GetModelInvocable().Should().ContainSingle(skill => skill.Name == "local");
        catalog.BuildSystemPromptSection().Should().Contain("local").And.NotContain("remote");
    }

    [Fact]
    public async Task UseSkillTool_RemoteSkillFetchesEveryCallWithCurrentToken()
    {
        var catalog = new LocalSkillCatalog();
        var fetcher = new RecordingRemoteSkillFetcher();
        var tool = new UseSkillTool(catalog, fetcher);

        using (BeginTokenScope("token-a"))
        {
            var result = await tool.ExecuteAsync("""{"skill":"nyxid"}""");
            ExtractText(result).Should().Contain("remote-token-a-1");
            ExtractLoaded(result).Should().BeTrue();
        }

        using (BeginTokenScope("token-b"))
        {
            var result = await tool.ExecuteAsync("""{"skill":"nyxid"}""");
            ExtractText(result).Should().Contain("remote-token-b-2");
            ExtractLoaded(result).Should().BeTrue();
        }

        fetcher.Requests.Should().Equal(
            ("token-a", "nyxid"),
            ("token-b", "nyxid"));
        catalog.Count.Should().Be(0);
    }

    [Fact]
    public async Task UseSkillTool_RemoteSkillUsesResolvedRequestTokenInsteadOfAmbientOwnerToken()
    {
        var catalog = new LocalSkillCatalog();
        var fetcher = new RecordingRemoteSkillFetcher();
        var resolver = new RecordingRemoteSkillAccessTokenResolver("sender-skill-token");
        var tool = new UseSkillTool(catalog, fetcher, remoteAccessTokenResolver: resolver);

        using var _ = BeginTokenScope("owner-token");
        var result = await tool.ExecuteAsync("""{"skill":"nyxid"}""");

        ExtractLoaded(result).Should().BeTrue();
        fetcher.Requests.Should().ContainSingle().Which.Should().Be(("sender-skill-token", "nyxid"));
        resolver.Requests.Should().ContainSingle().Which.Should().Be("nyxid");
    }

    [Fact]
    public async Task UseSkillTool_WhenRequestTokenResolutionFails_DoesNotUseAmbientOwnerToken()
    {
        var catalog = new LocalSkillCatalog();
        var fetcher = new RecordingRemoteSkillFetcher();
        var resolver = new RecordingRemoteSkillAccessTokenResolver(null);
        var tool = new UseSkillTool(catalog, fetcher, remoteAccessTokenResolver: resolver);

        using var _ = BeginTokenScope("owner-token");
        var result = await tool.ExecuteAsync("""{"skill":"nyxid"}""");

        ExtractLoaded(result).Should().BeFalse();
        ExtractStatus(result).Should().Be("access_denied");
        fetcher.Requests.Should().BeEmpty("a configured resolver must never fall back to ambient owner credentials");
        resolver.Requests.Should().ContainSingle().Which.Should().Be("nyxid");
        result.Should().NotContain("owner-token");
    }

    [Fact]
    public async Task UseSkillTool_LocalSkillDoesNotCallRemoteFetcher()
    {
        var catalog = new LocalSkillCatalog();
        var fetcher = new RecordingRemoteSkillFetcher();
        var tool = new UseSkillTool(catalog, fetcher);
        catalog.Register(MakeSkill("local", instructions: "local-body"));

        using var _ = BeginTokenScope("token-a");
        var result = await tool.ExecuteAsync("""{"skill":"local"}""");
        var text = ExtractText(result);

        text.Should().Contain("local-body");
        text.Should().Contain("Skill Continuation");
        text.Should().Contain("ornn_search_skills");
        ExtractLoaded(result).Should().BeTrue();
        fetcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UseSkillTool_RendersWorkflowTemplatesBeforeAssociatedFiles()
    {
        var catalog = new LocalSkillCatalog();
        var tool = new UseSkillTool(catalog);
        catalog.Register(MakeSkill(
            "workflow-skill",
            instructions: "run workflow",
            workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "summary-report",
                    WorkflowYamls = ["name: summary-report\nsteps: []"],
                }
            ],
            associatedFiles: new Dictionary<string, string>
            {
                ["workflows/summary-report.yaml"] = "name: summary-report\nsteps: []",
            }));

        var result = await tool.ExecuteAsync("""{"skill":"workflow-skill"}""");
        var text = ExtractText(result);

        text.Should().Contain("## Workflow Templates");
        text.Should().Contain("\"workflow_id\": \"summary-report\"");
        text.Should().Contain("\"workflow_yamls\"");
        text.Should().Contain("not runnable scope workflows by themselves");
        text.Should().Contain("Scope Workflow command path");
        text.IndexOf("## Workflow Templates", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("## Associated Files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UseSkillTool_WhenMountWorkflowsIsOmitted_LoadsSkillWithoutMounting()
    {
        var catalog = new LocalSkillCatalog();
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(catalog, workflowMountPort: mountPort);
        catalog.Register(MakeSkill(
            "workflow-skill",
            workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "summary-report",
                    WorkflowYamls = ["name: summary-report\nsteps: []"],
                }
            ]));

        using var _ = BeginMetadataScope(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.ScopeId] = "scope-alpha",
            [LLMRequestMetadataKeys.OwnerSubject] = "owner-alpha",
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-a",
        }, nyxIdUserId: "nyx-user-alpha");
        const string argumentsJson = """{"skill":"workflow-skill"}""";
        var result = await tool.ExecuteAsync(argumentsJson);

        ExtractLoaded(result).Should().BeTrue();
        var receipt = ((IAgentTool)tool).CreateResultReceipt(
            "call-load", tool.Name, argumentsJson, result);
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        mountPort.Requests.Should().BeEmpty();
        ExtractWorkflowMount(result).Should().BeNull();
        ((IAgentTool)tool).GetCallSafety("""{"skill":"workflow-skill"}""").Should().Be(
            new AgentToolCallSafety(
                RequiresApproval: false,
                IsReadOnly: true,
                IsDestructive: false));
    }

    [Fact]
    public async Task UseSkillTool_MountsWorkflows_WhenExplicitlyRequested()
    {
        var catalog = new LocalSkillCatalog();
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(catalog, workflowMountPort: mountPort);
        catalog.Register(MakeSkill(
            "workflow-skill",
            workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "summary-report",
                    WorkflowYamls = ["name: summary-report\nsteps: []"],
                }
            ]));

        using var _ = BeginMetadataScope(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.ScopeId] = "scope-alpha",
            [LLMRequestMetadataKeys.OwnerSubject] = "owner-alpha",
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-a",
        }, nyxIdUserId: "nyx-user-alpha");
        const string argumentsJson =
            """{"skill":"workflow-skill","mount_workflows":true}""";
        var result = await tool.ExecuteAsync(argumentsJson);

        ExtractLoaded(result).Should().BeTrue();
        var receipt = ((IAgentTool)tool).CreateResultReceipt(
            "call-mount", tool.Name, argumentsJson, result);
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        mountPort.Requests.Should().ContainSingle();
        mountPort.Requests[0].ScopeId.Should().Be("scope-alpha");
        mountPort.Requests[0].CallerId.Should().Be("nyx-user-alpha");
        mountPort.Requests[0].NyxIdAccessToken.Should().Be("token-a");
        mountPort.Requests[0].Workflows.Should().ContainSingle(x => x.WorkflowId == "summary-report");

        using var document = JsonDocument.Parse(result);
        var workflowMount = document.RootElement.GetProperty("workflow_mount");
        workflowMount.GetProperty("status").GetString().Should().Be("mounted");
        workflowMount.GetProperty("mounted").GetBoolean().Should().BeTrue();
        workflowMount.GetProperty("workflows")[0].GetProperty("service_id").GetString().Should().Be("summary-report");
        workflowMount.GetProperty("workflows")[0].GetProperty("endpoint_id").GetString().Should().Be("chat");
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflows_SafelyDegrades_WhenScopeMissing()
    {
        var catalog = new LocalSkillCatalog();
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(catalog, workflowMountPort: mountPort);
        catalog.Register(MakeSkill(
            "workflow-skill",
            workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "summary-report",
                    WorkflowYamls = ["name: summary-report\nsteps: []"],
                }
            ]));

        using var _ = BeginMetadataScope(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-a",
        });
        const string argumentsJson =
            """{"skill":"workflow-skill","mount_workflows":true}""";
        var result = await tool.ExecuteAsync(argumentsJson);

        ExtractLoaded(result).Should().BeTrue();
        var receipt = ((IAgentTool)tool).CreateResultReceipt(
            "call-mount", tool.Name, argumentsJson, result);
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("USE_SKILL_MOUNT_MISSING_SCOPE");
        mountPort.Requests.Should().BeEmpty();

        using var document = JsonDocument.Parse(result);
        var workflowMount = document.RootElement.GetProperty("workflow_mount");
        workflowMount.GetProperty("status").GetString().Should().Be("missing_scope");
        workflowMount.GetProperty("mounted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflows_SafelyDegrades_WhenCallerIdentityMissing()
    {
        var catalog = new LocalSkillCatalog();
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(catalog, workflowMountPort: mountPort);
        catalog.Register(MakeSkill(
            "workflow-skill",
            workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "summary-report",
                    WorkflowYamls = ["name: summary-report\nsteps: []"],
                }
            ]));

        using var _ = BeginMetadataScope(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.ScopeId] = "scope-alpha",
            [LLMRequestMetadataKeys.OwnerSubject] = "owner-alpha",
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-a",
        });
        var result = await tool.ExecuteAsync("""{"skill":"workflow-skill","mount_workflows":true}""");

        ExtractLoaded(result).Should().BeTrue();
        mountPort.Requests.Should().BeEmpty();

        using var document = JsonDocument.Parse(result);
        var workflowMount = document.RootElement.GetProperty("workflow_mount");
        workflowMount.GetProperty("status").GetString().Should().Be("missing_identity");
        workflowMount.GetProperty("mounted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflows_RejectsSenderBindingIdentityWithoutCallerAuthority()
    {
        var catalog = new LocalSkillCatalog();
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(catalog, workflowMountPort: mountPort);
        catalog.Register(MakeSkill(
            "workflow-skill",
            workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "summary-report",
                    WorkflowYamls = ["name: summary-report\nsteps: []"],
                }
            ]));

        using var _ = BeginMetadataScope(
            new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.ScopeId] = "scope-alpha",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-a",
            },
            senderNyxUserId: "sender-alpha");

        var result = await tool.ExecuteAsync("""{"skill":"workflow-skill","mount_workflows":true}""");

        mountPort.Requests.Should().BeEmpty();
        using var document = JsonDocument.Parse(result);
        var workflowMount = document.RootElement.GetProperty("workflow_mount");
        workflowMount.GetProperty("status").GetString().Should().Be("missing_identity");
        workflowMount.GetProperty("mounted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void SkillFrontmatterParser_ParsesWorkflowEntryScalar()
    {
        var parser = new SkillFrontmatterParser();

        var parsed = parser.Parse("""
            ---
            name: workflow-skill
            workflow_id: main-entry
            ---
            body
            """);

        parsed.WorkflowEntry.Should().Be("main-entry");
    }

    [Fact]
    public void SkillsSource_RemoteCacheRegressionTerms_DoNotAppearInExecutableCode()
    {
        var repoRoot = FindRepoRoot();
        var productionFiles = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src", "Aevatar.AI.ToolProviders.Skills"), "*.cs")
            .OrderBy(static path => path)
            .ToArray();
        var executableSource = string.Join("\n", productionFiles.Select(path => StripComments(File.ReadAllText(path))));
        var useSkillSource = StripComments(File.ReadAllText(
            Path.Combine(repoRoot, "src", "Aevatar.AI.ToolProviders.Skills", "UseSkillTool.cs")));

        executableSource.Should().NotContain("RemoteSkillCacheTtl");
        executableSource.Should().NotContain("SkillRegistry");
        executableSource.Should().NotContain("maxAge");
        Regex.IsMatch(useSkillSource, @"FetchSkillAsync[\s\S]*?_localCatalog\.Register\s*\(")
            .Should().BeFalse("remote skill fetch results must not be written into the local process catalog");
        Regex.IsMatch(useSkillSource, @"FetchSkillAsync[\s\S]*?\.Register\s*\(")
            .Should().BeFalse("remote skill fetch results must not be cached through any catalog registration path");
    }

    // Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
    //   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
    //   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
    private sealed class RecordingRemoteSkillFetcher : IRemoteSkillFetcher
    {
        private int _calls;

        public List<(string AccessToken, string NameOrId)> Requests { get; } = [];

        public Task<SkillDefinition?> FetchSkillAsync(
            string accessToken,
            string nameOrId,
            CancellationToken ct = default)
        {
            Requests.Add((accessToken, nameOrId));
            var call = ++_calls;
            return Task.FromResult<SkillDefinition?>(MakeSkill(
                nameOrId,
                instructions: $"remote-{accessToken}-{call}",
                remoteId: $"remote-{call}"));
        }
    }

    private sealed class RecordingRemoteSkillAccessTokenResolver(string? token) : IRemoteSkillAccessTokenResolver
    {
        public List<string> Requests { get; } = [];

        public Task<string?> ResolveAsync(string skillName, CancellationToken ct = default)
        {
            Requests.Add(skillName);
            return Task.FromResult(token);
        }
    }

    private sealed class RecordingSkillWorkflowMountPort : ISkillWorkflowMountPort
    {
        public List<SkillWorkflowMountRequest> Requests { get; } = [];

        public Task<SkillWorkflowMountResult> MountAsync(
            SkillWorkflowMountRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new SkillWorkflowMountResult(
                Status: "mounted",
                Mounted: true,
                Workflows: request.Workflows.Select(workflow => new MountedSkillWorkflow(
                    workflow.WorkflowId,
                    workflow.WorkflowId,
                    "chat",
                    "rev-1")).ToArray(),
                Message: "Mounted."));
        }
    }

    private static SkillDefinition MakeSkill(
        string name,
        string instructions = "body",
        string? remoteId = null,
        IReadOnlyList<SkillWorkflowDescriptor>? workflows = null,
        IReadOnlyDictionary<string, string>? associatedFiles = null)
    {
        return new SkillDefinition
        {
            Name = name,
            Description = $"{name} description",
            Instructions = instructions,
            Source = remoteId is null ? SkillSource.Local : SkillSource.Remote,
            RemoteId = remoteId,
            Workflows = workflows ?? [],
            AssociatedFiles = associatedFiles,
        };
    }

    private static IDisposable BeginTokenScope(string token)
    {
        return BeginMetadataScope(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = token,
        });
    }

    private static IDisposable BeginMetadataScope(
        IReadOnlyDictionary<string, string> metadata,
        string? senderNyxUserId = null,
        string? nyxIdUserId = null)
    {
        var previous = AgentToolRequestContext.Current;
        var context = global::TestAgentToolContexts.FromMetadata(metadata);
        if (!string.IsNullOrWhiteSpace(senderNyxUserId))
        {
            context = context with
            {
                SenderBinding = new AgentToolSenderBindingContext(
                    "binding-alpha",
                    senderNyxUserId.Trim()),
            };
        }
        if (!string.IsNullOrWhiteSpace(nyxIdUserId))
        {
            context = context with
            {
                NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                    "nyxid",
                    "tenant-alpha",
                    nyxIdUserId.Trim()),
            };
        }

        AgentToolRequestContext.Current = context;
        return new RestoreContextScope(previous);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private static string StripComments(string source)
    {
        var withoutBlockComments = Regex.Replace(
            source,
            @"/\*.*?\*/",
            "",
            RegexOptions.Singleline);

        return Regex.Replace(
            withoutBlockComments,
            @"//.*?$",
            "",
            RegexOptions.Multiline);
    }

    // refactor helper, no behavior change
    private sealed class RestoreContextScope(AgentToolExecutionContext? previous) : IDisposable
    {
        public void Dispose() => AgentToolRequestContext.Current = previous;
    }

    private static string ExtractText(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
    }

    private static bool ExtractLoaded(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("loaded").GetBoolean();
    }

    private static string ExtractStatus(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("status").GetString() ?? string.Empty;
    }

    private static JsonElement? ExtractWorkflowMount(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("workflow_mount", out var workflowMount) &&
               workflowMount.ValueKind != JsonValueKind.Null
            ? workflowMount.Clone()
            : null;
    }
}
