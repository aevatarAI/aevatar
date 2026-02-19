using Aevatar.Workflow.Core.Expressions;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Expressions;

public class WorkflowExpressionEvaluatorTests
{
    private readonly WorkflowExpressionEvaluator _eval = new();
    private readonly Dictionary<string, string> _vars = new()
    {
        ["name"] = "Alice",
        ["age"] = "30",
        ["empty"] = "",
        ["flag"] = "true",
        ["zero"] = "0",
    };

    // ─── Variable Resolution ───

    [Fact]
    public void Evaluate_PlainText_ReturnsUnchanged()
    {
        _eval.Evaluate("hello world", _vars).Should().Be("hello world");
    }

    [Fact]
    public void Evaluate_SimpleVariable()
    {
        _eval.Evaluate("Hello ${name}!", _vars).Should().Be("Hello Alice!");
    }

    [Fact]
    public void Evaluate_DottedVariable()
    {
        _eval.Evaluate("${variables.name}", _vars).Should().Be("Alice");
    }

    [Fact]
    public void Evaluate_MissingVariable_ReturnsEmpty()
    {
        _eval.Evaluate("${missing}", _vars).Should().Be("");
    }

    [Fact]
    public void Evaluate_MultipleExpressions()
    {
        _eval.Evaluate("${name} is ${age}", _vars).Should().Be("Alice is 30");
    }

    [Fact]
    public void Evaluate_NullOrEmpty_ReturnsAsIs()
    {
        _eval.Evaluate("", _vars).Should().Be("");
        _eval.Evaluate(null!, _vars).Should().BeNull();
    }

    // ─── String Literals ───

    [Fact]
    public void Evaluate_SingleQuoteLiteral()
    {
        _eval.Evaluate("${concat('Hi ', name)}", _vars).Should().Be("Hi Alice");
    }

    [Fact]
    public void Evaluate_DoubleQuoteLiteral()
    {
        _eval.EvaluateExpression("\"hello\"", _vars).Should().Be("hello");
    }

    // ─── Functions ───

    [Fact]
    public void If_TruthyCondition_ReturnsTrueBranch()
    {
        _eval.Evaluate("${if(flag, 'yes', 'no')}", _vars).Should().Be("yes");
    }

    [Fact]
    public void If_FalsyCondition_ReturnsFalseBranch()
    {
        _eval.Evaluate("${if(zero, 'yes', 'no')}", _vars).Should().Be("no");
    }

    [Fact]
    public void If_EmptyCondition_ReturnsFalseBranch()
    {
        _eval.Evaluate("${if(empty, 'has-value', 'empty')}", _vars).Should().Be("empty");
    }

    [Fact]
    public void Concat_MultipleArgs()
    {
        _eval.Evaluate("${concat(name, ' is ', age)}", _vars).Should().Be("Alice is 30");
    }

    [Fact]
    public void IsBlank_EmptyString_ReturnsTrue()
    {
        _eval.Evaluate("${isBlank(empty)}", _vars).Should().Be("true");
    }

    [Fact]
    public void IsBlank_NonEmpty_ReturnsFalse()
    {
        _eval.Evaluate("${isBlank(name)}", _vars).Should().Be("false");
    }

    [Fact]
    public void IsBlank_MissingVar_ReturnsTrue()
    {
        _eval.Evaluate("${isBlank(missing)}", _vars).Should().Be("true");
    }

    [Fact]
    public void Length_ReturnsStringLength()
    {
        _eval.Evaluate("${length(name)}", _vars).Should().Be("5");
    }

    [Fact]
    public void Length_EmptyString_ReturnsZero()
    {
        _eval.Evaluate("${length(empty)}", _vars).Should().Be("0");
    }

    // ─── Boolean Functions ───

    [Fact]
    public void Not_TruthyValue_ReturnsFalse()
    {
        _eval.Evaluate("${not(flag)}", _vars).Should().Be("false");
    }

    [Fact]
    public void Not_FalsyValue_ReturnsTrue()
    {
        _eval.Evaluate("${not(zero)}", _vars).Should().Be("true");
    }

    [Fact]
    public void And_BothTruthy_ReturnsTrue()
    {
        _eval.Evaluate("${and(flag, name)}", _vars).Should().Be("true");
    }

    [Fact]
    public void And_OneFalsy_ReturnsFalse()
    {
        _eval.Evaluate("${and(flag, zero)}", _vars).Should().Be("false");
    }

    [Fact]
    public void Or_OneTruthy_ReturnsTrue()
    {
        _eval.Evaluate("${or(zero, flag)}", _vars).Should().Be("true");
    }

    [Fact]
    public void Or_BothFalsy_ReturnsFalse()
    {
        _eval.Evaluate("${or(zero, empty)}", _vars).Should().Be("false");
    }

    // ─── String Functions ───

    [Fact]
    public void Upper_ConvertsToUpperCase()
    {
        _eval.Evaluate("${upper(name)}", _vars).Should().Be("ALICE");
    }

    [Fact]
    public void Lower_ConvertsToLowerCase()
    {
        var vars = new Dictionary<string, string> { ["x"] = "HELLO" };
        _eval.Evaluate("${lower(x)}", vars).Should().Be("hello");
    }

    [Fact]
    public void Trim_RemovesWhitespace()
    {
        var vars = new Dictionary<string, string> { ["x"] = "  hello  " };
        _eval.Evaluate("${trim(x)}", vars).Should().Be("hello");
    }

    // ─── Nested Functions ───

    [Fact]
    public void NestedFunctions_IfWithConcat()
    {
        _eval.Evaluate("${if(flag, concat('Hello ', name), 'bye')}", _vars)
            .Should().Be("Hello Alice");
    }

    [Fact]
    public void NestedFunctions_UpperInConcat()
    {
        _eval.Evaluate("${concat(upper(name), '!')}", _vars).Should().Be("ALICE!");
    }

    // ─── Truthiness Rules ───

    [Theory]
    [InlineData("true", "yes")]
    [InlineData("1", "yes")]
    [InlineData("anything", "yes")]
    [InlineData("false", "no")]
    [InlineData("0", "no")]
    [InlineData("", "no")]
    public void Truthiness_FollowsRules(string value, string expected)
    {
        var vars = new Dictionary<string, string> { ["v"] = value };
        _eval.Evaluate("${if(v, 'yes', 'no')}", vars).Should().Be(expected);
    }

    // ─── Unknown Function ───

    [Fact]
    public void UnknownFunction_ReturnsErrorTag()
    {
        _eval.Evaluate("${foo(bar)}", _vars).Should().Contain("unknown function");
    }

    // ─── No Expression ───

    [Fact]
    public void NoExpression_ReturnsSameString()
    {
        _eval.Evaluate("just plain text", _vars).Should().Be("just plain text");
    }
}
