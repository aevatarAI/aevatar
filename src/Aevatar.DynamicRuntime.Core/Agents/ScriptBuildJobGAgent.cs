using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.DynamicRuntime.Core.Agents;

public sealed class ScriptBuildJobGAgent : GAgentBase<ScriptBuildJobState>
{
    [EventHandler]
    public Task HandleSubmittedAsync(ScriptBuildPlanSubmittedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    [EventHandler]
    public Task HandleApprovedAsync(ScriptBuildApprovedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    [EventHandler]
    public Task HandlePublishedAsync(ScriptBuildPublishedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    protected override ScriptBuildJobState TransitionState(ScriptBuildJobState current, IMessage evt) =>
        StateTransitionMatcher.Match(current, evt)
            .On<ScriptBuildPlanSubmittedEvent>(ApplySubmitted)
            .On<ScriptBuildApprovedEvent>((state, _) => ApplyStatus(state, "Approved"))
            .On<ScriptBuildPublishedEvent>(ApplyPublished)
            .OrCurrent();

    private static ScriptBuildJobState ApplySubmitted(ScriptBuildJobState current, ScriptBuildPlanSubmittedEvent evt)
    {
        var next = current.Clone();
        next.BuildJobId = evt.BuildJobId ?? string.Empty;
        next.StackId = evt.StackId ?? string.Empty;
        next.ServiceName = evt.ServiceName ?? string.Empty;
        next.SourceBundleDigest = evt.SourceBundleDigest ?? string.Empty;
        next.Status = "Planned";
        return next;
    }

    private static ScriptBuildJobState ApplyPublished(ScriptBuildJobState current, ScriptBuildPublishedEvent evt)
    {
        var next = current.Clone();
        next.BuildJobId = evt.BuildJobId ?? current.BuildJobId;
        next.ResultImageDigest = evt.ResultImageDigest ?? string.Empty;
        next.Status = "Published";
        return next;
    }

    private static ScriptBuildJobState ApplyStatus(ScriptBuildJobState current, string status)
    {
        var next = current.Clone();
        next.Status = status;
        return next;
    }
}
