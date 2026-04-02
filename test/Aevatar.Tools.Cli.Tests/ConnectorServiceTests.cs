using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ConnectorServiceTests
{
    [Fact]
    public async Task SaveCatalogAsync_WhenRemoteMcpConnectorUsesUrl_ShouldAcceptConfiguration()
    {
        var store = new RecordingConnectorCatalogStore();
        var service = new ConnectorService(store, new StubConnectorCatalogImportParser());

        var response = await service.SaveCatalogAsync(new SaveConnectorCatalogRequest(
            [
                new ConnectorDefinitionDto(
                    Name: "nyxid_mcp",
                    Type: "mcp",
                    Enabled: true,
                    TimeoutMs: 60_000,
                    Retry: 1,
                    Http: EmptyHttp(),
                    Cli: EmptyCli(),
                    Mcp: new McpConnectorDefinitionDto(
                        ServerName: "nyxid",
                        Command: string.Empty,
                        Url: "https://nyxid.example.com/mcp",
                        Arguments: [],
                        Environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        AdditionalHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["x-tenant"] = "demo",
                        },
                        Auth: new ConnectorAuthDefinitionDto(
                            Type: "client_credentials",
                            TokenUrl: "https://auth.example.com/oauth/token",
                            ClientId: "svc-client",
                            ClientSecret: "svc-secret",
                            Scope: "proxy:*"),
                        DefaultTool: "chrono-graph__query",
                        AllowedTools: ["chrono-graph__query"],
                        AllowedInputKeys: ["query"]))
            ]));

        response.Connectors.Should().ContainSingle();
        store.LastSavedCatalog.Should().NotBeNull();
        store.LastSavedCatalog!.Connectors.Should().ContainSingle();
        store.LastSavedCatalog.Connectors[0].Mcp.Url.Should().Be("https://nyxid.example.com/mcp");
        store.LastSavedCatalog.Connectors[0].Mcp.Auth.Type.Should().Be("client_credentials");
        store.LastSavedCatalog.Connectors[0].Mcp.AdditionalHeaders.Should().ContainKey("x-tenant").WhoseValue.Should().Be("demo");
    }

    [Fact]
    public async Task SaveCatalogAsync_WhenMcpAuthConfiguredWithoutUrl_ShouldReject()
    {
        var service = new ConnectorService(new RecordingConnectorCatalogStore(), new StubConnectorCatalogImportParser());

        var act = async () => await service.SaveCatalogAsync(new SaveConnectorCatalogRequest(
            [
                new ConnectorDefinitionDto(
                    Name: "bad_mcp",
                    Type: "mcp",
                    Enabled: true,
                    TimeoutMs: 30_000,
                    Retry: 0,
                    Http: EmptyHttp(),
                    Cli: EmptyCli(),
                    Mcp: new McpConnectorDefinitionDto(
                        ServerName: "nyxid",
                        Command: "npx",
                        Url: string.Empty,
                        Arguments: ["-y", "server"],
                        Environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        AdditionalHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        Auth: new ConnectorAuthDefinitionDto(
                            Type: "client_credentials",
                            TokenUrl: "https://auth.example.com/oauth/token",
                            ClientId: "svc-client",
                            ClientSecret: "svc-secret",
                            Scope: "proxy:*"),
                        DefaultTool: string.Empty,
                        AllowedTools: [],
                        AllowedInputKeys: []))
            ]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*mcp.auth requires mcp.url*");
    }

    private static HttpConnectorDefinitionDto EmptyHttp() =>
        new(
            BaseUrl: string.Empty,
            AllowedMethods: [],
            AllowedPaths: [],
            AllowedInputKeys: [],
            DefaultHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Auth: new ConnectorAuthDefinitionDto(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty));

    private static CliConnectorDefinitionDto EmptyCli() =>
        new(
            Command: string.Empty,
            FixedArguments: [],
            AllowedOperations: [],
            AllowedInputKeys: [],
            WorkingDirectory: string.Empty,
            Environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private sealed class RecordingConnectorCatalogStore : IConnectorCatalogStore
    {
        public StoredConnectorCatalog? LastSavedCatalog { get; private set; }

        public Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(LastSavedCatalog ?? new StoredConnectorCatalog(string.Empty, string.Empty, false, []));

        public Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(StoredConnectorCatalog catalog, CancellationToken cancellationToken = default)
        {
            LastSavedCatalog = catalog with { FileExists = true };
            return Task.FromResult(LastSavedCatalog);
        }

        public Task<ImportedConnectorCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredConnectorDraft(string.Empty, string.Empty, false, null, null));

        public Task<StoredConnectorDraft> SaveConnectorDraftAsync(StoredConnectorDraft draft, CancellationToken cancellationToken = default) =>
            Task.FromResult(draft);

        public Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubConnectorCatalogImportParser : IConnectorCatalogImportParser
    {
        public Task<IReadOnlyList<StoredConnectorDefinition>> ParseCatalogAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            _ = stream;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<StoredConnectorDefinition>>([]);
        }
    }
}
