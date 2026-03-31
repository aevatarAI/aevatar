using System.CommandLine;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class AppInvokeCommand
{
    public static Command Create(Option<string?> urlOption)
    {
        var command = new Command("invoke", "Invoke a service chat endpoint with SSE streaming.");

        var scopeOption = new Option<string>("--scope", "Scope ID (tenant).") { IsRequired = true };
        var promptOption = new Option<string>("--prompt", "Prompt text to send.") { IsRequired = true };
        var serviceOption = new Option<string?>("--service", "Service ID. If omitted, invokes scope-level chat.");
        var sessionOption = new Option<string?>("--session", "Session ID for conversation continuity.");

        command.AddOption(scopeOption);
        command.AddOption(promptOption);
        command.AddOption(serviceOption);
        command.AddOption(sessionOption);
        command.AddOption(urlOption);

        command.SetHandler(async (string scope, string prompt, string? service, string? session, string? url) =>
        {
            var baseUrl = CliAppConfigStore.ResolveApiBaseUrl(url, "http://localhost:5080", out _);
            using var client = new AppApiClient(baseUrl);

            var target = string.IsNullOrWhiteSpace(service)
                ? $"scope '{scope}'"
                : $"service '{service}' in scope '{scope}'";
            Console.WriteLine($"Invoking chat on {target} ...");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            try
            {
                using var response = await client.StreamChatAsync(scope, prompt, service, session, cts.Token);
                await SseStreamReader.ReadAndPrintAsync(response, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\nCancelled.");
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
            }
        }, scopeOption, promptOption, serviceOption, sessionOption, urlOption);

        return command;
    }
}
