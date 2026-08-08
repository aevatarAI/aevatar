using System.Text.Json;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.AI.ToolProviders.Web.Tools;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatAskUserContractTests
{
    [Fact]
    public async Task ToolSource_ShouldExposeOnlyAskUser()
    {
        var tools = await new AskUserAgentToolSource().DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().ContainSingle().Which.Should()
            .Be("ask_user");
    }

    [Fact]
    public async Task ConditionToolSource_ShouldExposeEffectFreeTypedProposal()
    {
        var tools = await new ConditionEvaluateAgentToolSource().DiscoverToolsAsync();

        var tool = tools.Should().ContainSingle().Which;
        tool.Name.Should().Be("condition.evaluate");
        tool.IsReadOnly.Should().BeTrue();
        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        schema.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema.RootElement.GetProperty("properties").TryGetProperty("threshold", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenNumericThresholdIsBounded_ShouldReturnTypedSpec()
    {
        var parsed = NyxIdChatAskUserContract.TryParse(
            "input-alpha",
            """
            {
              "question": "Choose the score threshold.",
              "options": [],
              "allow_free_text": true,
              "numeric_threshold": {
                "suggested_value": 70,
                "minimum_value": 0,
                "maximum_value": 100
              }
            }
            """,
            out var request);

        parsed.Should().BeTrue();
        request.NumericThreshold.Should().BeEquivalentTo(
            new NyxIdChatNumericThresholdInputSpec
            {
                SuggestedValue = 70,
                MinimumValue = 0,
                MaximumValue = 100,
            });
    }

    [Theory]
    [InlineData(101, 0, 100)]
    [InlineData(70, 80, 10)]
    public void TryParse_WhenNumericThresholdBoundsAreInvalid_ShouldReject(
        long suggested,
        long minimum,
        long maximum)
    {
        var arguments = JsonSerializer.Serialize(new
        {
            question = "Choose the score threshold.",
            options = Array.Empty<object>(),
            allow_free_text = true,
            numeric_threshold = new
            {
                suggested_value = suggested,
                minimum_value = minimum,
                maximum_value = maximum,
            },
        });

        NyxIdChatAskUserContract.TryParse("input-alpha", arguments, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenFreeTextOnly_ShouldAcceptZeroOptions()
    {
        var parsed = NyxIdChatAskUserContract.TryParse(
            "input-alpha",
            """
            {
              "question": "Which region, budget, and launch date should I use?",
              "allow_free_text": true
            }
            """,
            out var request);

        parsed.Should().BeTrue();
        request.Prompt.Should().Be("Which region, budget, and launch date should I use?");
        request.Options.Should().BeEmpty();
        request.AllowFreeText.Should().BeTrue();
        request.MultiSelect.Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"question\":\"What should I use?\"}")]
    [InlineData("{\"question\":\"What should I use?\",\"options\":[]}")]
    [InlineData("{\"question\":\"What should I use?\",\"options\":[],\"allow_free_text\":false}")]
    public void TryParse_WhenZeroOptionsDoNotAllowFreeText_ShouldReject(string arguments)
    {
        NyxIdChatAskUserContract.TryParse("input-alpha", arguments, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenMoreThanSixOptionsAreProvided_ShouldReject()
    {
        var arguments = JsonSerializer.Serialize(new
        {
            question = "Which region should I use?",
            options = Enumerable.Range(1, 7)
                .Select(index => new { label = $"Region {index}" }),
        });

        NyxIdChatAskUserContract.TryParse("input-alpha", arguments, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenOneOptionIsProvided_ShouldReject()
    {
        NyxIdChatAskUserContract.TryParse(
                "input-alpha",
                """
                {
                  "question": "Which region should I use?",
                  "options": [{"label":"Singapore"}],
                  "allow_free_text": true
                }
                """,
                out _)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void TryParse_WhenChoiceQuestionHasTwoToSixOptions_ShouldPreserveChoiceBehavior(
        int optionCount)
    {
        var arguments = JsonSerializer.Serialize(new
        {
            question = "Which regions should I use?",
            options = Enumerable.Range(1, optionCount)
                .Select(index => new { label = $"Region {index}" }),
            multi_select = true,
        });

        var parsed = NyxIdChatAskUserContract.TryParse(
            "input-alpha",
            arguments,
            out var request);

        parsed.Should().BeTrue();
        request.Options.Should().HaveCount(optionCount);
        request.AllowFreeText.Should().BeFalse();
        request.MultiSelect.Should().BeTrue();
        request.Options.Select(static option => option.OptionId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TryParse_WhenFreeTextOnlyIsMultiSelect_ShouldReject()
    {
        NyxIdChatAskUserContract.TryParse(
                "input-alpha",
                """
                {
                  "question": "Which region, budget, and launch date should I use?",
                  "options": [],
                  "allow_free_text": true,
                  "multi_select": true
                }
                """,
                out _)
            .Should().BeFalse();
    }

    [Fact]
    public void ParametersSchema_ShouldDescribeTheClosedChoiceOrFreeTextContract()
    {
        var tool = new AskUserTool();
        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        var root = schema.RootElement;
        var properties = root.GetProperty("properties");

        root.GetProperty("required").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Equal("question");
        properties.GetProperty("allow_free_text").GetProperty("type").GetString()
            .Should().Be("boolean");
        properties.GetProperty("options").GetProperty("description").GetString().Should()
            .Contain("zero options")
            .And.Contain("allow_free_text is true")
            .And.Contain("2-6 options");
        var optionShapes = properties.GetProperty("options").GetProperty("oneOf")
            .EnumerateArray().ToArray();
        optionShapes.Should().HaveCount(2);
        optionShapes[0].GetProperty("maxItems").GetInt32().Should().Be(0);
        optionShapes[1].GetProperty("minItems").GetInt32().Should().Be(2);
        optionShapes[1].GetProperty("maxItems").GetInt32().Should().Be(6);
        properties.GetProperty("multi_select").GetProperty("description").GetString().Should()
            .Contain("Requires 2-6 options");
    }

    [Fact]
    public async Task ExecuteAsync_WhenFreeTextOnly_ShouldPreserveTheAnswerMode()
    {
        var result = await new AskUserTool().ExecuteAsync(
            """
            {
              "question": "Which region, budget, and launch date should I use?",
              "allow_free_text": true
            }
            """);
        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;

        root.GetProperty("question").GetString().Should()
            .Be("Which region, budget, and launch date should I use?");
        root.GetProperty("options").GetArrayLength().Should().Be(0);
        root.GetProperty("allow_free_text").GetBoolean().Should().BeTrue();
        root.GetProperty("multi_select").GetBoolean().Should().BeFalse();
    }
}
