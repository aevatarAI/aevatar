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
/// HTTP-boundary contract for the new ETag / If-Match flow.
/// Covers PR #434 review concerns:
///   1) Malformed If-Match must reject (400), not silently fall back to last-writer-wins.
///   2) Successful guarded write returns a deterministic next ETag (expected_version + 1).
/// And the GET surface that emits the ETag clients use as If-Match.
/// </summary>
public sealed class RolesControllerETagTests
{
    [Fact]
    public async Task Get_EmitsETagFromStoreVersion()
    {
        var store = new RecordingRoleCatalogStore { CatalogVersion = 12 };
        var controller = CreateController(store, ifMatch: null);

        var result = await controller.Get(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        controller.Response.Headers["ETag"].ToString().Should().Be("\"12\"");
    }

    [Fact]
    public async Task GetDraft_EmitsETagFromStoreVersion()
    {
        var store = new RecordingRoleCatalogStore { DraftVersion = 7 };
        var controller = CreateController(store, ifMatch: null);

        var result = await controller.GetDraft(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        controller.Response.Headers["ETag"].ToString().Should().Be("\"7\"");
    }

    [Fact]
    public async Task Save_WithMalformedIfMatch_Returns400_AndDoesNotInvokeStore()
    {
        var store = new RecordingRoleCatalogStore();
        var controller = CreateController(store, ifMatch: "W/\"3\"");

        var result = await controller.Save(
            new SaveRoleCatalogRequest(Roles: [SampleRole()]),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        store.SavedCatalog.Should().BeNull();
    }

    [Fact]
    public async Task Save_WithValidIfMatch_PassesExpectedVersion_AndEmitsDeterministicETag()
    {
        var store = new RecordingRoleCatalogStore();
        var controller = CreateController(store, ifMatch: "\"3\"");

        var result = await controller.Save(
            new SaveRoleCatalogRequest(Roles: [SampleRole()]),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        store.SavedCatalogExpectedVersion.Should().Be(3);
        controller.Response.Headers["ETag"].ToString().Should().Be("\"4\"");
    }

    [Fact]
    public async Task Save_WhenIfMatchHeaderDisagreesWithBodyExpectedVersion_Returns400()
    {
        var store = new RecordingRoleCatalogStore();
        var controller = CreateController(store, ifMatch: "\"3\"");

        var result = await controller.Save(
            new SaveRoleCatalogRequest(Roles: [SampleRole()], ExpectedVersion: 4),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        store.SavedCatalog.Should().BeNull();
    }

    [Fact]
    public async Task Save_WhenStoreThrowsOptimisticConflict_Returns409()
    {
        var store = new RecordingRoleCatalogStore
        {
            ThrowOnWrite = new EventStoreOptimisticConcurrencyException("role-catalog-test", 3, 5),
        };
        var controller = CreateController(store, ifMatch: "\"3\"");

        var result = await controller.Save(
            new SaveRoleCatalogRequest(Roles: [SampleRole()]),
            CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task SaveDraft_WithMalformedIfMatch_Returns400_AndDoesNotInvokeStore()
    {
        var store = new RecordingRoleCatalogStore();
        var controller = CreateController(store, ifMatch: "W/\"5\"");

        var result = await controller.SaveDraft(
            new SaveRoleDraftRequest(Draft: SampleRole()),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        store.SavedDraft.Should().BeNull();
        store.SavedDraftExpectedVersion.Should().BeNull();
    }

    [Fact]
    public async Task SaveDraft_WithValidIfMatch_PassesExpectedVersion_AndEmitsDeterministicETag()
    {
        var store = new RecordingRoleCatalogStore();
        var controller = CreateController(store, ifMatch: "\"5\"");

        var result = await controller.SaveDraft(
            new SaveRoleDraftRequest(Draft: SampleRole()),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        store.SavedDraftExpectedVersion.Should().Be(5);
        controller.Response.Headers["ETag"].ToString().Should().Be("\"6\"");
    }

    [Fact]
    public async Task SaveDraft_WithoutIfMatch_DoesNotEmitETag()
    {
        var store = new RecordingRoleCatalogStore();
        var controller = CreateController(store, ifMatch: null);

        var result = await controller.SaveDraft(
            new SaveRoleDraftRequest(Draft: SampleRole()),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        store.SavedDraftExpectedVersion.Should().BeNull();
        controller.Response.Headers.ContainsKey("ETag").Should().BeFalse();
    }

    [Fact]
    public async Task SaveDraft_WhenIfMatchHeaderDisagreesWithBodyExpectedVersion_Returns400()
    {
        var store = new RecordingRoleCatalogStore();
        var controller = CreateController(store, ifMatch: "\"5\"");

        var result = await controller.SaveDraft(
            new SaveRoleDraftRequest(Draft: SampleRole(), ExpectedVersion: 6),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        store.SavedDraft.Should().BeNull();
        store.SavedDraftExpectedVersion.Should().BeNull();
    }

    [Fact]
    public async Task SaveDraft_WhenIfMatchHeaderAgreesWithBodyExpectedVersion_HeaderWinsAndStoreReceivesIt()
    {
        var store = new RecordingRoleCatalogStore();
        var controller = CreateController(store, ifMatch: "\"5\"");

        var result = await controller.SaveDraft(
            new SaveRoleDraftRequest(Draft: SampleRole(), ExpectedVersion: 5),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        store.SavedDraftExpectedVersion.Should().Be(5);
    }

    [Fact]
    public async Task SaveDraft_WhenStoreThrowsOptimisticConflict_Returns409()
    {
        var store = new RecordingRoleCatalogStore
        {
            ThrowOnWrite = new EventStoreOptimisticConcurrencyException("role-catalog-test", 5, 7),
        };
        var controller = CreateController(store, ifMatch: "\"5\"");

        var result = await controller.SaveDraft(
            new SaveRoleDraftRequest(Draft: SampleRole()),
            CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task DeleteDraft_WithMalformedIfMatch_Returns400_AndDoesNotInvokeStore()
    {
        var store = new RecordingRoleCatalogStore();
        var controller = CreateController(store, ifMatch: "*");

        var result = await controller.DeleteDraft(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        store.DraftDeletes.Should().Be(0);
    }

    [Fact]
    public async Task DeleteDraft_WithValidIfMatch_PassesExpectedVersionToStore()
    {
        var store = new RecordingRoleCatalogStore();
        var controller = CreateController(store, ifMatch: "\"9\"");

        var result = await controller.DeleteDraft(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        store.DraftDeletes.Should().Be(1);
        store.DraftDeleteExpectedVersion.Should().Be(9);
    }

    [Fact]
    public async Task DeleteDraft_WhenStoreThrowsOptimisticConflict_Returns409()
    {
        var store = new RecordingRoleCatalogStore
        {
            ThrowOnDelete = new EventStoreOptimisticConcurrencyException("role-catalog-test", 9, 11),
        };
        var controller = CreateController(store, ifMatch: "\"9\"");

        var result = await controller.DeleteDraft(CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    private static RolesController CreateController(IRoleCatalogStore store, string? ifMatch)
    {
        var service = new RoleCatalogService(store, new StubRoleCatalogImportParser());
        var controller = new RolesController(service);
        var httpContext = new DefaultHttpContext();
        if (ifMatch is not null)
            httpContext.Request.Headers["If-Match"] = ifMatch;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static RoleDefinitionDto SampleRole() =>
        new(Id: "r1", Name: "Test", SystemPrompt: "p", Provider: "anthropic", Model: "claude", Connectors: []);

    private sealed class RecordingRoleCatalogStore : IRoleCatalogStore
    {
        public StoredRoleCatalog? SavedCatalog { get; private set; }
        public long? SavedCatalogExpectedVersion { get; private set; }
        public StoredRoleDraft? SavedDraft { get; private set; }
        public long? SavedDraftExpectedVersion { get; private set; }
        public int DraftDeletes { get; private set; }
        public long? DraftDeleteExpectedVersion { get; private set; }
        public long CatalogVersion { get; set; }
        public long DraftVersion { get; set; }
        public Exception? ThrowOnWrite { get; set; }
        public Exception? ThrowOnDelete { get; set; }

        public Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredRoleCatalog(string.Empty, string.Empty, false, [], CatalogVersion));

        public Task<StoredRoleCatalog> SaveRoleCatalogAsync(StoredRoleCatalog catalog, long? expectedVersion = null, CancellationToken cancellationToken = default)
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

        public Task<ImportedRoleCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredRoleDraft(string.Empty, string.Empty, false, null, null, DraftVersion));

        public Task<StoredRoleDraft> SaveRoleDraftAsync(StoredRoleDraft draft, long? expectedVersion = null, CancellationToken cancellationToken = default)
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

        public Task DeleteRoleDraftAsync(long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            if (ThrowOnDelete is not null)
                throw ThrowOnDelete;
            DraftDeletes++;
            DraftDeleteExpectedVersion = expectedVersion;
            return Task.CompletedTask;
        }
    }

    private sealed class StubRoleCatalogImportParser : IRoleCatalogImportParser
    {
        public Task<IReadOnlyList<StoredRoleDefinition>> ParseCatalogAsync(Stream stream, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredRoleDefinition>>([]);
    }
}
