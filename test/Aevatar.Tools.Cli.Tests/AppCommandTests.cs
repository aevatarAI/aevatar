using Aevatar.Tools.Cli.Commands;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public class AppCommandTests
{
    [Fact]
    public void Create_ShouldExposeUrlAndNoBrowserOptionsOnRootCommand()
    {
        var appCommand = AppCommand.Create();

        appCommand.Subcommands.Should().BeEmpty();
        appCommand.Options.Should().Contain(option => option.Aliases.Contains("--url"));
        appCommand.Options.Should().Contain(option => option.Aliases.Contains("--no-browser"));
    }
}
