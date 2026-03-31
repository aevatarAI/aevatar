using System.CommandLine;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class AppDraftRunCommand
{
    public static Command Create(Option<string?> urlOption)
    {
        var command = new Command("draft-run", "Run a draft workflow with SSE streaming.");

        var scopeOption = new Option<string>("--scope", "Scope ID (tenant).") { IsRequired = true };
        var promptOption = new Option<string>("--prompt", "Prompt text to send.") { IsRequired = true };
        var yamlOption = new Option<string[]?>("--yaml", "Path(s) to workflow YAML file(s).");

        command.AddOption(scopeOption);
        command.AddOption(promptOption);
        command.AddOption(yamlOption);
        command.AddOption(urlOption);

        command.SetHandler(async (string scope, string prompt, string[]? yamls, string? url) =>
        {
            var baseUrl = CliAppConfigStore.ResolveApiBaseUrl(url, "http://localhost:5080", out _);
            using var client = new AppApiClient(baseUrl);

            Console.WriteLine($"Connecting to scope '{scope}' ...");

            string[]? yamlContents = null;
            if (yamls is { Length: > 0 })
            {
                yamlContents = new string[yamls.Length];
                for (var i = 0; i < yamls.Length; i++)
                {
                    if (!File.Exists(yamls[i]))
                    {
                        Console.Error.WriteLine($"YAML file not found: {yamls[i]}");
                        return;
                    }
                    yamlContents[i] = await File.ReadAllTextAsync(yamls[i]);
                }
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            try
            {
                using var response = await client.StreamDraftRunAsync(scope, prompt, yamlContents, cts.Token);
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
        }, scopeOption, promptOption, yamlOption, urlOption);

        return command;
    }
}
