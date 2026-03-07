using Aevatar.Workflow.Core.Expressions;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Expressions;

public class WorkflowExpressionParserTests
{
    [Fact]
    public void ParseTemplate_ShouldSplitTextAndInterpolationSegments()
    {
        var template = new WorkflowExpressionParser().ParseTemplate("Hello ${concat(name, '!')}");

        template.Segments.Should().HaveCount(2);
        template.Segments[0].Should().BeOfType<WorkflowTextSegment>();
        template.Segments[1].Should().BeOfType<WorkflowInterpolatedExpressionSegment>();
    }

    [Fact]
    public void ParseExpression_ShouldBuildFunctionCallTree()
    {
        var expression = new WorkflowExpressionParser().ParseExpression("if(flag, concat('a', name), 'b')");

        var function = expression.Should().BeOfType<WorkflowFunctionCallExpression>().Subject;
        function.Name.Should().Be("if");
        function.Arguments.Should().HaveCount(3);
        function.Arguments[1].Should().BeOfType<WorkflowFunctionCallExpression>();
    }
}
