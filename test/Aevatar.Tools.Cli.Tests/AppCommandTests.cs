using Aevatar.Tools.Cli.Commands;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public class AppCommandTests
{
    [Fact]
    public void Create_ShouldExposeRestartSubcommandWithPortAndApiOptions()
    {
        var appCommand = AppCommand.Create();

        appCommand.Subcommands.Should().ContainSingle(command => command.Name == "restart");
        var restart = appCommand.Subcommands.Single(command => command.Name == "restart");

        restart.Options.Should().Contain(option => option.Aliases.Contains("--port"));
        restart.Options.Should().Contain(option => option.Aliases.Contains("--no-browser"));
        restart.Options.Should().Contain(option => option.Aliases.Contains("--api-base"));
    }
}
