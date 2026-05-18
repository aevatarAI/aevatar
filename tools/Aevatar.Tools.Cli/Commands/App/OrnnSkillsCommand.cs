using System.CommandLine;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class OrnnSkillsCommand
{
    private const string DefaultNyxIdBaseUrl = "https://nyx-api.chrono-ai.fun";

    public static Command Create()
    {
        var command = new Command("skills", "Browse and inspect Ornn skills.");

        var tokenOption = new Option<string>("--token", "NyxID bearer token.") { IsRequired = true };
        var nyxIdUrlOption = new Option<string?>("--nyxid-url", "NyxID base URL override (reads Cli:App:NyxId:Authority from config if not set).");
        var slugOption = new Option<string>("--slug", () => "ornn-api", "NyxID-bound Ornn service slug. Bare 'ornn' is the SPA frontend (returns HTML).");

        command.AddCommand(CreateListCommand(tokenOption, nyxIdUrlOption, slugOption));
        command.AddCommand(CreateShowCommand(tokenOption, nyxIdUrlOption, slugOption));

        return command;
    }

    private static Command CreateListCommand(Option<string> tokenOption, Option<string?> nyxIdUrlOption, Option<string> slugOption)
    {
        var command = new Command("list", "Search/list Ornn skills.");

        var queryOption = new Option<string>("--query", () => "", "Search keywords.");
        var scopeOption = new Option<string>("--scope", () => "mixed", "Search scope: public | private | mixed.");
        var pageOption = new Option<int>("--page", () => 1, "Page number.");
        var pageSizeOption = new Option<int>("--page-size", () => 20, "Results per page.");

        command.AddOption(tokenOption);
        command.AddOption(nyxIdUrlOption);
        command.AddOption(slugOption);
        command.AddOption(queryOption);
        command.AddOption(scopeOption);
        command.AddOption(pageOption);
        command.AddOption(pageSizeOption);

        command.SetHandler(async (string token, string? nyxIdUrl, string slug, string query, string scope, int page, int pageSize) =>
        {
            var client = TryCreateClient(nyxIdUrl, slug);
            if (client is null)
                return;

            try
            {
                var result = await client.SearchSkillsAsync(token, query, scope, page, pageSize, ct: CancellationToken.None);
                PrintSearchResults(result);
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
            }
        }, tokenOption, nyxIdUrlOption, slugOption, queryOption, scopeOption, pageOption, pageSizeOption);

        return command;
    }

    private static Command CreateShowCommand(Option<string> tokenOption, Option<string?> nyxIdUrlOption, Option<string> slugOption)
    {
        var command = new Command("show", "Show details of a specific Ornn skill.");

        var nameArg = new Argument<string>("name-or-id", "Skill name or GUID.");

        command.AddArgument(nameArg);
        command.AddOption(tokenOption);
        command.AddOption(nyxIdUrlOption);
        command.AddOption(slugOption);

        command.SetHandler(async (string nameOrId, string token, string? nyxIdUrl, string slug) =>
        {
            var client = TryCreateClient(nyxIdUrl, slug);
            if (client is null)
                return;

            try
            {
                var skill = await client.GetSkillJsonAsync(token, nameOrId, CancellationToken.None);
                if (skill == null)
                {
                    Console.Error.WriteLine($"Skill '{nameOrId}' not found or access denied.");
                    return;
                }

                PrintSkillDetail(skill);
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
            }
        }, nameArg, tokenOption, nyxIdUrlOption, slugOption);

        return command;
    }

    private static OrnnSkillClient? TryCreateClient(string? nyxIdUrlOverride, string slug)
    {
        var nyxIdUrl = ResolveNyxIdUrl(nyxIdUrlOverride);
        if (string.IsNullOrWhiteSpace(nyxIdUrl))
        {
            Console.Error.WriteLine(
                "NyxID base URL not configured. Use --nyxid-url or run: " +
                "aevatar config config-json set Cli:App:NyxId:Authority <url> --json");
            return null;
        }

        // CLI-only command path: the long-running server registers NyxIdApiClient via DI
        // and IHttpClientFactory in AddNyxIdTools; this short-lived process owns its client.
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = nyxIdUrl }, new HttpClient());
        return new OrnnSkillClient(new OrnnOptions { NyxIdSlug = slug }, nyxClient);
    }

    private static string? ResolveNyxIdUrl(string? nyxIdUrlOverride)
    {
        if (!string.IsNullOrWhiteSpace(nyxIdUrlOverride))
            return nyxIdUrlOverride.TrimEnd('/');

        // CLI's NyxID authority follows the same key the frontend / config UI uses.
        var configured = CliAppConfigStore.TryGetConfigValue("Cli:App:NyxId:Authority");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        // Fall back to the production NyxID host so dev workstations work without explicit
        // config — matches the default in tools/Aevatar.Tools.Cli/Frontend/src/auth/nyxid.ts.
        return DefaultNyxIdBaseUrl;
    }

    private static void PrintSearchResults(OrnnSearchResult result)
    {
        if (!string.IsNullOrEmpty(result.Error))
        {
            Console.Error.WriteLine($"Search failed: {result.Error}");
            return;
        }

        Console.WriteLine($"Skills found: {result.Total} (page {result.Page}/{result.TotalPages})");
        Console.WriteLine();

        if (result.Items.Count == 0)
        {
            Console.WriteLine("  (no skills found)");
            return;
        }

        Console.WriteLine($"  {"NAME",-30} {"CATEGORY",-15} {"VISIBILITY",-12} {"DESCRIPTION"}");
        Console.WriteLine($"  {new string('-', 30)} {new string('-', 15)} {new string('-', 12)} {new string('-', 40)}");

        foreach (var skill in result.Items)
        {
            var name = skill.Name ?? "(unnamed)";
            var category = skill.Metadata?.Category ?? "unknown";
            var visibility = skill.IsPrivate ? "private" : "public";
            var desc = Truncate(skill.Description ?? "", 60);
            Console.WriteLine($"  {Truncate(name, 30),-30} {Truncate(category, 15),-15} {visibility,-12} {desc}");
        }
    }

    private static void PrintSkillDetail(OrnnSkillJson skill)
    {
        Console.WriteLine($"Name:        {skill.Name}");
        Console.WriteLine($"Description: {skill.Description}");
        Console.WriteLine($"Category:    {skill.Metadata?.Category}");

        if (skill.Metadata?.Tags is { Count: > 0 })
            Console.WriteLine($"Tags:        {string.Join(", ", skill.Metadata.Tags)}");

        if (skill.Files is { Count: > 0 })
        {
            Console.WriteLine();
            Console.WriteLine("Files:");
            foreach (var (fileName, content) in skill.Files)
            {
                Console.WriteLine($"  --- {fileName} ---");
                Console.WriteLine(content);
                Console.WriteLine();
            }
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";
}
