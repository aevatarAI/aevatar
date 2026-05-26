using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Core.Runtime;

public interface IScriptCommandOutcomePublisher
{
    Task<ScriptBehaviorBoundEvent> ObserveBoundAsync(
        string commandId,
        CancellationToken ct);

    Task<ScriptDomainFactCommitted> ObserveCommittedFactAsync(
        string commandId,
        CancellationToken ct);

    Task PublishBoundAsync(
        string commandId,
        ScriptBehaviorBoundEvent bound,
        CancellationToken ct);

    Task PublishCommittedFactAsync(
        string commandId,
        ScriptDomainFactCommitted fact,
        CancellationToken ct);
}

public sealed class NoOpScriptCommandOutcomePublisher : IScriptCommandOutcomePublisher
{
    public static NoOpScriptCommandOutcomePublisher Instance { get; } = new();

    private NoOpScriptCommandOutcomePublisher()
    {
    }

    public Task<ScriptBehaviorBoundEvent> ObserveBoundAsync(
        string commandId,
        CancellationToken ct) =>
        Task.FromCanceled<ScriptBehaviorBoundEvent>(ct.IsCancellationRequested ? ct : new CancellationToken(true));

    public Task<ScriptDomainFactCommitted> ObserveCommittedFactAsync(
        string commandId,
        CancellationToken ct) =>
        Task.FromCanceled<ScriptDomainFactCommitted>(ct.IsCancellationRequested ? ct : new CancellationToken(true));

    public Task PublishBoundAsync(
        string commandId,
        ScriptBehaviorBoundEvent bound,
        CancellationToken ct) =>
        Task.CompletedTask;

    public Task PublishCommittedFactAsync(
        string commandId,
        ScriptDomainFactCommitted fact,
        CancellationToken ct) =>
        Task.CompletedTask;
}
