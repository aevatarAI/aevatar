using System.Text.Json;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Application.Contracts;
using Aevatar.Tools.Cli.Studio.Application.Services;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class CatalogImportServiceTests
{
    [Fact]
    public async Task ConnectorImportCatalogAsync_ShouldPersistUploadedCatalog()
    {
        var store = new StubConnectorCatalogStore();
        var parser = new StubConnectorCatalogImportParser(
        [
            new StoredConnectorDefinition(
                Name: "scope_web",
                Type: "http",
                Enabled: true,
                TimeoutMs: 30_000,
                Retry: 1,
                Http: new StoredHttpConnectorConfig(
                    BaseUrl: "https://example.com/api",
                    AllowedMethods: ["POST"],
                    AllowedPaths: ["/"],
                    AllowedInputKeys: ["input"],
                    DefaultHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                Cli: new StoredCliConnectorConfig(
                    Command: string.Empty,
                    FixedArguments: [],
                    AllowedOperations: [],
                    AllowedInputKeys: [],
                    WorkingDirectory: string.Empty,
                    Environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                Mcp: new StoredMcpConnectorConfig(
                    ServerName: string.Empty,
                    Command: string.Empty,
                    Arguments: [],
                    Environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    DefaultTool: string.Empty,
                    AllowedTools: [],
                    AllowedInputKeys: [])),
        ]);
        var service = new ConnectorService(store, parser);

        await using var stream = new MemoryStream([1, 2, 3]);
        var response = await service.ImportCatalogAsync("connectors.json", stream);

        response.SourceFilePath.Should().Be("connectors.json");
        response.ImportedCount.Should().Be(1);
        response.Connectors.Should().ContainSingle().Which.Name.Should().Be("scope_web");
        store.Catalog.Connectors.Should().ContainSingle().Which.Name.Should().Be("scope_web");
    }

    [Fact]
    public async Task ConnectorImportCatalogAsync_WhenParserReturnsInvalidJson_ShouldRaiseFriendlyError()
    {
        var service = new ConnectorService(
            new StubConnectorCatalogStore(),
            new ThrowingConnectorCatalogImportParser());

        await using var stream = new MemoryStream([1, 2, 3]);
        var action = () => service.ImportCatalogAsync("broken-connectors.json", stream);

        await action.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Connector catalog file 'broken-connectors.json' is not valid JSON.");
    }

    [Fact]
    public async Task ConnectorSaveCatalogAsync_WhenCatalogReadFails_ShouldPersistUploadedCatalog()
    {
        var store = new StubConnectorCatalogStore
        {
            ThrowOnGet = true,
        };
        var service = new ConnectorService(store, new StubConnectorCatalogImportParser([]));

        var response = await service.SaveCatalogAsync(new SaveConnectorCatalogRequest(
        [
            new ConnectorDefinitionDto(
                Name: "scope_web",
                Type: "http",
                Enabled: true,
                TimeoutMs: 30_000,
                Retry: 1,
                Http: new HttpConnectorDefinitionDto(
                    BaseUrl: "https://example.com/api",
                    AllowedMethods: ["POST"],
                    AllowedPaths: ["/"],
                    AllowedInputKeys: ["input"],
                    DefaultHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                Cli: new CliConnectorDefinitionDto(
                    Command: string.Empty,
                    FixedArguments: [],
                    AllowedOperations: [],
                    AllowedInputKeys: [],
                    WorkingDirectory: string.Empty,
                    Environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                Mcp: new McpConnectorDefinitionDto(
                    ServerName: string.Empty,
                    Command: string.Empty,
                    Arguments: [],
                    Environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    DefaultTool: string.Empty,
                    AllowedTools: [],
                    AllowedInputKeys: [])),
        ]));

        response.Connectors.Should().ContainSingle().Which.Name.Should().Be("scope_web");
        store.Catalog.Connectors.Should().ContainSingle().Which.Name.Should().Be("scope_web");
    }

    [Fact]
    public async Task RoleImportCatalogAsync_ShouldPersistUploadedCatalog()
    {
        var store = new StubRoleCatalogStore();
        var parser = new StubRoleCatalogImportParser(
        [
            new StoredRoleDefinition(
                Id: "assistant",
                Name: "Assistant",
                SystemPrompt: "You are helpful.",
                Provider: "openai-main",
                Model: "gpt-test",
                Connectors: ["scope_web"]),
        ]);
        var service = new RoleCatalogService(store, parser);

        await using var stream = new MemoryStream([1, 2, 3]);
        var response = await service.ImportCatalogAsync("roles.json", stream);

        response.SourceFilePath.Should().Be("roles.json");
        response.ImportedCount.Should().Be(1);
        response.Roles.Should().ContainSingle().Which.Id.Should().Be("assistant");
        store.Catalog.Roles.Should().ContainSingle().Which.Id.Should().Be("assistant");
    }

    [Fact]
    public async Task RoleImportCatalogAsync_WhenParserReturnsInvalidJson_ShouldRaiseFriendlyError()
    {
        var service = new RoleCatalogService(
            new StubRoleCatalogStore(),
            new ThrowingRoleCatalogImportParser());

        await using var stream = new MemoryStream([1, 2, 3]);
        var action = () => service.ImportCatalogAsync("broken-roles.json", stream);

        await action.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Role catalog file 'broken-roles.json' is not valid JSON.");
    }

    [Fact]
    public async Task RoleSaveCatalogAsync_WhenCatalogReadFails_ShouldPersistUploadedCatalog()
    {
        var store = new StubRoleCatalogStore
        {
            ThrowOnGet = true,
        };
        var service = new RoleCatalogService(store, new StubRoleCatalogImportParser([]));

        var response = await service.SaveCatalogAsync(new SaveRoleCatalogRequest(
        [
            new RoleDefinitionDto(
                Id: "assistant",
                Name: "Assistant",
                SystemPrompt: "You are helpful.",
                Provider: "openai-main",
                Model: "gpt-test",
                Connectors: ["scope_web"]),
        ]));

        response.Roles.Should().ContainSingle().Which.Id.Should().Be("assistant");
        store.Catalog.Roles.Should().ContainSingle().Which.Id.Should().Be("assistant");
    }

    private sealed class StubConnectorCatalogStore : IConnectorCatalogStore
    {
        public bool ThrowOnGet { get; set; }

        public StoredConnectorCatalog Catalog { get; private set; } = new(
            HomeDirectory: "chrono-storage://studio-catalogs",
            FilePath: "chrono-storage://studio-catalogs/aevatar/connectors/v1/test/catalog.json.enc",
            FileExists: false,
            Connectors: []);

        public Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default) =>
            ThrowOnGet
                ? Task.FromException<StoredConnectorCatalog>(new InvalidOperationException("simulated remote read failure"))
                : Task.FromResult(Catalog);

        public Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(
            StoredConnectorCatalog catalog,
            CancellationToken cancellationToken = default)
        {
            Catalog = catalog with { FileExists = true };
            return Task.FromResult(Catalog);
        }

        public Task<ImportedConnectorCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> SaveConnectorDraftAsync(
            StoredConnectorDraft draft,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubRoleCatalogStore : IRoleCatalogStore
    {
        public bool ThrowOnGet { get; set; }

        public StoredRoleCatalog Catalog { get; private set; } = new(
            HomeDirectory: "chrono-storage://studio-catalogs",
            FilePath: "chrono-storage://studio-catalogs/aevatar/roles/v1/test/catalog.json.enc",
            FileExists: false,
            Roles: []);

        public Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default) =>
            ThrowOnGet
                ? Task.FromException<StoredRoleCatalog>(new InvalidOperationException("simulated remote read failure"))
                : Task.FromResult(Catalog);

        public Task<StoredRoleCatalog> SaveRoleCatalogAsync(
            StoredRoleCatalog catalog,
            CancellationToken cancellationToken = default)
        {
            Catalog = catalog with { FileExists = true };
            return Task.FromResult(Catalog);
        }

        public Task<ImportedRoleCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleDraft> SaveRoleDraftAsync(
            StoredRoleDraft draft,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteRoleDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubConnectorCatalogImportParser : IConnectorCatalogImportParser
    {
        private readonly IReadOnlyList<StoredConnectorDefinition> _connectors;

        public StubConnectorCatalogImportParser(IReadOnlyList<StoredConnectorDefinition> connectors)
        {
            _connectors = connectors;
        }

        public Task<IReadOnlyList<StoredConnectorDefinition>> ParseCatalogAsync(
            Stream stream,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_connectors);
    }

    private sealed class ThrowingConnectorCatalogImportParser : IConnectorCatalogImportParser
    {
        public Task<IReadOnlyList<StoredConnectorDefinition>> ParseCatalogAsync(
            Stream stream,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<StoredConnectorDefinition>>(
                new JsonException("invalid json"));
    }

    private sealed class StubRoleCatalogImportParser : IRoleCatalogImportParser
    {
        private readonly IReadOnlyList<StoredRoleDefinition> _roles;

        public StubRoleCatalogImportParser(IReadOnlyList<StoredRoleDefinition> roles)
        {
            _roles = roles;
        }

        public Task<IReadOnlyList<StoredRoleDefinition>> ParseCatalogAsync(
            Stream stream,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_roles);
    }

    private sealed class ThrowingRoleCatalogImportParser : IRoleCatalogImportParser
    {
        public Task<IReadOnlyList<StoredRoleDefinition>> ParseCatalogAsync(
            Stream stream,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<StoredRoleDefinition>>(
                new JsonException("invalid json"));
    }
}
