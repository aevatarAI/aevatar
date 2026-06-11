using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Scripts;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;
using Microsoft.Extensions.Options;
using System.Reflection;

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

        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", SingleSource("print('hello')"));

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
    public async Task UpsertAsync_ShouldDispatchAcceptedOnlyCommandsWithoutReadModelActivation()
    {
        var executionLog = new List<string>();
        var definitionPort = new RecordingDefinitionCommandPort(executionLog);
        var catalogPort = new RecordingCatalogCommandPort(executionLog);
        var service = BuildService(definitionPort, catalogPort);

        var result = await service.UpsertAsync(
            new ScopeScriptUpsertRequest("scope-1", "my-script", SingleSource("source"), "rev-1"));

        executionLog.Should().Equal("definition-upsert", "catalog-promote");
        result.DefinitionCommand.CommandId.Should().Be("definition-command-1");
        result.CatalogCommand.CommandId.Should().Be("catalog-command-1");
    }

    [Fact]
    public void Constructor_ShouldNotDependOnAuthorityReadModelActivationPort()
    {
        // Refactor (iter49/issue-882-script-command-readmodel-activation):
        //   Old pattern: ScopeScriptCommandApplicationService.UpsertAsync explicitly activated definition/catalog readmodels via ActivateAsync before write commands.
        //   New principle: Command service dispatches accepted-only write commands; readmodel activation is owned by scripting committed-state projection activation plan provider.
        typeof(ScopeScriptCommandApplicationService)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should()
            .ContainSingle()
            .Subject
            .GetParameters()
            .Select(x => x.ParameterType)
            .Should()
            .NotContain(type => type.Name == "IScriptAuthorityReadModelActivationPort");
    }

    [Fact]
    public async Task UpsertAsync_ShouldComputeCanonicalPackageHash()
    {
        var definitionPort = new RecordingDefinitionCommandPort();
        var catalogPort = new RecordingCatalogCommandPort();
        var service = BuildService(definitionPort, catalogPort);

        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", SingleSource("hello"));

        await service.UpsertAsync(request);

        var expectedHash = ScriptPackageModel.ComputePackageHash(SingleSource("hello"));

        definitionPort.Calls.Should().ContainSingle();
        definitionPort.Calls[0].sourceHash.Should().Be(expectedHash);
        catalogPort.Calls[0].sourceHash.Should().Be(expectedHash);
    }

    [Fact]
    public async Task UpsertAsync_ShouldPreserveTypedPackage_ForMultiFilePackage()
    {
        var definitionPort = new RecordingDefinitionCommandPort();
        var catalogPort = new RecordingCatalogCommandPort();
        var service = BuildService(definitionPort, catalogPort);
        var package = new ScriptPackageSpec
        {
            EntrySourcePath = "src/Behavior.cs",
            CsharpSources =
            {
                new ScriptPackageFile { Path = "src/Behavior.cs", Content = "behavior" },
                new ScriptPackageFile { Path = "src/Helper.cs", Content = "helper" },
            },
            ProtoFiles =
            {
                new ScriptPackageFile { Path = "proto/contract.proto", Content = "syntax = \"proto3\";" },
            },
        };

        await service.UpsertAsync(new ScopeScriptUpsertRequest("scope-1", "my-script", package));

        var expectedHash = ScriptPackageModel.ComputePackageHash(package);
        definitionPort.Calls.Should().ContainSingle();
        definitionPort.Calls[0].sourceText.Should().Be("behavior");
        definitionPort.Calls[0].sourceHash.Should().Be(expectedHash);
        catalogPort.Calls[0].sourceHash.Should().Be(expectedHash);
    }

    [Fact]
    public async Task UpsertAsync_ShouldBuildActorIdFromScopeAndScriptId()
    {
        var definitionPort = new RecordingDefinitionCommandPort();
        var catalogPort = new RecordingCatalogCommandPort();
        var service = BuildService(definitionPort, catalogPort);

        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", SingleSource("source"));

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

        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", SingleSource("source"));

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
        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", SingleSource("source"), "rev-1");

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

        var request = new ScopeScriptUpsertRequest("scope-1", "my-script", SingleSource(""));

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

    private static ScriptPackageSpec SingleSource(string source) =>
        ScriptPackageSpecExtensions.CreateSingleSource(source);

    private sealed class RecordingDefinitionCommandPort : IScriptDefinitionCommandPort
    {
        private readonly List<string>? _executionLog;

        public RecordingDefinitionCommandPort(List<string>? executionLog = null) =>
            _executionLog = executionLog;

        public string ResultActorId { get; } = "def-actor-1";

        public List<(string scriptId, string scriptRevision, string sourceText, string sourceHash, string? definitionActorId, string? scopeId)> Calls { get; } = [];

        public Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
            string scriptId,
            string scriptRevision,
            ScriptPackageSpec scriptPackage,
            string? definitionActorId,
            CancellationToken ct)
        {
            _executionLog?.Add("definition-upsert");
            var sourceText = scriptPackage.GetPrimaryCSharpSource();
            var sourceHash = ScriptPackageModel.ComputePackageHash(scriptPackage);
            Calls.Add((scriptId, scriptRevision, sourceText, sourceHash, definitionActorId, null));
            return Task.FromResult(new ScriptDefinitionUpsertResult(
                ResultActorId,
                new ScriptDefinitionSnapshot(
                    scriptId, scriptRevision, sourceHash, scriptPackage,
                    string.Empty, string.Empty, string.Empty, string.Empty),
                new ScriptingCommandAcceptedReceipt(ResultActorId, "definition-command-1", "definition-correlation-1")));
        }

        public Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
            string scriptId,
            string scriptRevision,
            ScriptPackageSpec scriptPackage,
            string? definitionActorId,
            string? scopeId,
            CancellationToken ct)
        {
            _executionLog?.Add("definition-upsert");
            var sourceText = scriptPackage.GetPrimaryCSharpSource();
            var sourceHash = ScriptPackageModel.ComputePackageHash(scriptPackage);
            Calls.Add((scriptId, scriptRevision, sourceText, sourceHash, definitionActorId, scopeId));
            return Task.FromResult(new ScriptDefinitionUpsertResult(
                ResultActorId,
                new ScriptDefinitionSnapshot(
                    scriptId, scriptRevision, sourceHash, scriptPackage,
                    string.Empty, string.Empty, string.Empty, string.Empty,
                    ScopeId: scopeId ?? string.Empty),
                new ScriptingCommandAcceptedReceipt(ResultActorId, "definition-command-1", "definition-correlation-1")));
        }
    }

    private sealed class RecordingCatalogCommandPort : IScriptCatalogCommandPort
    {
        private readonly List<string>? _executionLog;

        public RecordingCatalogCommandPort(List<string>? executionLog = null) =>
            _executionLog = executionLog;

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
            _executionLog?.Add("catalog-promote");
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
            _executionLog?.Add("catalog-promote");
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
