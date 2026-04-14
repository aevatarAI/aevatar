using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Scripts;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeScriptCommandApplicationServiceTests
{
    private static readonly ScopeScriptCapabilityOptions DefaultOptions = new();

    [Fact]
    public async Task UpsertAsync_ShouldCreateDefinitionAndPromoteCatalog()
    {
        var definitionPort = new RecordingDefinitionCommandPort();
        var catalogPort = new RecordingCatalogCommandPort();
        var service = BuildService(definitionPort, catalogPort);

        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", "print('hello')");

        await service.UpsertAsync(request);

        definitionPort.Calls.Should().ContainSingle();
        definitionPort.Calls[0].scriptId.Should().Be("my-script");
        definitionPort.Calls[0].sourceText.Should().Be("print('hello')");
        definitionPort.Calls[0].scopeId.Should().Be("scope-1");

        catalogPort.Calls.Should().ContainSingle();
        catalogPort.Calls[0].scriptId.Should().Be("my-script");
        catalogPort.Calls[0].definitionActorId.Should().Be(definitionPort.ResultActorId);
        catalogPort.Calls[0].scopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task UpsertAsync_ShouldComputeSourceHash()
    {
        var definitionPort = new RecordingDefinitionCommandPort();
        var catalogPort = new RecordingCatalogCommandPort();
        var service = BuildService(definitionPort, catalogPort);

        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", "hello");

        await service.UpsertAsync(request);

        var expectedHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("hello"))).ToLowerInvariant();

        definitionPort.Calls.Should().ContainSingle();
        definitionPort.Calls[0].sourceHash.Should().Be(expectedHash);
    }

    [Fact]
    public async Task UpsertAsync_ShouldBuildActorIdFromScopeAndScriptId()
    {
        var definitionPort = new RecordingDefinitionCommandPort();
        var catalogPort = new RecordingCatalogCommandPort();
        var service = BuildService(definitionPort, catalogPort);

        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", "source");

        await service.UpsertAsync(request);

        definitionPort.Calls.Should().ContainSingle();
        definitionPort.Calls[0].definitionActorId.Should().StartWith("user-script-definition:");
    }

    [Fact]
    public async Task UpsertAsync_ShouldReturnAcceptedSummary()
    {
        var definitionPort = new RecordingDefinitionCommandPort();
        var catalogPort = new RecordingCatalogCommandPort();
        var service = BuildService(definitionPort, catalogPort);

        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", "source");

        var result = await service.UpsertAsync(request);

        result.AcceptedScript.ScopeId.Should().Be("scope-1");
        result.AcceptedScript.ScriptId.Should().Be("my-script");
        result.AcceptedScript.DefinitionActorId.Should().Be(definitionPort.ResultActorId);
        result.AcceptedScript.AcceptedAt.Should().Be(catalogPort.AcceptedAt);
        result.DefinitionCommand.CommandId.Should().Be("definition-command-1");
        result.CatalogCommand.CommandId.Should().Be("catalog-command-1");
    }

    [Fact]
    public async Task UpsertAsync_ShouldGenerateUniqueProposalId_ForRepeatedSameRevisionSaves()
    {
        var definitionPort = new RecordingDefinitionCommandPort();
        var catalogPort = new RecordingCatalogCommandPort();
        var service = BuildService(definitionPort, catalogPort);
        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", "source", "rev-1");

        var first = await service.UpsertAsync(request);
        var second = await service.UpsertAsync(request);

        first.AcceptedScript.ProposalId.Should().StartWith("scope-1:my-script:rev-1:");
        second.AcceptedScript.ProposalId.Should().StartWith("scope-1:my-script:rev-1:");
        first.AcceptedScript.ProposalId.Should().NotBe(second.AcceptedScript.ProposalId);
        catalogPort.Calls[0].proposalId.Should().Be(first.AcceptedScript.ProposalId);
        catalogPort.Calls[1].proposalId.Should().Be(second.AcceptedScript.ProposalId);
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenSourceTextIsEmpty()
    {
        var definitionPort = new RecordingDefinitionCommandPort();
        var catalogPort = new RecordingCatalogCommandPort();
        var service = BuildService(definitionPort, catalogPort);

        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", "");

        var act = () => service.UpsertAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static ScopeScriptCommandApplicationService BuildService(
        IScriptDefinitionCommandPort definitionPort,
        IScriptCatalogCommandPort catalogPort) =>
        new(
            definitionPort,
            catalogPort,
            Options.Create(DefaultOptions));

    private sealed class RecordingDefinitionCommandPort : IScriptDefinitionCommandPort
    {
        public string ResultActorId { get; } = "def-actor-1";

        public List<(string scriptId, string scriptRevision, string sourceText, string sourceHash, string? definitionActorId, string? scopeId)> Calls { get; } = [];

        public Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
            string scriptId,
            string scriptRevision,
            string sourceText,
            string sourceHash,
            string? definitionActorId,
            CancellationToken ct)
        {
            Calls.Add((scriptId, scriptRevision, sourceText, sourceHash, definitionActorId, null));
            return Task.FromResult(new ScriptDefinitionUpsertResult(
                ResultActorId,
                new ScriptDefinitionSnapshot(
                    scriptId, scriptRevision, sourceText, sourceHash,
                    string.Empty, string.Empty, string.Empty, string.Empty),
                new ScriptingCommandAcceptedReceipt(ResultActorId, "definition-command-1", "definition-correlation-1")));
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
            Calls.Add((scriptId, scriptRevision, sourceText, sourceHash, definitionActorId, scopeId));
            return Task.FromResult(new ScriptDefinitionUpsertResult(
                ResultActorId,
                new ScriptDefinitionSnapshot(
                    scriptId, scriptRevision, sourceText, sourceHash,
                    string.Empty, string.Empty, string.Empty, string.Empty,
                    ScopeId: scopeId ?? string.Empty),
                new ScriptingCommandAcceptedReceipt(ResultActorId, "definition-command-1", "definition-correlation-1")));
        }
    }

    private sealed class RecordingCatalogCommandPort : IScriptCatalogCommandPort
    {
        public DateTimeOffset AcceptedAt { get; } = new(2026, 4, 13, 9, 0, 0, TimeSpan.Zero);

        public List<(string? catalogActorId, string scriptId, string expectedBaseRevision, string revision, string definitionActorId, string sourceHash, string proposalId, string? scopeId)> Calls { get; } = [];

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
            Calls.Add((catalogActorId, scriptId, expectedBaseRevision, revision, definitionActorId, sourceHash, proposalId, null));
            return Task.FromResult(new ScriptingCommandAcceptedReceipt(
                catalogActorId ?? "catalog-actor-1",
                "catalog-command-1",
                proposalId,
                AcceptedAt));
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
            Calls.Add((catalogActorId, scriptId, expectedBaseRevision, revision, definitionActorId, sourceHash, proposalId, scopeId));
            return Task.FromResult(new ScriptingCommandAcceptedReceipt(
                catalogActorId ?? "catalog-actor-1",
                "catalog-command-1",
                proposalId,
                AcceptedAt));
        }

        public Task<ScriptingCommandAcceptedReceipt> RollbackCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            string expectedCurrentRevision,
            CancellationToken ct) =>
            Task.FromResult(new ScriptingCommandAcceptedReceipt(
                catalogActorId ?? "catalog-actor-1",
                "catalog-rollback-command-1",
                proposalId,
                AcceptedAt));

        public Task<ScriptingCommandAcceptedReceipt> RollbackCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            string expectedCurrentRevision,
            string? scopeId,
            CancellationToken ct) =>
            Task.FromResult(new ScriptingCommandAcceptedReceipt(
                catalogActorId ?? "catalog-actor-1",
                "catalog-rollback-command-1",
                proposalId,
                AcceptedAt));
    }
}
