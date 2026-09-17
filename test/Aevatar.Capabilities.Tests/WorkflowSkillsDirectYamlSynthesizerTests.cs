using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Mainnet.Host.Api.Skills;
using FluentAssertions;

namespace Aevatar.Capabilities.Tests;

public sealed class WorkflowSkillsDirectYamlSynthesizerTests
{
    [Fact]
    public void Synthesize_ShouldEmitSingleLlmCallWorkflow_WithInstructionsAsSystemPrompt()
    {
        var skill = new SkillDefinition
        {
            Name = "Daily Brief",
            Description = "writes a daily brief",
            Instructions = "Line one\nLine two: with colon\n\nFinal line",
            RemoteId = "abc-123",
        };

        var yaml = SkillDirectWorkflowYamlSynthesizer.Synthesize(skill);

        yaml.Should().Contain("name: ornn-skill-abc-123");
        yaml.Should().Contain("type: llm_call");
        yaml.Should().Contain("target_role: assistant");
        yaml.Should().Contain("system_prompt: |");
        // Instruction lines are indented under the block scalar (6 spaces); colons/blank lines survive.
        yaml.Should().Contain("      Line one");
        yaml.Should().Contain("      Line two: with colon");
        yaml.Should().Contain("      Final line");
    }

    [Fact]
    public void Synthesize_ShouldFallBackToDescription_WhenInstructionsBlank()
    {
        var skill = new SkillDefinition
        {
            Name = "x",
            Description = "the description",
            Instructions = "   ",
        };

        var yaml = SkillDirectWorkflowYamlSynthesizer.Synthesize(skill);

        yaml.Should().Contain("      the description");
        yaml.Should().Contain("name: ornn-skill-x");
    }
}
