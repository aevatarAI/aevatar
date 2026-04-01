using Aevatar.Tools.Cli.Commands;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ConfigCommandTests
{
    [Fact]
    public void Create_ShouldHaveCorrectName()
    {
        var command = ConfigCommand.Create();

        command.Name.Should().Be("config");
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("doctor")]
    [InlineData("paths")]
    [InlineData("secrets")]
    [InlineData("config-json")]
    [InlineData("llm")]
    [InlineData("workflows")]
    [InlineData("connectors")]
    [InlineData("mcp")]
    [InlineData("ornn")]
    public void Create_ShouldRegisterSubcommand(string subcommandName)
    {
        var command = ConfigCommand.Create();

        command.Subcommands.Should().Contain(cmd => cmd.Name == subcommandName);
    }

    [Fact]
    public void Create_ShouldExposeGlobalOptions()
    {
        var command = ConfigCommand.Create();

        command.Options.Should().Contain(o => o.Aliases.Contains("--json"));
        command.Options.Should().Contain(o => o.Aliases.Contains("--quiet"));
        command.Options.Should().Contain(o => o.Aliases.Contains("--yes"));
    }

    [Fact]
    public void Create_ShouldRegisterExactly10Subcommands()
    {
        var command = ConfigCommand.Create();

        command.Subcommands.Should().HaveCount(10);
    }
}
