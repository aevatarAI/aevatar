using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowParserPresentationStructureTests
{
    [Theory]
    [MemberData(nameof(InvalidToolScopes))]
    public void Parse_WhenToolScopeContainsObjectsOrNullEntries_ShouldRejectAtTheSourcePath(
        string yaml,
        string expectedPath)
    {
        AssertStructureFailure(yaml, expectedPath);
    }

    [Theory]
    [MemberData(nameof(ValidToolScopes))]
    public void Parse_WhenToolScopeUsesSupportedScalarOrListForms_ShouldPreserveTheNames(
        string owner,
        string field,
        string value,
        string[] expectedNames)
    {
        var workflow = new WorkflowParser().Parse(BuildToolScopeYaml(owner, field, value));
        var scope = owner == "role" ? workflow.Roles[0].AgentToolScope : workflow.Steps[0].AgentToolScope;
        var isAllowedTools = field is "allowed_tools" or "allowedTools";

        scope.Should().NotBeNull();
        scope!.RestrictAllowedToolNames.Should().Be(isAllowedTools);
        scope.RestrictToolSets.Should().Be(!isAllowedTools);
        if (isAllowedTools)
            scope.AllowedToolNames.Should().Equal(expectedNames);
        else
            scope.ToolSetRefs.Should().Equal(expectedNames);

        if (owner == "parameters")
            workflow.Steps[0].Parameters.Should().NotContainKey(field);
    }

    [Theory]
    [InlineData("interaction_spec: []", "/steps/0/interaction_spec")]
    [InlineData("interaction_spec: 42", "/steps/0/interaction_spec")]
    [InlineData("interaction_template_spec: []", "/steps/0/interaction_template_spec")]
    [InlineData("presentation: { interaction_spec: [] }", "/steps/0/presentation/interaction_spec")]
    [InlineData("parameters: { interaction_spec: [] }", "/steps/0/parameters/interaction_spec")]
    [InlineData("parameters: { interactionSpec: [] }", "/steps/0/parameters/interactionSpec")]
    [InlineData("interaction_spec: { title: Review, actions: [null] }", "/steps/0/interaction_spec/actions/0")]
    [InlineData("interaction_spec: { title: Review, fields: [invalid] }", "/steps/0/interaction_spec/fields/0")]
    [InlineData("interaction_spec: { title: Review, cards: [null] }", "/steps/0/interaction_spec/cards/0")]
    [InlineData("presentation: { title: Review, actions: [invalid] }", "/steps/0/presentation/actions/0")]
    public void Parse_WhenInteractionPresentationHasTheWrongShape_ShouldRejectBeforeDiscardingIt(
        string fields,
        string expectedPath)
    {
        AssertStructureFailure($"name: bad_presentation\nsteps:\n  - id: notify\n    type: notify\n    {fields}\n", expectedPath);
    }

    [Theory]
    [InlineData("{ title: Review, actions: [{ action_id: approve, label: Approve }] }")]
    [InlineData("'{\"title\":\"Review\",\"actions\":[{\"action_id\":\"approve\",\"label\":\"Approve\"}]}'")]
    public void Parse_WhenInteractionSpecUsesAYamlMappingOrJsonObjectString_ShouldPreserveItsContent(string spec)
    {
        var yaml = $"name: valid_presentation\nsteps:\n  - id: notify\n    type: notify\n    interaction_spec: {spec}\n";

        var workflow = new WorkflowParser().Parse(yaml);

        var interaction = workflow.Steps[0].Presentation?.InteractionSpec;
        interaction.Should().NotBeNull();
        interaction!.Title.Should().Be("Review");
        interaction.Actions.Should().ContainSingle().Which.ActionId.Should().Be("approve");
    }

    [Theory]
    [InlineData("{ actions: [{ action_id: approve, id: reject }] }")]
    [InlineData("'{\"actions\":[{\"action_id\":\"approve\",\"id\":\"reject\"}]}'")]
    public void Parse_WhenInteractionAliasesSpecifyTheSameFieldTwice_ShouldRejectTheSecondAlias(string spec)
    {
        var yaml = $"name: duplicate_action_alias\nsteps:\n  - id: notify\n    type: notify\n    interaction_spec: {spec}\n";

        AssertStructureFailure(yaml, "/steps/0/interaction_spec/actions/0/id");
    }

    [Theory]
    [InlineData("allowed_tools: [search], allowedTools: [calendar]", "allowedTools")]
    [InlineData("tool_sets: [search], toolSets: [calendar]", "toolSets")]
    [InlineData("interaction_spec: { title: Review }, interactionSpec: { title: Other }", "interactionSpec")]
    [InlineData("interaction_template_spec: { template_id: first }, interactionTemplateSpec: { template_id: second }", "interactionTemplateSpec")]
    public void Parse_WhenParameterAliasesSpecifyTheSameReservedFieldTwice_ShouldRejectTheSecondAlias(
        string parameters,
        string secondAlias)
    {
        var yaml = $"name: duplicate_parameter_alias\nsteps:\n  - id: notify\n    type: notify\n    parameters: {{ {parameters} }}\n";

        AssertStructureFailure(yaml, $"/steps/0/parameters/{secondAlias}");
    }

    [Fact]
    public void Parse_WhenJsonEncodedInteractionExceedsTheNodeBudget_ShouldRejectTheDecodedTree()
    {
        var cards = string.Join(',', Enumerable.Repeat("{\"title\":\"card\"}", WorkflowYamlResourceGuard.MaxNodes / 2));
        var yaml = $"name: oversized_interaction\nsteps:\n  - id: notify\n    type: notify\n    interaction_spec: '{{\"cards\":[{cards}]}}'\n";
        var validateOuterYaml = () => WorkflowYamlResourceGuard.Validate(yaml);
        var parse = () => new WorkflowParser().Parse(yaml);

        validateOuterYaml.Should().NotThrow("the encoded interaction is a single YAML scalar below the byte limit");
        var exception = parse.Should().Throw<WorkflowYamlResourceLimitException>().Which;
        exception.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.Nodes);
        exception.Maximum.Should().Be(WorkflowYamlResourceGuard.MaxNodes);
    }

    [Theory]
    [InlineData("actions: [{ action_id: approve, disabled: definitely }]", "/steps/0/interaction_spec/actions/0/disabled")]
    [InlineData("actions: [{ action_id: approve, is_disabled: definitely }]", "/steps/0/interaction_spec/actions/0/is_disabled")]
    [InlineData("fields: [{ title: Environment, is_short: definitely }]", "/steps/0/interaction_spec/fields/0/is_short")]
    public void Parse_WhenInteractionBooleanUsesAnInvalidScalar_ShouldRejectInsteadOfDefaultingToFalse(
        string fields,
        string expectedPath)
    {
        var yaml = $"name: invalid_interaction_boolean\nsteps:\n  - id: notify\n    type: notify\n    interaction_spec: {{ {fields} }}\n";

        AssertStructureFailure(yaml, expectedPath);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    public void Parse_WhenInteractionBooleansAreValidOrNull_ShouldPreserveTheValueOrDefault(
        string value,
        bool expectedValue)
    {
        var yaml = $$"""
                     name: valid_interaction_boolean
                     steps:
                       - id: notify
                         type: notify
                         interaction_spec:
                           actions:
                             - action_id: approve
                               disabled: {{value}}
                           fields:
                             - title: Environment
                               is_short: {{value}}
                     """;

        var workflow = new WorkflowParser().Parse(yaml);

        var interaction = workflow.Steps[0].Presentation?.InteractionSpec;
        interaction.Should().NotBeNull();
        interaction!.Actions.Should().ContainSingle().Which.Disabled.Should().Be(expectedValue);
        interaction.Fields.Should().ContainSingle().Which.IsShort.Should().Be(expectedValue);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("is_disabled")]
    [InlineData("isDisabled")]
    [InlineData("IS_DISABLED")]
    public void Parse_WhenDisabledUsesASupportedCaseInsensitiveAlias_ShouldPreserveItsValue(string field)
    {
        var yaml = $"name: disabled_alias\nsteps:\n  - id: notify\n    type: notify\n    interaction_spec: {{ actions: [{{ id: approve, {field}: true }}] }}\n";

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Steps[0].Presentation!.InteractionSpec.Actions.Should().ContainSingle().Which.Disabled.Should().BeTrue();
    }

    public static IEnumerable<object[]> InvalidToolScopes()
    {
        foreach (var (owner, field) in ToolScopeLocations())
        {
            var path = ToolScopePath(owner, field);
            yield return [BuildToolScopeYaml(owner, field, "{ name: search }"), path];
            yield return [BuildToolScopeYaml(owner, field, "[{ name: search }]"), $"{path}/0"];
            yield return [BuildToolScopeYaml(owner, field, "[null]"), $"{path}/0"];
        }
    }

    public static IEnumerable<object[]> ValidToolScopes()
    {
        foreach (var (owner, field) in ToolScopeLocations())
        {
            yield return [owner, field, "[]", Array.Empty<string>()];
            yield return [owner, field, "search", new[] { "search" }];
            yield return [owner, field, "[search, calendar]", new[] { "search", "calendar" }];
        }
    }

    private static IEnumerable<(string Owner, string Field)> ToolScopeLocations()
    {
        foreach (var owner in new[] { "role", "step", "parameters" })
        {
            yield return (owner, "allowed_tools");
            yield return (owner, "tool_sets");
        }
        yield return ("parameters", "allowedTools");
        yield return ("parameters", "toolSets");
    }

    private static string ToolScopePath(string owner, string field) => owner switch
    {
        "role" => $"/roles/0/{field}",
        "step" => $"/steps/0/{field}",
        _ => $"/steps/0/parameters/{field}",
    };

    private static string BuildToolScopeYaml(string owner, string field, string value)
    {
        var body = owner switch
        {
            "role" => $"roles: [{{ id: analyst, {field}: {value} }}]\nsteps: []",
            "step" => $"steps: [{{ id: first, type: assign, {field}: {value} }}]",
            _ => $"steps: [{{ id: first, type: assign, parameters: {{ {field}: {value} }} }}]",
        };
        return $"name: tool_scope\n{body}\n";
    }

    private static void AssertStructureFailure(string yaml, string expectedPath)
    {
        var parse = () => new WorkflowParser().Parse(yaml);

        var exception = parse.Should().Throw<WorkflowYamlStructureException>().Which;
        exception.Code.Should().Be("yaml_structure");
        exception.Path.Should().Be(expectedPath);
    }
}
