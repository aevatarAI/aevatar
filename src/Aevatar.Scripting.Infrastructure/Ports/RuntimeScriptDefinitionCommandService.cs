using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptDefinitionCommandService : IScriptDefinitionCommandPort
{
    private readonly ICommandDispatchService<UpsertScriptDefinitionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _dispatchService;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly IScriptAuthorityReadModelActivationPort _authorityReadModelActivationPort;
    private readonly IScriptBehaviorCompiler _compiler;

    public RuntimeScriptDefinitionCommandService(
        ICommandDispatchService<UpsertScriptDefinitionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService,
        IScriptingActorAddressResolver addressResolver,
        IScriptAuthorityReadModelActivationPort authorityReadModelActivationPort,
        IScriptBehaviorCompiler compiler)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _authorityReadModelActivationPort = authorityReadModelActivationPort ?? throw new ArgumentNullException(nameof(authorityReadModelActivationPort));
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    }

    public async Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct)
    {
        var actorId = string.IsNullOrWhiteSpace(definitionActorId)
            ? _addressResolver.GetDefinitionActorId(scriptId)
            : definitionActorId;
        var snapshot = await BuildDefinitionSnapshotAsync(
            scriptId,
            scriptRevision,
            sourceText,
            sourceHash);
        await _authorityReadModelActivationPort.ActivateAsync(actorId, ct);

        var result = await _dispatchService.DispatchAsync(
            new UpsertScriptDefinitionCommand(
                scriptId,
                scriptRevision,
                sourceText,
                sourceHash,
                actorId),
            ct);
        if (!result.Succeeded || result.Receipt == null)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script definition dispatch failed.");

        return new ScriptDefinitionUpsertResult(result.Receipt.ActorId, snapshot);
    }

    private async Task<ScriptDefinitionSnapshot> BuildDefinitionSnapshotAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash)
    {
        var parsedPackage = ScriptSourcePackageSerializer.DeserializeOrWrapCSharp(sourceText ?? string.Empty);
        var scriptPackage = ScriptPackageModel.ToPackageSpec(parsedPackage);
        var entrySourceText = ScriptPackageModel.GetEntrySourceText(scriptPackage);
        var packageHash = string.IsNullOrWhiteSpace(sourceHash)
            ? ScriptPackageModel.ComputePackageHash(scriptPackage)
            : sourceHash;
        var compilation = _compiler.Compile(
            new ScriptBehaviorCompilationRequest(
                scriptId ?? string.Empty,
                scriptRevision ?? string.Empty,
                scriptPackage,
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
                entrySourceText,
                packageHash,
                scriptPackage,
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
