using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptEvolutionManagerGAgentBranchCoverageTests
{
    [Fact]
    public void Ctor_ShouldThrow_WhenDependenciesAreNull()
    {
        Action actWithNullFlow = () => new ScriptEvolutionManagerGAgent(null!, new StaticAddressResolver());
        Action actWithNullResolver = () => new ScriptEvolutionManagerGAgent(new NoopFlowPort(), null!);

        actWithNullFlow.Should().Throw<ArgumentNullException>();
        actWithNullResolver.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task SendTerminalDecisionResponseAsync_ShouldHandleMissingCallback_AndNullPayloadFields()
    {
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(new NoopFlowPort(), new StaticAddressResolver())
        {
            EventPublisher = publisher,
        };

        await InvokePrivateInstanceTask(
            agent,
            "SendTerminalDecisionResponseAsync",
            new ProposeScriptEvolutionRequestedEvent
            {
                CallbackActorId = string.Empty,
                CallbackRequestId = string.Empty,
            },
            false,
            new ScriptEvolutionProposal("proposal-1", "script-1", "rev-1", "rev-2", "source", "hash", "reason"),
            "rev-2",
            ScriptEvolutionStatuses.Rejected,
            "failed",
            "definition-1",
            "catalog-1",
            Array.Empty<string>(),
            CancellationToken.None);

        publisher.Sent.Should().BeEmpty();

        await InvokePrivateInstanceTask(
            agent,
            "SendTerminalDecisionResponseAsync",
            new ProposeScriptEvolutionRequestedEvent
            {
                CallbackActorId = "callback-actor",
                CallbackRequestId = "callback-request",
            },
            false,
            new ScriptEvolutionProposal(null!, null!, null!, null!, null!, null!, null!),
            null!,
            null!,
            null!,
            null!,
            null!,
            Array.Empty<string>(),
            CancellationToken.None);

        publisher.Sent.Should().ContainSingle();
        var firstResponse = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        firstResponse.RequestId.Should().Be("callback-request");
        firstResponse.ProposalId.Should().BeEmpty();
        firstResponse.ScriptId.Should().BeEmpty();
        firstResponse.BaseRevision.Should().BeEmpty();
        firstResponse.CandidateRevision.Should().BeEmpty();
        firstResponse.Status.Should().BeEmpty();
        firstResponse.FailureReason.Should().BeEmpty();
        firstResponse.DefinitionActorId.Should().BeEmpty();
        firstResponse.CatalogActorId.Should().BeEmpty();
        firstResponse.Diagnostics.Should().BeEmpty();

        publisher.Sent.Clear();
        await InvokePrivateInstanceTask(
            agent,
            "SendTerminalDecisionResponseAsync",
            new ProposeScriptEvolutionRequestedEvent
            {
                CallbackActorId = "callback-actor",
                CallbackRequestId = "callback-request-2",
            },
            true,
            new ScriptEvolutionProposal("proposal-2", "script-2", "rev-1", "rev-2", "source", "hash", "reason"),
            "rev-2",
            ScriptEvolutionStatuses.Promoted,
            string.Empty,
            "definition-2",
            "catalog-2",
            new[] { "diag-1" },
            CancellationToken.None);

        publisher.Sent.Should().ContainSingle();
        var secondResponse = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        secondResponse.Diagnostics.Should().ContainSingle("diag-1");
    }

    [Fact]
    public void NormalizeProposal_ShouldGenerateProposalId_AndNormalizeOptionalFields()
    {
        var evt = new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = string.Empty,
            ScriptId = "script-1",
            BaseRevision = string.Empty,
            CandidateRevision = "rev-2",
            CandidateSource = "source-2",
            CandidateSourceHash = string.Empty,
            Reason = string.Empty,
        };

        var proposal = InvokePrivateStatic<ScriptEvolutionProposal>(
            typeof(ScriptEvolutionManagerGAgent),
            "NormalizeProposal",
            evt);

        proposal.ProposalId.Should().NotBeNullOrWhiteSpace();
        proposal.ScriptId.Should().Be("script-1");
        proposal.BaseRevision.Should().BeEmpty();
        proposal.CandidateRevision.Should().Be("rev-2");
        proposal.CandidateSource.Should().Be("source-2");
        proposal.CandidateSourceHash.Should().BeEmpty();
        proposal.Reason.Should().BeEmpty();
    }

    [Fact]
    public void NormalizeProposal_ShouldThrow_WhenRequiredFieldsMissing()
    {
        Action missingScriptId = () => InvokePrivateStatic<object?>(
            typeof(ScriptEvolutionManagerGAgent),
            "NormalizeProposal",
            new ProposeScriptEvolutionRequestedEvent
            {
                ProposalId = "proposal-1",
                ScriptId = string.Empty,
                CandidateRevision = "rev-2",
                CandidateSource = "source-2",
            });

        Action missingCandidateRevision = () => InvokePrivateStatic<object?>(
            typeof(ScriptEvolutionManagerGAgent),
            "NormalizeProposal",
            new ProposeScriptEvolutionRequestedEvent
            {
                ProposalId = "proposal-2",
                ScriptId = "script-1",
                CandidateRevision = string.Empty,
                CandidateSource = "source-2",
            });

        Action missingCandidateSource = () => InvokePrivateStatic<object?>(
            typeof(ScriptEvolutionManagerGAgent),
            "NormalizeProposal",
            new ProposeScriptEvolutionRequestedEvent
            {
                ProposalId = "proposal-3",
                ScriptId = "script-1",
                CandidateRevision = "rev-2",
                CandidateSource = string.Empty,
            });

        missingScriptId.Should().Throw<TargetInvocationException>()
            .Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("ScriptId is required.");
        missingCandidateRevision.Should().Throw<TargetInvocationException>()
            .Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("CandidateRevision is required.");
        missingCandidateSource.Should().Throw<TargetInvocationException>()
            .Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("CandidateSource is required.");
    }

    [Fact]
    public void ApplyProposed_ShouldUpdateLatestProposalOnly_WhenIdsArePresent()
    {
        var state = new ScriptEvolutionManagerState();

        var withoutIds = InvokePrivateStatic<ScriptEvolutionManagerState>(
            typeof(ScriptEvolutionManagerGAgent),
            "ApplyProposed",
            state,
            new ScriptEvolutionProposedEvent
            {
                ProposalId = string.Empty,
                ScriptId = string.Empty,
                BaseRevision = string.Empty,
                CandidateRevision = string.Empty,
                CandidateSourceHash = string.Empty,
                Reason = string.Empty,
            });

        withoutIds.LatestProposalByScript.Should().BeEmpty();

        var withIds = InvokePrivateStatic<ScriptEvolutionManagerState>(
            typeof(ScriptEvolutionManagerGAgent),
            "ApplyProposed",
            state,
            new ScriptEvolutionProposedEvent
            {
                ProposalId = "proposal-valid",
                ScriptId = "script-valid",
                BaseRevision = "rev-1",
                CandidateRevision = "rev-2",
                CandidateSourceHash = "hash-2",
                Reason = "reason",
            });

        withIds.LatestProposalByScript.Should().ContainKey("script-valid");
        withIds.LatestProposalByScript["script-valid"].Should().Be("proposal-valid");
    }

    [Fact]
    public void ApplyRollbackRequested_ShouldCoverTargetRevisionBranches_AndFailureReasonNormalization()
    {
        var state = new ScriptEvolutionManagerState();
        state.Proposals["proposal-1"] = new ScriptEvolutionProposalState
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            PromotedRevision = "rev-current",
        };

        var withEmptyTarget = InvokePrivateStatic<ScriptEvolutionManagerState>(
            typeof(ScriptEvolutionManagerGAgent),
            "ApplyRollbackRequested",
            state,
            new ScriptEvolutionRollbackRequestedEvent
            {
                ProposalId = "proposal-1",
                ScriptId = "script-1",
                TargetRevision = string.Empty,
                Reason = string.Empty,
            });

        withEmptyTarget.Proposals["proposal-1"].Status.Should().Be(ScriptEvolutionStatuses.RollbackRequested);
        withEmptyTarget.Proposals["proposal-1"].PromotedRevision.Should().Be("rev-current");
        withEmptyTarget.Proposals["proposal-1"].FailureReason.Should().BeEmpty();

        var withTarget = InvokePrivateStatic<ScriptEvolutionManagerState>(
            typeof(ScriptEvolutionManagerGAgent),
            "ApplyRollbackRequested",
            state,
            new ScriptEvolutionRollbackRequestedEvent
            {
                ProposalId = "proposal-1",
                ScriptId = "script-1",
                TargetRevision = "rev-target",
                Reason = "manual rollback",
            });

        withTarget.Proposals["proposal-1"].PromotedRevision.Should().Be("rev-target");
        withTarget.Proposals["proposal-1"].FailureReason.Should().Be("manual rollback");
    }

    [Fact]
    public void ApplyRolledBack_ShouldFallbackToEmpty_WhenTargetRevisionIsNull()
    {
        var state = new ScriptEvolutionManagerState();
        state.Proposals["proposal-1"] = new ScriptEvolutionProposalState
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            PromotedRevision = "rev-current",
            FailureReason = "stale error",
        };

        var next = InvokePrivateStatic<ScriptEvolutionManagerState>(
            typeof(ScriptEvolutionManagerGAgent),
            "ApplyRolledBack",
            state,
            new ScriptEvolutionRolledBackEvent
            {
                ProposalId = "proposal-1",
                ScriptId = "script-1",
                TargetRevision = string.Empty,
            });

        next.Proposals["proposal-1"].Status.Should().Be(ScriptEvolutionStatuses.RolledBack);
        next.Proposals["proposal-1"].PromotedRevision.Should().BeEmpty();
        next.Proposals["proposal-1"].FailureReason.Should().BeEmpty();
    }

    [Fact]
    public void ApplyWithProposal_ShouldStampEmptyProposalId_WhenMutationClearsProposalId()
    {
        var state = new ScriptEvolutionManagerState();
        Action<ScriptEvolutionManagerState, ScriptEvolutionProposalState> mutate = (_, proposal) =>
        {
            proposal.ProposalId = string.Empty;
        };

        var next = InvokePrivateStatic<ScriptEvolutionManagerState>(
            typeof(ScriptEvolutionManagerGAgent),
            "ApplyWithProposal",
            state,
            "proposal-1",
            "script-1",
            "custom-status",
            mutate);

        next.LastEventId.Should().Be(":custom-status");
    }

    [Fact]
    public void GetOrCreateProposal_ShouldNormalizeNullProposalId_AndReuseExistingInstance()
    {
        var state = new ScriptEvolutionManagerState();

        var created = InvokePrivateStatic<ScriptEvolutionProposalState>(
            typeof(ScriptEvolutionManagerGAgent),
            "GetOrCreateProposal",
            state,
            null,
            "script-1");

        created.ProposalId.Should().BeEmpty();
        created.ScriptId.Should().Be("script-1");
        state.Proposals.Should().ContainKey(string.Empty);

        var reused = InvokePrivateStatic<ScriptEvolutionProposalState>(
            typeof(ScriptEvolutionManagerGAgent),
            "GetOrCreateProposal",
            state,
            null,
            "script-ignored");

        reused.Should().BeSameAs(created);
    }

    [Fact]
    public void PromotionFailureReasonHelpers_ShouldHandleTaggedAndPlainInputs()
    {
        var tagged = InvokePrivateStatic<string>(
            typeof(ScriptEvolutionManagerGAgent),
            "TagPromotionFailedFailureReason",
            "failed");
        tagged.Should().Be("[promotion_failed]failed");

        var alreadyTagged = InvokePrivateStatic<string>(
            typeof(ScriptEvolutionManagerGAgent),
            "TagPromotionFailedFailureReason",
            "[promotion_failed]failed");
        alreadyTagged.Should().Be("[promotion_failed]failed");

        var argsTagged = new object?[] { "[promotion_failed]why", false };
        var normalizedTagged = InvokePrivateStaticWithArgs<string>(
            typeof(ScriptEvolutionManagerGAgent),
            "NormalizeFailureReason",
            argsTagged);
        normalizedTagged.Should().Be("why");
        ((bool)argsTagged[1]!).Should().BeTrue();

        var argsPlain = new object?[] { "plain-failure", false };
        var normalizedPlain = InvokePrivateStaticWithArgs<string>(
            typeof(ScriptEvolutionManagerGAgent),
            "NormalizeFailureReason",
            argsPlain);
        normalizedPlain.Should().Be("plain-failure");
        ((bool)argsPlain[1]!).Should().BeFalse();
    }

    private static async Task InvokePrivateInstanceTask(object instance, string methodName, params object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull($"private instance method `{methodName}` must exist for branch tests");
        var task = method!.Invoke(instance, args).Should().BeAssignableTo<Task>().Subject;
        await task;
    }

    private static TResult InvokePrivateStatic<TResult>(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull($"private static method `{methodName}` must exist for branch tests");
        var result = method!.Invoke(null, args);
        return (TResult)result!;
    }

    private static TResult InvokePrivateStaticWithArgs<TResult>(Type type, string methodName, object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull($"private static method `{methodName}` must exist for branch tests");
        var result = method!.Invoke(null, args);
        return (TResult)result!;
    }

    private sealed class NoopFlowPort : IScriptEvolutionFlowPort
    {
        public Task<ScriptEvolutionFlowResult> ExecuteAsync(ScriptEvolutionProposal proposal, CancellationToken ct)
        {
            _ = proposal;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ScriptEvolutionFlowResult.PolicyRejected("noop"));
        }

        public Task RollbackAsync(ScriptRollbackRequest request, CancellationToken ct)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetEvolutionManagerActorId() => "script-evolution-manager";

        public string GetEvolutionSessionActorId(string proposalId) => $"script-evolution-session:{proposalId}";

        public string GetCatalogActorId() => "script-catalog";

        public string GetDefinitionActorId(string scriptId) => $"script-definition:{scriptId}";
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<PublishedMessage> Sent { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where TEvent : IMessage
        {
            _ = evt;
            _ = direction;
            _ = sourceEnvelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where TEvent : IMessage
        {
            _ = sourceEnvelope;
            ct.ThrowIfCancellationRequested();
            Sent.Add(new PublishedMessage(targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedMessage(string TargetActorId, IMessage Payload);
}
