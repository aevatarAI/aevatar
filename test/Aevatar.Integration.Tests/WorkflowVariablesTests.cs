using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

public class WorkflowVariablesTests
{
    [Fact]
    public void SetGetMergeClearAndCount_ShouldWork()
    {
        var vars = new WorkflowVariables();

        vars.Set("a", "1");
        vars.Set("b", "2");
        vars.Count.Should().Be(2);
        vars.Get("a").Should().Be("1");

        vars.Merge(new Dictionary<string, string>
        {
            ["b"] = "22",
            ["c"] = "3",
        });

        vars.Get("b").Should().Be("22");
        vars.Get("c").Should().Be("3");
        vars.GetAll().Should().ContainKey("a");

        vars.Clear();
        vars.Count.Should().Be(0);
        vars.Get("a").Should().BeNull();
    }

    [Fact]
    public void Get_ShouldResolveJsonDotPath_AndReturnNullForInvalidPathOrInvalidJson()
    {
        var vars = new WorkflowVariables();
        vars.Set("root", """{"obj":{"x":"v","n":123}}""");
        vars.Set("bad", "{not-json");

        vars.Get("root.obj.x").Should().Be("v");
        vars.Get("root.obj.n").Should().Be("123");
        vars.Get("root.obj.missing").Should().BeNull();
        vars.Get("missing.obj").Should().BeNull();
        vars.Get("bad.any").Should().BeNull();
        vars.Get("plainWithoutDot").Should().BeNull();
    }

    [Fact]
    public void Interpolate_ShouldReplaceKnownTokensAndKeepUnknown()
    {
        var vars = new WorkflowVariables();
        vars.Set("name", "Alice");
        vars.Set("task", "Review");

        vars.Interpolate("Hello {{name}}, {{task}} now.").Should().Be("Hello Alice, Review now.");
        vars.Interpolate("Unknown={{unknown}}").Should().Be("Unknown={{unknown}}");
        vars.Interpolate(string.Empty).Should().BeEmpty();
    }
}
