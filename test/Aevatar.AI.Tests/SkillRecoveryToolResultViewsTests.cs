using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Chat;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class SkillRecoveryToolResultViewsTests
{
    private const string SearchTool = "ornn_search_skills";
    private const string LoadTool = "use_skill";

    [Theory]
    [InlineData(null, "payload")]
    [InlineData("", "payload")]
    [InlineData("   ", "payload")]
    [InlineData(SearchTool, null)]
    [InlineData(SearchTool, "")]
    [InlineData(SearchTool, "   ")]
    [InlineData("unrecognized_tool", "payload")]
    public void Parse_WhenToolOrPayloadMissingOrUnknown_ReturnsNull(string? toolName, string? rawToolResult)
    {
        SkillRecoveryToolResultViews.Parse(toolName, rawToolResult).Should().BeNull();
    }

    [Fact]
    public void Parse_StructuredSearch_MapsMatchesStatusAndDisplayText()
    {
        const string raw = """
        {
          "result_type": "skill_search",
          "status": "success",
          "http_status": 200,
          "text": "rendered display",
          "matches": [
            { "skill_name": "alpha", "description": "first", "is_private": true, "category": "ops", "tags": ["a", "b"] }
          ]
        }
        """;

        var view = SkillRecoveryToolResultViews.Parse(SearchTool, raw);

        view.Should().NotBeNull();
        view!.ToolName.Should().Be(SearchTool);
        view.SkillLoad.Should().BeNull();
        var search = view.SkillSearch!;
        search.Status.Should().Be(ToolResultViewStatus.Success);
        search.HasMatches.Should().BeTrue();
        search.HttpStatus.Should().Be(200);
        search.DisplayText.Should().Be("rendered display");
        search.Matches.Should().ContainSingle();
        var match = search.Matches[0];
        match.SkillName.Should().Be("alpha");
        match.Description.Should().Be("first");
        match.IsPrivate.Should().BeTrue();
        match.Category.Should().Be("ops");
        match.Tags.Should().Equal("a", "b");
    }

    [Fact]
    public void Parse_StructuredSearch_FallsBackToNameField_SkipsNonObjectsAndNamelessEntries()
    {
        const string raw = """
        {
          "result_type": "skill_search",
          "matches": [
            { "name": "beta" },
            123,
            { "description": "no name here" }
          ]
        }
        """;

        var search = SkillRecoveryToolResultViews.Parse(SearchTool, raw)!.SkillSearch!;

        search.Matches.Should().ContainSingle();
        search.Matches[0].SkillName.Should().Be("beta");
        search.Matches[0].IsPrivate.Should().BeFalse();
        search.Matches[0].Tags.Should().BeEmpty();
        // No explicit status, but matches present -> Success fallback; no "text" -> raw is display text.
        search.Status.Should().Be(ToolResultViewStatus.Success);
        search.HasMatches.Should().BeTrue();
        search.DisplayText.Should().Be(raw);
    }

    [Fact]
    public void Parse_StructuredSearch_NoMatchStatus_NoMatches()
    {
        const string raw = """{ "result_type": "skill_search", "status": "no_match", "text": "nothing" }""";

        var search = SkillRecoveryToolResultViews.Parse(SearchTool, raw)!.SkillSearch!;

        search.Status.Should().Be(ToolResultViewStatus.NoMatch);
        search.HasMatches.Should().BeFalse();
        search.Matches.Should().BeEmpty();
        search.DisplayText.Should().Be("nothing");
    }

    [Fact]
    public void Parse_StructuredSearch_ErrorStatusCarriesErrorAndHttpStatus()
    {
        const string raw = """{ "result_type": "skill_search", "status": "error", "error": "boom", "http_status": 500 }""";

        var search = SkillRecoveryToolResultViews.Parse(SearchTool, raw)!.SkillSearch!;

        search.Status.Should().Be(ToolResultViewStatus.Error);
        search.Error.Should().Be("boom");
        search.HttpStatus.Should().Be(500);
    }

    [Fact]
    public void Parse_LegacySearch_NumericStatusJson_IsError()
    {
        const string raw = """{ "status": 404, "message": "missing" }""";

        var search = SkillRecoveryToolResultViews.Parse(SearchTool, raw)!.SkillSearch!;

        search.Status.Should().Be(ToolResultViewStatus.Error);
        search.HttpStatus.Should().Be(404);
        search.HasMatches.Should().BeFalse();
    }

    [Theory]
    [InlineData("Search failed: upstream timeout")]
    [InlineData("Error: tool unavailable")]
    [InlineData("The skill source is not available right now")]
    [InlineData("No NyxID access token was provided")]
    public void Parse_LegacySearch_KnownErrorPhrases_AreErrors(string raw)
    {
        var search = SkillRecoveryToolResultViews.Parse(SearchTool, raw)!.SkillSearch!;

        search.Status.Should().Be(ToolResultViewStatus.Error);
        search.DisplayText.Should().Be(raw);
        search.HasMatches.Should().BeFalse();
    }

    [Theory]
    [InlineData("Error: rejected with 401", 401)]
    [InlineData("Error: gateway returned 502", 502)]
    [InlineData("Error: backend 503 unavailable", 503)]
    public void Parse_LegacySearch_ExtractsInlineHttpStatus(string raw, int expected)
    {
        var search = SkillRecoveryToolResultViews.Parse(SearchTool, raw)!.SkillSearch!;

        search.Status.Should().Be(ToolResultViewStatus.Error);
        search.HttpStatus.Should().Be(expected);
    }

    [Fact]
    public void Parse_LegacySearch_NoSkillsFound_IsNoMatch()
    {
        var search = SkillRecoveryToolResultViews.Parse(SearchTool, "No skills found for that query")!.SkillSearch!;

        search.Status.Should().Be(ToolResultViewStatus.NoMatch);
        search.HasMatches.Should().BeFalse();
    }

    [Fact]
    public void Parse_LegacySearch_FoundHeaderWithBulletMatches_IsSuccess()
    {
        const string raw = "Found 2 skills:\n- **alpha**: ship things\n- **beta** (private)";

        var search = SkillRecoveryToolResultViews.Parse(SearchTool, raw)!.SkillSearch!;

        search.Status.Should().Be(ToolResultViewStatus.Success);
        search.HasMatches.Should().BeTrue();
        search.Matches.Select(m => m.SkillName).Should().Equal("alpha", "beta");
    }

    [Fact]
    public void Parse_LegacySearch_BulletMatchesWithoutHeader_IsSuccess()
    {
        const string raw = "- **gamma**: do work\n- delta (plain): more";

        var search = SkillRecoveryToolResultViews.Parse(SearchTool, raw)!.SkillSearch!;

        search.Status.Should().Be(ToolResultViewStatus.Success);
        search.HasMatches.Should().BeTrue();
        search.Matches.Select(m => m.SkillName).Should().Equal("gamma", "delta");
    }

    [Fact]
    public void Parse_LegacySearch_UnrecognizedFreeText_IsUnknownWithoutMatches()
    {
        var search = SkillRecoveryToolResultViews.Parse(SearchTool, "some unstructured prose")!.SkillSearch!;

        search.Status.Should().Be(ToolResultViewStatus.Unknown);
        search.HasMatches.Should().BeFalse();
        search.Matches.Should().BeEmpty();
    }

    [Fact]
    public void Parse_StructuredLoad_LoadedSuccess_MapsFields()
    {
        const string raw = """
        { "result_type": "skill_load", "status": "success", "skill_name": "alpha", "loaded": true, "http_status": 200, "text": "loaded body" }
        """;

        var view = SkillRecoveryToolResultViews.Parse(LoadTool, raw);

        view!.SkillSearch.Should().BeNull();
        var load = view.SkillLoad!;
        load.Status.Should().Be(ToolResultViewStatus.Success);
        load.SkillName.Should().Be("alpha");
        load.Loaded.Should().BeTrue();
        load.HttpStatus.Should().Be(200);
        load.DisplayText.Should().Be("loaded body");
    }

    [Fact]
    public void Parse_StructuredLoad_NotLoadedWithoutStatus_IsUnknown()
    {
        const string raw = """{ "result_type": "skill_load", "loaded": false }""";

        var load = SkillRecoveryToolResultViews.Parse(LoadTool, raw)!.SkillLoad!;

        load.Loaded.Should().BeFalse();
        load.Status.Should().Be(ToolResultViewStatus.Unknown);
    }

    [Fact]
    public void Parse_LegacyLoad_ErrorJson_IsError()
    {
        const string raw = """{ "error": "skill blew up", "status": 403 }""";

        var load = SkillRecoveryToolResultViews.Parse(LoadTool, raw)!.SkillLoad!;

        load.Status.Should().Be(ToolResultViewStatus.Error);
        load.Error.Should().Be("skill blew up");
        load.HttpStatus.Should().Be(403);
        load.Loaded.Should().BeFalse();
    }

    [Fact]
    public void Parse_LegacyLoad_NotFoundText_IsNotFound()
    {
        var load = SkillRecoveryToolResultViews.Parse(LoadTool, "Requested skill not found")!.SkillLoad!;

        load.Status.Should().Be(ToolResultViewStatus.NotFound);
        load.Loaded.Should().BeFalse();
    }

    [Fact]
    public void Parse_LegacyLoad_MarkdownHeader_ExtractsSkillNameAndSucceeds()
    {
        var load = SkillRecoveryToolResultViews.Parse(LoadTool, "# project-summary\n\nInstructions follow")!.SkillLoad!;

        load.Status.Should().Be(ToolResultViewStatus.Success);
        load.SkillName.Should().Be("project-summary");
        load.Loaded.Should().BeTrue();
    }

    [Fact]
    public void Parse_LegacyLoad_PlainTextWithoutHeader_SucceedsWithoutSkillName()
    {
        var load = SkillRecoveryToolResultViews.Parse(LoadTool, "loaded without a markdown header")!.SkillLoad!;

        load.Status.Should().Be(ToolResultViewStatus.Success);
        load.SkillName.Should().BeNull();
        load.Loaded.Should().BeTrue();
    }

    [Fact]
    public void Parse_LegacyError_LongMessageIsTrimmedAndTruncated()
    {
        var raw = "Error: " + new string('x', 400);

        var search = SkillRecoveryToolResultViews.Parse(SearchTool, raw)!.SkillSearch!;

        search.Status.Should().Be(ToolResultViewStatus.Error);
        search.Error!.Should().EndWith("...");
        search.Error!.Length.Should().Be(363);
    }

    [Fact]
    public void Attach_WhenNoViewParsed_ReturnsOriginalMessageUnchanged()
    {
        var message = ChatMessage.Tool("call-1", "raw content");

        var result = SkillRecoveryToolResultViews.Attach(message, toolName: null, "raw content");

        result.Should().BeSameAs(message);
    }

    [Fact]
    public void Attach_WhenViewParsed_ProjectsDisplayTextAndAttachesView()
    {
        var message = ChatMessage.Tool("call-1", "original content");
        const string raw = """{ "result_type": "skill_load", "status": "success", "skill_name": "alpha", "loaded": true, "text": "shown to user" }""";

        var result = SkillRecoveryToolResultViews.Attach(message, LoadTool, raw);

        result.Should().NotBeSameAs(message);
        result.Role.Should().Be("tool");
        result.ToolCallId.Should().Be("call-1");
        result.Content.Should().Be("shown to user");
        result.ToolResultView!.SkillLoad!.SkillName.Should().Be("alpha");
    }

    [Theory]
    [InlineData(AgentToolReceiptStatus.Error)]
    [InlineData(AgentToolReceiptStatus.Denied)]
    [InlineData(AgentToolReceiptStatus.AuthorizationRequired)]
    public void Attach_WhenReceiptRepresentsFailure_ProjectsOnlyNarrowSafeFailureView(
        AgentToolReceiptStatus status)
    {
        var message = ChatMessage.Tool("call-failure", "safe display text");
        var receipt = new AgentToolReceipt
        {
            CallId = "call-failure",
            ToolName = "aevatar_invoke_team",
            Status = status,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            ErrorCode = "backend_unavailable",
            ErrorMessage = "safe failure message",
            ResultJson = """{"full_key":"must-not-enter-history"}""",
        };

        var result = SkillRecoveryToolResultViews.Attach(
            message,
            "aevatar_invoke_team",
            message.Content!,
            receipt);

        result.Should().NotBeSameAs(message);
        result.Content.Should().Be("safe display text");
        result.ToolResultView!.Failure.Should().Be(new ToolFailureResultView(
            status,
            "backend_unavailable",
            "safe failure message"));
        result.ToolResultView.ToString().Should().NotContain("must-not-enter-history");
    }

    [Fact]
    public void Attach_WhenReceiptIsSuccessful_DoesNotCreateFailureView()
    {
        var message = ChatMessage.Tool("call-success", "success");
        var receipt = new AgentToolReceipt
        {
            CallId = "call-success",
            ToolName = "aevatar_invoke_team",
            Status = AgentToolReceiptStatus.Success,
            ResultJson = """{"status":"accepted"}""",
        };

        var result = SkillRecoveryToolResultViews.Attach(
            message,
            "aevatar_invoke_team",
            message.Content!,
            receipt);

        result.Should().BeSameAs(message);
    }
}
