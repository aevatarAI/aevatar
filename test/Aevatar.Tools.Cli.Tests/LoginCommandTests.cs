using Aevatar.Tools.Cli.Commands;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class LoginCommandTests
{
    [Fact]
    public void Create_ShouldExposePasswordAndEmailOptions()
    {
        var command = LoginCommand.Create();

        command.Options.Should().Contain(o => o.Aliases.Contains("--password"));
        command.Options.Should().Contain(o => o.Aliases.Contains("--email"));
    }

    [Fact]
    public void Create_ShouldHaveCorrectName()
    {
        var command = LoginCommand.Create();

        command.Name.Should().Be("login");
    }

    [Fact]
    public void Create_ShouldHaveDescription()
    {
        var command = LoginCommand.Create();

        command.Description.Should().NotBeNullOrWhiteSpace();
    }
}
