using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;

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
        fetcher.Requests.Should().BeEmpty();
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

    private static SkillDefinition MakeSkill(string name, string instructions = "body", string? remoteId = null)
    {
        return new SkillDefinition
        {
            Name = name,
            Description = $"{name} description",
            Instructions = instructions,
            Source = remoteId is null ? SkillSource.Local : SkillSource.Remote,
            RemoteId = remoteId,
        };
    }

    private static IDisposable BeginTokenScope(string token)
    {
        var previous = AgentToolRequestContext.CurrentMetadata;
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = token,
        };

        return new RestoreContextScope(previous);
    }

    // refactor helper, no behavior change
    private sealed class RestoreContextScope(IReadOnlyDictionary<string, string>? previous) : IDisposable
    {
        public void Dispose() => AgentToolRequestContext.CurrentMetadata = previous;
    }
}
