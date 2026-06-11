using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptDefinitionCommandService : IScriptDefinitionCommandPort
{
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Scripting persists and republishes source_text as a compatibility shadow of ScriptPackageSpec; multi-file packages can be encoded as JSON text and reparsed from persisted source.
    //   New principle: ScriptPackageSpec is the sole internal source-package contract for commands/state/events/readmodels; source_text is only an external one-file adapter field at Host/Application boundary.
    private readonly ICommandDispatchService<UpsertScriptDefinitionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _dispatchService;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly IScriptBehaviorCompiler _compiler;

    public RuntimeScriptDefinitionCommandService(
        ICommandDispatchService<UpsertScriptDefinitionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService,
        IScriptingActorAddressResolver addressResolver,
        IScriptBehaviorCompiler compiler)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    }

    public async Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
        string scriptId,
        string scriptRevision,
        ScriptPackageSpec scriptPackage,
        string? definitionActorId,
        CancellationToken ct) =>
        await UpsertDefinitionWithSnapshotAsync(
            scriptId,
            scriptRevision,
            scriptPackage,
            definitionActorId,
            scopeId: null,
            ct);

    public async Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
        string scriptId,
        string scriptRevision,
        ScriptPackageSpec scriptPackage,
        string? definitionActorId,
        string? scopeId,
        CancellationToken ct)
    {
        var actorId = string.IsNullOrWhiteSpace(definitionActorId)
            ? _addressResolver.GetDefinitionActorId(scriptId, scopeId)
            : definitionActorId;
        var snapshot = await BuildDefinitionSnapshotAsync(
            scriptId,
            scriptRevision,
            scriptPackage);

        var result = await _dispatchService.DispatchAsync(
            new UpsertScriptDefinitionCommand(
                scriptId,
                scriptRevision,
                snapshot.SourceHash,
                actorId,
                scopeId,
                snapshot.ScriptPackage?.Clone() ?? new ScriptPackageSpec()),
            ct);
        if (!result.Succeeded || result.Receipt == null)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script definition dispatch failed.");

        var receipt = result.Receipt;
        snapshot.DefinitionActorId = receipt.ActorId;
        snapshot.ScopeId = scopeId?.Trim() ?? string.Empty;
        return new ScriptDefinitionUpsertResult(receipt.ActorId, snapshot, receipt);
    }

    private async Task<ScriptDefinitionSnapshot> BuildDefinitionSnapshotAsync(
        string scriptId,
        string scriptRevision,
        ScriptPackageSpec scriptPackage)
    {
        var normalizedPackage = ScriptPackageModel.ToPackageSpec(ScriptPackageModel.ToSourcePackage(scriptPackage));
        var packageHash = ScriptPackageModel.ComputePackageHash(normalizedPackage);
        var compilation = _compiler.Compile(
            new ScriptBehaviorCompilationRequest(
                scriptId ?? string.Empty,
                scriptRevision ?? string.Empty,
                normalizedPackage,
                packageHash));
        try
        {
            if (!compilation.IsSuccess || compilation.Artifact == null)
            {
                throw new InvalidOperationException(
                    "Script definition compilation failed: " + string.Join("; ", compilation.Diagnostics));
            }

            var readModelSchemaVersion = string.Empty;
            var readModelSchemaHash = string.Empty;
            if (ScriptSchemaDescriptorExtractor.TryExtractFromDescriptor(
                    compilation.Artifact.Descriptor,
                    out var extracted))
            {
                readModelSchemaVersion = extracted.SchemaVersion;
                readModelSchemaHash = extracted.SchemaHash;
            }

            return new ScriptDefinitionSnapshot(
                scriptId ?? string.Empty,
                scriptRevision ?? string.Empty,
                packageHash,
                normalizedPackage,
                compilation.Artifact.Contract.StateTypeUrl ?? string.Empty,
                compilation.Artifact.Contract.ReadModelTypeUrl ?? string.Empty,
                readModelSchemaVersion,
                readModelSchemaHash,
                compilation.Artifact.Contract.ProtocolDescriptorSet ?? Google.Protobuf.ByteString.Empty,
                compilation.Artifact.Contract.StateDescriptorFullName ?? string.Empty,
                compilation.Artifact.Contract.ReadModelDescriptorFullName ?? string.Empty,
                compilation.Artifact.Contract.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec());
        }
        finally
        {
            if (compilation.Artifact != null)
                await compilation.Artifact.DisposeAsync();
        }
    }
}
