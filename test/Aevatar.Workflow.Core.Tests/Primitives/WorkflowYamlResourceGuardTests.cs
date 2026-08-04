using System.Text;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowYamlResourceGuardTests
{
    [Fact]
    public void Parse_WhenChildrenDepthIsBelowLimit_ShouldSucceed()
    {
        var yaml = BuildNestedWorkflow(childLinks: 30);

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Name.Should().Be("nested");
    }

    [Fact]
    public void Parse_WhenChildrenDepthExceedsLimit_ShouldRejectBeforeDeserialization()
    {
        var yaml = BuildNestedWorkflow(childLinks: 31);

        var act = () => new WorkflowParser().Parse(yaml);

        var exception = act.Should().Throw<WorkflowYamlResourceLimitException>().Which;
        exception.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.NestingDepth);
        exception.Actual.Should().Be(65);
        exception.Maximum.Should().Be(WorkflowYamlResourceGuard.MaxNestingDepth);
    }

    [Fact]
    public void Parse_WhenUtf8BytesExceedLimit_ShouldRejectWithTypedLimit()
    {
        var yaml = $"name: oversized\ndescription: {new string('a', WorkflowYamlResourceGuard.MaxUtf8Bytes)}\nroles: []\nsteps: []\n";

        var act = () => new WorkflowParser().Parse(yaml);

        var exception = act.Should().Throw<WorkflowYamlResourceLimitException>().Which;
        exception.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.Utf8Bytes);
        exception.Actual.Should().BeGreaterThan(WorkflowYamlResourceGuard.MaxUtf8Bytes);
        exception.Maximum.Should().Be(WorkflowYamlResourceGuard.MaxUtf8Bytes);
    }

    [Fact]
    public void Parse_WhenNodeCountExceedsLimit_ShouldRejectWithTypedLimit()
    {
        var parameters = string.Join(
            '\n',
            Enumerable.Range(0, 5_100).Select(index => $"      key_{index}: value_{index}"));
        var yaml = $"name: nodes\nroles: []\nsteps:\n  - id: assign\n    type: assign\n    parameters:\n{parameters}\n";

        var act = () => new WorkflowParser().Parse(yaml);

        var exception = act.Should().Throw<WorkflowYamlResourceLimitException>().Which;
        exception.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.Nodes);
        exception.Actual.Should().Be(WorkflowYamlResourceGuard.MaxNodes + 1);
        exception.Maximum.Should().Be(WorkflowYamlResourceGuard.MaxNodes);
    }

    [Fact]
    public void Validate_WhenCollectionAliasCreatesCycle_ShouldRejectWithDepthLimit()
    {
        const string yaml = """
                            root: &root
                              self: *root
                            """;

        var act = () => WorkflowYamlResourceGuard.Validate(yaml);

        var exception = act.Should().Throw<WorkflowYamlResourceLimitException>().Which;
        exception.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.NestingDepth);
        exception.Actual.Should().Be(WorkflowYamlResourceGuard.MaxNestingDepth + 1);
    }

    [Fact]
    public void Validate_WhenAliasesExpandBeyondNodeLimit_ShouldRejectWithNodeLimit()
    {
        var yaml = BuildAliasExpansionYaml(levels: 14);

        var act = () => WorkflowYamlResourceGuard.Validate(yaml);

        var exception = act.Should().Throw<WorkflowYamlResourceLimitException>().Which;
        exception.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.Nodes);
        exception.Actual.Should().Be(WorkflowYamlResourceGuard.MaxNodes + 1);
    }

    [Fact]
    public void Validate_WhenForwardCollectionAliasesCreateCycle_ShouldRejectWithDepthLimit()
    {
        const string yaml = """
                            root: *a
                            first: &a
                              child: *b
                            second: &b
                              child: *a
                            """;

        var act = () => WorkflowYamlResourceGuard.Validate(yaml);

        var exception = act.Should().Throw<WorkflowYamlResourceLimitException>().Which;
        exception.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.NestingDepth);
        exception.Actual.Should().Be(WorkflowYamlResourceGuard.MaxNestingDepth + 1);
    }

    [Fact]
    public void Validate_WhenForwardAliasesExpandBeyondNodeLimit_ShouldRejectWithNodeLimit()
    {
        var yaml = BuildForwardAliasExpansionYaml(levels: 14);

        var act = () => WorkflowYamlResourceGuard.Validate(yaml);

        var exception = act.Should().Throw<WorkflowYamlResourceLimitException>().Which;
        exception.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.Nodes);
        exception.Actual.Should().Be(WorkflowYamlResourceGuard.MaxNodes + 1);
    }

    [Fact]
    public void Parse_WhenWorkflowChildrenAliasCreatesCycle_ShouldRejectBeforeDeserialization()
    {
        const string yaml = """
                            name: cyclic
                            roles: []
                            steps: &steps
                              - id: loop
                                type: assign
                                children: *steps
                            """;

        var act = () => new WorkflowParser().Parse(yaml);

        act.Should().Throw<WorkflowYamlResourceLimitException>()
            .Which.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.NestingDepth);
    }

    [Fact]
    public void Validate_WhenUtf8ByteCountEqualsLimit_ShouldAcceptMultibyteYaml()
    {
        const string prefix = "value: ";
        var remainingBytes = WorkflowYamlResourceGuard.MaxUtf8Bytes - Encoding.UTF8.GetByteCount(prefix);
        var yaml = prefix + "a" + new string('\u00E9', (remainingBytes - 1) / 2);
        Encoding.UTF8.GetByteCount(yaml).Should().Be(WorkflowYamlResourceGuard.MaxUtf8Bytes);

        var act = () => WorkflowYamlResourceGuard.Validate(yaml);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WhenNodeCountEqualsLimit_ShouldSucceed()
    {
        var yaml = BuildScalarSequenceYaml(WorkflowYamlResourceGuard.MaxNodes - 3);

        var act = () => WorkflowYamlResourceGuard.Validate(yaml);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WhenCollectionDepthEqualsLimit_ShouldSucceed()
    {
        var yaml = new string('[', WorkflowYamlResourceGuard.MaxNestingDepth) +
                   "value" +
                   new string(']', WorkflowYamlResourceGuard.MaxNestingDepth);

        var act = () => WorkflowYamlResourceGuard.Validate(yaml);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WhenNodeCountAcrossDocumentsExceedsLimit_ShouldRejectCumulatively()
    {
        var document = BuildScalarSequenceYaml(5_000);
        var yaml = $"---\n{document}---\n{document}";

        var act = () => WorkflowYamlResourceGuard.Validate(yaml);

        act.Should().Throw<WorkflowYamlResourceLimitException>()
            .Which.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.Nodes);
    }

    [Fact]
    public void Validate_WhenYamlIsMalformed_ShouldPreserveYamlSyntaxFailure()
    {
        var act = () => WorkflowYamlResourceGuard.Validate("root: [value");

        act.Should().Throw<YamlDotNet.Core.YamlException>();
    }

    [Fact]
    public void Validate_WhenAliasTargetsScalar_ShouldRemainValid()
    {
        const string yaml = """
                            source: &source value
                            copy: *source
                            """;

        var act = () => WorkflowYamlResourceGuard.Validate(yaml);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WhenAnchorNameIsRedefined_ShouldKeepBackwardAliasEncounterTarget()
    {
        var nestedAlias = new string('[', WorkflowYamlResourceGuard.MaxNestingDepth - 1) +
                          "*shared" +
                          new string(']', WorkflowYamlResourceGuard.MaxNestingDepth - 1);
        var yaml = $"original: &shared value\nuse: {nestedAlias}\nreplacement: &shared [value]\n";

        var act = () => WorkflowYamlResourceGuard.Validate(yaml);

        act.Should().NotThrow();
    }

    [Fact]
    public void Parse_WhenAliasTargetIsMissing_ShouldPreserveYamlSyntaxFailure()
    {
        const string yaml = """
                            name: missing-alias
                            roles: []
                            steps: *missing
                            """;

        var act = () => new WorkflowParser().Parse(yaml);

        act.Should().Throw<YamlDotNet.Core.YamlException>();
    }

    [Fact]
    public void Validate_WhenAliasAndAnchorAreInDifferentDocuments_ShouldNotResolveAcrossDocuments()
    {
        var firstDocument = new string('[', WorkflowYamlResourceGuard.MaxNestingDepth) +
                            "*shared" +
                            new string(']', WorkflowYamlResourceGuard.MaxNestingDepth);
        var yaml = $"---\n{firstDocument}\n---\ntarget: &shared [value]\n";

        var act = () => WorkflowYamlResourceGuard.Validate(yaml);

        act.Should().NotThrow();
    }

    internal static string BuildNestedWorkflow(int childLinks)
    {
        var yaml = new StringBuilder()
            .AppendLine("name: nested")
            .AppendLine("roles: []")
            .AppendLine("steps:");

        for (var index = 0; index <= childLinks; index++)
        {
            var itemIndent = new string(' ', 2 + (index * 4));
            var propertyIndent = new string(' ', 4 + (index * 4));
            yaml.Append(itemIndent).Append("- id: step_").AppendLine(index.ToString());
            yaml.Append(propertyIndent).AppendLine("type: assign");
            if (index < childLinks)
                yaml.Append(propertyIndent).AppendLine("children:");
        }

        return yaml.ToString();
    }

    private static string BuildAliasExpansionYaml(int levels)
    {
        var yaml = new StringBuilder().AppendLine("seed: &level_0 [value]");
        for (var level = 1; level <= levels; level++)
        {
            yaml.Append("level_").Append(level).Append(": &level_").Append(level)
                .Append(" [*level_").Append(level - 1).Append(", *level_")
                .Append(level - 1).AppendLine("]");
        }

        return yaml.Append("root: *level_").AppendLine(levels.ToString()).ToString();
    }

    private static string BuildForwardAliasExpansionYaml(int levels)
    {
        var yaml = new StringBuilder()
            .Append("root: *level_").AppendLine(levels.ToString());
        for (var level = levels; level > 0; level--)
        {
            yaml.Append("level_").Append(level).Append(": &level_").Append(level)
                .Append(" [*level_").Append(level - 1).Append(", *level_")
                .Append(level - 1).AppendLine("]");
        }

        return yaml.AppendLine("level_0: &level_0 [value]").ToString();
    }

    private static string BuildScalarSequenceYaml(int scalarCount)
    {
        var yaml = new StringBuilder().AppendLine("items:");
        for (var index = 0; index < scalarCount; index++)
            yaml.AppendLine("  - value");
        return yaml.ToString();
    }
}
