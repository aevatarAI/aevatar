using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Compilation;

public sealed class RoslynScriptExecutionEngine : IScriptExecutionEngine
{
    public async Task<ScriptHandlerResult> HandleRequestedEventAsync(
        string source,
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new ScriptHandlerResult(Array.Empty<IMessage>());

        await using var loaded = await ScriptRuntimeLoader.LoadFromSourceAsync(source, ct);
        return await loaded.Runtime.HandleRequestedEventAsync(requestedEvent, context, ct)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        string source,
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            return currentState;

        await using var loaded = await ScriptRuntimeLoader.LoadFromSourceAsync(source, ct);
        return await loaded.Runtime.ApplyDomainEventAsync(
            currentState,
            domainEvent,
            ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        string source,
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            return currentReadModel;

        await using var loaded = await ScriptRuntimeLoader.LoadFromSourceAsync(source, ct);
        return await loaded.Runtime.ReduceReadModelAsync(
            currentReadModel,
            domainEvent,
            ct).ConfigureAwait(false);
    }
}
