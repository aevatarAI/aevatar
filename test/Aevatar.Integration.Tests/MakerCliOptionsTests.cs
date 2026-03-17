using Aevatar.Demos.Maker;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

public class MakerCliOptionsTests
{
    [Fact]
    public void Parse_ShouldUseDefaults_WhenNoArgumentsProvided()
    {
        var options = MakerCliOptions.Parse([]);

        options.Mode.Should().Be(MakerCliOptions.LlmMode);
        options.InputText.Should().BeEmpty();
        options.ShowHelp.Should().BeFalse();
        options.IsDeterministicMode.Should().BeFalse();
    }

    [Fact]
    public void Parse_ShouldParseDeterministicModeAndJoinInputParts()
    {
        var options = MakerCliOptions.Parse(["--mode", "deterministic", "analyze", "this"]);

        options.Mode.Should().Be(MakerCliOptions.DeterministicMode);
        options.IsDeterministicMode.Should().BeTrue();
        options.InputText.Should().Be("analyze this");
    }

    [Fact]
    public void Parse_ShouldRecognizeHelpFlags()
    {
        var longHelp = MakerCliOptions.Parse(["--help"]);
        var shortHelp = MakerCliOptions.Parse(["-h"]);

        longHelp.ShowHelp.Should().BeTrue();
        shortHelp.ShowHelp.Should().BeTrue();
    }

    [Fact]
    public void Parse_ShouldThrow_WhenModeValueMissing()
    {
        Action act = () => MakerCliOptions.Parse(["--mode"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Missing value for --mode*");
    }

    [Fact]
    public void Parse_ShouldThrow_WhenModeUnsupported()
    {
        Action act = () => MakerCliOptions.Parse(["--mode", "unsupported"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unsupported mode*");
    }

    [Fact]
    public void BuildHelpText_ShouldContainUsageAndModeDescriptions()
    {
        var helpText = MakerCliOptions.BuildHelpText();

        helpText.Should().Contain("Usage:");
        helpText.Should().Contain("deterministic");
        helpText.Should().Contain("dotnet run -- --mode llm");
    }
}
