using System.CommandLine;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class OrnnSkillsCommand
{
    public static Command Create()
    {
        var command = new Command("skills", "Browse and inspect Ornn skills.");

        var tokenOption = new Option<string>("--token", "NyxID bearer token.") { IsRequired = true };
        var ornnUrlOption = new Option<string?>("--ornn-url", "Ornn base URL override (reads Ornn:BaseUrl from config if not set).");

        command.AddCommand(CreateListCommand(tokenOption, ornnUrlOption));
        command.AddCommand(CreateShowCommand(tokenOption, ornnUrlOption));

        return command;
    }

    private static Command CreateListCommand(Option<string> tokenOption, Option<string?> ornnUrlOption)
    {
        var command = new Command("list", "Search/list Ornn skills.");

        var queryOption = new Option<string>("--query", () => "", "Search keywords.");
        var scopeOption = new Option<string>("--scope", () => "mixed", "Search scope: public | private | mixed.");
        var pageOption = new Option<int>("--page", () => 1, "Page number.");
        var pageSizeOption = new Option<int>("--page-size", () => 20, "Results per page.");

        command.AddOption(tokenOption);
        command.AddOption(ornnUrlOption);
        command.AddOption(queryOption);
        command.AddOption(scopeOption);
        command.AddOption(pageOption);
        command.AddOption(pageSizeOption);

        command.SetHandler(async (string token, string? ornnUrl, string query, string scope, int page, int pageSize) =>
        {
            var baseUrl = ResolveOrnnUrl(ornnUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                Console.Error.WriteLine("Ornn base URL not configured. Use --ornn-url or run: aevatar config ornn set-url <url>");
                return;
            }

            var options = new OrnnOptions { BaseUrl = baseUrl };
            var client = new OrnnSkillClient(options);

            try
            {
                var result = await client.SearchSkillsAsync(token, query, scope, page, pageSize, CancellationToken.None);
                PrintSearchResults(result);
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
            }
        }, tokenOption, ornnUrlOption, queryOption, scopeOption, pageOption, pageSizeOption);

        return command;
    }

    private static Command CreateShowCommand(Option<string> tokenOption, Option<string?> ornnUrlOption)
    {
        var command = new Command("show", "Show details of a specific Ornn skill.");

        var nameArg = new Argument<string>("name-or-id", "Skill name or GUID.");

        command.AddArgument(nameArg);
        command.AddOption(tokenOption);
        command.AddOption(ornnUrlOption);

        command.SetHandler(async (string nameOrId, string token, string? ornnUrl) =>
        {
            var baseUrl = ResolveOrnnUrl(ornnUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                Console.Error.WriteLine("Ornn base URL not configured. Use --ornn-url or run: aevatar config ornn set-url <url>");
                return;
            }

            var options = new OrnnOptions { BaseUrl = baseUrl };
            var client = new OrnnSkillClient(options);

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
        }, nameArg, tokenOption, ornnUrlOption);

        return command;
    }

    private static string? ResolveOrnnUrl(string? ornnUrlOverride)
    {
        if (!string.IsNullOrWhiteSpace(ornnUrlOverride))
            return ornnUrlOverride.TrimEnd('/');

        // Read from ~/.aevatar/config.json at Ornn:BaseUrl
        return CliAppConfigStore.TryGetConfigValue("Ornn:BaseUrl");
    }

    private static void PrintSearchResults(OrnnSearchResult result)
    {
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
