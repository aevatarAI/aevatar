using Aevatar.Tools.Cli.Commands;
using Aevatar.Tools.Cli.Hosting;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class VoiceCommandTests
{
    [Fact]
    public void Create_ShouldExposeExpectedOptions()
    {
        var command = VoiceCommand.Create();

        command.Options.Should().Contain(option => option.Aliases.Contains("--agent"));
        command.Options.Should().Contain(option => option.Aliases.Contains("--port"));
        command.Options.Should().Contain(option => option.Aliases.Contains("--url"));
        command.Options.Should().Contain(option => option.Aliases.Contains("--provider"));
        command.Options.Should().Contain(option => option.Aliases.Contains("--voice"));
    }

    [Fact]
    public void BuildUiUrl_ShouldEncodeVoiceParameters()
    {
        var url = VoiceCommandHandler.BuildUiUrl(
            "http://localhost:6688",
            "robot dog",
            "openai",
            "alloy",
            24000);

        url.Should().Be("http://localhost:6688/voice?agent=robot%20dog&sampleRateHz=24000&provider=openai&voice=alloy");
    }
}
