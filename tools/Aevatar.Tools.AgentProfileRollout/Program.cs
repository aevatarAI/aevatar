using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn;
using Microsoft.Extensions.Logging;

namespace Aevatar.Tools.AgentProfileRollout;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        }));

        var options = AgentProfileRolloutCliOptions.Parse(args);
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            return 2;
        }

        if (options.Command == AgentProfileRolloutCommand.Evaluate)
            return await AgentProfileRolloutCommands.EvaluateFileAsync(options.InputPath!, CancellationToken.None);

        var accessToken = Environment.GetEnvironmentVariable(options.AccessTokenEnvironmentVariable!);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            Console.Error.WriteLine($"Missing access token environment variable '{options.AccessTokenEnvironmentVariable}'.");
            return 2;
        }

        var nyxIdOptions = new NyxIdToolOptions();
        if (!string.IsNullOrWhiteSpace(options.NyxIdBaseUrl))
            nyxIdOptions.BaseUrl = options.NyxIdBaseUrl;
        using var httpClient = new HttpClient();
        using var nyxIdClient = new NyxIdApiClient(nyxIdOptions, httpClient, loggerFactory.CreateLogger<NyxIdApiClient>());
        var ornnOptions = new OrnnOptions { NyxIdSlug = options.OrnnServiceSlug! };
        var ornnClient = new OrnnSkillClient(
            ornnOptions,
            nyxIdClient,
            loggerFactory.CreateLogger<OrnnSkillClient>());
        var commands = new AgentProfileRolloutCommands(
            new OrnnAgentProfileRolloutGateway(ornnClient),
            loggerFactory.CreateLogger<AgentProfileRolloutCommands>());

        return await commands.ProvisionAsync(
            accessToken,
            options.InputPath!,
            options.OutputDirectory!,
            CancellationToken.None);
    }
}
