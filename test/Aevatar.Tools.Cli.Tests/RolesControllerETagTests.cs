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
/// HTTP-boundary contract for the new ETag / If-Match flow. Covers the two correctness
/// concerns surfaced in PR #434 review:
///   1) Malformed If-Match must reject (400), not silently fall back to last-writer-wins.
///   2) Successful guarded write returns a deterministic next ETag (expected_version + 1).
/// </summary>
public sealed class RolesControllerETagTests
{
    [Fact]
    public async Task SaveDraft_WithMalformedIfMatch_Returns400_AndDoesNotInvokeStore()
    {
        var store = new RecordingRoleCatalogStore();
        var controller = CreateController(store, ifMatch: "W/\"5\"");

        var result = await controller.SaveDraft(
            new SaveRoleDraftRequest(Draft: SampleRole()),
            CancellationToken.None);

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().NotBeNull();
        store.SavedDraft.Should().BeNull();
        store.SavedExpectedVersion.Should().BeNull();
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
    public async Task SaveDraft_WithValidIfMatch_PassesExpectedVersionToStore_AndEmitsDeterministicETag()
    {
        var store = new RecordingRoleCatalogStore();
        var controller = CreateController(store, ifMatch: "\"5\"");

        var result = await controller.SaveDraft(
            new SaveRoleDraftRequest(Draft: SampleRole()),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        store.SavedExpectedVersion.Should().Be(5);
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
        store.SavedExpectedVersion.Should().BeNull();
        controller.Response.Headers.ContainsKey("ETag").Should().BeFalse();
    }

    [Fact]
    public async Task SaveDraft_WhenStoreThrowsOptimisticConflict_Returns409()
    {
        var store = new RecordingRoleCatalogStore
        {
            SaveDraftException = new EventStoreOptimisticConcurrencyException("role-catalog-test", 5, 7),
        };
        var controller = CreateController(store, ifMatch: "\"5\"");

        var result = await controller.SaveDraft(
            new SaveRoleDraftRequest(Draft: SampleRole()),
            CancellationToken.None);

        var conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().NotBeNull();
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
        public StoredRoleDraft? SavedDraft { get; private set; }
        public long? SavedExpectedVersion { get; private set; }
        public int DraftDeletes { get; private set; }
        public Exception? SaveDraftException { get; set; }

        public Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredRoleCatalog(string.Empty, string.Empty, false, [], 0));

        public Task<StoredRoleCatalog> SaveRoleCatalogAsync(StoredRoleCatalog catalog, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            if (SaveDraftException is not null)
                throw SaveDraftException;
            return Task.FromResult(catalog with
            {
                Version = expectedVersion is null ? 0 : expectedVersion.Value + 1,
            });
        }

        public Task<ImportedRoleCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredRoleDraft(string.Empty, string.Empty, false, null, null, 0));

        public Task<StoredRoleDraft> SaveRoleDraftAsync(StoredRoleDraft draft, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            if (SaveDraftException is not null)
                throw SaveDraftException;
            SavedDraft = draft;
            SavedExpectedVersion = expectedVersion;
            return Task.FromResult(draft with
            {
                Version = expectedVersion is null ? 0 : expectedVersion.Value + 1,
            });
        }

        public Task DeleteRoleDraftAsync(long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            DraftDeletes++;
            return Task.CompletedTask;
        }
    }

    private sealed class StubRoleCatalogImportParser : IRoleCatalogImportParser
    {
        public Task<IReadOnlyList<StoredRoleDefinition>> ParseCatalogAsync(Stream stream, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredRoleDefinition>>([]);
    }
}
