using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Tests;

public sealed class StudioLocalCatalogImportReaderTests
{
    [Fact]
    public async Task ReadConnectorCatalog_WhenFileMissing_ShouldReturnResolvedMissingPath()
    {
        using var temp = new TempStudioRoot();
        var reader = CreateReader(temp.Root);

        var catalog = await ((IStudioLocalConnectorCatalogImportReader)reader).ReadAsync();

        catalog.FileExists.Should().BeFalse();
        catalog.HomeDirectory.Should().Be(temp.Root);
        catalog.FilePath.Should().Be(Path.Combine(temp.Root, "connectors.json"));
        catalog.Connectors.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadConnectorCatalog_WhenFilePresent_ShouldParseRepresentativeFields()
    {
        using var temp = new TempStudioRoot();
        await File.WriteAllTextAsync(Path.Combine(temp.Root, "connectors.json"), """
            {
              "connectors": [
                {
                  "name": "inventory-api",
                  "type": "http",
                  "enabled": false,
                  "timeoutMs": 12000,
                  "http": {
                    "baseUrl": "https://inventory.example.test",
                    "allowedMethods": ["GET"],
                    "allowedPaths": ["/items"],
                    "defaultHeaders": { "X-Team": "studio" }
                  }
                }
              ]
            }
            """);
        var reader = CreateReader(temp.Root);

        var catalog = await ((IStudioLocalConnectorCatalogImportReader)reader).ReadAsync();

        catalog.FileExists.Should().BeTrue();
        var connector = catalog.Connectors.Should().ContainSingle().Subject;
        connector.Name.Should().Be("inventory-api");
        connector.Type.Should().Be("http");
        connector.Enabled.Should().BeFalse();
        connector.TimeoutMs.Should().Be(12000);
        connector.Http.BaseUrl.Should().Be("https://inventory.example.test");
        connector.Http.DefaultHeaders.Should().ContainKey("X-Team").WhoseValue.Should().Be("studio");
    }

    [Fact]
    public async Task ReadRoleCatalog_WhenFileMissing_ShouldReturnResolvedMissingPath()
    {
        using var temp = new TempStudioRoot();
        var reader = CreateReader(temp.Root);

        var catalog = await ((IStudioLocalRoleCatalogImportReader)reader).ReadAsync();

        catalog.FileExists.Should().BeFalse();
        catalog.HomeDirectory.Should().Be(temp.Root);
        catalog.FilePath.Should().Be(Path.Combine(temp.Root, "roles.json"));
        catalog.Roles.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRoleCatalog_WhenFilePresent_ShouldParseRepresentativeFields()
    {
        using var temp = new TempStudioRoot();
        await File.WriteAllTextAsync(Path.Combine(temp.Root, "roles.json"), """
            {
              "roles": [
                {
                  "id": "planner",
                  "name": "Planner",
                  "systemPrompt": "Plan work",
                  "provider": "openai",
                  "model": "gpt-5.4",
                  "connectors": ["inventory-api"]
                }
              ]
            }
            """);
        var reader = CreateReader(temp.Root);

        var catalog = await ((IStudioLocalRoleCatalogImportReader)reader).ReadAsync();

        catalog.FileExists.Should().BeTrue();
        var role = catalog.Roles.Should().ContainSingle().Subject;
        role.Id.Should().Be("planner");
        role.Name.Should().Be("Planner");
        role.SystemPrompt.Should().Be("Plan work");
        role.Provider.Should().Be("openai");
        role.Model.Should().Be("gpt-5.4");
        role.Connectors.Should().ContainSingle().Which.Should().Be("inventory-api");
    }

    private static StudioLocalCatalogImportReader CreateReader(string rootDirectory) =>
        new(Options.Create(new StudioStorageOptions { RootDirectory = rootDirectory }));

    private sealed class TempStudioRoot : IDisposable
    {
        public TempStudioRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), $"aevatar-studio-catalog-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
