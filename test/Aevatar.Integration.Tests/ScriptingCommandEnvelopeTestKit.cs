using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Integration.Tests;

internal static class ScriptingCommandEnvelopeTestKit
{
    public static EventEnvelope CreateUpsertDefinition(
        string definitionActorId,
        string scriptId,
        string revision,
        string sourceText,
        string sourceHash)
    {
        return ScriptingActorRequestEnvelopeFactory.Create(
            definitionActorId,
            revision,
            new UpsertScriptDefinitionRequestedEvent
            {
                ScriptId = scriptId,
                ScriptRevision = revision,
                SourceText = sourceText,
                SourceHash = sourceHash,
            });
    }

    public static EventEnvelope CreateRunScript(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType = "")
    {
        return ScriptingActorRequestEnvelopeFactory.Create(
            runtimeActorId,
            runId,
            new RunScriptRequestedEvent
            {
                RunId = runId,
                InputPayload = inputPayload?.Clone(),
                ScriptRevision = scriptRevision,
                DefinitionActorId = definitionActorId,
                RequestedEventType = requestedEventType,
            });
    }

    public static EventEnvelope CreateRunScript(
        string runtimeActorId,
        RunScriptRequestedEvent requestedEvent)
    {
        ArgumentNullException.ThrowIfNull(requestedEvent);

        return ScriptingActorRequestEnvelopeFactory.Create(
            runtimeActorId,
            requestedEvent.RunId ?? string.Empty,
            requestedEvent);
    }
}
