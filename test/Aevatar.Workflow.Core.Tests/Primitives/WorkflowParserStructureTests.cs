using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowParserStructureTests
{
    [Theory]
    [InlineData("oops", "/steps/0/branches")]
    [InlineData("[oops]", "/steps/0/branches/0")]
    [InlineData("[null]", "/steps/0/branches/0")]
    [InlineData("[{ next: done }]", "/steps/0/branches/0")]
    [InlineData("[{ condition: ready }]", "/steps/0/branches/0")]
    [InlineData("[{ condition: '', next: done }]", "/steps/0/branches/0/condition")]
    [InlineData("[{ condition: ready, next: '' }]", "/steps/0/branches/0/next")]
    [InlineData("{ ready: '' }", "/steps/0/branches/ready")]
    [InlineData("{ ready: null }", "/steps/0/branches/ready")]
    [InlineData("{ 'a/b~c': '' }", "/steps/0/branches/a~1b~0c")]
    public void Parse_WhenBranchesCannotDescribeARoute_ShouldRejectBeforeNormalization(
        string branches,
        string expectedPath)
    {
        var yaml = $$"""
                     name: invalid_branches
                     steps:
                       - id: first
                         type: assign
                         parameters: { target: result, value: ok }
                         branches: {{branches}}
                       - id: done
                         type: assign
                     """;

        AssertStructureFailure(yaml, expectedPath);
    }

    [Fact]
    public void Parse_WhenACompleteBranchListAlsoContainsAnIncompleteEntry_ShouldRejectTheBadEntry()
    {
        const string yaml = """
                            name: mixed_branches
                            steps:
                              - id: gate
                                type: conditional
                                parameters: { condition: ready }
                                branches:
                                  - condition: "true"
                                    next: done
                                  - condition: "false"
                                    next: done
                                  - if: ignored
                              - id: done
                                type: assign
                            """;

        AssertStructureFailure(yaml, "/steps/0/branches/2");
    }

    [Fact]
    public void Parse_WhenBranchAliasesRepeatTheSameCondition_ShouldRejectInsteadOfOverwritingTheRoute()
    {
        const string yaml = """
                            name: duplicate_branch
                            steps:
                              - id: gate
                                type: assign
                                branches:
                                  - condition: ready
                                    next: first
                                  - when: ready
                                    to: second
                              - id: first
                                type: assign
                              - id: second
                                type: assign
                            """;

        AssertStructureFailure(yaml, "/steps/0/branches/1");
    }

    [Theory]
    [InlineData("steps: [{ id: first, branches: { [ready, retry]: done } }]", "/steps/0/branches")]
    [InlineData("steps: [{ id: first, parameters: { [key, part]: value } }]", "/steps/0/parameters")]
    public void Parse_WhenAMappingUsesACollectionAsItsKey_ShouldRejectAtTheMappingPath(
        string body,
        string expectedPath)
    {
        AssertStructureFailure($"name: collection_key\n{body}\n", expectedPath);
    }

    [Theory]
    [InlineData("roles: {}\nsteps: []", "/roles")]
    [InlineData("roles: [null]\nsteps: []", "/roles/0")]
    [InlineData("roles: [analyst]\nsteps: []", "/roles/0")]
    [InlineData("steps: {}", "/steps")]
    [InlineData("steps: [null]", "/steps/0")]
    [InlineData("steps: [assign]", "/steps/0")]
    [InlineData("steps: [{ id: first, children: {} }]", "/steps/0/children")]
    [InlineData("steps: [{ id: first, children: [null] }]", "/steps/0/children/0")]
    [InlineData("steps: [{ id: first, children: [assign] }]", "/steps/0/children/0")]
    [InlineData("steps: [{ id: first, parameters: [] }]", "/steps/0/parameters")]
    [InlineData("steps: [{ id: first, retry: [] }]", "/steps/0/retry")]
    [InlineData("configuration: []\nsteps: []", "/configuration")]
    public void Parse_WhenAContainerHasTheWrongShape_ShouldReportItsSourcePath(
        string body,
        string expectedPath)
    {
        AssertStructureFailure($"name: invalid_container\n{body}\n", expectedPath);
    }

    [Theory]
    [InlineData("description: {}\nsteps: []", "/description")]
    [InlineData("roles: [{ id: analyst, model: [] }]\nsteps: []", "/roles/0/model")]
    [InlineData("roles: [{ id: analyst, max_tokens: {} }]\nsteps: []", "/roles/0/max_tokens")]
    [InlineData("steps: [{ id: {}, type: assign }]", "/steps/0/id")]
    [InlineData("steps: [{ id: first, type: assign, next: {} }]", "/steps/0/next")]
    [InlineData("steps: [{ id: first, type: assign, timeout_ms: {} }]", "/steps/0/timeout_ms")]
    [InlineData("steps: [{ id: first, type: assign, retry: { max_attempts: [] } }]", "/steps/0/retry/max_attempts")]
    public void Parse_WhenAScalarFieldContainsACollection_ShouldReportItsSourcePath(
        string body,
        string expectedPath)
    {
        AssertStructureFailure($"name: invalid_scalar\n{body}\n", expectedPath);
    }

    [Theory]
    [InlineData("roles: [{ id: analyst, typo: value }]\nsteps: []", "/roles/0/typo")]
    [InlineData("steps: [{ id: first, typo: value }]", "/steps/0/typo")]
    [InlineData("steps: [{ id: first, 'next ': done }]", "/steps/0/next ")]
    [InlineData("steps: [{ id: first, 'typo/key~value': value }]", "/steps/0/typo~1key~0value")]
    [InlineData("steps: [{ id: first, children: [{ id: child, typo: value }] }]", "/steps/0/children/0/typo")]
    [InlineData("steps: [{ id: first, retry: { typo: value } }]", "/steps/0/retry/typo")]
    public void Parse_WhenANestedFieldIsUnknown_ShouldIdentifyTheExactField(
        string body,
        string expectedPath)
    {
        AssertStructureFailure($"name: unknown_field\n{body}\n", expectedPath, "unknown_field");
    }

    [Theory]
    [InlineData("!!str null", "null")]
    [InlineData("!!str ~", "~")]
    [InlineData("'null'", "null")]
    [InlineData("'~'", "~")]
    [InlineData("\"null\"", "null")]
    [InlineData("\"~\"", "~")]
    public void Parse_WhenNullLikeTextIsExplicitlyAString_ShouldPreserveIdentitiesAndBranchTargets(
        string scalar,
        string expectedValue)
    {
        var yaml = $$"""
                     name: {{scalar}}
                     roles:
                       - id: {{scalar}}
                     steps:
                       - id: gate
                         type: assign
                         branches:
                           ready: {{scalar}}
                       - id: {{scalar}}
                         type: assign
                     """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Name.Should().Be(expectedValue);
        workflow.Roles[0].Id.Should().Be(expectedValue);
        workflow.Steps[1].Id.Should().Be(expectedValue);
        workflow.Steps[0].Branches.Should().ContainKey("ready").WhoseValue.Should().Be(expectedValue);
        WorkflowValidator.Validate(workflow).Should().BeEmpty();
    }

    [Theory]
    [InlineData("steps: [{ id: first, children: [null] }]", "/steps/0/children/0")]
    [InlineData("steps: [{ id: first, branches: [{ condition: ready }] }]", "/steps/0/branches/0")]
    [InlineData("steps: [{ id: first, 'next ': done }]", "/steps/0/next ")]
    public void Validate_WhenYamlHasAStructureError_ShouldReturnTheSourcePathInItsDiagnostic(
        string body,
        string expectedPath)
    {
        IWorkflowYamlValidator validator = new WorkflowYamlValidatorImpl();

        var result = validator.Validate($"name: invalid_structure\n{body}\n");

        result.Success.Should().BeFalse();
        var diagnostic = result.Diagnostics.Should().ContainSingle().Which;
        diagnostic.Severity.Should().Be("error");
        diagnostic.Field.Should().Be(expectedPath);
        diagnostic.Message.Should().Contain(expectedPath);
    }

    [Fact]
    public void Parse_WhenRequiredFieldKeysHaveExplicitStringTags_ShouldRecognizeTheirNames()
    {
        const string yaml = """
                            !!str name: tagged_keys
                            roles:
                              - !!str id: analyst
                            steps:
                              - !!str id: first
                                type: assign
                            """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Name.Should().Be("tagged_keys");
        workflow.Roles.Should().ContainSingle().Which.Id.Should().Be("analyst");
        workflow.Steps.Should().ContainSingle().Which.Id.Should().Be("first");
    }

    [Theory]
    [InlineData("name: duplicate\n!!str name: overwritten\nsteps: []", "/name")]
    [InlineData("name: duplicate\nsteps: [{ id: first, parameters: { key: one, !!str key: two } }]", "/steps/0/parameters/key")]
    [InlineData("name: duplicate\nsteps: [{ id: first, branches: { ready: first, !!str ready: first } }]", "/steps/0/branches/ready")]
    public void Parse_WhenKeysDifferOnlyByAnExplicitStringTag_ShouldRejectDuplicateFields(string yaml, string expectedPath)
    {
        AssertStructureFailure(yaml, expectedPath);
    }

    [Fact]
    public void Parse_WhenBranchConditionsHaveDifferentLiteralWhitespace_ShouldPreserveBothConditions()
    {
        const string yaml = """
                            name: literal_conditions
                            steps:
                              - id: first
                                type: assign
                                branches:
                                  - condition: ready
                                    next: first
                                  - condition: ' ready '
                                    next: first
                            """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Steps[0].Branches.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["ready"] = "first",
            [" ready "] = "first",
        });
    }

    [Theory]
    [InlineData("condition", "next")]
    [InlineData("when", "to")]
    [InlineData("case", "target")]
    [InlineData("label", "step")]
    [InlineData("if", "next")]
    public void Parse_WhenBranchListUsesSupportedAliases_ShouldPreserveTheRoute(
        string conditionField,
        string targetField)
    {
        var yaml = $$"""
                     name: branch_aliases
                     steps:
                       - id: first
                         type: assign
                         branches:
                           - {{conditionField}}: ready
                             {{targetField}}: done
                       - id: done
                         type: assign
                     """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Steps[0].Branches.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["ready"] = "done",
        });
        WorkflowValidator.Validate(workflow).Should().BeEmpty();
    }

    [Theory]
    [InlineData("next")]
    [InlineData("to")]
    [InlineData("target")]
    [InlineData("step")]
    public void Parse_WhenBranchMappingUsesSupportedTargetAliases_ShouldPreserveTheRoute(string targetField)
    {
        var yaml = $$"""
                     name: branch_mapping_aliases
                     steps:
                       - id: first
                         type: assign
                         branches:
                           ready:
                             {{targetField}}: done
                           fallback: done
                       - id: done
                         type: assign
                     """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Steps[0].Branches.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["ready"] = "done",
            ["fallback"] = "done",
        });
    }

    [Fact]
    public void Parse_WhenParametersContainNestedValuesAndYamlAliases_ShouldPreserveThem()
    {
        const string yaml = """
                            name: structured_parameters
                            steps:
                              - id: first
                                type: assign
                                parameters:
                                  target: result
                                  value: &payload
                                    enabled: true
                                    items: [one, two]
                                  copy: *payload
                            """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Steps[0].Parameters["value"].Should().Be("""{"enabled":"true","items":["one","two"]}""");
        workflow.Steps[0].Parameters["copy"].Should().Be(workflow.Steps[0].Parameters["value"]);
    }

    [Fact]
    public void Parse_WhenOptionalFieldsAreExplicitlyNull_ShouldRetainTheirDefaults()
    {
        const string yaml = """
                            name: optional_nulls
                            description: null
                            configuration: null
                            roles: null
                            on_failure: null
                            steps:
                              - id: first
                                type: assign
                                children: null
                                parameters: null
                                retry: null
                                on_error: null
                                branches: null
                                presentation: null
                            """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Description.Should().BeEmpty();
        workflow.Roles.Should().BeEmpty();
        workflow.Steps.Should().ContainSingle();
        workflow.Steps[0].Children.Should().BeNull();
        workflow.Steps[0].Branches.Should().BeNull();
        workflow.Steps[0].Parameters.Should().BeEmpty();
        WorkflowValidator.Validate(workflow).Should().BeEmpty();
    }

    private static void AssertStructureFailure(string yaml, string expectedPath, string expectedCode = "yaml_structure")
    {
        var parse = () => new WorkflowParser().Parse(yaml);

        var exception = parse.Should().Throw<WorkflowYamlStructureException>().Which;
        exception.Code.Should().Be(expectedCode);
        exception.Path.Should().Be(expectedPath);
        exception.Message.Should().Contain(expectedPath);
    }
}
