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

        var definitionCommandPort = new FakeScriptDefinitionCommandPort
        {
            NextResult = new ScriptDefinitionUpsertResult(
                expectedDefinitionActorId,
                CreateDefinitionSnapshot(scriptId, revisionId, sourceText, expectedSourceHash, expectedDefinitionActorId, scopeId),
                new ScriptingCommandAcceptedReceipt(expectedDefinitionActorId, "definition-command-1", "definition-correlation-1")),
        };
        var catalogCommandPort = new FakeScriptCatalogCommandPort();
        var authorityReadModelActivationPort = new RecordingScriptAuthorityReadModelActivationPort();
        var service = new ScopeScriptCommandApplicationService(
            definitionCommandPort,
            catalogCommandPort,
            authorityReadModelActivationPort,
            Options.Create(options));

        var result = await service.UpsertAsync(
            new ScopeScriptUpsertRequest(
                scopeId,
                scriptId,
                sourceText,
                revisionId,
                expectedBaseRevision));

        result.AcceptedScript.ScopeId.Should().Be(scopeId);
        result.AcceptedScript.ScriptId.Should().Be(scriptId);
        result.AcceptedScript.CatalogActorId.Should().Be(expectedCatalogActorId);
        result.AcceptedScript.DefinitionActorId.Should().Be(expectedDefinitionActorId);
        result.RevisionId.Should().Be(revisionId);
        result.CatalogActorId.Should().Be(expectedCatalogActorId);
        result.DefinitionActorId.Should().Be(expectedDefinitionActorId);
        result.DefinitionCommand.CommandId.Should().Be("definition-command-1");
        result.CatalogCommand.CommandId.Should().Be("catalog-command-1");
        authorityReadModelActivationPort.Calls.Should().Equal(expectedDefinitionActorId, expectedCatalogActorId);

        definitionCommandPort.LastRequest.Should().BeEquivalentTo(
            new FakeScriptDefinitionCommandPort.Request(
                scriptId,
                revisionId,
                sourceText,
                expectedSourceHash,
                expectedDefinitionActorId,
                scopeId));
        catalogCommandPort.LastPromoteRequest.Should().NotBeNull();
        catalogCommandPort.LastPromoteRequest!.CatalogActorId.Should().Be(expectedCatalogActorId);
        catalogCommandPort.LastPromoteRequest.ScriptId.Should().Be(scriptId);
        catalogCommandPort.LastPromoteRequest.ExpectedBaseRevision.Should().Be(expectedBaseRevision);
        catalogCommandPort.LastPromoteRequest.Revision.Should().Be(revisionId);
        catalogCommandPort.LastPromoteRequest.DefinitionActorId.Should().Be(expectedDefinitionActorId);
        catalogCommandPort.LastPromoteRequest.SourceHash.Should().Be(expectedSourceHash);
        catalogCommandPort.LastPromoteRequest.ProposalId.Should().StartWith($"{scopeId}:{scriptId}:{revisionId}:");
        catalogCommandPort.LastPromoteRequest.ScopeId.Should().Be(scopeId);
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

    [Fact]
    public async Task ObserveAsync_ShouldReturnApplied_WhenCatalogMatchesAcceptedRevision()
    {
        var options = new ScopeScriptCapabilityOptions
        {
            CatalogActorIdPrefix = "user-script-catalog",
        };
        const string scopeId = "external-user-4";
        const string scriptId = "script-live";
        var updatedAt = DateTimeOffset.UtcNow;
        var queryPort = new FakeScriptCatalogQueryPort
        {
            GetResult = new ScriptCatalogEntrySnapshot(
                ScriptId: scriptId,
                ActiveRevision: "rev-2",
                ActiveDefinitionActorId: "definition-2",
                ActiveSourceHash: "hash-2",
                PreviousRevision: "rev-1",
                RevisionHistory: ["rev-1", "rev-2"],
                LastProposalId: $"{scopeId}:{scriptId}:rev-2:attempt-1",
                CatalogActorId: options.BuildCatalogActorId(scopeId),
                ScopeId: scopeId,
                UpdatedAtUnixTimeMs: updatedAt.ToUnixTimeMilliseconds()),
        };
        var service = new ScopeScriptSaveObservationService(queryPort, Options.Create(options));

        var result = await service.ObserveAsync(
            scopeId,
            scriptId,
            new ScopeScriptSaveObservationRequest(
                RevisionId: "rev-2",
                DefinitionActorId: "definition-2",
                SourceHash: "hash-2",
                ProposalId: $"{scopeId}:{scriptId}:rev-2:attempt-1",
                ExpectedBaseRevision: "rev-1",
                AcceptedAt: updatedAt.AddSeconds(-5)));

        result.Status.Should().Be(ScopeScriptSaveObservationStatuses.Applied);
        result.IsTerminal.Should().BeTrue();
        result.CurrentScript.Should().NotBeNull();
        result.CurrentScript!.ActiveRevision.Should().Be("rev-2");
        queryPort.LastGetRequest.Should().BeEquivalentTo(
            new FakeScriptCatalogQueryPort.GetRequest(options.BuildCatalogActorId(scopeId), scriptId));
    }

    [Fact]
    public async Task ObserveAsync_ShouldReturnRejected_WhenExpectedBaseRevisionHasMoved()
    {
        var options = new ScopeScriptCapabilityOptions
        {
            CatalogActorIdPrefix = "user-script-catalog",
        };
        const string scopeId = "external-user-5";
        const string scriptId = "script-conflict";
        var queryPort = new FakeScriptCatalogQueryPort
        {
            GetResult = new ScriptCatalogEntrySnapshot(
                ScriptId: scriptId,
                ActiveRevision: "rev-9",
                ActiveDefinitionActorId: "definition-9",
                ActiveSourceHash: "hash-9",
                PreviousRevision: "rev-8",
                RevisionHistory: ["rev-8", "rev-9"],
                LastProposalId: $"{scopeId}:{scriptId}:rev-9:attempt-2",
                CatalogActorId: options.BuildCatalogActorId(scopeId),
                ScopeId: scopeId,
                UpdatedAtUnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        };
        var service = new ScopeScriptSaveObservationService(queryPort, Options.Create(options));

        var result = await service.ObserveAsync(
            scopeId,
            scriptId,
            new ScopeScriptSaveObservationRequest(
                RevisionId: "rev-10",
                DefinitionActorId: "definition-10",
                SourceHash: "hash-10",
                ProposalId: $"{scopeId}:{scriptId}:rev-10:attempt-1",
                ExpectedBaseRevision: "rev-8",
                AcceptedAt: DateTimeOffset.UtcNow.AddSeconds(-10)));

        result.Status.Should().Be(ScopeScriptSaveObservationStatuses.Rejected);
        result.IsTerminal.Should().BeTrue();
        result.Message.Should().Contain("expected base revision 'rev-8'");
        result.CurrentScript.Should().NotBeNull();
        result.CurrentScript!.ActiveRevision.Should().Be("rev-9");
    }

    [Fact]
    public async Task ObserveAsync_ShouldReturnRejected_WhenLaterSaveReusesSameRevision()
    {
        var options = new ScopeScriptCapabilityOptions
        {
            CatalogActorIdPrefix = "user-script-catalog",
        };
        const string scopeId = "external-user-6";
        const string scriptId = "script-same-revision";
        var queryPort = new FakeScriptCatalogQueryPort
        {
            GetResult = new ScriptCatalogEntrySnapshot(
                ScriptId: scriptId,
                ActiveRevision: "rev-7",
                ActiveDefinitionActorId: "definition-new",
                ActiveSourceHash: "hash-new",
                PreviousRevision: "rev-6",
                RevisionHistory: ["rev-6", "rev-7"],
                LastProposalId: $"{scopeId}:{scriptId}:rev-7:attempt-2",
                CatalogActorId: options.BuildCatalogActorId(scopeId),
                ScopeId: scopeId,
                UpdatedAtUnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        };
        var service = new ScopeScriptSaveObservationService(queryPort, Options.Create(options));

        var result = await service.ObserveAsync(
            scopeId,
            scriptId,
            new ScopeScriptSaveObservationRequest(
                RevisionId: "rev-7",
                DefinitionActorId: "definition-old",
                SourceHash: "hash-old",
                ProposalId: $"{scopeId}:{scriptId}:rev-7:attempt-1",
                ExpectedBaseRevision: "rev-6",
                AcceptedAt: DateTimeOffset.UtcNow.AddSeconds(-10)));

        result.Status.Should().Be(ScopeScriptSaveObservationStatuses.Rejected);
        result.IsTerminal.Should().BeTrue();
        result.Message.Should().Contain("different accepted catalog payload");
        result.CurrentScript.Should().NotBeNull();
        result.CurrentScript!.ActiveRevision.Should().Be("rev-7");
        result.CurrentScript.DefinitionActorId.Should().Be("definition-new");
    }

    [Fact]
    public async Task ObserveAsync_ShouldReturnPending_WhenCatalogStillShowsExpectedBaseRevision()
    {
        var options = new ScopeScriptCapabilityOptions
        {
            CatalogActorIdPrefix = "user-script-catalog",
        };
        const string scopeId = "external-user-7";
        const string scriptId = "script-pending";
        var queryPort = new FakeScriptCatalogQueryPort
        {
            GetResult = new ScriptCatalogEntrySnapshot(
                ScriptId: scriptId,
                ActiveRevision: "rev-3",
                ActiveDefinitionActorId: "definition-3",
                ActiveSourceHash: "hash-3",
                PreviousRevision: "rev-2",
                RevisionHistory: ["rev-2", "rev-3"],
                LastProposalId: $"{scopeId}:{scriptId}:rev-3:attempt-1",
                CatalogActorId: options.BuildCatalogActorId(scopeId),
                ScopeId: scopeId,
                UpdatedAtUnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        };
        var service = new ScopeScriptSaveObservationService(queryPort, Options.Create(options));

        var result = await service.ObserveAsync(
            scopeId,
            scriptId,
            new ScopeScriptSaveObservationRequest(
                RevisionId: "rev-4",
                DefinitionActorId: "definition-4",
                SourceHash: "hash-4",
                ProposalId: $"{scopeId}:{scriptId}:rev-4:attempt-2",
                ExpectedBaseRevision: "rev-3",
                AcceptedAt: DateTimeOffset.UtcNow.AddSeconds(-10)));

        result.Status.Should().Be(ScopeScriptSaveObservationStatuses.Pending);
        result.IsTerminal.Should().BeFalse();
        result.Message.Should().Contain("waiting to appear");
        result.CurrentScript.Should().NotBeNull();
        result.CurrentScript!.ActiveRevision.Should().Be("rev-3");
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
            CreateDefinitionSnapshot("script", "rev-1", "source", "hash", "definition-actor", "scope"),
            new ScriptingCommandAcceptedReceipt("definition-actor", "definition-command-1", "definition-correlation-1"));

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

    private sealed class RecordingScriptAuthorityReadModelActivationPort : IScriptAuthorityReadModelActivationPort
    {
        public List<string> Calls { get; } = [];

        public Task ActivateAsync(string actorId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScriptCatalogCommandPort : IScriptCatalogCommandPort
    {
        public PromoteRequest? LastPromoteRequest { get; private set; }

        public Task<ScriptingCommandAcceptedReceipt> PromoteCatalogRevisionAsync(
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
            return Task.FromResult(new ScriptingCommandAcceptedReceipt(
                catalogActorId ?? "catalog-actor",
                "catalog-command-1",
                proposalId));
        }

        public Task<ScriptingCommandAcceptedReceipt> PromoteCatalogRevisionAsync(
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
            return Task.FromResult(new ScriptingCommandAcceptedReceipt(
                catalogActorId ?? "catalog-actor",
                "catalog-command-1",
                proposalId));
        }

        public Task<ScriptingCommandAcceptedReceipt> RollbackCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            string expectedCurrentRevision,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ScriptingCommandAcceptedReceipt(
                catalogActorId ?? "catalog-actor",
                "catalog-rollback-command-1",
                proposalId));
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
