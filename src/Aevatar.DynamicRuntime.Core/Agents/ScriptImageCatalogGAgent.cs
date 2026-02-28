using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.DynamicRuntime.Core.Agents;

public sealed class ScriptImageCatalogGAgent : GAgentBase<ScriptImageCatalogState>
{
    [EventHandler]
    public Task HandleAsync(ScriptImagePublishedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    protected override ScriptImageCatalogState TransitionState(ScriptImageCatalogState current, IMessage evt) =>
        StateTransitionMatcher.Match(current, evt)
            .On<ScriptImagePublishedEvent>(Apply)
            .OrCurrent();

    private static ScriptImageCatalogState Apply(ScriptImageCatalogState current, ScriptImagePublishedEvent evt)
    {
        var next = current.Clone();
        next.ImageName = evt.ImageName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.Tag) && !string.IsNullOrWhiteSpace(evt.Digest))
            next.Tags[evt.Tag] = evt.Digest;
        if (!string.IsNullOrWhiteSpace(evt.Digest) && !next.Digests.Contains(evt.Digest))
            next.Digests.Add(evt.Digest);
        return next;
    }
}
