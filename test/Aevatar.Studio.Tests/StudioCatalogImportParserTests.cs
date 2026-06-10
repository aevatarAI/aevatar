using System.Text;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;
using Google.Protobuf;

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
    public async Task Connector_import_parser_should_parse_secret_ref_header_auth()
    {
        const string json = """
        {
          "connectors": [
            {
              "name": "twitterapi",
              "type": "http",
              "http": {
                "baseUrl": "https://api.twitterapi.io",
                "auth": {
                  "type": "secret_ref_header",
                  "secretRef": "secrets://connectors/twitterapi",
                  "headerName": "X-API-Key",
                  "headerValuePrefix": "Bearer "
                }
              }
            }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var parser = new ConnectorCatalogImportParser();

        var result = await parser.ParseCatalogAsync(stream, CancellationToken.None);

        var auth = result.Should().ContainSingle().Subject.Http.Auth;
        auth.Type.Should().Be("secret_ref_header");
        auth.SecretRef.Should().Be("secrets://connectors/twitterapi");
        auth.HeaderName.Should().Be("X-API-Key");
        auth.HeaderValuePrefix.Should().Be("Bearer ");
    }

    [Fact]
    public void ConnectorAuthEntry_ShouldRoundTripSecretRefHeaderFields()
    {
        var entry = new ConnectorAuthEntry
        {
            Type = "secret_ref_header",
            SecretRef = "secrets://connectors/twitterapi",
            HeaderName = "X-API-Key",
            HeaderValuePrefix = "Bearer ",
        };

        var parsed = ConnectorAuthEntry.Parser.ParseFrom(entry.ToByteArray());

        parsed.Type.Should().Be("secret_ref_header");
        parsed.SecretRef.Should().Be("secrets://connectors/twitterapi");
        parsed.HeaderName.Should().Be("X-API-Key");
        parsed.HeaderValuePrefix.Should().Be("Bearer ");
        ConnectorAuthEntry.Descriptor.Fields.InDeclarationOrder()
            .Should().Contain(field => field.FieldNumber == 6 && field.Name == "secret_ref")
            .And.Contain(field => field.FieldNumber == 7 && field.Name == "header_name")
            .And.Contain(field => field.FieldNumber == 8 && field.Name == "header_value_prefix");
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
