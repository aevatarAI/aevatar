using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptDefinitionLifecyclePort : IScriptDefinitionLifecyclePort
{
    private readonly IActorRuntime _runtime;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly UpsertScriptDefinitionActorRequestAdapter _adapter = new();

    public RuntimeScriptDefinitionLifecyclePort(
        IActorRuntime runtime,
        IScriptingActorAddressResolver addressResolver)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
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
            ? _addressResolver.GetDefinitionActorId(scriptId)
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
                new UpsertScriptDefinitionActorRequest(
                    ScriptId: scriptId,
                    ScriptRevision: scriptRevision,
                    SourceText: sourceText,
                    SourceHash: sourceHash ?? string.Empty),
                actorId),
            ct);

        return actorId;
    }
}
