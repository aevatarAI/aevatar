using System.Text;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioCatalogImportParserTests
{
    [Fact]
    public async Task Connector_import_parser_keeps_json_catalog_boundary()
    {
        const string json = """
        {
          "connectors": [
            {
              "name": "github",
              "type": "http",
              "enabled": true,
              "timeoutMs": 1200,
              "retry": 2,
              "http": {
                "baseUrl": "https://api.github.com",
                "allowedMethods": ["GET"],
                "allowedPaths": ["/repos"],
                "allowedInputKeys": ["owner"],
                "defaultHeaders": { "Accept": "application/json" },
                "auth": { "type": "bearer", "scope": "repo" }
              }
            }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var parser = new ConnectorCatalogImportParser();

        var result = await parser.ParseCatalogAsync(stream, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Name.Should().Be("github");
        result[0].Http.DefaultHeaders.Should().ContainKey("Accept");
    }

    [Fact]
    public async Task Role_import_parser_keeps_json_catalog_boundary()
    {
        const string json = """
        {
          "roles": [
            {
              "id": "reviewer",
              "name": "Reviewer",
              "systemPrompt": "Review carefully.",
              "provider": "openai",
              "model": "gpt-5.5",
              "connectors": ["github"]
            }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var parser = new RoleCatalogImportParser();

        var result = await parser.ParseCatalogAsync(stream, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Id.Should().Be("reviewer");
        result[0].Connectors.Should().ContainSingle("github");
    }
}
