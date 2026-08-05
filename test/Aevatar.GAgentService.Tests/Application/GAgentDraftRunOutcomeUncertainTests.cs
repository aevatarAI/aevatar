using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.AGUI.Contracts;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class GAgentDraftRunOutcomeUncertainTests
{
    [Fact]
    public void CompletionPolicy_ShouldResolveOutcomeUncertainRunError()
    {
        var completionPolicy = new GAgentDraftRunCompletionPolicy();

        var resolved = completionPolicy.TryResolve(
            new AGUIEvent
            {
                RunError = new RunErrorEvent
                {
                    Code = GAgentRunFailureCodes.OutcomeUncertain,
                    Message = "The interrupted session may have produced side effects.",
                },
            },
            out var completion);

        resolved.Should().BeTrue();
        completion.Should().Be(GAgentDraftRunCompletionStatus.OutcomeUncertain);
    }

    [Fact]
    public async Task DurableCompletionResolver_ShouldResolveOutcomeUncertainAsCompleted()
    {
        var terminalQuery = new FixedGAgentRunTerminalQueryPort(new GAgentRunTerminalSnapshot(
            "actor-1",
            "session-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.DraftRun,
            GAgentRunTerminalStatus.OutcomeUncertain,
            GAgentRunFailureCodes.OutcomeUncertain,
            "The interrupted session may have produced side effects.",
            4,
            "evt-uncertain",
            DateTimeOffset.UtcNow));
        var durableResolver = new GAgentDraftRunDurableCompletionResolver(terminalQuery);

        var result = await durableResolver.ResolveAsync(
            new GAgentDraftRunAcceptedReceipt("actor-1", "actor-type", "cmd-1", "corr-1", "session-1"),
            CancellationToken.None);

        result.Should().Be(new CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>(
            true,
            GAgentDraftRunCompletionStatus.OutcomeUncertain));
    }

    private sealed class FixedGAgentRunTerminalQueryPort(GAgentRunTerminalSnapshot snapshot)
        : IGAgentRunTerminalQueryPort
    {
        public Task<GAgentRunTerminalSnapshot?> GetByCorrelationIdAsync(
            string actorId,
            string correlationId,
            CancellationToken ct = default) =>
            Task.FromResult<GAgentRunTerminalSnapshot?>(snapshot);

        public Task<GAgentRunTerminalSnapshot?> GetBySessionIdAsync(
            string actorId,
            string sessionId,
            CancellationToken ct = default) =>
            Task.FromResult<GAgentRunTerminalSnapshot?>(snapshot);
    }
}
