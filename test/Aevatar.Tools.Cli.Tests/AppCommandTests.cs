using Aevatar.Tools.Cli.Commands;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public class AppCommandTests
{
    [Fact]
    public void Create_ShouldExposeUrlPortAndNoBrowserOptions()
    {
        var appCommand = AppCommand.Create();

        appCommand.Options.Should().Contain(option => option.Aliases.Contains("--url"));
        appCommand.Options.Should().Contain(option => option.Aliases.Contains("--port"));
        appCommand.Options.Should().Contain(option => option.Aliases.Contains("--no-browser"));
    }

    [Fact]
    public void Create_ShouldRegisterRuntimeSubcommands()
    {
        var appCommand = AppCommand.Create();

        appCommand.Subcommands.Should().Contain(cmd => cmd.Name == "draft-run");
        appCommand.Subcommands.Should().Contain(cmd => cmd.Name == "services");
        appCommand.Subcommands.Should().Contain(cmd => cmd.Name == "bindings");
        appCommand.Subcommands.Should().Contain(cmd => cmd.Name == "invoke");
        appCommand.Subcommands.Should().Contain(cmd => cmd.Name == "logs");
    }
}
