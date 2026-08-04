using System.Text.Json;
using FluentAssertions;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetWebToolConfigurationTests
{
    [Fact]
    public async Task AppSettings_ShouldConfigureDefaultNyxIdWebSearchBackend()
    {
        var appSettingsPath = Path.Combine(
            FindRepoRoot(),
            "src",
            "Aevatar.Mainnet.Host.Api",
            "appsettings.json");

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(appSettingsPath));

        var aevatar = document.RootElement.GetProperty("Aevatar");
        aevatar.TryGetProperty("Web", out var web).Should().BeTrue();
        web.GetProperty("NyxIdBaseUrl").GetString().Should().Be("https://nyx-api.chrono-ai.fun");
        web.GetProperty("NyxIdSearchSlug").GetString().Should().Be("api-firecrawl");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
