using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptDefinitionLifecycleService
    : ScriptActorCommandPortBase<ScriptDefinitionGAgent>,
      IScriptDefinitionCommandPort
{
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly UpsertScriptDefinitionActorRequestAdapter _upsertDefinitionAdapter = new();

    public RuntimeScriptDefinitionLifecycleService(
        IActorDispatchPort dispatchPort,
        RuntimeScriptActorAccessor actorAccessor,
        IScriptingActorAddressResolver addressResolver)
        : base(dispatchPort, actorAccessor)
    {
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

        _ = await GetOrCreateActorAsync(
            actorId,
            "Script definition actor not found",
            ct);

        await DispatchAsync(
            actorId,
            new UpsertScriptDefinitionActorRequest(
                ScriptId: scriptId,
                ScriptRevision: scriptRevision,
                SourceText: sourceText,
                SourceHash: sourceHash ?? string.Empty),
            _upsertDefinitionAdapter.Map,
            ct);

        return actorId;
    }
}
