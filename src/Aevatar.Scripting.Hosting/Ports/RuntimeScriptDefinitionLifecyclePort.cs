using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptDefinitionLifecyclePort : IScriptDefinitionLifecyclePort
{
    private readonly IActorRuntime _runtime;
    private readonly UpsertScriptDefinitionCommandAdapter _adapter = new();

    public RuntimeScriptDefinitionLifecyclePort(IActorRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task<string> UpsertAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);

        var actorId = string.IsNullOrWhiteSpace(definitionActorId)
            ? $"script-definition:{scriptId}"
            : definitionActorId;

        IActor actor;
        if (await _runtime.ExistsAsync(actorId))
        {
            actor = await _runtime.GetAsync(actorId)
                ?? throw new InvalidOperationException($"Script definition actor not found: {actorId}");
        }
        else
        {
            actor = await _runtime.CreateAsync<ScriptDefinitionGAgent>(actorId, ct);
        }

        await actor.HandleEventAsync(
            _adapter.Map(
                new UpsertScriptDefinitionCommand(
                    ScriptId: scriptId,
                    ScriptRevision: scriptRevision,
                    SourceText: sourceText,
                    SourceHash: sourceHash ?? string.Empty),
                actorId),
            ct);

        return actorId;
    }
}
