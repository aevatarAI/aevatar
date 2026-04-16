using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class RootCommandFactoryTests
{
    [Fact]
    public void Create_ShouldReturnRootCommandWithDescription()
    {
        var root = RootCommandFactory.Create();

        root.Description.Should().Be("Aevatar unified CLI");
    }

    [Theory]
    [InlineData("login")]
    [InlineData("logout")]
    [InlineData("whoami")]
    [InlineData("config")]
    [InlineData("app")]
    [InlineData("chat")]
    [InlineData("voice")]
    public void Create_ShouldRegisterSubcommand(string commandName)
    {
        var root = RootCommandFactory.Create();

        root.Subcommands.Should().Contain(cmd => cmd.Name == commandName);
    }

    [Fact]
    public void Create_ShouldRegisterExactly7Subcommands()
    {
        var root = RootCommandFactory.Create();

        root.Subcommands.Should().HaveCount(7);
    }
}
