using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class SkillRecoveryPlannerTests
{
    [Fact]
    public async Task Orchestrator_ApplyInitialDirectivesAsync_WhenSearchFindsSkill_ShouldExecuteSearchThenUseSkill()
    {
        var tools = new ToolManager();
        tools.Register(new DelegateTool("ornn_search_skills", _ => "Found 1 skill\n- **project-summary**: summary plan"));
        tools.Register(new DelegateTool("use_skill", args => "loaded:" + args));
        var orchestrator = new SkillRecoveryOrchestrator(
            Recovery(primarySkillName: null),
            _ => new StreamingToolExecutor(tools));
        var messages = new List<ChatMessage> { ChatMessage.User("/goal ship") };
        var pending = new List<ChatMessage> { messages[0] };

        var applied = await orchestrator.ApplyInitialDirectivesAsync(
            toolContext: null,
            messages,
            pending,
            callIdPrefix: "req-orchestrator",
            CancellationToken.None);

        applied.Should().BeTrue();
        messages.Count(message => message.Role == "assistant" && message.ToolCalls is { Count: 1 }).Should().Be(2);
        messages
            .Where(message => message.Role == "assistant" && message.ToolCalls is { Count: 1 })
            .Should()
            .OnlyContain(message => !string.IsNullOrWhiteSpace(message.ReasoningContent));
        messages.Should().Contain(message =>
            message.Role == "tool" &&
            message.ToolCallId == "req-orchestrator:skill-recovery:ornn-search-skills:recovery:1" &&
            message.Content != null &&
            message.Content.Contains("Found 1 skill", StringComparison.Ordinal));
        messages.Should().Contain(message =>
            message.Role == "tool" &&
            message.ToolCallId == "req-orchestrator:skill-recovery:use-skill:recovery:2" &&
            message.Content != null &&
            message.Content.Contains("project-summary", StringComparison.Ordinal));
        messages
            .Where(message => message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
            .SelectMany(message => message.ToolCalls!)
            .Select(call => call.Id)
            .Should()
            .OnlyHaveUniqueItems();
        pending.Should().HaveSameCount(messages);
    }

    [Fact]
    public async Task Orchestrator_TryRecoverFinalAnswerAsync_WhenNoDirective_ShouldNotCreateExecutor()
    {
        var executorFactoryCalls = 0;
        var orchestrator = new SkillRecoveryOrchestrator(
            Recovery(requireInitialSearch: false, primarySkillName: null),
            _ =>
            {
                executorFactoryCalls++;
                return new StreamingToolExecutor(new ToolManager());
            });
        var messages = new List<ChatMessage> { ChatMessage.User("/goal ship") };
        var pending = new List<ChatMessage> { messages[0] };

        var recovered = await orchestrator.TryRecoverFinalAnswerAsync(
            toolContext: null,
            messages,
            pending,
            finalContent: "done",
            callIdPrefix: "req-noop",
            CancellationToken.None);

        recovered.Should().BeFalse();
        executorFactoryCalls.Should().Be(0);
        messages.Should().ContainSingle();
        pending.Should().ContainSingle();
    }

    [Fact]
    public async Task Orchestrator_TryRecoverFinalAnswerAsync_WhenPlannerReturnsNudge_ShouldAppendNudge()
    {
        var orchestrator = new SkillRecoveryOrchestrator(
            Recovery(primarySkillName: null, maxAttempts: 1),
            _ => new StreamingToolExecutor(new ToolManager()));
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ChatMessage.Tool("search-1", "Found 1 skill in the catalog, see result payload"),
        };
        var pending = new List<ChatMessage>(messages);

        var recovered = await orchestrator.TryRecoverFinalAnswerAsync(
            toolContext: null,
            messages,
            pending,
            finalContent: "cannot complete",
            callIdPrefix: "req-nudge",
            CancellationToken.None);

        recovered.Should().BeTrue();
        messages.Last().Role.Should().Be("user");
        messages.Last().Content.Should().Contain("Ornn skill search returned matching skills");
        pending.Last().Should().BeSameAs(messages.Last());
    }

    [Fact]
    public void TryPlanNextDirective_WhenDisabled_ShouldReturnFalse()
    {
        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            AgentSkillRecoveryContext.Empty,
            [ChatMessage.User("/goal ship")],
            finalContent: "done",
            recoveryAttempts: 0,
            callIdPrefix: "req-1",
            out var directive);

        forced.Should().BeFalse();
        directive.ToolCall.Should().BeNull();
        directive.Nudge.Should().BeNull();
        directive.ConsumesOrnnSearchAttempt.Should().BeFalse();
    }

    [Fact]
    public void TryPlanNextDirective_WhenInitialOrnnSearchMissing_ShouldBuildSearchCall()
    {
        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: "project-summary"),
            [ChatMessage.User("/goal ship")],
            finalContent: null,
            recoveryAttempts: 0,
            callIdPrefix: "req-1",
            out var directive);

        forced.Should().BeTrue();
        directive.ConsumesOrnnSearchAttempt.Should().BeTrue();
        directive.Nudge.Should().BeNull();
        directive.ToolCall.Should().NotBeNull();
        directive.ToolCall!.Id.Should().Be("req-1:skill-recovery:ornn-search-skills");
        directive.ToolCall.Name.Should().Be("ornn_search_skills");
        directive.ToolCall.ArgumentsJson.Should().Contain("\"query\":\"project-summary\"");
        directive.ToolCall.ArgumentsJson.Should().Contain("\"scope\":\"mixed\"");
    }

    [Fact]
    public void TryPlanNextDirective_WhenInitialSearchAttemptsExhausted_ShouldReturnFalse()
    {
        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: "project-summary", maxAttempts: 1),
            [ChatMessage.User("/goal ship")],
            finalContent: null,
            recoveryAttempts: 1,
            callIdPrefix: null,
            out var directive);

        forced.Should().BeFalse();
        directive.ToolCall.Should().BeNull();
        directive.Nudge.Should().BeNull();
        directive.ConsumesOrnnSearchAttempt.Should().BeFalse();
    }

    [Fact]
    public void TryPlanNextDirective_WhenPrimarySkillWasNotLoaded_ShouldBuildUseSkillCallWithCommandArgs()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship today"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ChatMessage.Tool("search-1", "Found 1 skill\n- **project-summary**: summary planning"),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(originalCommand: "/goal ship today", primarySkillName: "project-summary"),
            messages,
            finalContent: "done",
            recoveryAttempts: 1,
            callIdPrefix: "req-2",
            out var directive);

        forced.Should().BeTrue();
        directive.ConsumesOrnnSearchAttempt.Should().BeFalse();
        directive.ToolCall.Should().NotBeNull();
        directive.ToolCall!.Name.Should().Be("use_skill");
        directive.ToolCall.Id.Should().Be("req-2:skill-recovery:use-skill");
        using var document = JsonDocument.Parse(directive.ToolCall.ArgumentsJson);
        document.RootElement.GetProperty("skill").GetString().Should().Be("project-summary");
        document.RootElement.GetProperty("args").GetString().Should().Be("ship today");
    }

    [Fact]
    public void TryPlanNextDirective_WhenPrimarySkillAlreadyLoadedWithMalformedArguments_ShouldNotRepeatUseSkill()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ChatMessage.Tool("search-1", "No skills found"),
            AssistantToolCall("use-1", "use_skill", "skill: project-summary"),
            ChatMessage.Tool("use-1", "loaded"),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: "project-summary"),
            messages,
            finalContent: "done",
            recoveryAttempts: 1,
            callIdPrefix: "req-3",
            out var directive);

        forced.Should().BeFalse();
        directive.ToolCall.Should().BeNull();
        directive.Nudge.Should().BeNull();
        directive.ConsumesOrnnSearchAttempt.Should().BeFalse();
    }

    [Fact]
    public void TryPlanNextDirective_WhenLatestSearchHasMarkdownMatch_ShouldUseFirstDiscoveredSkill()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ChatMessage.Tool("search-1", "Found 2 skills\n- **project-summary**: summary plan\n- other"),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: null),
            messages,
            finalContent: "I cannot answer yet",
            recoveryAttempts: 1,
            callIdPrefix: null,
            out var directive);

        forced.Should().BeTrue();
        directive.ToolCall.Should().NotBeNull();
        directive.ToolCall!.Id.Should().Be("skill-recovery:use-skill");
        directive.ToolCall.Name.Should().Be("use_skill");
        directive.ToolCall.ArgumentsJson.Should().Contain("\"skill\":\"project-summary\"");
    }

    [Fact]
    public void TryPlanNextDirective_WhenLatestSearchHasPlainMatch_ShouldTrimBeforeParenthesis()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ChatMessage.Tool("search-1", "Found 1 skill\n- project-summary (remote): summary plan"),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: null),
            messages,
            finalContent: "I cannot answer yet",
            recoveryAttempts: 1,
            callIdPrefix: "req-4",
            out var directive);

        forced.Should().BeTrue();
        directive.ToolCall.Should().NotBeNull();
        directive.ToolCall!.ArgumentsJson.Should().Contain("\"skill\":\"project-summary\"");
    }

    [Fact]
    public void TryPlanNextDirective_WhenSearchHasMatchButNoExtractableSkillAndAttemptsRemain_ShouldNudge()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ChatMessage.Tool("search-1", "Found 1 skill in the catalog, see result payload"),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: null, maxAttempts: 2),
            messages,
            finalContent: "I cannot answer yet",
            recoveryAttempts: 1,
            callIdPrefix: "req-5",
            out var directive);

        forced.Should().BeTrue();
        directive.ToolCall.Should().BeNull();
        directive.ConsumesOrnnSearchAttempt.Should().BeTrue();
        directive.Nudge.Should().Contain("Ornn skill search returned matching skills");
        directive.Nudge.Should().Contain("/goal ship");
    }

    [Fact]
    public void TryPlanNextDirective_WhenSearchHasMatchButNoExtractableSkillAndAttemptsExhausted_ShouldNotNudge()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ChatMessage.Tool("search-1", "Found 1 skill in the catalog, see result payload"),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: null, maxAttempts: 1),
            messages,
            finalContent: "I cannot answer yet",
            recoveryAttempts: 1,
            callIdPrefix: "req-5",
            out var directive);

        forced.Should().BeFalse();
        directive.ToolCall.Should().BeNull();
        directive.ConsumesOrnnSearchAttempt.Should().BeFalse();
        directive.Nudge.Should().BeNull();
    }

    [Fact]
    public void TryPlanNextDirective_WhenSearchHasNoMatchPhrase_ShouldNotTreatAsDiscoveredSkill()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ChatMessage.Tool("search-1", "Search failed: Found 1 skill but backend unavailable"),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: null),
            messages,
            finalContent: "done",
            recoveryAttempts: 1,
            callIdPrefix: "req-6",
            out var directive);

        forced.Should().BeFalse();
        directive.ToolCall.Should().BeNull();
        directive.Nudge.Should().BeNull();
        directive.ConsumesOrnnSearchAttempt.Should().BeFalse();
    }

    [Fact]
    public void TryPlanNextDirective_WhenBlockerAppearsAfterUseSkill_ShouldBuildBlockerSearch()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ChatMessage.Tool("search-1", "No skills found"),
            AssistantToolCall("use-1", "use_skill", """{"skill":"project-summary"}"""),
            ChatMessage.Tool("use-1", "{\"error\":\"backend unavailable while executing skill\"}"),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: null, maxAttempts: 2),
            messages,
            finalContent: "The skill failed.",
            recoveryAttempts: 1,
            callIdPrefix: "req-7",
            out var directive);

        forced.Should().BeTrue();
        directive.ConsumesOrnnSearchAttempt.Should().BeTrue();
        directive.ToolCall.Should().NotBeNull();
        directive.ToolCall!.Name.Should().Be("ornn_search_skills");
        directive.ToolCall.ArgumentsJson.Should().Contain("backend unavailable");
    }

    [Theory]
    [InlineData("无法完成请求")]
    [InlineData("The command cannot complete")]
    public void TryPlanNextDirective_WhenFinalAnswerContainsBlocker_ShouldBuildBlockerSearch(string finalContent)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("use-1", "use_skill", """{"skill":"project-summary"}"""),
            ChatMessage.Tool("use-1", "loaded"),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(requireInitialSearch: false, primarySkillName: null, maxAttempts: 2),
            messages,
            finalContent,
            recoveryAttempts: 0,
            callIdPrefix: "req-8",
            out var directive);

        forced.Should().BeTrue();
        directive.ToolCall.Should().NotBeNull();
        directive.ToolCall!.Name.Should().Be("ornn_search_skills");
    }

    [Theory]
    [InlineData("{\"error\":true,\"status\":404,\"body\":\"...\"}")]
    [InlineData("NyxID API request failed: GET https://nyx-api.example/api/v1/skills/project-summary/files -> 404")]
    [InlineData("Upstream returned 403 forbidden while listing repository contents")]
    [InlineData("Unauthorized: token missing required scope")]
    [InlineData("Bad Request: parameter team_id was rejected")]
    public void TryPlanNextDirective_WhenToolResultCarriesHttpStatusBlocker_ShouldBuildBlockerSearch(string toolResult)
    {
        // /summary wandering after use_skill is the actual prod symptom we are guarding
        // against: nyxid_proxy tool results come back as 404/401/403/500 envelopes and
        // the LLM keeps trying alternate paths instead of re-searching Ornn. The planner
        // should treat these envelopes as blockers so the next recovery directive fires
        // a fresh ornn_search_skills with the upstream failure as the query.
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/summary alice"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"project-summary"}"""),
            ChatMessage.Tool("search-1", "Found 1 skill\n- **project-summary**: summary plan"),
            AssistantToolCall("use-1", "use_skill", """{"skill":"project-summary"}"""),
            ChatMessage.Tool("use-1", "loaded"),
            AssistantToolCall("proxy-1", "nyxid_proxy", """{"slug":"ornn-api","path":"/api/v1/skills/project-summary/files"}"""),
            ChatMessage.Tool("proxy-1", toolResult),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: "project-summary", maxAttempts: 2),
            messages,
            finalContent: "Following the loaded skill, but proxy call failed.",
            recoveryAttempts: 1,
            callIdPrefix: "req-http-blocker",
            out var directive);

        forced.Should().BeTrue();
        directive.ConsumesOrnnSearchAttempt.Should().BeTrue();
        directive.ToolCall.Should().NotBeNull();
        directive.ToolCall!.Name.Should().Be("ornn_search_skills");
    }

    [Fact]
    public void TryPlanNextDirective_WhenBlockerSearchAttemptsExhausted_ShouldReturnFalse()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("use-1", "use_skill", """{"skill":"project-summary"}"""),
            ChatMessage.Tool("use-1", "loaded"),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(requireInitialSearch: false, primarySkillName: null, maxAttempts: 1),
            messages,
            finalContent: "cannot complete",
            recoveryAttempts: 1,
            callIdPrefix: "req-9",
            out var directive);

        forced.Should().BeFalse();
        directive.ToolCall.Should().BeNull();
        directive.Nudge.Should().BeNull();
        directive.ConsumesOrnnSearchAttempt.Should().BeFalse();
    }

    [Fact]
    public void TryPlanNextDirective_WhenNoUseSkillCallExistsForBlockerOnlyRecovery_ShouldReturnFalse()
    {
        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(requireInitialSearch: false, primarySkillName: null),
            [ChatMessage.User("/goal ship")],
            finalContent: "cannot complete",
            recoveryAttempts: 0,
            callIdPrefix: "req-10",
            out var directive);

        forced.Should().BeFalse();
        directive.ToolCall.Should().BeNull();
        directive.Nudge.Should().BeNull();
        directive.ConsumesOrnnSearchAttempt.Should().BeFalse();
    }

    private static AgentSkillRecoveryContext Recovery(
        bool requireInitialSearch = true,
        string originalCommand = "/goal ship",
        string? primarySkillName = "project-summary",
        int maxAttempts = 2) =>
        new(
            RequireInitialOrnnSearch: requireInitialSearch,
            RequireOrnnSearchOnBlocker: true,
            CommandName: "goal",
            OriginalCommand: originalCommand,
            PrimarySkillName: primarySkillName,
            MaxOrnnSearchAttempts: maxAttempts);

    private static ChatMessage AssistantToolCall(string id, string name, string argumentsJson) =>
        new()
        {
            Role = "assistant",
            ToolCalls =
            [
                new ToolCall
                {
                    Id = id,
                    Name = name,
                    ArgumentsJson = argumentsJson,
                },
            ],
        };

    private sealed class DelegateTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name => name;
        public string Description => "delegate";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(execute(argumentsJson));
        }
    }
}
