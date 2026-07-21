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
    public async Task Orchestrator_ApplyInitialDirectivesAsync_WhenStructuredSearchFindsSkill_ShouldExecuteSearchThenUseSkill()
    {
        var tools = new ToolManager();
        tools.Register(new DelegateTool("ornn_search_skills", _ => SearchResult(
            status: "success",
            text: "display text changed",
            matches:
            [
                new { skill_name = "project-summary", description = "summary plan", is_private = false, category = "ops", tags = Array.Empty<string>() },
            ])));
        tools.Register(new DelegateTool("use_skill", args => LoadResult(
            status: "success",
            skillName: "project-summary",
            loaded: true,
            error: null,
            text: "loaded:" + args)));
        var orchestrator = new SkillRecoveryOrchestrator(
            Recovery(primarySkillName: null),
            _ => new StreamingToolExecutor(tools));
        var messages = new List<ChatMessage> { ChatMessage.User("/goal ship") };
        var pending = new List<ChatMessage> { messages[0] };

        var progress = new List<SkillRecoveryToolProgress>();
        await foreach (var item in orchestrator.ApplyInitialDirectivesAsync(
                           toolContext: null,
                           messages,
                           pending,
                           callIdPrefix: "req-orchestrator",
                           CancellationToken.None))
        {
            progress.Add(item);
        }

        progress.Should().HaveCount(4);
        var searchMessage = messages.Single(message =>
            message.Role == "tool" &&
            message.ToolCallId == "req-orchestrator:skill-recovery:ornn-search-skills:recovery:1");
        searchMessage.ToolResultView!.SkillSearch!.Matches[0].SkillName.Should().Be("project-summary");

        var loadMessage = messages.Single(message =>
            message.Role == "tool" &&
            message.ToolCallId == "req-orchestrator:skill-recovery:use-skill:recovery:2");
        loadMessage.ToolResultView!.SkillLoad!.Loaded.Should().BeTrue();
    }

    [Fact]
    public async Task Orchestrator_ApplyInitialDirectivesAsync_WhenCallIdPrefixIsLong_ShouldKeepSyntheticCallIdsWithinOpenAiLimit()
    {
        var tools = new ToolManager();
        tools.Register(new DelegateTool("ornn_search_skills", _ => SearchResult(
            status: "success",
            text: "display text changed",
            matches:
            [
                new { skill_name = "project-summary", description = "summary plan", is_private = false, category = "ops", tags = Array.Empty<string>() },
            ])));
        tools.Register(new DelegateTool("use_skill", _ => LoadResult(
            status: "success",
            skillName: "project-summary",
            loaded: true,
            error: null,
            text: "# project-summary\n\nInstructions")));
        var orchestrator = new SkillRecoveryOrchestrator(
            Recovery(primarySkillName: null),
            _ => new StreamingToolExecutor(tools));
        var messages = new List<ChatMessage> { ChatMessage.User("/goal ship") };
        var pending = new List<ChatMessage> { messages[0] };
        var longPrefix = "req-" + new string('a', 50);

        var progress = new List<SkillRecoveryToolProgress>();
        await foreach (var item in orchestrator.ApplyInitialDirectivesAsync(
                           toolContext: null,
                           messages,
                           pending,
                           longPrefix,
                           CancellationToken.None))
        {
            progress.Add(item);
        }

        progress.Should().HaveCount(4);
        var toolCallIds = messages
            .Where(message => message.Role == "tool")
            .Select(message => message.ToolCallId)
            .ToArray();
        toolCallIds.Should().HaveCount(2);
        toolCallIds.Should().OnlyContain(callId =>
            callId != null && callId.Length <= SkillRecoveryPlanner.MaxCallIdLength);
        toolCallIds.Should().OnlyHaveUniqueItems();
        toolCallIds[0].Should().Contain("ornn-search-skills:recovery:1");
        toolCallIds[1].Should().Contain("use-skill:recovery:2");
        $"{longPrefix}:skill-recovery:ornn-search-skills:recovery:1".Length.Should().BeGreaterThan(71);
    }

    [Fact]
    public async Task Orchestrator_TryRecoverFinalAnswerAsync_WhenTypedSearchHasMatchesButNoLoad_ShouldExecuteUseSkill()
    {
        var tools = new ToolManager();
        tools.Register(new DelegateTool("use_skill", _ => LoadResult(
            status: "success",
            skillName: "project-summary",
            loaded: true,
            error: null,
            text: "# project-summary\n\nInstructions")));
        var orchestrator = new SkillRecoveryOrchestrator(
            Recovery(primarySkillName: null, maxAttempts: 1),
            _ => new StreamingToolExecutor(tools));
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ToolResult("search-1", "ornn_search_skills", SearchResult(
                status: "success",
                text: "ornn text changed again",
                matches:
                [
                    new { skill_name = "project-summary", description = "summary plan", is_private = false, category = "ops", tags = Array.Empty<string>() },
                ])),
        };
        var pending = new List<ChatMessage>(messages);

        orchestrator.ShouldRecoverFinalAnswer(pending, "cannot complete", "req-nudge").Should().BeTrue();
        var progress = new List<SkillRecoveryToolProgress>();
        await foreach (var item in orchestrator.RecoverFinalAnswerAsync(
                           toolContext: null,
                           messages,
                           pending,
                           finalContent: "cannot complete",
                           callIdPrefix: "req-nudge",
                           CancellationToken.None))
        {
            progress.Add(item);
        }

        progress.Should().HaveCount(2);
        messages.Last().Role.Should().Be("tool");
        messages.Last().ToolResultView!.SkillLoad!.Loaded.Should().BeTrue();
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
        directive.ToolCall!.Name.Should().Be("use_skill");
    }

    [Fact]
    public void TryPlanNextDirective_WhenLongPrefixesDiffer_ShouldKeepUseSkillCallIdsBoundedAndDistinct()
    {
        var first = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: "project-summary"),
            [ChatMessage.User("/goal ship")],
            finalContent: null,
            recoveryAttempts: 0,
            callIdPrefix: "req-" + new string('a', 50),
            out var firstDirective);
        var second = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: "project-summary"),
            [ChatMessage.User("/goal ship")],
            finalContent: null,
            recoveryAttempts: 0,
            callIdPrefix: "req-" + new string('b', 50),
            out var secondDirective);

        first.Should().BeTrue();
        second.Should().BeTrue();
        firstDirective.ToolCall!.Id.Length.Should().BeLessThanOrEqualTo(SkillRecoveryPlanner.MaxCallIdLength);
        secondDirective.ToolCall!.Id.Length.Should().BeLessThanOrEqualTo(SkillRecoveryPlanner.MaxCallIdLength);
        firstDirective.ToolCall.Id.Should().NotBe(secondDirective.ToolCall.Id);
        firstDirective.ToolCall.Id.Should().Contain("use-skill");
    }

    [Fact]
    public void TryPlanNextDirective_WhenDiscoveryRequested_ShouldBuildSearchWithoutFakeSkill()
    {
        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: true,
                RequireOrnnSearchOnBlocker: false,
                CommandName: null,
                OriginalCommand: "::",
                PrimarySkillName: null,
                MaxOrnnSearchAttempts: 1,
                CommandArguments: null,
                DiscoveryRequested: true),
            [ChatMessage.User("::")],
            finalContent: null,
            recoveryAttempts: 0,
            callIdPrefix: "req-discovery",
            out var directive);

        forced.Should().BeTrue();
        directive.ToolCall!.Name.Should().Be("ornn_search_skills");
        directive.ToolCall.ArgumentsJson.Should().Contain("skill discovery");
        directive.ToolCall.ArgumentsJson.Should().NotContain("\"skill\"");
    }

    [Fact]
    public void TryPlanNextDirective_WhenPrimarySkillWasNotLoaded_ShouldBuildUseSkillCallWithCommandArgs()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship today"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ToolResult("search-1", "ornn_search_skills", SearchResult(
                status: "success",
                text: "display text one",
                matches:
                [
                    new { skill_name = "project-summary", description = "summary planning", is_private = false, category = "ops", tags = Array.Empty<string>() },
                ])),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(originalCommand: "/goal ship today", primarySkillName: "project-summary", commandArguments: "typed args"),
            messages,
            finalContent: "done",
            recoveryAttempts: 1,
            callIdPrefix: "req-2",
            out var directive);

        forced.Should().BeTrue();
        using var document = JsonDocument.Parse(directive.ToolCall!.ArgumentsJson);
        document.RootElement.GetProperty("skill").GetString().Should().Be("project-summary");
        document.RootElement.GetProperty("args").GetString().Should().Be("typed args");
    }

    [Fact]
    public void TryPlanNextDirective_WhenPrimarySkillAlreadyLoadedWithMalformedArguments_ShouldNotRepeatUseSkill()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ToolResult("search-1", "ornn_search_skills", SearchResult(status: "no_match", text: "No skills found", matches: Array.Empty<object>())),
            AssistantToolCall("use-1", "use_skill", "skill: project-summary"),
            ToolResult("use-1", "use_skill", LoadResult(
                status: "success",
                skillName: "project-summary",
                loaded: true,
                error: null,
                text: "# project-summary\n\nInstructions")),
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
    }

    [Fact]
    public void TryPlanNextDirective_WhenStructuredSearchHasMatch_ShouldUseFirstDiscoveredSkill()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ToolResult("search-1", "ornn_search_skills", SearchResult(
                status: "success",
                text: "display text changed completely",
                matches:
                [
                    new { skill_name = "project-summary", description = "summary plan", is_private = false, category = "ops", tags = Array.Empty<string>() },
                    new { skill_name = "other-skill", description = "other", is_private = true, category = "misc", tags = new[] { "a" } },
                ])),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: null),
            messages,
            finalContent: "I cannot answer yet",
            recoveryAttempts: 1,
            callIdPrefix: null,
            out var directive);

        forced.Should().BeTrue();
        directive.ToolCall!.ArgumentsJson.Should().Contain("\"skill\":\"project-summary\"");
    }

    [Fact]
    public void TryPlanNextDirective_WhenLegacyDisplayTextChangesButTypedResultStable_ShouldKeepSameDecision()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ToolResult("search-1", "ornn_search_skills", SearchResult(
                status: "success",
                text: "this text no longer says Found 1 skill or markdown bullets",
                matches:
                [
                    new { skill_name = "project-summary", description = "summary plan", is_private = false, category = "ops", tags = Array.Empty<string>() },
                ])),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: null),
            messages,
            finalContent: "done",
            recoveryAttempts: 1,
            callIdPrefix: "req-stable",
            out var directive);

        forced.Should().BeTrue();
        directive.ToolCall!.Name.Should().Be("use_skill");
        directive.ToolCall.ArgumentsJson.Should().Contain("\"skill\":\"project-summary\"");
    }

    [Fact]
    public void TryPlanNextDirective_WhenLegacyTextImpliesMatchButBoundaryCannotExtractTypedSkill_ShouldNudge()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ToolResult("search-1", "ornn_search_skills", "Found 1 skill in the catalog, see result payload"),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: null, maxAttempts: 2),
            messages,
            finalContent: "I cannot answer yet",
            recoveryAttempts: 1,
            callIdPrefix: "req-legacy-nudge",
            out var directive);

        forced.Should().BeTrue();
        directive.ToolCall.Should().BeNull();
        directive.Nudge.Should().Contain("Ornn skill search returned matching skills");
    }

    [Fact]
    public void TryPlanNextDirective_WhenStructuredSearchHasNoMatch_ShouldNotTreatAsDiscoveredSkill()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ToolResult("search-1", "ornn_search_skills", SearchResult(status: "no_match", text: "Nothing here", matches: Array.Empty<object>())),
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
    }

    [Fact]
    public void TryPlanNextDirective_WhenSearchToolErrors_ShouldNotTreatAsMatch()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ToolResult("search-1", "ornn_search_skills", SearchResult(status: "error", text: "Search failed", matches: Array.Empty<object>(), error: "backend unavailable")),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: null),
            messages,
            finalContent: "done",
            recoveryAttempts: 1,
            callIdPrefix: "req-err",
            out var directive);

        forced.Should().BeFalse();
        directive.ToolCall.Should().BeNull();
    }

    [Fact]
    public void TryPlanNextDirective_WhenUseSkillFails_ShouldBuildBlockerSearchFromTypedError()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("/goal ship"),
            AssistantToolCall("search-1", "ornn_search_skills", """{"query":"goal"}"""),
            ToolResult("search-1", "ornn_search_skills", SearchResult(status: "no_match", text: "No skills found", matches: Array.Empty<object>())),
            AssistantToolCall("use-1", "use_skill", """{"skill":"project-summary"}"""),
            ToolResult("use-1", "use_skill", LoadResult(
                status: "error",
                skillName: "project-summary",
                loaded: false,
                error: "backend unavailable while executing skill",
                text: "Some display text")),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(primarySkillName: null, maxAttempts: 2),
            messages,
            finalContent: "The skill failed.",
            recoveryAttempts: 1,
            callIdPrefix: "req-7",
            out var directive);

        forced.Should().BeTrue();
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
            ToolResult("use-1", "use_skill", LoadResult(
                status: "success",
                skillName: "project-summary",
                loaded: true,
                error: null,
                text: "# project-summary\n\nInstructions")),
        };

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(requireInitialSearch: false, primarySkillName: null, maxAttempts: 2),
            messages,
            finalContent,
            recoveryAttempts: 0,
            callIdPrefix: "req-8",
            out var directive);

        forced.Should().BeTrue();
        directive.ToolCall!.Name.Should().Be("ornn_search_skills");
    }

    [Theory]
    [InlineData("{\"error\":true,\"status\":404,\"body\":\"...\"}")]
    [InlineData("Search failed: NyxID proxy returned status=403.")]
    public void TryPlanNextDirective_WhenLegacySearchOutputStillParsesInBoundary_ShouldUseTypedError(string rawResult)
    {
        var message = ToolResult("search-1", "ornn_search_skills", rawResult);

        message.ToolResultView.Should().NotBeNull();
        message.ToolResultView!.SkillSearch!.Status.Should().Be(ToolResultViewStatus.Error);
    }

    [Theory]
    [InlineData("# project-summary\n\nInstructions")]
    [InlineData("Skill 'project-summary' not found.")]
    public void TryPlanNextDirective_WhenLegacyUseSkillOutputStillParsesInBoundary_ShouldRecoverTypedLoadState(string rawResult)
    {
        var message = ToolResult("use-1", "use_skill", rawResult);

        message.ToolResultView.Should().NotBeNull();
        message.ToolResultView!.SkillLoad.Should().NotBeNull();
    }

    private static AgentSkillRecoveryContext Recovery(
        bool requireInitialSearch = true,
        string originalCommand = "/goal ship",
        string? primarySkillName = "project-summary",
        int maxAttempts = 2,
        string? commandArguments = null) =>
        new(
            RequireInitialOrnnSearch: requireInitialSearch,
            RequireOrnnSearchOnBlocker: true,
            CommandName: "goal",
            OriginalCommand: originalCommand,
            PrimarySkillName: primarySkillName,
            MaxOrnnSearchAttempts: maxAttempts,
            CommandArguments: commandArguments,
            DiscoveryRequested: false);

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

    private static ChatMessage ToolResult(string callId, string toolName, string rawResult) =>
        ToolCallLoop.BuildToolResultMessage(callId, toolName, rawResult);

    private static string SearchResult(
        string status,
        string text,
        IEnumerable<object> matches,
        string? error = null) =>
        JsonSerializer.Serialize(new
        {
            result_type = "skill_search",
            status,
            error,
            http_status = (int?)null,
            matches,
            text,
        });

    private static string LoadResult(
        string status,
        string? skillName,
        bool loaded,
        string? error,
        string text) =>
        JsonSerializer.Serialize(new
        {
            result_type = "skill_load",
            status,
            skill_name = skillName,
            loaded,
            error,
            http_status = (int?)null,
            text,
        });

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
