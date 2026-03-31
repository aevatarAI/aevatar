using System.CommandLine;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class LoginCommand
{
    public static Command Create()
    {
        var command = new Command("login", "Log in to NyxID.");

        var passwordOption = new Option<bool>(
            "--password",
            "Use email/password login instead of opening the browser.");
        var emailOption = new Option<string?>(
            "--email",
            "Email address (only used with --password).");

        command.AddOption(passwordOption);
        command.AddOption(emailOption);

        command.SetHandler(async (bool password, string? email) =>
        {
            var success = password
                ? await NyxIdLoginHandler.PasswordLoginAsync(email, CancellationToken.None)
                : await NyxIdLoginHandler.BrowserLoginAsync(CancellationToken.None);

            Environment.ExitCode = success ? 0 : 1;
        }, passwordOption, emailOption);

        return command;
    }
}
