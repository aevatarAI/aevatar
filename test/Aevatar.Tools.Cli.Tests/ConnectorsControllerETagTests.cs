using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Hosting.Controllers;
using Aevatar.Foundation.Abstractions.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Tools.Cli.Tests;

/// <summary>
/// Mirror of <see cref="RolesControllerETagTests"/> for the connector surface.
/// Locks in identical ETag / If-Match semantics: GET emits ETag, PUT/DELETE accept
/// If-Match with strict tristate parse, malformed → 400, valid → deterministic next
/// ETag, optimistic conflict → 409.
/// </summary>
public sealed class ConnectorsControllerETagTests
{
    [Fact]
    public async Task Get_EmitsETagFromStoreVersion()
    {
        var store = new RecordingConnectorCatalogStore { CatalogVersion = 12 };
        var controller = CreateController(store, ifMatch: null);

        var result = await controller.Get(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        controller.Response.Headers["ETag"].ToString().Should().Be("\"12\"");
    }

    [Fact]
    public async Task GetDraft_EmitsETagFromStoreVersion()
    {
        var store = new RecordingConnectorCatalogStore { DraftVersion = 7 };
        var controller = CreateController(store, ifMatch: null);

        var result = await controller.GetDraft(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        controller.Response.Headers["ETag"].ToString().Should().Be("\"7\"");
    }

    [Fact]
    public async Task Save_WithMalformedIfMatch_Returns400_AndDoesNotInvokeStore()
    {
        var store = new RecordingConnectorCatalogStore();
        var controller = CreateController(store, ifMatch: "W/\"3\"");

        var result = await controller.Save(
            new SaveConnectorCatalogRequest(Connectors: [SampleHttpConnector()]),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        store.SavedCatalog.Should().BeNull();
    }

    [Fact]
    public async Task Save_WithValidIfMatch_PassesExpectedVersion_AndEmitsDeterministicETag()
    {
        var store = new RecordingConnectorCatalogStore();
        var controller = CreateController(store, ifMatch: "\"3\"");

        var result = await controller.Save(
            new SaveConnectorCatalogRequest(Connectors: [SampleHttpConnector()]),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        store.SavedCatalogExpectedVersion.Should().Be(3);
        controller.Response.Headers["ETag"].ToString().Should().Be("\"4\"");
    }

    [Fact]
    public async Task Save_WhenIfMatchHeaderDisagreesWithBodyExpectedVersion_Returns400()
    {
        var store = new RecordingConnectorCatalogStore();
        var controller = CreateController(store, ifMatch: "\"3\"");

        var result = await controller.Save(
            new SaveConnectorCatalogRequest(Connectors: [SampleHttpConnector()], ExpectedVersion: 4),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        store.SavedCatalog.Should().BeNull();
    }

    [Fact]
    public async Task Save_WhenStoreThrowsOptimisticConflict_Returns409()
    {
        var store = new RecordingConnectorCatalogStore
        {
            ThrowOnWrite = new EventStoreOptimisticConcurrencyException("connector-catalog-test", 3, 5),
        };
        var controller = CreateController(store, ifMatch: "\"3\"");

        var result = await controller.Save(
            new SaveConnectorCatalogRequest(Connectors: [SampleHttpConnector()]),
            CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task SaveDraft_WithMalformedIfMatch_Returns400_AndDoesNotInvokeStore()
    {
        var store = new RecordingConnectorCatalogStore();
        var controller = CreateController(store, ifMatch: "*");

        var result = await controller.SaveDraft(
            new SaveConnectorDraftRequest(Draft: SampleHttpConnector()),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        store.SavedDraft.Should().BeNull();
        store.SavedDraftExpectedVersion.Should().BeNull();
    }

    [Fact]
    public async Task SaveDraft_WithValidIfMatch_PassesExpectedVersion_AndEmitsDeterministicETag()
    {
        var store = new RecordingConnectorCatalogStore();
        var controller = CreateController(store, ifMatch: "\"5\"");

        var result = await controller.SaveDraft(
            new SaveConnectorDraftRequest(Draft: SampleHttpConnector()),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        store.SavedDraftExpectedVersion.Should().Be(5);
        controller.Response.Headers["ETag"].ToString().Should().Be("\"6\"");
    }

    [Fact]
    public async Task SaveDraft_WithoutIfMatch_DoesNotEmitETag()
    {
        var store = new RecordingConnectorCatalogStore();
        var controller = CreateController(store, ifMatch: null);

        var result = await controller.SaveDraft(
            new SaveConnectorDraftRequest(Draft: SampleHttpConnector()),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        store.SavedDraftExpectedVersion.Should().BeNull();
        controller.Response.Headers.ContainsKey("ETag").Should().BeFalse();
    }

    [Fact]
    public async Task SaveDraft_WhenIfMatchHeaderDisagreesWithBodyExpectedVersion_Returns400()
    {
        var store = new RecordingConnectorCatalogStore();
        var controller = CreateController(store, ifMatch: "\"5\"");

        var result = await controller.SaveDraft(
            new SaveConnectorDraftRequest(Draft: SampleHttpConnector(), ExpectedVersion: 6),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        store.SavedDraft.Should().BeNull();
        store.SavedDraftExpectedVersion.Should().BeNull();
    }

    [Fact]
    public async Task SaveDraft_WhenIfMatchHeaderAgreesWithBodyExpectedVersion_HeaderWinsAndStoreReceivesIt()
    {
        var store = new RecordingConnectorCatalogStore();
        var controller = CreateController(store, ifMatch: "\"5\"");

        var result = await controller.SaveDraft(
            new SaveConnectorDraftRequest(Draft: SampleHttpConnector(), ExpectedVersion: 5),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        store.SavedDraftExpectedVersion.Should().Be(5);
    }

    [Fact]
    public async Task SaveDraft_WhenStoreThrowsOptimisticConflict_Returns409()
    {
        var store = new RecordingConnectorCatalogStore
        {
            ThrowOnWrite = new EventStoreOptimisticConcurrencyException("connector-catalog-test", 5, 7),
        };
        var controller = CreateController(store, ifMatch: "\"5\"");

        var result = await controller.SaveDraft(
            new SaveConnectorDraftRequest(Draft: SampleHttpConnector()),
            CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task DeleteDraft_WithMalformedIfMatch_Returns400_AndDoesNotInvokeStore()
    {
        var store = new RecordingConnectorCatalogStore();
        var controller = CreateController(store, ifMatch: "not-a-number");

        var result = await controller.DeleteDraft(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        store.DraftDeletes.Should().Be(0);
    }

    [Fact]
    public async Task DeleteDraft_WithValidIfMatch_PassesExpectedVersionToStore()
    {
        var store = new RecordingConnectorCatalogStore();
        var controller = CreateController(store, ifMatch: "\"9\"");

        var result = await controller.DeleteDraft(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        store.DraftDeletes.Should().Be(1);
        store.DraftDeleteExpectedVersion.Should().Be(9);
    }

    [Fact]
    public async Task DeleteDraft_WhenStoreThrowsOptimisticConflict_Returns409()
    {
        var store = new RecordingConnectorCatalogStore
        {
            ThrowOnDelete = new EventStoreOptimisticConcurrencyException("connector-catalog-test", 9, 11),
        };
        var controller = CreateController(store, ifMatch: "\"9\"");

        var result = await controller.DeleteDraft(CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    private static ConnectorsController CreateController(IConnectorCatalogStore store, string? ifMatch)
    {
        var service = new ConnectorService(store, new StubConnectorCatalogImportParser());
        var controller = new ConnectorsController(service);
        var httpContext = new DefaultHttpContext();
        if (ifMatch is not null)
            httpContext.Request.Headers["If-Match"] = ifMatch;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static ConnectorDefinitionDto SampleHttpConnector() =>
        new(
            Name: "conn-1",
            Type: "http",
            Enabled: true,
            TimeoutMs: 30_000,
            Retry: 1,
            Http: new HttpConnectorDefinitionDto(
                BaseUrl: "https://example.com/api",
                AllowedMethods: ["GET"],
                AllowedPaths: [],
                AllowedInputKeys: [],
                DefaultHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Auth: EmptyAuth()),
            Cli: EmptyCli(),
            Mcp: EmptyMcp());

    private static ConnectorAuthDefinitionDto EmptyAuth() =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    private static CliConnectorDefinitionDto EmptyCli() =>
        new(
            Command: string.Empty,
            FixedArguments: [],
            AllowedOperations: [],
            AllowedInputKeys: [],
            WorkingDirectory: string.Empty,
            Environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static McpConnectorDefinitionDto EmptyMcp() =>
        new(
            ServerName: string.Empty,
            Command: string.Empty,
            Url: string.Empty,
            Arguments: [],
            Environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            AdditionalHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Auth: EmptyAuth(),
            DefaultTool: string.Empty,
            AllowedTools: [],
            AllowedInputKeys: []);

    private sealed class RecordingConnectorCatalogStore : IConnectorCatalogStore
    {
        public StoredConnectorCatalog? SavedCatalog { get; private set; }
        public long? SavedCatalogExpectedVersion { get; private set; }
        public StoredConnectorDraft? SavedDraft { get; private set; }
        public long? SavedDraftExpectedVersion { get; private set; }
        public int DraftDeletes { get; private set; }
        public long? DraftDeleteExpectedVersion { get; private set; }
        public long CatalogVersion { get; set; }
        public long DraftVersion { get; set; }
        public Exception? ThrowOnWrite { get; set; }
        public Exception? ThrowOnDelete { get; set; }

        public Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredConnectorCatalog(string.Empty, string.Empty, false, [], CatalogVersion));

        public Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(StoredConnectorCatalog catalog, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            if (ThrowOnWrite is not null)
                throw ThrowOnWrite;
            SavedCatalog = catalog;
            SavedCatalogExpectedVersion = expectedVersion;
            return Task.FromResult(catalog with
            {
                Version = expectedVersion is null ? 0 : expectedVersion.Value + 1,
            });
        }

        public Task<ImportedConnectorCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredConnectorDraft(string.Empty, string.Empty, false, null, null, DraftVersion));

        public Task<StoredConnectorDraft> SaveConnectorDraftAsync(StoredConnectorDraft draft, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            if (ThrowOnWrite is not null)
                throw ThrowOnWrite;
            SavedDraft = draft;
            SavedDraftExpectedVersion = expectedVersion;
            return Task.FromResult(draft with
            {
                Version = expectedVersion is null ? 0 : expectedVersion.Value + 1,
            });
        }

        public Task DeleteConnectorDraftAsync(long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            if (ThrowOnDelete is not null)
                throw ThrowOnDelete;
            DraftDeletes++;
            DraftDeleteExpectedVersion = expectedVersion;
            return Task.CompletedTask;
        }
    }

    private sealed class StubConnectorCatalogImportParser : IConnectorCatalogImportParser
    {
        public Task<IReadOnlyList<StoredConnectorDefinition>> ParseCatalogAsync(Stream stream, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredConnectorDefinition>>([]);
    }
}
