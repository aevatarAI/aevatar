using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Scripts;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeScriptQueryApplicationServiceTests
{
    private const string ScopeId = "test-scope";

    [Fact]
    public async Task ListAsync_ShouldReturnScriptsFromCatalogQueryPort()
    {
        var olderEntry = CreateEntry("script-a", "rev-1", updatedAtMs: 1000);
        var newerEntry = CreateEntry("script-b", "rev-2", updatedAtMs: 2000);
        var catalogPort = new FakeScriptCatalogQueryPort(new[] { olderEntry, newerEntry });
        var service = CreateService(catalogPort);

        var result = await service.ListAsync(ScopeId);

        result.Should().HaveCount(2);
        result[0].ScriptId.Should().Be("script-b", "newer entry should come first");
        result[1].ScriptId.Should().Be("script-a");
    }

    [Fact]
    public async Task ListAsync_ShouldFilterOutScriptsWithoutActiveRevision()
    {
        var active1 = CreateEntry("script-a", "rev-1", updatedAtMs: 3000);
        var noRevision = CreateEntry("script-b", "", updatedAtMs: 2000);
        var active2 = CreateEntry("script-c", "rev-3", updatedAtMs: 1000);
        var catalogPort = new FakeScriptCatalogQueryPort(new[] { active1, noRevision, active2 });
        var service = CreateService(catalogPort);

        var result = await service.ListAsync(ScopeId);

        result.Should().HaveCount(2);
        result.Select(s => s.ScriptId).Should().BeEquivalentTo(new[] { "script-a", "script-c" });
    }

    [Fact]
    public async Task ListAsync_ShouldReturnEmptyList_WhenNoEntriesExist()
    {
        var catalogPort = new FakeScriptCatalogQueryPort(Array.Empty<ScriptCatalogEntrySnapshot>());
        var service = CreateService(catalogPort);

        var result = await service.ListAsync(ScopeId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByScriptIdAsync_ShouldReturnNull_WhenNotFound()
    {
        var catalogPort = new FakeScriptCatalogQueryPort(getEntryResult: null);
        var service = CreateService(catalogPort);

        var result = await service.GetByScriptIdAsync(ScopeId, "nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByScriptIdAsync_ShouldReturnNull_WhenActiveRevisionIsEmpty()
    {
        var entry = CreateEntry("script-a", "", updatedAtMs: 1000);
        var catalogPort = new FakeScriptCatalogQueryPort(getEntryResult: entry);
        var service = CreateService(catalogPort);

        var result = await service.GetByScriptIdAsync(ScopeId, "script-a");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByScriptIdAsync_ShouldReturnSummary_WhenFound()
    {
        var entry = CreateEntry(
            scriptId: "my-script",
            activeRevision: "rev-42",
            catalogActorId: "catalog-actor-1",
            definitionActorId: "def-actor-1",
            sourceHash: "abc123",
            updatedAtMs: 1710000000000);
        var catalogPort = new FakeScriptCatalogQueryPort(getEntryResult: entry);
        var service = CreateService(catalogPort);

        var result = await service.GetByScriptIdAsync(ScopeId, "my-script");

        result.Should().NotBeNull();
        result!.ScopeId.Should().Be(ScopeId);
        result.ScriptId.Should().Be("my-script");
        result.CatalogActorId.Should().Be("catalog-actor-1");
        result.DefinitionActorId.Should().Be("def-actor-1");
        result.ActiveRevision.Should().Be("rev-42");
        result.ActiveSourceHash.Should().Be("abc123");
        result.UpdatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1710000000000));
    }

    private static ScopeScriptQueryApplicationService CreateService(
        FakeScriptCatalogQueryPort catalogPort) =>
        new(
            catalogPort,
            Options.Create(new ScopeScriptCapabilityOptions()));

    private static ScriptCatalogEntrySnapshot CreateEntry(
        string scriptId,
        string activeRevision,
        long updatedAtMs,
        string catalogActorId = "catalog-actor",
        string definitionActorId = "def-actor",
        string sourceHash = "hash") =>
        new(
            ScriptId: scriptId,
            ActiveRevision: activeRevision,
            ActiveDefinitionActorId: definitionActorId,
            ActiveSourceHash: sourceHash,
            PreviousRevision: "",
            RevisionHistory: null,
            LastProposalId: "",
            CatalogActorId: catalogActorId,
            UpdatedAtUnixTimeMs: updatedAtMs);

    private sealed class FakeScriptCatalogQueryPort : IScriptCatalogQueryPort
    {
        private readonly IReadOnlyList<ScriptCatalogEntrySnapshot> _listResult;
        private readonly ScriptCatalogEntrySnapshot? _getEntryResult;

        public FakeScriptCatalogQueryPort(
            IReadOnlyList<ScriptCatalogEntrySnapshot>? listResult = null,
            ScriptCatalogEntrySnapshot? getEntryResult = null)
        {
            _listResult = listResult ?? Array.Empty<ScriptCatalogEntrySnapshot>();
            _getEntryResult = getEntryResult;
        }

        public Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
            string? catalogActorId,
            string scriptId,
            CancellationToken ct) =>
            Task.FromResult(_getEntryResult);

        public Task<IReadOnlyList<ScriptCatalogEntrySnapshot>> ListCatalogEntriesAsync(
            string? catalogActorId,
            int take,
            CancellationToken ct) =>
            Task.FromResult(_listResult);
    }
}
