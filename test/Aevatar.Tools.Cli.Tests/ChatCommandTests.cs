using Aevatar.Tools.Cli.Commands;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public class ChatCommandTests
{
    [Fact]
    public void Create_ShouldExposeWorkflowSubcommandWithExpectedOptions()
    {
        var chatCommand = ChatCommand.Create();

        chatCommand.Subcommands.Should().ContainSingle(command => command.Name == "workflow");
        var workflow = chatCommand.Subcommands.Single(command => command.Name == "workflow");
        workflow.Arguments.Should().Contain(argument => argument.Name == "message");
        workflow.Options.Should().Contain(option => option.Aliases.Contains("--stdin"));
        workflow.Options.Should().Contain(option => option.Aliases.Contains("--url"));
        workflow.Options.Should().Contain(option => option.Aliases.Contains("--filename"));
        workflow.Options.Should().Contain(option => option.Aliases.Contains("--yes"));
    }
}
