using System.CommandLine;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class LogoutCommand
{
    public static Command Create()
    {
        var command = new Command("logout", "Log out from NyxID and clear stored credentials.");

        command.SetHandler(() =>
        {
            NyxIdTokenStore.ClearToken();
            Console.Error.WriteLine("Logged out.");
        });

        return command;
    }
}
