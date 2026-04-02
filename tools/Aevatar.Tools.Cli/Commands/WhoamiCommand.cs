using System.CommandLine;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class WhoamiCommand
{
    public static Command Create()
    {
        var command = new Command("whoami", "Show the currently logged-in NyxID user.");

        command.SetHandler(() =>
        {
            var token = NyxIdTokenStore.LoadToken();
            if (token is null)
            {
                Console.Error.WriteLine("Not logged in. Run 'aevatar login' to authenticate.");
                Environment.ExitCode = 1;
                return;
            }

            var (email, name) = NyxIdTokenStore.LoadUserInfo();
            if (name is not null)
                Console.WriteLine(email is not null ? $"{name} ({email})" : name);
            else if (email is not null)
                Console.WriteLine(email);
            else
                Console.WriteLine("Logged in (no user info available).");
        });

        return command;
    }
}
