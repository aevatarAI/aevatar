using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;
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
            result.Should().Contain("remote-token-a-1");
        }

        using (BeginTokenScope("token-b"))
        {
            var result = await tool.ExecuteAsync("""{"skill":"nyxid"}""");
            result.Should().Contain("remote-token-b-2");
        }

        fetcher.Requests.Should().Equal(
            ("token-a", "nyxid"),
            ("token-b", "nyxid"));
        catalog.Count.Should().Be(0);
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

        result.Should().Contain("local-body");
        result.Should().Contain("Skill Continuation");
        result.Should().Contain("ornn_search_skills");
        fetcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UseSkillTool_RendersWorkflowHandoffBeforeAssociatedFiles()
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

        result.Should().Contain("## aevatar_start_workflow Handoff");
        result.Should().Contain("\"workflow_id\": \"summary-report\"");
        result.Should().Contain("\"workflow_yamls\"");
        result.IndexOf("## aevatar_start_workflow Handoff", StringComparison.Ordinal)
            .Should().BeLessThan(result.IndexOf("## Associated Files", StringComparison.Ordinal));
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
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = token,
        });

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
}
