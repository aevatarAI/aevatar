using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Abstractions.Workflows;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

/// <summary>
/// Pins the strict-parser contract for workflow YAML written in a foreign
/// workflow dialect. Unguided LLM authors fall back to GitHub-Actions-style
/// top-level keys (<c>version:</c>, <c>inputs:</c>); the parser must reject the
/// document fail-fast and the error must name the offending key so an authoring
/// agent can self-repair instead of shipping a definition whose fields are
/// silently dropped.
/// </summary>
public sealed class WorkflowParserForeignDialectRejectionTests
{
    [Theory]
    [MemberData(nameof(UnsupportedDialectRootFields))]
    public void Parse_WhenTopLevelKeyComesFromForeignWorkflowDialect_ShouldFailFastNamingTheKey(
        string foreignKey)
    {
        var parser = new WorkflowParser();

        Action parse = () => parser.Parse(
            $"""
             {foreignKey}: {BuildScalarRootValue(foreignKey)}
             name: monitor
             steps:
               - id: step_1
                 type: llm_call
             """);

        parse.Should().Throw<InvalidOperationException>()
            .WithMessage($"Unsupported workflow YAML root field '{foreignKey}'.*")
            .And.Message.Should().Contain(WorkflowYamlRootSchema.FormatAcceptedRootFields());
    }

    [Theory]
    [MemberData(nameof(AcceptedRootFields))]
    public void Parse_WhenRootFieldIsInSharedSchema_ShouldAcceptRealParserPath(string rootField)
    {
        var parser = new WorkflowParser();

        Action parse = () => parser.Parse(BuildYamlWithRootField(rootField));

        parse.Should().NotThrow();
    }

    public static IEnumerable<object[]> UnsupportedDialectRootFields() =>
        WorkflowYamlRootSchema.UnsupportedDialectRootFieldOrder.Select(static field => new object[] { field });

    public static IEnumerable<object[]> AcceptedRootFields() =>
        WorkflowYamlRootSchema.AcceptedRootFieldOrder.Select(static field => new object[] { field });

    private static string BuildYamlWithRootField(string rootField) =>
        rootField switch
        {
            "name" => """
                name: monitor
                steps: []
                """,
            "description" => """
                name: monitor
                description: Test workflow
                steps: []
                """,
            "when_to_use" => """
                name: monitor
                when_to_use: Use when monitoring is needed.
                steps: []
                """,
            "configuration" => """
                name: monitor
                configuration:
                  closed_world_mode: true
                steps: []
                """,
            "roles" => """
                name: monitor
                roles:
                  - id: analyst
                    name: Analyst
                steps: []
                """,
            "steps" => """
                name: monitor
                steps:
                  - id: step_1
                    type: llm_call
                """,
            "on_failure" => """
                name: monitor
                on_failure:
                  action: fail
                  max_attempts: 1
                steps: []
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(rootField), rootField, null),
        };

    private static string BuildScalarRootValue(string rootField) =>
        rootField is "inputs" or "outputs" or "triggers" or "on" or "env" or "jobs"
            ? "{}"
            : "\"1.0\"";
}
