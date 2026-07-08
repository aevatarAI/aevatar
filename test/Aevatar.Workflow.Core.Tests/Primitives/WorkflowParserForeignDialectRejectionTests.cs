using Aevatar.Workflow.Core.Primitives;
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
    [InlineData("version", "version: \"1.0\"")]
    [InlineData("inputs", "inputs:\n  topic: string")]
    public void Parse_WhenTopLevelKeyComesFromForeignWorkflowDialect_ShouldFailFastNamingTheKey(
        string foreignKey, string foreignLine)
    {
        var parser = new WorkflowParser();

        Action parse = () => parser.Parse(
            $"""
             {foreignLine}
             name: monitor
             steps:
               - id: step_1
                 type: llm_call
             """);

        parse.Should().Throw<Exception>()
            .Where(ex => ex.ToString().Contains($"'{foreignKey}'"));
    }
}
