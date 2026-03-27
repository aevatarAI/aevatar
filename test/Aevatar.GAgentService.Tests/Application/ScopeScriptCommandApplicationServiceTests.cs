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
        var queryPort = new RecordingQueryPort();
        var service = BuildService(definitionPort, catalogPort, queryPort);

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
        var queryPort = new RecordingQueryPort();
        var service = BuildService(definitionPort, catalogPort, queryPort);

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
        var queryPort = new RecordingQueryPort();
        var service = BuildService(definitionPort, catalogPort, queryPort);

        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", "source");

        await service.UpsertAsync(request);

        definitionPort.Calls.Should().ContainSingle();
        definitionPort.Calls[0].definitionActorId.Should().StartWith("user-script-definition:");
    }

    [Fact]
    public async Task UpsertAsync_ShouldReturnFallbackSummary_WhenQueryReturnsNull()
    {
        var definitionPort = new RecordingDefinitionCommandPort();
        var catalogPort = new RecordingCatalogCommandPort();
        var queryPort = new RecordingQueryPort { GetByScriptIdResult = null };
        var service = BuildService(definitionPort, catalogPort, queryPort);

        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", "source");

        var result = await service.UpsertAsync(request);

        result.Script.Should().NotBeNull();
        result.Script.ScopeId.Should().Be("scope-1");
        result.Script.ScriptId.Should().Be("my-script");
        result.Script.DefinitionActorId.Should().Be(definitionPort.ResultActorId);
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenSourceTextIsEmpty()
    {
        var definitionPort = new RecordingDefinitionCommandPort();
        var catalogPort = new RecordingCatalogCommandPort();
        var queryPort = new RecordingQueryPort();
        var service = BuildService(definitionPort, catalogPort, queryPort);

        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", "");

        var act = () => service.UpsertAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static ScopeScriptCommandApplicationService BuildService(
        IScriptDefinitionCommandPort definitionPort,
        IScriptCatalogCommandPort catalogPort,
        IScopeScriptQueryPort queryPort) =>
        new(
            definitionPort,
            catalogPort,
            queryPort,
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
                    string.Empty, string.Empty, string.Empty, string.Empty)));
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
                    ScopeId: scopeId ?? string.Empty)));
        }
    }

    private sealed class RecordingCatalogCommandPort : IScriptCatalogCommandPort
    {
        public List<(string? catalogActorId, string scriptId, string expectedBaseRevision, string revision, string definitionActorId, string sourceHash, string proposalId, string? scopeId)> Calls { get; } = [];

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
            Calls.Add((catalogActorId, scriptId, expectedBaseRevision, revision, definitionActorId, sourceHash, proposalId, null));
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
            Calls.Add((catalogActorId, scriptId, expectedBaseRevision, revision, definitionActorId, sourceHash, proposalId, scopeId));
            return Task.CompletedTask;
        }

        public Task RollbackCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            string expectedCurrentRevision,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task RollbackCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            string expectedCurrentRevision,
            string? scopeId,
            CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class RecordingQueryPort : IScopeScriptQueryPort
    {
        public ScopeScriptSummary? GetByScriptIdResult { get; init; }

        public Task<ScopeScriptSummary?> GetByScriptIdAsync(
            string scopeId,
            string scriptId,
            CancellationToken ct = default) =>
            Task.FromResult(GetByScriptIdResult);

        public Task<IReadOnlyList<ScopeScriptSummary>> ListAsync(
            string scopeId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeScriptSummary>>([]);
    }
}
