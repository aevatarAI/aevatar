using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptDefinitionLifecycleService
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly RuntimeScriptQueryClient _queryClient;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly TimeSpan _definitionMutationTimeout;
    private readonly UpsertScriptDefinitionActorRequestAdapter _upsertDefinitionAdapter = new();

    public RuntimeScriptDefinitionLifecycleService(
        RuntimeScriptActorAccessor actorAccessor,
        RuntimeScriptQueryClient queryClient,
        IScriptingActorAddressResolver addressResolver,
        IScriptingPortTimeouts timeouts)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _definitionMutationTimeout = (timeouts ?? throw new ArgumentNullException(nameof(timeouts)))
            .GetDefinitionMutationTimeout();
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

        var response = await _queryClient.QueryAsync<ScriptDefinitionCommandRespondedEvent>(
            ScriptingQueryRouteConventions.DefinitionReplyStreamPrefix,
            _definitionMutationTimeout,
            (requestId, replyStreamId) => actor.HandleEventAsync(
                _upsertDefinitionAdapter.Map(
                    new UpsertScriptDefinitionActorRequest(
                        ScriptId: scriptId,
                        ScriptRevision: scriptRevision,
                        SourceText: sourceText,
                        SourceHash: sourceHash ?? string.Empty,
                        RequestId: requestId,
                        ReplyStreamId: replyStreamId),
                    actorId),
                ct),
            static (response, requestId) => string.Equals(response.RequestId, requestId, StringComparison.Ordinal),
            ScriptingQueryRouteConventions.BuildDefinitionMutationTimeoutMessage,
            ct);
        if (!response.Succeeded)
        {
            throw new ScriptDefinitionMutationRejectedException(
                string.IsNullOrWhiteSpace(response.FailureReason)
                    ? $"Script definition upsert did not produce a definition snapshot. actor_id=`{actorId}` revision=`{scriptRevision}`."
                    : response.FailureReason,
                response.Diagnostics.ToArray());
        }
        if (!string.Equals(response.Revision, scriptRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Script definition upsert returned unexpected revision. expected=`{scriptRevision}` actual=`{response.Revision}` actor_id=`{actorId}`.");
        }
        if (!string.Equals(response.ScriptId, scriptId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Script definition upsert completed with mismatched script_id. expected=`{scriptId}` actual=`{response.ScriptId}` actor_id=`{actorId}`.");
        }

        return actorId;
    }
}
