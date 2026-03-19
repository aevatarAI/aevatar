using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Scripts;
using Aevatar.Scripting.Core.Ports;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeScriptApplicationServicesTests
{
    [Fact]
    public async Task UpsertAsync_ShouldCreateScopedDefinitionRevisionAndPromoteCatalog()
    {
        var options = new ScopeScriptCapabilityOptions
        {
            CatalogActorIdPrefix = "user-script-catalog",
            DefinitionActorIdPrefix = "user-script-definition",
        };
        const string scopeId = "external-user-1";
        const string scriptId = "text-normalizer";
        const string revisionId = "rev-001";
        const string expectedBaseRevision = "rev-000";
        const string sourceText = "public sealed class DemoScript {}";
        var expectedCatalogActorId = options.BuildCatalogActorId(scopeId);
        var expectedDefinitionActorId = options.BuildDefinitionActorId(scopeId, scriptId, revisionId);
        var expectedSourceHash = ComputeSha256(sourceText);
        var updatedAt = DateTimeOffset.UtcNow;

        var definitionCommandPort = new FakeScriptDefinitionCommandPort
        {
            NextResult = new ScriptDefinitionUpsertResult(
                expectedDefinitionActorId,
                CreateDefinitionSnapshot(scriptId, revisionId, sourceText, expectedSourceHash, expectedDefinitionActorId, scopeId)),
        };
        var catalogCommandPort = new FakeScriptCatalogCommandPort();
        var scriptQueryPort = new FakeScopeScriptQueryPort
        {
            NextScript = new ScopeScriptSummary(
                scopeId,
                scriptId,
                expectedCatalogActorId,
                expectedDefinitionActorId,
                revisionId,
                expectedSourceHash,
                updatedAt),
        };
        var service = new ScopeScriptCommandApplicationService(
            definitionCommandPort,
            catalogCommandPort,
            scriptQueryPort,
            Options.Create(options));

        var result = await service.UpsertAsync(
            new ScopeScriptUpsertRequest(
                scopeId,
                scriptId,
                sourceText,
                revisionId,
                expectedBaseRevision));

        result.Script.ScopeId.Should().Be(scopeId);
        result.Script.ScriptId.Should().Be(scriptId);
        result.Script.CatalogActorId.Should().Be(expectedCatalogActorId);
        result.Script.DefinitionActorId.Should().Be(expectedDefinitionActorId);
        result.RevisionId.Should().Be(revisionId);
        result.CatalogActorId.Should().Be(expectedCatalogActorId);
        result.DefinitionActorId.Should().Be(expectedDefinitionActorId);

        definitionCommandPort.LastRequest.Should().BeEquivalentTo(
            new FakeScriptDefinitionCommandPort.Request(
                scriptId,
                revisionId,
                sourceText,
                expectedSourceHash,
                expectedDefinitionActorId,
                scopeId));
        catalogCommandPort.LastPromoteRequest.Should().BeEquivalentTo(
            new FakeScriptCatalogCommandPort.PromoteRequest(
                expectedCatalogActorId,
                scriptId,
                expectedBaseRevision,
                revisionId,
                expectedDefinitionActorId,
                expectedSourceHash,
                $"{scopeId}:{scriptId}:{revisionId}",
                scopeId));
        scriptQueryPort.LastGetRequest.Should().BeEquivalentTo(
            new FakeScopeScriptQueryPort.GetRequest(scopeId, scriptId));
    }

    [Fact]
    public async Task ListAsync_ShouldQueryScopedCatalogAndSortByUpdatedAtDesc()
    {
        var options = new ScopeScriptCapabilityOptions
        {
            CatalogActorIdPrefix = "user-script-catalog",
            DefinitionActorIdPrefix = "user-script-definition",
            ListTake = 50,
        };
        const string scopeId = "external-user-2";
        var catalogActorId = options.BuildCatalogActorId(scopeId);
        var queryPort = new FakeScriptCatalogQueryPort
        {
            ListResult =
            [
                new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-a",
                    ActiveRevision: "rev-1",
                    ActiveDefinitionActorId: "definition-a",
                    ActiveSourceHash: "hash-a",
                    PreviousRevision: string.Empty,
                    RevisionHistory: ["rev-1"],
                    LastProposalId: "proposal-a",
                    CatalogActorId: catalogActorId,
                    ScopeId: scopeId,
                    UpdatedAtUnixTimeMs: DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()),
                new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-b",
                    ActiveRevision: "rev-2",
                    ActiveDefinitionActorId: "definition-b",
                    ActiveSourceHash: "hash-b",
                    PreviousRevision: "rev-1",
                    RevisionHistory: ["rev-1", "rev-2"],
                    LastProposalId: "proposal-b",
                    CatalogActorId: catalogActorId,
                    ScopeId: scopeId,
                    UpdatedAtUnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-empty",
                    ActiveRevision: string.Empty,
                    ActiveDefinitionActorId: "definition-empty",
                    ActiveSourceHash: "hash-empty",
                    PreviousRevision: string.Empty,
                    RevisionHistory: [],
                    LastProposalId: "proposal-empty",
                    CatalogActorId: catalogActorId,
                    ScopeId: scopeId,
                    UpdatedAtUnixTimeMs: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds()),
            ],
        };
        var service = new ScopeScriptQueryApplicationService(queryPort, Options.Create(options));

        var scripts = await service.ListAsync(scopeId);

        scripts.Select(static item => item.ScriptId).Should().Equal("script-b", "script-a");
        scripts.Should().OnlyContain(static item => item.ScopeId == scopeId);
        queryPort.LastListRequest.Should().BeEquivalentTo(
            new FakeScriptCatalogQueryPort.ListRequest(catalogActorId, options.ListTake));
    }

    [Fact]
    public async Task GetByScriptIdAsync_ShouldReturnNull_WhenCatalogEntryHasNoActiveRevision()
    {
        var options = new ScopeScriptCapabilityOptions
        {
            CatalogActorIdPrefix = "user-script-catalog",
        };
        const string scopeId = "external-user-3";
        const string scriptId = "script-empty";
        var queryPort = new FakeScriptCatalogQueryPort
        {
            GetResult = new ScriptCatalogEntrySnapshot(
                ScriptId: scriptId,
                ActiveRevision: string.Empty,
                ActiveDefinitionActorId: "definition-empty",
                ActiveSourceHash: "hash-empty",
                PreviousRevision: string.Empty,
                RevisionHistory: [],
                LastProposalId: "proposal-empty",
                CatalogActorId: options.BuildCatalogActorId(scopeId),
                ScopeId: scopeId,
                UpdatedAtUnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        };
        var service = new ScopeScriptQueryApplicationService(queryPort, Options.Create(options));

        var script = await service.GetByScriptIdAsync(scopeId, scriptId);

        script.Should().BeNull();
        queryPort.LastGetRequest.Should().BeEquivalentTo(
            new FakeScriptCatalogQueryPort.GetRequest(options.BuildCatalogActorId(scopeId), scriptId));
    }

    private static ScriptDefinitionSnapshot CreateDefinitionSnapshot(
        string scriptId,
        string revision,
        string sourceText,
        string sourceHash,
        string definitionActorId,
        string scopeId) =>
        new(
            scriptId,
            revision,
            sourceText,
            sourceHash,
            StateTypeUrl: string.Empty,
            ReadModelTypeUrl: string.Empty,
            ReadModelSchemaVersion: string.Empty,
            ReadModelSchemaHash: string.Empty,
            DefinitionActorId: definitionActorId,
            ScopeId: scopeId);

    private static string ComputeSha256(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class FakeScriptDefinitionCommandPort : IScriptDefinitionCommandPort
    {
        public Request? LastRequest { get; private set; }

        public ScriptDefinitionUpsertResult NextResult { get; set; } = new(
            "definition-actor",
            CreateDefinitionSnapshot("script", "rev-1", "source", "hash", "definition-actor", "scope"));

        public Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
            string scriptId,
            string scriptRevision,
            string sourceText,
            string sourceHash,
            string? definitionActorId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = new Request(
                scriptId,
                scriptRevision,
                sourceText,
                sourceHash,
                definitionActorId,
                null);
            return Task.FromResult(NextResult);
        }

        public Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
            string scriptId,
            string scriptRevision,
            string sourceText,
            string sourceHash,
            string? definitionActorId,
            string? scopeId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = new Request(
                scriptId,
                scriptRevision,
                sourceText,
                sourceHash,
                definitionActorId,
                scopeId);
            return Task.FromResult(NextResult);
        }

        public sealed record Request(
            string ScriptId,
            string ScriptRevision,
            string SourceText,
            string SourceHash,
            string? DefinitionActorId,
            string? ScopeId);
    }

    private sealed class FakeScriptCatalogCommandPort : IScriptCatalogCommandPort
    {
        public PromoteRequest? LastPromoteRequest { get; private set; }

        public Task PromoteCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string expectedBaseRevision,
            string revision,
            string definitionActorId,
            string sourceHash,
            string proposalId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastPromoteRequest = new PromoteRequest(
                catalogActorId,
                scriptId,
                expectedBaseRevision,
                revision,
                definitionActorId,
                sourceHash,
                proposalId,
                null);
            return Task.CompletedTask;
        }

        public Task PromoteCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string expectedBaseRevision,
            string revision,
            string definitionActorId,
            string sourceHash,
            string proposalId,
            string? scopeId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastPromoteRequest = new PromoteRequest(
                catalogActorId,
                scriptId,
                expectedBaseRevision,
                revision,
                definitionActorId,
                sourceHash,
                proposalId,
                scopeId);
            return Task.CompletedTask;
        }

        public Task RollbackCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            string expectedCurrentRevision,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public sealed record PromoteRequest(
            string? CatalogActorId,
            string ScriptId,
            string ExpectedBaseRevision,
            string Revision,
            string DefinitionActorId,
            string SourceHash,
            string ProposalId,
            string? ScopeId);
    }

    private sealed class FakeScopeScriptQueryPort : IScopeScriptQueryPort
    {
        public ScopeScriptSummary? NextScript { get; set; }

        public GetRequest? LastGetRequest { get; private set; }

        public Task<IReadOnlyList<ScopeScriptSummary>> ListAsync(string scopeId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScopeScriptSummary>>(
                NextScript == null ? [] : [NextScript]);
        }

        public Task<ScopeScriptSummary?> GetByScriptIdAsync(string scopeId, string scriptId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastGetRequest = new GetRequest(scopeId, scriptId);
            return Task.FromResult(NextScript);
        }

        public sealed record GetRequest(string ScopeId, string ScriptId);
    }

    private sealed class FakeScriptCatalogQueryPort : IScriptCatalogQueryPort
    {
        public ScriptCatalogEntrySnapshot? GetResult { get; set; }

        public IReadOnlyList<ScriptCatalogEntrySnapshot> ListResult { get; set; } = [];

        public GetRequest? LastGetRequest { get; private set; }

        public ListRequest? LastListRequest { get; private set; }

        public Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
            string? catalogActorId,
            string scriptId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastGetRequest = new GetRequest(catalogActorId, scriptId);
            return Task.FromResult(GetResult);
        }

        public Task<IReadOnlyList<ScriptCatalogEntrySnapshot>> ListCatalogEntriesAsync(
            string? catalogActorId,
            int take,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastListRequest = new ListRequest(catalogActorId, take);
            return Task.FromResult(ListResult);
        }

        public sealed record GetRequest(string? CatalogActorId, string ScriptId);

        public sealed record ListRequest(string? CatalogActorId, int Take);
    }
}
