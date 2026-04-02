using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Tools;

public class TextToolCallParserTests
{
    [Fact]
    public void Parse_DsmlSingleToolCall_ExtractsToolAndCleansContent()
    {
        var content = """
            我来帮你查一下。

            < | DSML | function_calls>
            < | DSML | invoke name="nyxid_proxy">
            < | DSML | parameter name="input" string="true">action=discover</ | DSML | parameter>
            </ | DSML | invoke>
            </ | DSML | function_calls>

            继续处理。
            """;

        var result = TextToolCallParser.Parse(content);

        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls[0].Name.Should().Be("nyxid_proxy");
        result.ToolCalls[0].ArgumentsJson.Should().Be("{\"input\":\"action=discover\"}");
        result.ToolCalls[0].Id.Should().StartWith("text-tc-");
        result.CleanedContent.Should().NotContain("DSML");
        result.CleanedContent.Should().NotContain("function_calls");
        result.CleanedContent.Should().Contain("我来帮你查一下");
        result.CleanedContent.Should().Contain("继续处理");
    }

    [Fact]
    public void Parse_DsmlMultipleToolCalls_ExtractsAll()
    {
        var content = """
            < | DSML | function_calls>
            < | DSML | invoke name="tool_a">
            < | DSML | parameter name="input" string="true">arg_a</ | DSML | parameter>
            </ | DSML | invoke>
            < | DSML | invoke name="tool_b">
            < | DSML | parameter name="input" string="true">arg_b</ | DSML | parameter>
            </ | DSML | invoke>
            </ | DSML | function_calls>
            """;

        var result = TextToolCallParser.Parse(content);

        result.ToolCalls.Should().HaveCount(2);
        result.ToolCalls[0].Name.Should().Be("tool_a");
        result.ToolCalls[1].Name.Should().Be("tool_b");
    }

    [Fact]
    public void Parse_XmlFormat_ExtractsToolCall()
    {
        var content = """
            Let me check.

            <function_calls>
            <invoke name="search">
            <parameter name="input">hello world</parameter>
            </invoke>
            </function_calls>

            Done.
            """;

        var result = TextToolCallParser.Parse(content);

        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls[0].Name.Should().Be("search");
        result.ToolCalls[0].ArgumentsJson.Should().Be("{\"input\":\"hello world\"}");
        result.CleanedContent.Should().NotContain("function_calls");
        result.CleanedContent.Should().Contain("Let me check");
        result.CleanedContent.Should().Contain("Done");
    }

    [Fact]
    public void Parse_MultipleParameters_BuildsJsonObject()
    {
        var content = """
            <function_calls>
            <invoke name="api_call">
            <parameter name="method">POST</parameter>
            <parameter name="path">/users</parameter>
            <parameter name="body">{"name":"test"}</parameter>
            </invoke>
            </function_calls>
            """;

        var result = TextToolCallParser.Parse(content);

        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls[0].Name.Should().Be("api_call");
        // Multiple params → JSON object
        result.ToolCalls[0].ArgumentsJson.Should().Contain("\"method\"");
        result.ToolCalls[0].ArgumentsJson.Should().Contain("\"POST\"");
        result.ToolCalls[0].ArgumentsJson.Should().Contain("\"path\"");
    }

    [Fact]
    public void Parse_NoToolCalls_ReturnsEmptyAndOriginalContent()
    {
        var content = "Just a normal text message with no tool calls.";

        var result = TextToolCallParser.Parse(content);

        result.ToolCalls.Should().BeEmpty();
        result.CleanedContent.Should().Be(content);
    }

    [Fact]
    public void Parse_EmptyContent_ReturnsEmpty()
    {
        var result = TextToolCallParser.Parse("");
        result.ToolCalls.Should().BeEmpty();
        result.CleanedContent.Should().BeEmpty();

        var resultNull = TextToolCallParser.Parse(null!);
        resultNull.ToolCalls.Should().BeEmpty();
    }

    [Fact]
    public void Parse_IncompleteBlock_DoesNotMatch()
    {
        // Dangling open tag without close — should not parse as tool call
        var content = "正在检查\n\n< | DSML | function_calls>\n< | DSML | invoke name=\"foo\">";

        var result = TextToolCallParser.Parse(content);

        result.ToolCalls.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MixedDsmlAndXml_ExtractsBoth()
    {
        var content = """
            Step 1

            < | DSML | function_calls>
            < | DSML | invoke name="dsml_tool">
            < | DSML | parameter name="input" string="true">dsml_arg</ | DSML | parameter>
            </ | DSML | invoke>
            </ | DSML | function_calls>

            Step 2

            <function_calls>
            <invoke name="xml_tool">
            <parameter name="input">xml_arg</parameter>
            </invoke>
            </function_calls>

            Done.
            """;

        var result = TextToolCallParser.Parse(content);

        result.ToolCalls.Should().HaveCount(2);
        result.ToolCalls[0].Name.Should().Be("dsml_tool");
        result.ToolCalls[1].Name.Should().Be("xml_tool");
        result.CleanedContent.Should().Contain("Step 1");
        result.CleanedContent.Should().Contain("Step 2");
        result.CleanedContent.Should().Contain("Done");
    }

    [Fact]
    public void Parse_DsmlWithFullWidthUnicodePipes_ExtractsToolCall()
    {
        // U+FF5C full-width vertical line — emitted by DeepSeek and other CJK-context LLMs
        var content = "检查配置\n\n<\uff5cDSML\uff5cfunction_calls>\n<\uff5cDSML\uff5cinvoke name=\"nyxid_approvals\">\n<\uff5cDSML\uff5cparameter name=\"input\" string=\"true\">action=configs</\uff5cDSML\uff5cparameter>\n</\uff5cDSML\uff5cinvoke>\n</\uff5cDSML\uff5cfunction_calls>\n\n完成。";

        var result = TextToolCallParser.Parse(content);

        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls[0].Name.Should().Be("nyxid_approvals");
        result.ToolCalls[0].ArgumentsJson.Should().Contain("action=configs");
        result.CleanedContent.Should().NotContain("DSML");
        result.CleanedContent.Should().Contain("检查配置");
        result.CleanedContent.Should().Contain("完成");
    }

    [Fact]
    public void Parse_XmlWithWhitespaceBeforeClose_StillMatches()
    {
        var content = "<function_calls >\n<invoke name=\"ws_tool\" >\n<parameter name=\"input\" >value</parameter >\n</invoke >\n</function_calls >";

        var result = TextToolCallParser.Parse(content);

        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls[0].Name.Should().Be("ws_tool");
        result.ToolCalls[0].ArgumentsJson.Should().Contain("\"input\"");
    }

    [Fact]
    public void Parse_ToolCallWithNoParameters_ReturnsEmptyArgs()
    {
        var content = """
            <function_calls>
            <invoke name="no_args_tool">
            </invoke>
            </function_calls>
            """;

        var result = TextToolCallParser.Parse(content);

        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls[0].Name.Should().Be("no_args_tool");
        result.ToolCalls[0].ArgumentsJson.Should().Be("{}");
    }
}
