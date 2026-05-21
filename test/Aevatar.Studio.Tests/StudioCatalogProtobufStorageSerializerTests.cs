using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioCatalogProtobufStorageSerializerTests
{
    [Fact]
    public async Task Connector_catalog_storage_round_trips_as_protobuf_fact()
    {
        var connector = NewConnector();
        using var stream = new MemoryStream();

        await ConnectorCatalogStorageSerializer.WriteCatalogAsync(stream, [connector], CancellationToken.None);

        stream.ToArray()[0].Should().NotBe((byte)'{');
        stream.Position = 0;
        var result = await ConnectorCatalogStorageSerializer.ReadCatalogAsync(stream, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().BeEquivalentTo(connector);
    }

    [Fact]
    public async Task Connector_draft_storage_round_trips_as_protobuf_fact()
    {
        var updatedAt = new DateTimeOffset(2026, 5, 21, 1, 2, 3, TimeSpan.Zero);
        var connector = NewConnector();
        using var stream = new MemoryStream();

        await ConnectorCatalogStorageSerializer.WriteDraftAsync(stream, connector, updatedAt, CancellationToken.None);

        stream.ToArray()[0].Should().NotBe((byte)'{');
        stream.Position = 0;
        var result = await ConnectorCatalogStorageSerializer.ReadDraftAsync(
            stream,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        result.UpdatedAtUtc.Should().Be(updatedAt);
        result.Draft.Should().BeEquivalentTo(connector);
    }

    [Fact]
    public async Task Connector_reader_keeps_json_as_import_fallback()
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

        var result = await ConnectorCatalogStorageSerializer.ReadCatalogAsync(stream, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Name.Should().Be("github");
        result[0].Http.DefaultHeaders.Should().ContainKey("Accept");
    }

    [Fact]
    public async Task Role_catalog_storage_round_trips_as_protobuf_fact()
    {
        var role = NewRole();
        using var stream = new MemoryStream();

        await RoleCatalogStorageSerializer.WriteCatalogAsync(stream, [role], CancellationToken.None);

        stream.ToArray()[0].Should().NotBe((byte)'{');
        stream.Position = 0;
        var result = await RoleCatalogStorageSerializer.ReadCatalogAsync(stream, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().BeEquivalentTo(role);
    }

    [Fact]
    public async Task Role_draft_storage_round_trips_as_protobuf_fact()
    {
        var updatedAt = new DateTimeOffset(2026, 5, 21, 4, 5, 6, TimeSpan.Zero);
        var role = NewRole();
        using var stream = new MemoryStream();

        await RoleCatalogStorageSerializer.WriteDraftAsync(stream, role, updatedAt, CancellationToken.None);

        stream.ToArray()[0].Should().NotBe((byte)'{');
        stream.Position = 0;
        var result = await RoleCatalogStorageSerializer.ReadDraftAsync(
            stream,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        result.UpdatedAtUtc.Should().Be(updatedAt);
        result.Draft.Should().BeEquivalentTo(role);
    }

    [Fact]
    public async Task Role_reader_keeps_json_as_import_fallback()
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

        var result = await RoleCatalogStorageSerializer.ReadCatalogAsync(stream, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Id.Should().Be("reviewer");
        result[0].Connectors.Should().ContainSingle("github");
    }

    private static StoredRoleDefinition NewRole() =>
        new(
            Id: "builder",
            Name: "Builder",
            SystemPrompt: "Build the workflow.",
            Provider: "openai",
            Model: "gpt-5.5",
            Connectors: ["github", "slack"]);

    private static StoredConnectorDefinition NewConnector() =>
        new(
            Name: "github",
            Type: "http",
            Enabled: true,
            TimeoutMs: 1200,
            Retry: 2,
            Http: new StoredHttpConnectorConfig(
                BaseUrl: "https://api.github.com",
                AllowedMethods: ["GET", "POST"],
                AllowedPaths: ["/repos"],
                AllowedInputKeys: ["owner"],
                DefaultHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Accept"] = "application/json",
                },
                Auth: new StoredConnectorAuthConfig(
                    Type: "bearer",
                    TokenUrl: "https://auth.example/token",
                    ClientId: "client",
                    ClientSecret: "secret",
                    Scope: "repo")),
            Cli: new StoredCliConnectorConfig(
                Command: "gh",
                FixedArguments: ["repo"],
                AllowedOperations: ["list"],
                AllowedInputKeys: ["owner"],
                WorkingDirectory: "/tmp",
                Environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GH_HOST"] = "github.com",
                }),
            Mcp: new StoredMcpConnectorConfig(
                ServerName: "github",
                Command: "npx",
                Url: "https://mcp.example",
                Arguments: ["github-mcp"],
                Environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MCP_ENV"] = "test",
                },
                AdditionalHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-Test"] = "1",
                },
                Auth: new StoredConnectorAuthConfig(
                    Type: "oauth",
                    TokenUrl: "https://auth.example/mcp",
                    ClientId: "mcp-client",
                    ClientSecret: "mcp-secret",
                    Scope: "tools"),
                DefaultTool: "search",
                AllowedTools: ["search"],
                AllowedInputKeys: ["query"]));
}
