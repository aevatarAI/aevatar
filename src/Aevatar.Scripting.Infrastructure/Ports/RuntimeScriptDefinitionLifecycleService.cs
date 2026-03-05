using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptDefinitionLifecycleService
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly UpsertScriptDefinitionActorRequestAdapter _upsertDefinitionAdapter = new();

    public RuntimeScriptDefinitionLifecycleService(
        RuntimeScriptActorAccessor actorAccessor,
        IScriptingActorAddressResolver addressResolver)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    public async Task<string> UpsertDefinitionAsync(
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

        var actor = await _actorAccessor.GetOrCreateAsync<ScriptDefinitionGAgent>(
            actorId,
            "Script definition actor not found",
            ct);

        await actor.HandleEventAsync(
            _upsertDefinitionAdapter.Map(
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
