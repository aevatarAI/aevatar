using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptProvisioningService : IScriptRuntimeProvisioningPort
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly IScriptDefinitionSnapshotPort _definitionSnapshotPort;
    private readonly IScriptBehaviorCompiler _compiler;
    private readonly IScriptExecutionProjectionPort _projectionPort;

    public RuntimeScriptProvisioningService(
        RuntimeScriptActorAccessor actorAccessor,
        IScriptDefinitionSnapshotPort definitionSnapshotPort,
        IScriptBehaviorCompiler compiler,
        IScriptExecutionProjectionPort projectionPort)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _definitionSnapshotPort = definitionSnapshotPort ?? throw new ArgumentNullException(nameof(definitionSnapshotPort));
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<string> EnsureRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionActorId);

        var normalizedRevision = string.IsNullOrWhiteSpace(scriptRevision)
            ? "latest"
            : scriptRevision;
        var actorId = string.IsNullOrWhiteSpace(runtimeActorId)
            ? $"script-runtime:{definitionActorId}:{normalizedRevision}:{Guid.NewGuid():N}"
            : runtimeActorId;

        var snapshot = await _definitionSnapshotPort.GetRequiredAsync(
            definitionActorId,
            normalizedRevision,
            ct);
        var compilation = _compiler.Compile(new ScriptBehaviorCompilationRequest(
            snapshot.ScriptId,
            snapshot.Revision,
            snapshot.SourceText));
        if (!compilation.IsSuccess || compilation.Artifact == null)
            throw new InvalidOperationException("Failed to provision script runtime: " + string.Join("; ", compilation.Diagnostics));

        var actor = await _actorAccessor.GetOrCreateAsync<ScriptBehaviorGAgent>(
            actorId,
            "Script behavior actor not found after create",
            ct);
        await actor.HandleEventAsync(
            Aevatar.Scripting.Application.ScriptingActorRequestEnvelopeFactory.Create(
                actorId,
                correlationId: snapshot.Revision,
                payload: new BindScriptBehaviorRequestedEvent
                {
                    DefinitionActorId = definitionActorId,
                    ScriptId = snapshot.ScriptId,
                    Revision = snapshot.Revision,
                    SourceText = snapshot.SourceText,
                    SourceHash = string.IsNullOrWhiteSpace(snapshot.SourceHash)
                        ? ComputeSourceHash(snapshot.SourceText)
                        : snapshot.SourceHash,
                    StateTypeUrl = compilation.Artifact.Contract.StateTypeUrl ?? string.Empty,
                    ReadModelTypeUrl = compilation.Artifact.Contract.ReadModelTypeUrl ?? string.Empty,
                    ReadModelSchemaVersion = snapshot.ReadModelSchemaVersion,
                    ReadModelSchemaHash = snapshot.ReadModelSchemaHash,
                }),
            ct);
        await _projectionPort.EnsureActorProjectionAsync(actorId, ct);
        await compilation.Artifact.DisposeAsync();
        return actorId;
    }

    private static string ComputeSourceHash(string sourceText)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceText ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
