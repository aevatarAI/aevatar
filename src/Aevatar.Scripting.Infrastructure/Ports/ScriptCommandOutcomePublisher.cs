using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Runtime;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptCommandOutcomePublisher : IScriptCommandOutcomePublisher
{
    private readonly IActorOutcomeChannel<ScriptBehaviorBoundEvent> _boundOutcomes;
    private readonly IActorOutcomeChannel<ScriptDomainFactCommitted> _committedFactOutcomes;

    public ScriptCommandOutcomePublisher(
        IActorOutcomeChannel<ScriptBehaviorBoundEvent> boundOutcomes,
        IActorOutcomeChannel<ScriptDomainFactCommitted> committedFactOutcomes)
    {
        _boundOutcomes = boundOutcomes ?? throw new ArgumentNullException(nameof(boundOutcomes));
        _committedFactOutcomes = committedFactOutcomes ?? throw new ArgumentNullException(nameof(committedFactOutcomes));
    }

    public async Task<ScriptBehaviorBoundEvent> ObserveBoundAsync(
        string commandId,
        CancellationToken ct)
    {
        await using var subscription = await _boundOutcomes.SubscribeAsync(commandId, ct);
        return await subscription.Outcome.WaitAsync(ct);
    }

    public async Task<ScriptDomainFactCommitted> ObserveCommittedFactAsync(
        string commandId,
        CancellationToken ct)
    {
        await using var subscription = await _committedFactOutcomes.SubscribeAsync(commandId, ct);
        return await subscription.Outcome.WaitAsync(ct);
    }

    public Task PublishBoundAsync(
        string commandId,
        ScriptBehaviorBoundEvent bound,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            return Task.CompletedTask;

        return _boundOutcomes.PublishAsync(commandId, bound, ct);
    }

    public Task PublishCommittedFactAsync(
        string commandId,
        ScriptDomainFactCommitted fact,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            return Task.CompletedTask;

        return _committedFactOutcomes.PublishAsync(commandId, fact, ct);
    }
}
