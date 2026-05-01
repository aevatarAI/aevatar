using System.CommandLine;
using Aevatar.Tools.Cli.Commands;

namespace Aevatar.Tools.Cli;

public static class RootCommandFactory
{
    public static RootCommand Create()
    {
        var root = new RootCommand("Aevatar unified CLI");
        root.AddCommand(LoginCommand.Create());
        root.AddCommand(LogoutCommand.Create());
        root.AddCommand(WhoamiCommand.Create());
        root.AddCommand(ConfigCommand.Create());
        root.AddCommand(AppCommand.Create());
        root.AddCommand(ChatCommand.Create());
        root.AddCommand(ModelCommand.Create());
        return root;
    }
}
