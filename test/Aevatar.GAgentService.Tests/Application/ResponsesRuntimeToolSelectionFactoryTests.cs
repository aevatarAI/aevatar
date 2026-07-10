using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesRuntimeToolSelectionFactoryTests
{
    private static ResponsesToolClassification Classification(
        IReadOnlyList<ResponsesApplicationToolDeclaration>? forwarded = null,
        IReadOnlyList<string>? substituted = null,
        IReadOnlyList<string>? additive = null,
        IReadOnlyList<string>? owned = null) =>
        new(
            forwarded ?? [],
            EffectiveTools: [],
            substituted ?? [],
            additive ?? [],
            owned ?? []);

    [Fact]
    public void Create_ShouldCopyClassificationNameLists()
    {
        var selection = ResponsesRuntimeToolSelectionFactory.Create(
            Classification(
                substituted: ["web_search"],
                additive: ["aevatar_todo_write", "custom_additive"],
                owned: ["web_search", "aevatar_todo_write", "custom_additive"]),
            ResponsesToolChoiceHintPlan.Empty,
            routeToolSetName: null);

        selection.SubstitutedToolNames.Should().Equal("web_search");
        selection.AdditiveToolNames.Should().Equal("aevatar_todo_write", "custom_additive");
        selection.OwnedToolNames.Should().Equal("web_search", "aevatar_todo_write", "custom_additive");
    }

    [Fact]
    public void Create_WhenRouteToolSetNameIsProvided_ShouldSetToolSetName()
    {
        var selection = ResponsesRuntimeToolSelectionFactory.Create(
            Classification(),
            ResponsesToolChoiceHintPlan.Empty,
            routeToolSetName: "workspace.default");

        selection.ToolSetName.Should().Be("workspace.default");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WhenRouteToolSetNameIsBlank_ShouldLeaveToolSetNameEmpty(string? routeToolSetName)
    {
        var selection = ResponsesRuntimeToolSelectionFactory.Create(
            Classification(),
            ResponsesToolChoiceHintPlan.Empty,
            routeToolSetName);

        // proto string default is empty, never null.
        selection.ToolSetName.Should().BeEmpty();
    }

    [Fact]
    public void Create_WhenToolChoiceHintIsEmpty_ShouldNotSetHintFields()
    {
        var selection = ResponsesRuntimeToolSelectionFactory.Create(
            Classification(),
            ResponsesToolChoiceHintPlan.Empty,
            routeToolSetName: null);

        selection.ToolChoiceHintName.Should().BeEmpty();
        selection.ToolChoiceHintArgumentsJson.Should().BeEmpty();
        selection.ToolChoiceHintArguments.Should().BeNull();
    }

    [Fact]
    public void Create_WhenToolChoiceHintIsPresent_ShouldSetHintNameJsonAndStruct()
    {
        var prefilled = new Struct
        {
            Fields =
            {
                ["city"] = Google.Protobuf.WellKnownTypes.Value.ForString("Singapore"),
            },
        };
        var hintPlan = ResponsesToolChoiceHints.Create("get_weather", prefilled);

        var selection = ResponsesRuntimeToolSelectionFactory.Create(
            Classification(),
            hintPlan,
            routeToolSetName: null);

        selection.ToolChoiceHintName.Should().Be("get_weather");
        selection.ToolChoiceHintArgumentsJson.Should().Be("""{"city":"Singapore"}""");
        selection.ToolChoiceHintArguments.Should().NotBeNull();
        selection.ToolChoiceHintArguments.Fields["city"].StringValue.Should().Be("Singapore");
    }

    [Fact]
    public void Create_WhenToolChoiceHintHasNoPrefilledArguments_ShouldEmitEmptyJsonObjectAndStruct()
    {
        var hintPlan = ResponsesToolChoiceHints.Create("get_weather", prefilledArguments: null);

        var selection = ResponsesRuntimeToolSelectionFactory.Create(
            Classification(),
            hintPlan,
            routeToolSetName: null);

        selection.ToolChoiceHintName.Should().Be("get_weather");
        selection.ToolChoiceHintArgumentsJson.Should().Be("{}");
        selection.ToolChoiceHintArguments.Should().NotBeNull();
        selection.ToolChoiceHintArguments.Fields.Should().BeEmpty();
    }

    [Fact]
    public void Create_ShouldProjectForwardedToolsWithParsedParametersStruct()
    {
        var selection = ResponsesRuntimeToolSelectionFactory.Create(
            Classification(forwarded:
            [
                new ResponsesApplicationToolDeclaration(
                    "client_tool",
                    "client description",
                    """{"type":"object","properties":{"q":{"type":"string"}}}""",
                    "schema-hash"),
            ]),
            ResponsesToolChoiceHintPlan.Empty,
            routeToolSetName: null);

        var forwarded = selection.ForwardedTools.Should().ContainSingle().Subject;
        forwarded.ToolName.Should().Be("client_tool");
        forwarded.Description.Should().Be("client description");
        forwarded.ParametersJson.Should().Be("""{"type":"object","properties":{"q":{"type":"string"}}}""");
        forwarded.SchemaHash.Should().Be("schema-hash");
        forwarded.Parameters.Should().NotBeNull();
        forwarded.Parameters.Fields.Should().ContainKey("type");
        forwarded.Parameters.Fields["type"].StringValue.Should().Be("object");
    }

    [Fact]
    public void Create_WhenForwardedToolParametersJsonIsMalformed_ShouldYieldEmptyParametersStruct()
    {
        var selection = ResponsesRuntimeToolSelectionFactory.Create(
            Classification(forwarded:
            [
                new ResponsesApplicationToolDeclaration(
                    "broken_tool",
                    "desc",
                    "not json {",
                    "hash"),
            ]),
            ResponsesToolChoiceHintPlan.Empty,
            routeToolSetName: null);

        var forwarded = selection.ForwardedTools.Should().ContainSingle().Subject;
        forwarded.ParametersJson.Should().Be("not json {");
        forwarded.Parameters.Should().NotBeNull();
        forwarded.Parameters.Fields.Should().BeEmpty();
    }

    [Fact]
    public void Create_ShouldProjectMultipleForwardedToolsInOrder()
    {
        var selection = ResponsesRuntimeToolSelectionFactory.Create(
            Classification(forwarded:
            [
                new ResponsesApplicationToolDeclaration("tool_a", "a", """{"type":"object"}""", "hash-a"),
                new ResponsesApplicationToolDeclaration("tool_b", "b", """{"type":"object"}""", "hash-b"),
            ]),
            ResponsesToolChoiceHintPlan.Empty,
            routeToolSetName: null);

        selection.ForwardedTools.Select(static tool => tool.ToolName)
            .Should().Equal("tool_a", "tool_b");
    }

    [Fact]
    public void Create_WhenClassificationIsEmpty_ShouldProduceEmptySelection()
    {
        var selection = ResponsesRuntimeToolSelectionFactory.Create(
            Classification(),
            ResponsesToolChoiceHintPlan.Empty,
            routeToolSetName: null);

        selection.ForwardedTools.Should().BeEmpty();
        selection.SubstitutedToolNames.Should().BeEmpty();
        selection.AdditiveToolNames.Should().BeEmpty();
        selection.OwnedToolNames.Should().BeEmpty();
        selection.ToolSetName.Should().BeEmpty();
        selection.ToolChoiceHintName.Should().BeEmpty();
    }
}
