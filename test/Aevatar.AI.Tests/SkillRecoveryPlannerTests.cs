using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
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
            toolContext => NewStreamingToolExecutor(tools, toolContext));
        var messages = new List<ChatMessage> { ChatMessage.User("/goal ship") };
        var pending = new List<ChatMessage> { messages[0] };

        var progress = new List<SkillRecoveryToolProgress>();
        await foreach (var item in orchestrator.ApplyInitialDirectivesAsync(
                           toolContext: TestToolContext("req-orchestrator"),
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
            toolContext => NewStreamingToolExecutor(tools, toolContext));
        var messages = new List<ChatMessage> { ChatMessage.User("/goal ship") };
        var pending = new List<ChatMessage> { messages[0] };
        var longPrefix = "req-" + new string('a', 50);

        var progress = new List<SkillRecoveryToolProgress>();
        await foreach (var item in orchestrator.ApplyInitialDirectivesAsync(
                           toolContext: TestToolContext(longPrefix),
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
            toolContext => NewStreamingToolExecutor(tools, toolContext));
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
                           toolContext: TestToolContext("req-nudge"),
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
    public async Task Orchestrator_ApplyInitialDirectivesAsync_WhenToolFails_ShouldRedactPendingArguments()
    {
        var tools = new ToolManager();
        tools.Register(new FailedReceiptTool("ornn_search_skills"));
        var orchestrator = new SkillRecoveryOrchestrator(
            Recovery(
                primarySkillName: null,
                maxAttempts: 1,
                originalCommand: "/goal query-secret",
                commandArguments: "header-secret"),
            toolContext => NewStreamingToolExecutor(tools, toolContext));
        var messages = new List<ChatMessage> { ChatMessage.User("/goal query-secret") };
        var pending = new List<ChatMessage> { messages[0] };

        var progress = new List<SkillRecoveryToolProgress>();
        await foreach (var item in orchestrator.ApplyInitialDirectivesAsync(
                           toolContext: TestToolContext("req-sensitive-recovery"),
                           messages,
                           pending,
                           callIdPrefix: "req-sensitive-recovery",
                           CancellationToken.None))
        {
            progress.Add(item);
        }

        progress.Should().HaveCount(2);
        var assistant = messages.Should().ContainSingle(message =>
            message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls.Count == 1).Which;
        assistant.ToolCalls![0].Id.Should().NotBeNullOrWhiteSpace();
        assistant.ToolCalls[0].Name.Should().Be("ornn_search_skills");
        assistant.ToolCalls[0].ArgumentsJson.Should()
            .NotContain("query-secret")
            .And.NotContain("header-secret");
        assistant.ToolCalls[0].ArgumentsJson.Should().Be("{}");
        pending.Should().ContainSingle(message => ReferenceEquals(message, assistant));
        messages.Should().ContainSingle(message =>
            message.Role == "tool" && message.ToolCallId == assistant.ToolCalls[0].Id);
    }

    [Fact]
    public async Task Orchestrator_ApplyInitialDirectivesAsync_WhenPrimarySkillFails_ShouldAttemptItOnlyOnce()
    {
        var tools = new ToolManager();
        tools.Register(new FailedReceiptTool("use_skill"));
        tools.Register(new FailedReceiptTool("ornn_search_skills"));
        var orchestrator = new SkillRecoveryOrchestrator(
            Recovery(primarySkillName: "project-summary", maxAttempts: 1),
            toolContext => NewStreamingToolExecutor(tools, toolContext));
        var messages = new List<ChatMessage> { ChatMessage.User("/goal ship") };
        var pending = new List<ChatMessage> { messages[0] };

        await foreach (var _ in orchestrator.ApplyInitialDirectivesAsync(
                           toolContext: TestToolContext("req-primary-failure"),
                           messages,
                           pending,
                           callIdPrefix: "req-primary-failure",
                           CancellationToken.None))
        {
        }

        var useSkillCalls = messages
            .SelectMany(message => message.ToolCalls ?? [])
            .Where(call => string.Equals(call.Name, "use_skill", StringComparison.Ordinal))
            .ToArray();
        useSkillCalls.Should().ContainSingle();
        useSkillCalls[0].ArgumentsJson.Should().Be("{}");
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

    [Fact]
    public void TryPlanNextDirective_WhenChannelWorkflowDeliveryRequiresConfiguration_ShouldNotSearchForAnotherSkill()
    {
        const string displayText = "当前 channel workflow delivery 不可用。";
        var receipt = new AgentToolReceipt
        {
            CallId = "invoke-1",
            ToolName = "aevatar_invoke_team",
            Status = AgentToolReceiptStatus.Error,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            ErrorCode = AgentToolFailureCodes.ChannelWorkflowResultDeliveryUnavailable,
            ErrorMessage = "Open /channels and choose Repair workflow replies.",
            ResultJson = """{"error":{"code":"channel_workflow_delivery_unavailable"}}""",
        };
        var messages = MessagesWithInvocationFailure(displayText, receipt);

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(requireInitialSearch: false, primarySkillName: null, maxAttempts: 2),
            messages,
            finalContent: "当前 channel workflow delivery 不可用。 The workflow is unavailable.",
            recoveryAttempts: 0,
            callIdPrefix: "request-alpha",
            out var directive);

        messages.Last().ToolResultView!.Failure!.ErrorCode.Should().Be(
            AgentToolFailureCodes.ChannelWorkflowResultDeliveryUnavailable);
        forced.Should().BeFalse();
        directive.ToolCall.Should().BeNull();
    }

    [Fact]
    public void TryPlanNextDirective_WhenSameDisplayTextHasAnotherErrorCode_ShouldRetainBlockerSearch()
    {
        const string displayText = "当前 channel workflow delivery 不可用。";
        var receipt = new AgentToolReceipt
        {
            CallId = "invoke-1",
            ToolName = "aevatar_invoke_team",
            Status = AgentToolReceiptStatus.Error,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            ErrorCode = "backend_unavailable",
            ErrorMessage = displayText,
            ResultJson = """{"error":{"code":"backend_unavailable"}}""",
        };
        var messages = MessagesWithInvocationFailure(displayText, receipt);

        var forced = SkillRecoveryPlanner.TryPlanNextDirective(
            Recovery(requireInitialSearch: false, primarySkillName: null, maxAttempts: 2),
            messages,
            finalContent: "当前 channel workflow delivery 不可用。 The workflow is unavailable.",
            recoveryAttempts: 0,
            callIdPrefix: "request-beta",
            out var directive);

        forced.Should().BeTrue();
        directive.ToolCall.Should().NotBeNull();
        directive.ToolCall!.Name.Should().Be("ornn_search_skills");
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

    private static StreamingToolExecutor NewStreamingToolExecutor(
        ToolManager tools,
        AgentToolExecutionContext? toolContext) =>
        new(
            tools,
            toolContext: toolContext,
            toolExecutionPort: new AdmittedAgentToolExecutor(
                AlwaysStartingAgentToolAdmissionLedger.Instance,
                new AppendedAuditTrail(),
                new StableIdentityHasher()));

    private static AgentToolExecutionContext TestToolContext(string requestId) =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(requestId, null),
            ExecutionOwner = AgentToolExecutionOwners.HostService(nameof(SkillRecoveryPlannerTests)),
        };

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

    private static List<ChatMessage> MessagesWithInvocationFailure(
        string displayText,
        AgentToolReceipt receipt) =>
    [
        ChatMessage.User("/goal ship"),
        AssistantToolCall("use-1", "use_skill", """{"skill":"project-summary"}"""),
        ToolResult("use-1", "use_skill", LoadResult(
            status: "success",
            skillName: "project-summary",
            loaded: true,
            error: null,
            text: "# project-summary\n\nInstructions")),
        AssistantToolCall("invoke-1", "aevatar_invoke_team", """{"team_id":"team-alpha"}"""),
        ToolResult("invoke-1", "aevatar_invoke_team", displayText, receipt),
    ];

    private static ChatMessage ToolResult(
        string callId,
        string toolName,
        string rawResult,
        AgentToolReceipt? receipt = null) =>
        ToolCallLoop.BuildToolResultMessage(callId, toolName, rawResult, receipt);

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
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public AgentToolReceipt? CreateSuccessReceipt(
            string callId,
            string toolName,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
            };

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(execute(argumentsJson));
        }
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }

    private sealed class FailedReceiptTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => "returns a typed failure";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(SearchResult(
                status: "error",
                text: "Search failed.",
                matches: [],
                error: "safe_failure"));
        }

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Error,
                ErrorCode = "SAFE_FAILURE",
                ErrorMessage = "Search failed.",
                ResultJson = resultJson,
            };
    }
}
