using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Routing;
using Aevatar.AI.Core.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public class RoleGAgentReplayContractTests
{
    [Theory]
    [InlineData(0, 1_000, 1_000)]
    [InlineData(-1, 1_000, 1_000)]
    [InlineData(250, 1_000, 250)]
    [InlineData(1_000, 1_000, 1_000)]
    [InlineData(2_000, 1_000, 1_000)]
    public void ResolveLlmTimeoutMs_ShouldEnforceHostCap(
        int requestedTimeoutMs,
        int maxTurnDeadlineMs,
        int expectedTimeoutMs)
    {
        RoleGAgent.ResolveLlmTimeoutMs(requestedTimeoutMs, maxTurnDeadlineMs)
            .Should().Be(expectedTimeoutMs);
    }

    [Fact]
    public async Task HostDeadline_ShouldCancelHangingStreamCommitOneTimeoutAndReleaseNextTurn()
    {
        const string actorId = "role-host-deadline";
        const string timedOutSessionId = "session-timeout";
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        var timeProvider = new ManualDeadlineTimeProvider();
        var provider = new CancellationAwareHangingProviderFactory();
        var agent = CreateAgent(
            services,
            actorId,
            provider,
            timeProvider,
            new RoleChatExecutionOptions(1_000));
        await agent.ActivateAsync();

        var timedOutTurn = agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = timedOutSessionId,
            Prompt = "wait forever",
            TimeoutMs = 0,
        });
        await provider.FirstStreamStarted;
        timeProvider.Advance(TimeSpan.FromMilliseconds(1_000));
        await timedOutTurn;

        var timeout = (await store.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Where(completed => completed.SessionId == timedOutSessionId)
            .Should().ContainSingle().Which;
        timeout.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
        timeout.FailureCode.Should().Be("LLM_TIMEOUT");
        agent.State.Sessions[timedOutSessionId].FailureCode.Should().Be("LLM_TIMEOUT");

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-next",
            Prompt = "continue",
        });

        agent.State.Sessions["session-next"].Completed.Should().BeTrue();
        agent.State.Sessions["session-next"].FinalContent.Should().Be("next turn completed");
    }

    [Fact]
    public async Task PostTurnDeadline_ShouldReleaseActorTurnWhenTerminalPublisherHangs()
    {
        const string actorId = "role-post-turn-publisher-deadline";
        const string sessionId = "session-post-turn-publisher-deadline";
        const int timeoutMs = 1_000;
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        var timeProvider = new ManualDeadlineTimeProvider();
        var provider = new CountingLlmProviderFactory("completed output");
        var agent = CreateAgent(
            services,
            actorId,
            provider,
            timeProvider,
            new RoleChatExecutionOptions(
                maxTurnDeadlineMs: 5_000,
                postTurnProcessingTimeoutMs: timeoutMs));
        var probe = new PostTurnPublicationProbe();
        var publisher = new RecordingEventPublisher
        {
            BeforePublishAsync = (evt, ct) => evt is TextMessageEndEvent
                ? probe.HangIgnoringCancellationAsync()
                : Task.CompletedTask,
        };
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();

        var turn = agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = sessionId,
            Prompt = "finish and publish",
        });
        await probe.Started;
        agent.State.Sessions[sessionId].Completed.Should().BeTrue();

        timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await turn;

        publisher.BeforePublishAsync = null;
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-after-post-turn-publisher-deadline",
            Prompt = "continue",
        });

        provider.StreamCallCount.Should().Be(2);
        agent.State.Sessions["session-after-post-turn-publisher-deadline"].Completed.Should().BeTrue();
    }

    [Fact]
    public async Task PostCommitRefreshDeadline_ShouldPublishCommittedProgressAndNormalizeLateFailureToTimeout()
    {
        const string actorId = "role-post-commit-refresh-deadline";
        const string timedOutSessionId = "session-post-commit-refresh-deadline";
        var store = new InMemoryEventStoreForTests();
        var source = new LateFailingPostCommitToolSource();
        var services = BuildServices(store, collection =>
            collection.AddSingleton<IAgentToolSource>(source));
        var timeProvider = new ManualDeadlineTimeProvider();
        var agent = CreateAgent(
            services,
            actorId,
            new CountingLlmProviderFactory("next turn completed"),
            timeProvider,
            new RoleChatExecutionOptions(
                maxTurnDeadlineMs: 5_000,
                postCommitConfigRefreshTimeoutMs: 1_000,
                postTurnProcessingTimeoutMs: 1_000));
        var committedPublisher = AttachCommittedPublisher(agent);
        await agent.ActivateAsync();
        source.BlockNextDiscovery();

        var timedOutTurn = agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = timedOutSessionId,
            Prompt = "block during committed refresh",
        });
        await source.Started;

        timeProvider.Advance(TimeSpan.FromMilliseconds(1_000));
        await source.CancellationObserved;
        source.ReleaseLateFailure();
        await timedOutTurn;

        committedPublisher.Published.Should().Contain(published =>
            published.StateEvent.EventData.Is(RoleChatSessionStartedEvent.Descriptor) &&
            published.StateEvent.EventData.Unpack<RoleChatSessionStartedEvent>().SessionId ==
            timedOutSessionId);
        var timeout = (await store.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should().ContainSingle(completed => completed.SessionId == timedOutSessionId).Which;
        timeout.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
        timeout.FailureCode.Should().Be("LLM_TIMEOUT");

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-after-post-commit-timeout",
            Prompt = "continue",
        });

        agent.State.Sessions["session-after-post-commit-timeout"].Completed.Should().BeTrue();
        agent.State.Sessions["session-after-post-commit-timeout"].FinalContent
            .Should().Be("next turn completed");
    }

    [Fact]
    public async Task HostDeadline_ShouldRejectProviderCompletionYieldedAfterCancellation()
    {
        const string actorId = "role-host-deadline-late-provider";
        const string sessionId = "session-late-provider";
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        var timeProvider = new ManualDeadlineTimeProvider();
        var provider = new LateCompletionAfterCancellationProviderFactory();
        var agent = CreateAgent(
            services,
            actorId,
            provider,
            timeProvider,
            new RoleChatExecutionOptions(1_000));
        var committedPublisher = AttachCommittedPublisher(agent);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();

        var turn = agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = sessionId,
            Prompt = "ignore the host deadline",
        });
        await provider.StreamStarted;
        timeProvider.Advance(TimeSpan.FromMilliseconds(1_000));
        await provider.CancellationObserved;
        provider.ReleaseLateCompletion();
        await turn;

        var completions = (await store.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Where(completed => completed.SessionId == sessionId)
            .ToArray();
        completions.Should().ContainSingle();
        completions[0].Outcome.Should().Be(RoleChatSessionOutcome.Failed);
        completions[0].FailureCode.Should().Be("LLM_TIMEOUT");
        completions[0].Content.Should().NotContain("late provider completion");
        committedPublisher.Published
            .Where(published => published.StateEvent?.EventData?.Is(RoleChatSessionCompletedEvent.Descriptor) == true)
            .Select(published => published.StateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Where(completed => completed.SessionId == sessionId)
            .Should().ContainSingle(completed => completed.FailureCode == "LLM_TIMEOUT");
        publisher.Published.OfType<TextMessageContentEvent>()
            .Should().NotContain(content => content.Delta.Contains("late provider completion", StringComparison.Ordinal));

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = sessionId,
            Prompt = "ignore the host deadline",
        });

        (await store.GetEventsAsync(actorId))
            .Count(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Should().Be(1);
        provider.StreamCallCount.Should().Be(1);
        agent.State.Sessions[sessionId].FailureCode.Should().Be("LLM_TIMEOUT");
    }

    [Fact]
    public async Task HostDeadline_WhenSuccessCompletionCommitWaitsPastDeadline_ShouldCommitOnlyTypedTimeout()
    {
        const string actorId = "role-post-stream-deadline";
        const string sessionId = "session-post-stream-deadline";
        const int timeoutMs = 1_000;
        var inner = new InMemoryEventStoreForTests();
        var store = new BlockingRoleSuccessCompletionEventStore(inner);
        var services = BuildServices(store);
        var timeProvider = new ManualDeadlineTimeProvider();
        var agent = CreateAgent(
            services,
            actorId,
            new CountingLlmProviderFactory("completed before persistence"),
            timeProvider,
            new RoleChatExecutionOptions(timeoutMs));
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "counting",
            SystemPrompt = "system",
        });

        var turn = agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = sessionId,
            Prompt = "finish then wait on persistence",
        });
        await store.SuccessCompletionAppendStarted;
        timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await store.CancellationObserved;
        await turn;

        var completions = (await inner.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Where(completed => completed.SessionId == sessionId)
            .ToArray();
        completions.Should().ContainSingle();
        completions[0].Outcome.Should().Be(RoleChatSessionOutcome.Failed);
        completions[0].FailureCode.Should().Be("LLM_TIMEOUT");
        completions[0].Content.Should().NotContain("completed before persistence");
        publisher.Published.OfType<TextMessageEndEvent>()
            .Should().ContainSingle(end =>
                end.SessionId == sessionId &&
                end.Content.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HostDeadline_WhenSuccessCommitResultReturnsAfterDeadline_ShouldKeepCommittedSuccess()
    {
        const string actorId = "role-committed-before-deadline-result";
        const string sessionId = "session-committed-before-deadline-result";
        const int timeoutMs = 1_000;
        var inner = new InMemoryEventStoreForTests();
        var store = new LateReturningCommittedRoleSuccessEventStore(inner);
        var services = BuildServices(store);
        var timeProvider = new ManualDeadlineTimeProvider();
        var agent = CreateAgent(
            services,
            actorId,
            new CountingLlmProviderFactory("committed role success"),
            timeProvider,
            new RoleChatExecutionOptions(timeoutMs));
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "counting",
            SystemPrompt = "system",
        });

        var turn = agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = sessionId,
            Prompt = "commit before the deadline result returns",
        });
        await store.SuccessCommitCompleted;
        timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await store.DeadlineObserved;
        await turn;

        var completions = (await inner.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Where(completed => completed.SessionId == sessionId)
            .ToArray();
        completions.Should().ContainSingle();
        completions[0].Outcome.Should().Be(RoleChatSessionOutcome.Completed);
        completions[0].FailureCode.Should().BeEmpty();
        completions[0].Content.Should().Be("committed role success");
        publisher.Published.OfType<TextMessageEndEvent>()
            .Should().ContainSingle(end =>
                end.SessionId == sessionId &&
                end.Content == "committed role success");
    }

    [Fact]
    public async Task NewProfiledSession_ShouldCommitStartedAndInitialAuthorityInOneOrderedBatch()
    {
        var inner = new InMemoryEventStoreForTests();
        var operationLog = new List<string>();
        var store = new RecordingBatchEventStore(inner, operationLog);
        var services = BuildServices(store);
        var agent = CreateProfiledAgent(services, "role-profiled-batch", operationLog: operationLog);
        agent.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-a" };
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-a",
            Prompt = "hello",
        });

        var authorityBatch = store.Batches.Should().ContainSingle(batch =>
                batch.Any(stateEvent => stateEvent.EventData.Is(RoleChatSessionStartedEvent.Descriptor)))
            .Subject;
        authorityBatch.Select(stateEvent => stateEvent.EventData.TypeUrl).Should().Equal(
            Any.Pack(new RoleChatSessionStartedEvent()).TypeUrl,
            Any.Pack(new AgentProfileTurnAuthorityCommittedEvent()).TypeUrl);
        authorityBatch[1].EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>().CommitKind
            .Should().Be(AgentProfileTurnAuthorityCommitKind.Initial);
        agent.State.AgentProfileTurnAuthority.ReconciliationKey.SessionId.Should().Be("session-a");
        operationLog.Should().ContainInOrder("commit:INITIAL:1", "materialize:1");
    }

    [Fact]
    public async Task NewProfiledSession_WhenAuthorityBatchAppendFails_ShouldExposeNeitherFact()
    {
        var inner = new InMemoryEventStoreForTests();
        var store = new FailOnceOnInitialAuthorityBatchEventStore(inner);
        var services = BuildServices(store);
        const string actorId = "role-profiled-batch-fail";
        var agent = CreateProfiledAgent(services, actorId);
        agent.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-a" };
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        var committedPublisher = AttachCommittedPublisher(agent);
        await agent.ActivateAsync();

        var act = () => agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-a",
            Prompt = "hello",
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*authority batch*");
        agent.State.Sessions.Should().NotContainKey("session-a");
        agent.State.AgentProfileTurnAuthority.Should().BeNull();
        (await inner.GetEventsAsync(actorId)).Should().BeEmpty();
        committedPublisher.Published.Should().BeEmpty();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-b",
            Prompt = "next",
        });

        var persisted = await inner.GetEventsAsync(actorId);
        persisted.Should().NotContain(stateEvent => IsSessionFact(stateEvent, "session-a"));
        persisted.Should().Contain(stateEvent => IsSessionFact(stateEvent, "session-b"));
        committedPublisher.Published
            .Should().NotContain(published => IsSessionFact(published.StateEvent, "session-a"));

        var replayed = CreateProfiledAgent(services, actorId);
        await replayed.ActivateAsync();
        replayed.State.Sessions.Should().NotContainKey("session-a");
        replayed.State.Sessions.Should().ContainKey("session-b");
        replayed.State.AgentProfileTurnAuthority.ReconciliationKey.SessionId.Should().Be("session-b");
    }

    [Theory]
    [InlineData(InitialAuthorityMutation.WrongSession)]
    [InlineData(InitialAuthorityMutation.AttemptNotOne)]
    [InlineData(InitialAuthorityMutation.RecoveryWithEmptyCeiling)]
    public async Task NewProfiledSession_WhenInitialAuthorityIsInvalid_ShouldExposeNoFactsFramesOrLlmCall(
        InitialAuthorityMutation initialAuthorityMutation)
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        var provider = new CountingLlmProviderFactory("must not run");
        const string actorId = "role-profiled-invalid-initial";
        var agent = CreateProfiledAgent(
            services,
            actorId,
            providerFactory: provider,
            initialAuthorityMutation: initialAuthorityMutation);
        agent.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-a" };
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        var committedPublisher = AttachCommittedPublisher(agent);
        await agent.ActivateAsync();

        var act = () => agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-a",
            Prompt = "hello",
        });

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Prepared turn authority is not valid for the new session.");
        agent.PrepareCallCount.Should().Be(1);
        agent.MaterializeCallCount.Should().Be(0);
        provider.StreamCallCount.Should().Be(0);
        agent.State.Sessions.Should().NotContainKey("session-a");
        agent.State.AgentProfileTurnAuthority.Should().BeNull();
        (await store.GetEventsAsync(actorId)).Should().BeEmpty();
        committedPublisher.Published.Should().BeEmpty();
        publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAppendFailure_ShouldNotLeakIntoCompletionCommitOrReplay()
    {
        var inner = new InMemoryEventStoreForTests();
        var store = new FailOnceOnReconcileEventStore(inner);
        var services = BuildServices(store);
        const string actorId = "role-profiled-reconcile-fail";
        var agent = CreateProfiledAgent(services, actorId);
        agent.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-a" };
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        var committedPublisher = AttachCommittedPublisher(agent);
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-a",
            Prompt = "hello",
        });

        var persisted = await inner.GetEventsAsync(actorId);
        persisted
            .Where(stateEvent => stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>().CommitKind)
            .Should().Equal(AgentProfileTurnAuthorityCommitKind.Initial);
        persisted.Should().ContainSingle(stateEvent =>
            stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor));
        committedPublisher.Published
            .Where(published => published.StateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor))
            .Select(published => published.StateEvent.EventData
                .Unpack<AgentProfileTurnAuthorityCommittedEvent>().CommitKind)
            .Should().Equal(AgentProfileTurnAuthorityCommitKind.Initial);

        var replayed = CreateProfiledAgent(services, actorId);
        await replayed.ActivateAsync();
        replayed.State.Sessions["session-a"].Completed.Should().BeTrue();
        replayed.State.AgentProfileTurnAuthority.ReconciliationKey.Attempt.Should().Be(1);
    }

    [Fact]
    public async Task InvalidReconcileProposal_ShouldNotAppendCallLlmOrReplaceReplayedAuthority()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        var provider = new CountingLlmProviderFactory("must not run");
        const string actorId = "role-profiled-invalid-reconcile";
        var agent = CreateProfiledAgent(
            services,
            actorId,
            providerFactory: provider,
            reconcileProposalMutation: ReconcileProposalMutation.WidenCeiling);
        agent.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-a" };
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-a",
            Prompt = "hello",
        });

        provider.StreamCallCount.Should().Be(0);
        agent.MaterializeCallCount.Should().Be(1);
        var authorityEvents = (await store.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>())
            .ToArray();
        authorityEvents.Should().ContainSingle();
        authorityEvents[0].CommitKind.Should().Be(AgentProfileTurnAuthorityCommitKind.Initial);
        authorityEvents[0].Authority.AuthorityCeilingToolNames.Should().Equal("recovery", "task");

        var replayed = CreateProfiledAgent(services, actorId);
        await replayed.ActivateAsync();
        replayed.State.AgentProfileTurnAuthority.Should().BeEquivalentTo(authorityEvents[0].Authority);
    }

    [Theory]
    [InlineData(ReconcileProposalMutation.RecoveryWithEmptyCeiling)]
    [InlineData(ReconcileProposalMutation.RestrictedEmptyWithNonEmptyCeiling)]
    public async Task ContradictoryAuthorityKindAndCeiling_ShouldFailCommandPrevalidationAndReplay(
        ReconcileProposalMutation reconcileProposalMutation)
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        var provider = new CountingLlmProviderFactory("must not run");
        const string actorId = "role-profiled-contradictory-authority";
        var agent = CreateProfiledAgent(
            services,
            actorId,
            providerFactory: provider,
            reconcileProposalMutation: reconcileProposalMutation);
        agent.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-a" };
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-a",
            Prompt = "hello",
        });

        provider.StreamCallCount.Should().Be(0);
        var authorityEvents = (await store.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>())
            .ToArray();
        var initial = authorityEvents.Should().ContainSingle().Which;
        initial.CommitKind.Should().Be(AgentProfileTurnAuthorityCommitKind.Initial);

        var replayState = new RoleGAgentState
        {
            AgentProfileTurnAuthority = initial.Authority.Clone(),
            Sessions = { ["session-a"] = new RoleChatSessionState { Sequence = 1 } },
        };
        var malformed = MutateReconcileProposal(initial.Authority, reconcileProposalMutation);
        ApplyTurnAuthority(replayState, new AgentProfileTurnAuthorityCommittedEvent
            {
                CommitKind = AgentProfileTurnAuthorityCommitKind.Reconcile,
                Authority = malformed,
            })
            .Should()
            .BeSameAs(replayState);
    }

    [Fact]
    public async Task IncompleteProfiledSession_ShouldNotCreateRetryAuthorityFence()
    {
        var inner = new InMemoryEventStoreForTests();
        const string actorId = "role-profiled-retry-fail";
        await inner.AppendAsync(
            actorId,
            [
                StateEventFor(actorId, 1, new RoleChatSessionStartedEvent
                {
                    SessionId = "session-a",
                    Prompt = "hello",
                }),
                StateEventFor(actorId, 2, new AgentProfileTurnAuthorityCommittedEvent
                {
                    CommitKind = AgentProfileTurnAuthorityCommitKind.Initial,
                    Authority = TurnAuthority("session-a", 1, "intent-a", "skill-a"),
                }),
            ],
            expectedVersion: 0);
        var store = new FailOnceOnRetryStartedEventStore(inner);
        var services = BuildServices(store);
        var agent = CreateProfiledAgent(services, actorId);
        var committedPublisher = AttachCommittedPublisher(agent);
        await agent.ActivateAsync();
        agent.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-a" };
        var request = new ChatRequestEvent { SessionId = "session-a", Prompt = "hello" };

        await agent.HandleChatRequest(request);
        agent.State.AgentProfileTurnAuthority.ReconciliationKey.Attempt.Should().Be(1);

        await agent.HandleChatRequest(request.Clone());

        var retryEvents = (await inner.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>())
            .Where(authorityEvent => authorityEvent.CommitKind == AgentProfileTurnAuthorityCommitKind.RetryStarted)
            .ToArray();
        retryEvents.Should().BeEmpty();
        committedPublisher.Published
            .Where(published => published.StateEvent.EventData.Is(
                AgentProfileTurnAuthorityCommittedEvent.Descriptor))
            .Select(published => published.StateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>())
            .Should().BeEmpty();
        agent.State.Sessions["session-a"].Completed.Should().BeTrue();
        agent.State.Sessions["session-a"].FailureCode.Should().Be("SESSION_ORPHANED");

        var replayed = CreateProfiledAgent(services, actorId);
        await replayed.ActivateAsync();
        replayed.State.AgentProfileTurnAuthority.ReconciliationKey.Attempt.Should().Be(1);
    }

    [Fact]
    public async Task StartedAuthorityReplay_ShouldFinalizeWithoutMaterializationOrReclassification()
    {
        var store = new InMemoryEventStoreForTests();
        const string actorId = "role-profiled-replay";
        await store.AppendAsync(
            actorId,
            [
                StateEventFor(actorId, 1, new RoleChatSessionStartedEvent
                {
                    SessionId = "session-a",
                    Prompt = "hello",
                }),
                StateEventFor(actorId, 2, new AgentProfileTurnAuthorityCommittedEvent
                {
                    CommitKind = AgentProfileTurnAuthorityCommitKind.Initial,
                    Authority = TurnAuthority("session-a", 1, "intent-frozen", "skill-frozen"),
                }),
            ],
            expectedVersion: 0);
        var services = BuildServices(store);
        var agent = CreateProfiledAgent(services, actorId);
        await agent.ActivateAsync();
        agent.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-a" };

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-a",
            Prompt = "hello",
        });

        agent.PrepareCallCount.Should().Be(0);
        agent.MaterializeCallCount.Should().Be(0);
        agent.MaterializedAuthorities.Should().BeEmpty();
        agent.State.Sessions["session-a"].FailureCode.Should().Be("SESSION_ORPHANED");

        const string recoveryActorId = "role-profiled-replay-recovery";
        var recoveryAuthority = TurnAuthority("session-recovery", 1, "intent-recovery", "skill-unused");
        recoveryAuthority.SelectedExactSkillRef = null;
        recoveryAuthority.AuthorityKind = AgentProfileTurnAuthorityKind.Recovery;
        await store.AppendAsync(
            recoveryActorId,
            [
                StateEventFor(recoveryActorId, 1, new RoleChatSessionStartedEvent
                {
                    SessionId = "session-recovery",
                    Prompt = "recover",
                }),
                StateEventFor(recoveryActorId, 2, new AgentProfileTurnAuthorityCommittedEvent
                {
                    CommitKind = AgentProfileTurnAuthorityCommitKind.Initial,
                    Authority = recoveryAuthority,
                }),
            ],
            expectedVersion: 0);
        var recoveryAgent = CreateProfiledAgent(services, recoveryActorId);
        await recoveryAgent.ActivateAsync();
        recoveryAgent.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-a" };
        await recoveryAgent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-recovery",
            Prompt = "recover",
        });
        recoveryAgent.PrepareCallCount.Should().Be(0);
        recoveryAgent.MaterializedAuthorities.Should().BeEmpty();
        recoveryAgent.State.Sessions["session-recovery"].FailureCode.Should().Be("SESSION_ORPHANED");
        (await store.GetEventsAsync(recoveryActorId))
            .Where(stateEvent => stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>().CommitKind)
            .Should().NotContain(AgentProfileTurnAuthorityCommitKind.RetryStarted);
    }

    [Fact]
    public async Task RetryReplay_ShouldFinalizeWithoutExactIoAndRejectLateResult()
    {
        var inner = new InMemoryEventStoreForTests();
        var operationLog = new List<string>();
        var store = new RecordingBatchEventStore(inner, operationLog);
        const string actorId = "role-profiled-retry";
        await inner.AppendAsync(
            actorId,
            [
                StateEventFor(actorId, 1, new RoleChatSessionStartedEvent
                {
                    SessionId = "session-a",
                    Prompt = "hello",
                }),
                StateEventFor(actorId, 2, new AgentProfileTurnAuthorityCommittedEvent
                {
                    CommitKind = AgentProfileTurnAuthorityCommitKind.Initial,
                    Authority = TurnAuthority("session-a", 1, "intent-a", "skill-a"),
                }),
            ],
            expectedVersion: 0);
        var services = BuildServices(store);
        var agent = CreateProfiledAgent(services, actorId, operationLog: operationLog);
        await agent.ActivateAsync();
        agent.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-a" };

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-a",
            Prompt = "hello",
        });

        agent.MaterializedAuthorities.Should().BeEmpty();
        store.Batches.SelectMany(static batch => batch)
            .Where(stateEvent => stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>())
            .Should().NotContain(authorityEvent =>
                authorityEvent.CommitKind == AgentProfileTurnAuthorityCommitKind.RetryStarted);
        operationLog.Should().NotContain(entry => entry.Contains("materialize", StringComparison.Ordinal));

        var stateBeforeLateResult = agent.State;
        var late = new AgentProfileTurnAuthorityCommittedEvent
        {
            CommitKind = AgentProfileTurnAuthorityCommitKind.Reconcile,
            Authority = TurnAuthority("session-a", 1, "intent-a", "skill-a"),
        };
        ApplyTurnAuthority(stateBeforeLateResult, late).Should().BeSameAs(stateBeforeLateResult);
    }

    [Fact]
    public async Task LegacyIncompleteSessionWithoutAuthority_ShouldFinalizeWithoutInventingAuthority()
    {
        var store = new InMemoryEventStoreForTests();
        const string actorId = "role-profiled-legacy";
        await store.AppendAsync(
            actorId,
            [StateEventFor(actorId, 1, new RoleChatSessionStartedEvent
            {
                SessionId = "session-legacy",
                Prompt = "hello",
            })],
            expectedVersion: 0);
        var services = BuildServices(store);
        var agent = CreateProfiledAgent(services, actorId);
        await agent.ActivateAsync();
        agent.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-a" };
        var request = new ChatRequestEvent { SessionId = "session-legacy", Prompt = "hello" };

        await agent.HandleChatRequest(request);
        await agent.HandleChatRequest(request.Clone());

        agent.PrepareCallCount.Should().Be(0);
        var legacyEvents = (await store.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>())
            .Where(authorityEvent => authorityEvent.CommitKind == AgentProfileTurnAuthorityCommitKind.Initial)
            .ToArray();
        legacyEvents.Should().BeEmpty();
        agent.State.Sessions["session-legacy"].Completed.Should().BeTrue();
        agent.State.Sessions["session-legacy"].FailureCode.Should().Be("SESSION_ORPHANED");
    }

    [Fact]
    public async Task LegacyIncompleteSessionWithActiveAuthority_ShouldFinalizeWithoutReplacingAuthority()
    {
        var store = new InMemoryEventStoreForTests();
        const string actorId = "role-profiled-legacy-replacement";
        await store.AppendAsync(
            actorId,
            [
                StateEventFor(actorId, 1, new RoleChatSessionStartedEvent
                {
                    SessionId = "session-active",
                    Prompt = "active",
                }),
                StateEventFor(actorId, 2, new AgentProfileTurnAuthorityCommittedEvent
                {
                    CommitKind = AgentProfileTurnAuthorityCommitKind.Initial,
                    Authority = TurnAuthority("session-active", 1, "intent-active", "skill-active"),
                }),
                StateEventFor(actorId, 3, new RoleChatSessionStartedEvent
                {
                    SessionId = "session-legacy",
                    Prompt = "legacy",
                }),
            ],
            expectedVersion: 0);
        var services = BuildServices(store);
        var agent = CreateProfiledAgent(services, actorId);
        await agent.ActivateAsync();
        agent.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-a" };

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-legacy",
            Prompt = "legacy",
        });

        agent.PrepareCallCount.Should().Be(0);
        var persisted = await store.GetEventsAsync(actorId);
        persisted
            .Where(stateEvent => stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>())
            .Should().ContainSingle(authorityEvent =>
                authorityEvent.CommitKind == AgentProfileTurnAuthorityCommitKind.Initial &&
                authorityEvent.Authority.ReconciliationKey.SessionId == "session-active");

        var replayed = CreateProfiledAgent(services, actorId);
        await replayed.ActivateAsync();
        replayed.State.Sessions["session-active"].Sequence.Should().Be(1);
        replayed.State.Sessions["session-active"].Completed.Should().BeFalse();
        replayed.State.Sessions["session-legacy"].Sequence.Should().Be(2);
        replayed.State.Sessions["session-legacy"].Completed.Should().BeTrue();
        replayed.State.AgentProfileTurnAuthority.ReconciliationKey.SessionId.Should().Be("session-active");
    }

    [Fact]
    public async Task ParallelActorsAndSessions_ShouldKeepTurnAuthorityIsolated()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        var agentA = CreateProfiledAgent(services, "role-profiled-a", "intent-a", "skill-a");
        var agentB = CreateProfiledAgent(services, "role-profiled-b", "intent-b", "skill-b");
        agentA.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-a" };
        agentB.State.AgentProfile = new AgentProfileSnapshot { ProfileId = "profile-b" };
        await agentA.ActivateAsync();
        await agentB.ActivateAsync();

        await Task.WhenAll(
            agentA.HandleChatRequest(new ChatRequestEvent { SessionId = "session-a", Prompt = "a" }),
            agentB.HandleChatRequest(new ChatRequestEvent { SessionId = "session-b", Prompt = "b" }));

        agentA.State.AgentProfileTurnAuthority.ReconciliationKey.SessionId.Should().Be("session-a");
        agentA.State.AgentProfileTurnAuthority.CandidateRoute.IntentId.Should().Be("intent-a");
        agentA.State.AgentProfileTurnAuthority.SelectedExactSkillRef.Guid.Should().Be("skill-a");
        agentB.State.AgentProfileTurnAuthority.ReconciliationKey.SessionId.Should().Be("session-b");
        agentB.State.AgentProfileTurnAuthority.CandidateRoute.IntentId.Should().Be("intent-b");
        agentB.State.AgentProfileTurnAuthority.SelectedExactSkillRef.Guid.Should().Be("skill-b");
        (await store.GetEventsAsync("role-profiled-a")).Should().OnlyContain(stateEvent =>
            stateEvent.AgentId == "role-profiled-a");
        (await store.GetEventsAsync("role-profiled-b")).Should().OnlyContain(stateEvent =>
            stateEvent.AgentId == "role-profiled-b");
    }

    [Fact]
    public async Task InitializeRoleEvent_ShouldPersistAndReplayRoleState()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-init-replay");
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "role-researcher",
            RoleName = "researcher",
            ProviderName = "mock",
            Model = "m1",
            SystemPrompt = "be helpful",
            MaxToolRounds = 4,
            MaxHistoryMessages = 32,
        });
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync("role-init-replay");
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(InitializeRoleAgentEvent), StringComparison.Ordinal));

        var agent2 = CreateAgent(services, "role-init-replay");
        await agent2.ActivateAsync();

        agent2.RoleId.Should().Be("role-researcher");
        agent2.State.RoleId.Should().Be("role-researcher");
        agent2.RoleName.Should().Be("researcher");
        agent2.State.RoleName.Should().Be("researcher");
        agent2.EffectiveConfig.ProviderName.Should().Be("mock");
        agent2.EffectiveConfig.Model.Should().Be("m1");
        agent2.EffectiveConfig.SystemPrompt.Should().Be("be helpful");
        agent2.EffectiveConfig.MaxToolRounds.Should().Be(4);
        agent2.EffectiveConfig.MaxHistoryMessages.Should().Be(32);
    }

    [Fact]
    public async Task InitializeRoleEvent_ShouldPreserveExplicitZeroTemperature()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent = CreateAgent(services, "role-temperature-zero");
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            SystemPrompt = "system",
            Temperature = 0,
        });

        agent.EffectiveConfig.Temperature.Should().Be(0);

        var persisted = await store.GetEventsAsync("role-temperature-zero");
        persisted.Should().ContainSingle();
        var evt = persisted.Single().EventData.Unpack<InitializeRoleAgentEvent>();
        evt.HasTemperature.Should().BeTrue();
        evt.Temperature.Should().Be(0);
    }

    [Fact]
    public async Task RoleGAgentFactory_ShouldUseEventSourcedInitializePath()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-factory-replay");
        await agent1.ActivateAsync();
        await RoleGAgentFactory.ApplyInitialization(agent1, new RoleYamlConfig
        {
            Name = "assistant",
            Provider = "mock",
            SystemPrompt = "system",
        }, services);
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync("role-factory-replay");
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(InitializeRoleAgentEvent), StringComparison.Ordinal));

        var agent2 = CreateAgent(services, "role-factory-replay");
        await agent2.ActivateAsync();
        agent2.State.RoleName.Should().Be("assistant");
        agent2.RoleName.Should().Be("assistant");
    }

    [Fact]
    public async Task RoutedModules_ShouldReplayAfterReactivate_WithoutReapplyingOnSessionStateChanges()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("module replay");
        var moduleFactory = new CountingEventModuleFactory();
        var services = BuildServices(store, services =>
        {
            services.AddSingleton<IEventModuleFactory<IEventHandlerContext>>(moduleFactory);
        });

        var agent1 = CreateAgent(services, "role-module-replay", provider);
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
            EventModules = "routable,bypass",
            EventRoutes = "event.type == ChatRequestEvent -> routable",
        });

        agent1.State.EventModules.Should().Be("routable,bypass");
        agent1.State.EventRoutes.Should().Be("event.type == ChatRequestEvent -> routable");
        agent1.GetModules().Should().HaveCount(2);
        agent1.GetModules().Should().ContainSingle(m => m.Name == "routable" && m is RoutedEventModule);
        agent1.GetModules().Should().ContainSingle(m => m.Name == "bypass" && m is CountingBypassModule);
        moduleFactory.TryCreateCallCount.Should().Be(2);

        await agent1.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-module-replay",
        });

        moduleFactory.TryCreateCallCount.Should().Be(2);
        await agent1.DeactivateAsync();

        var agent2 = CreateAgent(services, "role-module-replay", provider);
        await agent2.ActivateAsync();

        agent2.State.EventModules.Should().Be("routable,bypass");
        agent2.State.EventRoutes.Should().Be("event.type == ChatRequestEvent -> routable");
        agent2.GetModules().Should().HaveCount(2);
        agent2.GetModules().Should().ContainSingle(m => m.Name == "routable" && m is RoutedEventModule);
        agent2.GetModules().Should().ContainSingle(m => m.Name == "bypass" && m is CountingBypassModule);
        moduleFactory.TryCreateCallCount.Should().Be(4);
    }

    [Fact]
    public async Task InitializeRoleEvent_ShouldInitializeLifecycleModulesAppliedAfterActivation()
    {
        var store = new InMemoryEventStoreForTests();
        var moduleFactory = new CountingEventModuleFactory();
        var services = BuildServices(store, services =>
        {
            services.AddSingleton<IEventModuleFactory<IEventHandlerContext>>(moduleFactory);
        });

        var agent = CreateAgent(services, "role-lifecycle-module");
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            SystemPrompt = "system",
            EventModules = "lifecycle",
        });

        var module = agent.GetModules().OfType<CountingLifecycleModule>().Single();
        module.InitializeCallCount.Should().Be(1);
        module.DisposeCallCount.Should().Be(0);

        await agent.DeactivateAsync();

        module.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task CompletedSession_ShouldReplayCachedCompletionWithoutCallingProviderAgain()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("cached answer");
        var services = BuildServices(store);

        var terminalPublisher = new RecordingEventPublisher();
        var agent1 = CreateAgent(services, "role-session-replay", provider);
        agent1.EventPublisher = terminalPublisher;
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "role-assistant",
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });
        await agent1.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
        });
        await agent1.DeactivateAsync();

        provider.StreamCallCount.Should().Be(1);
        provider.StreamRequests.Should().ContainSingle();
        provider.StreamRequests[0].RequestId.Should().Be("session-1");
        var persisted = await store.GetEventsAsync("role-session-replay");
        persisted.Should().Contain(x => x.EventType.Contains(nameof(RoleChatSessionStartedEvent), StringComparison.Ordinal));
        persisted.Should().Contain(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal));
        persisted
            .Single(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal))
            .EventData
            .Unpack<RoleChatSessionCompletedEvent>()
            .RoleId
            .Should()
            .Be("role-assistant");

        var agent2 = CreateAgent(services, "role-session-replay", provider);
        agent2.EventPublisher = terminalPublisher;
        await agent2.ActivateAsync();

        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
        });

        provider.StreamCallCount.Should().Be(1);
        terminalPublisher.Published
            .OfType<TextMessageStartEvent>()
            .Should()
            .HaveCount(2)
            .And.OnlyContain(x => x.SessionId == "session-1");
        terminalPublisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .HaveCount(2)
            .And.OnlyContain(x => x.Delta == "cached answer" && x.SessionId == "session-1");
        terminalPublisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .HaveCount(2)
            .And.OnlyContain(x => x.Content == "cached answer" && x.SessionId == "session-1");

        var replayedEvents = await store.GetEventsAsync("role-session-replay");
        var completions = replayedEvents
            .Where(x => x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .ToArray();
        completions.Should()
            .ContainSingle(x =>
                x.SessionId == "session-1" &&
                x.Prompt == "hello" &&
                x.Content == "cached answer");
        completions[0].TerminalTime.Should().NotBeNull();
        var replay = replayedEvents
            .Where(x => x.EventData.Is(RoleChatSessionProgressedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<RoleChatSessionProgressedEvent>())
            .Should()
            .ContainSingle(progress =>
                progress.SessionId == "session-1" &&
                progress.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Replay)
            .Which;
        replay.Replay.Snapshot.TerminalTime.Should().Be(completions[0].TerminalTime);
        replay.Replay.Snapshot.Content.Should().Be("cached answer");
        agent2.State.Sessions["session-1"].TerminalTime.Should().Be(completions[0].TerminalTime);
    }

    [Fact]
    public async Task Completion_ShouldEmbedTerminalTailInOneCommittedFact()
    {
        var store = new RecordingTerminalBatchEventStore();
        var services = BuildServices(store);
        var provider = new CountingLlmProviderFactory("atomic answer");
        var agent = CreateAgent(services, "role-atomic-terminal", provider);
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "turn-atomic-terminal",
        });

        var terminalBatch = store.Appends.Should().ContainSingle(batch =>
            batch.Any(evt => evt.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))).Which;
        var completion = terminalBatch.Should().ContainSingle().Which.EventData
            .Unpack<RoleChatSessionCompletedEvent>();
        var progress = completion.TerminalProgress.ToArray();
        progress.Select(evt => evt.PayloadCase).Should().Equal(
            RoleChatSessionProgressedEvent.PayloadOneofCase.Usage,
            RoleChatSessionProgressedEvent.PayloadOneofCase.TextEnded,
            RoleChatSessionProgressedEvent.PayloadOneofCase.Terminal);
        var terminalSequences = progress.Select(evt => evt.Sequence).ToArray();
        terminalSequences.Should().Equal(
            Enumerable.Range(0, terminalSequences.Length)
                .Select(offset => terminalSequences[0] + offset));
        agent.State.Sessions[completion.SessionId].LastProgressSequence.Should().Be(terminalSequences[^1]);
    }

    [Fact]
    public async Task StreamingChat_ShouldBoundCommittedProgressForProviderTokenFragments()
    {
        const string actorId = "role-bounded-stream-progress";
        const string sessionId = "turn-bounded-stream-progress";
        const int textChunkCount = 4_097;
        const int reasoningChunkCount = 2_049;
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        var provider = new FragmentedLlmProviderFactory(textChunkCount, reasoningChunkCount);
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, actorId, provider);
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "stream many token fragments",
            SessionId = sessionId,
        });

        var progress = (await store.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionProgressedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionProgressedEvent>())
            .ToArray();
        var textDeltas = progress
            .Where(evt => evt.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.TextDelta)
            .Select(evt => evt.TextDelta.Delta)
            .ToArray();
        var reasoningDeltas = progress
            .Where(evt => evt.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.ReasoningDelta)
            .Select(evt => evt.ReasoningDelta.Delta)
            .ToArray();

        textDeltas.Should().HaveCount(5);
        reasoningDeltas.Should().HaveCount(3);
        string.Concat(textDeltas).Should().Be(new string('t', textChunkCount));
        string.Concat(reasoningDeltas).Should().Be(new string('r', reasoningChunkCount));
        publisher.Published.OfType<TextMessageContentEvent>()
            .Select(evt => evt.Delta)
            .Should().Equal(textDeltas);
        publisher.Published.OfType<TextMessageReasoningEvent>()
            .Select(evt => evt.Delta)
            .Should().Equal(reasoningDeltas);
        agent.State.Sessions[sessionId].FinalContent.Should().Be(new string('t', textChunkCount));
        agent.State.Sessions[sessionId].FinalReasoningContent.Should().Be(new string('r', reasoningChunkCount));
    }

    [Fact]
    public async Task StreamingChat_ShouldFlushSmallDeltasAtInteractionCadence()
    {
        const string actorId = "role-paced-stream-progress";
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        var timeProvider = new ManualDeadlineTimeProvider();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            services,
            actorId,
            new PacedLlmProviderFactory(timeProvider),
            timeProvider);
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "stream paced fragments",
            SessionId = "turn-paced-stream-progress",
        });

        publisher.Published.OfType<TextMessageContentEvent>()
            .Select(evt => evt.Delta)
            .Should().Equal("a", "b", "c");
        (await store.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionProgressedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionProgressedEvent>())
            .Where(progress => progress.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.TextDelta)
            .Select(progress => progress.TextDelta.Delta)
            .Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task CompletedConversationHistory_ShouldBeRestoredAfterActorReactivation()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("answer");
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-history-reactivation", provider);
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });
        await agent1.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "first prompt",
            SessionId = "turn-first",
        });
        await agent1.DeactivateAsync();

        var agent2 = CreateAgent(services, "role-history-reactivation", provider);
        await agent2.ActivateAsync();
        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "second prompt",
            SessionId = "turn-second",
        });

        provider.StreamRequests.Should().HaveCount(2);
        provider.StreamRequests[1].Messages
            .Where(static message => message.Role != "system")
            .Select(static message => (message.Role, message.Content))
            .Should()
            .ContainInOrder(
                ("user", "first prompt"),
                ("assistant", "answer"),
                ("user", "second prompt"));
    }

    [Fact]
    public async Task CompletedSession_WithDifferentPrompt_ShouldCommitTypedConflictWithoutOverwritingReplay()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("first answer");
        var services = BuildServices(store);
        var agent = CreateAgent(services, "role-session-conflict", provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "first prompt",
            SessionId = "turn-client-request-1",
            CommandAttemptId = "cmd-attempt-original",
        });
        var completedProgressSequence = agent.State.Sessions["turn-client-request-1"].LastProgressSequence;

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "different prompt",
            SessionId = "turn-client-request-1",
            CommandAttemptId = "cmd-attempt-rejected",
        });

        provider.StreamCallCount.Should().Be(1);
        agent.State.Sessions["turn-client-request-1"].Prompt.Should().Be("first prompt");
        agent.State.Sessions["turn-client-request-1"].FinalContent.Should().Be("first answer");
        var persisted = await store.GetEventsAsync("role-session-conflict");
        var conflict = persisted
            .Single(x => x.EventType.Contains(nameof(RoleChatCommandAttemptRejectedEvent), StringComparison.Ordinal))
            .EventData
            .Unpack<RoleChatCommandAttemptRejectedEvent>();
        conflict.RequestedSessionId.Should().Be("turn-client-request-1");
        conflict.CommandAttemptId.Should().Be("cmd-attempt-rejected");
        conflict.Reason.Should().Be(RoleChatCommandAttemptRejectionReason.PromptMismatch);
        conflict.SafeMessage.Should().NotContain("first prompt").And.NotContain("different prompt");
        persisted
            .Where(x => x.EventData.Is(RoleChatSessionProgressedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<RoleChatSessionProgressedEvent>())
            .Should()
            .NotContain(progress =>
                progress.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Terminal &&
                progress.Terminal.FailureCode == "IDEMPOTENCY_CONFLICT");
        agent.State.Sessions["turn-client-request-1"].LastProgressSequence
            .Should().Be(completedProgressSequence);
    }

    [Fact]
    public async Task HandleChatRequest_WhenHandlerFailsOutsideProviderStream_ShouldCommitSafeTypedFailure()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("unused answer");
        var services = BuildServices(store);
        var agent = CreateAgent(services, "role-handler-failure", provider);
        agent.EventPublisher = new ThrowOnceEventPublisher(
            static evt => evt is TextMessageStartEvent,
            new InvalidOperationException("bearer-secret should never leave the actor"));
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "turn-handler-failure",
        });

        provider.StreamCallCount.Should().Be(0);
        var persisted = await store.GetEventsAsync("role-handler-failure");
        var completed = persisted
            .Where(evt => evt.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(evt => evt.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should()
            .ContainSingle()
            .Which;
        completed.SessionId.Should().Be("turn-handler-failure");
        completed.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
        completed.FailureCode.Should().Be("CHAT_HANDLER_FAILURE");
        completed.SafeMessage.Should().Be("The chat request failed. Please try again.");
        completed.ToString().Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task AuthorizationRequiredReceipt_ShouldBlockOnlyCurrentTurn_AndAdmitNextTurn()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new AuthorizationThenSuccessLlmProviderFactory();
        var tool = new AuthorizationRequiredTool();
        var services = BuildServices(store, collection =>
            collection.AddSingleton<IAgentToolSource>(
                new StaticToolSource([tool])));
        var agent = CreateExplicitToolAgent(
            services,
            "role-authorization-blocker",
            provider,
            [tool]);
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "read private resource",
            SessionId = "turn-blocked",
        });
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "ordinary follow-up",
            SessionId = "turn-next",
        });

        provider.StreamCallCount.Should().Be(2);
        var blockedDiagnostics = agent.State.Sessions["turn-blocked"].ToString();
        agent.State.Sessions["turn-blocked"].Outcome.Should().Be(
            RoleChatSessionOutcome.Blocked,
            "blocked session was {0}",
            blockedDiagnostics);
        agent.State.Sessions["turn-next"].Outcome.Should().Be(RoleChatSessionOutcome.Completed);
        var completions = (await store.GetEventsAsync("role-authorization-blocker"))
            .Where(evt => evt.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(evt => evt.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .ToArray();
        var blocked = completions.Should().ContainSingle(evt => evt.SessionId == "turn-blocked").Which;
        blocked.Outcome.Should().Be(RoleChatSessionOutcome.Blocked);
        blocked.AuthorizationRequired.ServiceSlug.Should().Be("api-github");
        blocked.AuthorizationRequired.ReasonCode.Should().Be("NYXID_UNAUTHORIZED");
        blocked.AuthorizationRequired.SafeMessage.Should().Be("Connect or reauthorize api-github to continue.");
        blocked.ToString().Should().NotContain("bearer-secret").And.NotContain("credential");
        completions.Should().ContainSingle(evt =>
            evt.SessionId == "turn-next" && evt.Outcome == RoleChatSessionOutcome.Completed);
    }

    [Fact]
    public async Task CompletionNotification_ShouldReplayCommittedTerminalFactAfterRestart()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("completed output");
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var services = BuildServices(store, collection =>
            collection.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        var failingPublisher = new RecordingEventPublisher { FailSends = true };
        var first = CreateAgent(services, "role-terminal-replay", provider);
        first.EventPublisher = failingPublisher;
        await first.ActivateAsync();
        await first.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "role-1",
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        var request = new ChatRequestEvent
        {
            Prompt = "complete work",
            SessionId = "session-1",
            RunContext = new RoleChatRunContext
            {
                RunId = "run-1",
                CommandId = "cmd-1",
                CorrelationId = "corr-1",
                CompletionNotificationActorId = "service-run:tenant:svc:run-1",
            },
        };
        await first.HandleChatRequest(request);
        first.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.RetryScheduled);
        scheduler.TimeoutRequests.Should().ContainSingle();
        var committed = (await store.GetEventsAsync("role-terminal-replay"))
            .Single(x => x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .EventData
            .Unpack<RoleChatSessionCompletedEvent>();
        committed.ActorId.Should().Be("role-terminal-replay");
        committed.RunContext.Should().BeEquivalentTo(request.RunContext);
        committed.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
        committed.Content.Should().Be("completed output");
        committed.TerminalTime.Should().NotBeNull();

        var recoveredPublisher = new RecordingEventPublisher();
        var recovered = CreateAgent(services, "role-terminal-replay", provider);
        recovered.EventPublisher = recoveredPublisher;

        await recovered.ActivateAsync();

        provider.StreamCallCount.Should().Be(1);
        var sent = recoveredPublisher.Sends.Should().ContainSingle().Subject;
        sent.TargetActorId.Should().Be("service-run:tenant:svc:run-1");
        sent.Options!.Delivery!.OperationId.Should()
            .Be("role-chat-terminal:run-1:cmd-1:outcome:1");
        var notification = sent.Event.Should().BeOfType<RoleChatSessionCompletedEvent>().Which;
        var expectedNotification = committed.Clone();
        expectedNotification.TerminalProgress.Clear();
        notification.Should().BeEquivalentTo(expectedNotification);
        notification.TerminalProgress.Should().BeEmpty(
            "actor-to-actor completion notification carries final authority, not AGUI presentation tail");
        recovered.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Dispatched);
    }

    [Fact]
    public async Task Activation_WhenPendingCompletionDeliveryFails_ShouldStillRequestIncompleteFinalization()
    {
        var innerStore = new InMemoryEventStoreForTests();
        const string actorId = "role-activation-delivery-isolation";
        await innerStore.AppendAsync(
            actorId,
            [
                StateEventFor(actorId, 1, new RoleChatSessionStartedEvent
                {
                    SessionId = "terminal-session",
                    Prompt = "already finished",
                    RunContext = new RoleChatRunContext
                    {
                        RunId = "run-terminal",
                        CommandId = "command-terminal",
                        CompletionNotificationActorId = "service-run:scope:service:run-terminal",
                    },
                }),
                StateEventFor(actorId, 2, new RoleChatSessionCompletedEvent
                {
                    SessionId = "terminal-session",
                    Prompt = "already finished",
                    Outcome = RoleChatSessionOutcome.Completed,
                    RunContext = new RoleChatRunContext
                    {
                        RunId = "run-terminal",
                        CommandId = "command-terminal",
                        CompletionNotificationActorId = "service-run:scope:service:run-terminal",
                    },
                }),
                StateEventFor(actorId, 3, new RoleChatSessionStartedEvent
                {
                    SessionId = "incomplete-session",
                    Prompt = "recover me",
                }),
            ],
            expectedVersion: 0);
        var store = new FailOnCompletionNotificationDispatchedEventStore(innerStore);
        var services = BuildServices(store);
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, actorId);
        agent.EventPublisher = publisher;

        await agent.ActivateAsync();

        publisher.Sends.Should().ContainSingle(send => send.Event is RoleChatSessionCompletedEvent);
        publisher.Published.OfType<RoleChatIncompleteSessionFinalizationRequested>()
            .Should().ContainSingle()
            .Which.SessionId.Should().Be("incomplete-session");
    }

    [Fact]
    public async Task ActivationSignal_StartedOnlySession_ShouldCommitObservableOrphanedFailure()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("must not run");
        var services = BuildServices(store);
        const string actorId = "role-session-orphaned";
        await store.AppendAsync(
            actorId,
            [
                StateEventFor(
                    actorId,
                    1,
                    new RoleChatSessionStartedEvent
                    {
                        SessionId = "session-2",
                        Prompt = "hello again",
                        RunContext = new RoleChatRunContext
                        {
                            RunId = "run-2",
                            CommandId = "command-2",
                            CorrelationId = "correlation-2",
                            CompletionNotificationActorId = "service-run:scope:service:run-2",
                        },
                    }),
            ],
            expectedVersion: 0);

        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, actorId, provider);
        agent.EventPublisher = publisher;
        var committedPublisher = AttachCommittedPublisher(agent);
        await agent.ActivateAsync();

        var signalPublication = publisher.Publications.Should().ContainSingle(publication =>
                publication.Event is RoleChatIncompleteSessionFinalizationRequested)
            .Which;
        signalPublication.Audience.Should().Be(TopologyAudience.Self);
        var signal = signalPublication.Event
            .Should().BeOfType<RoleChatIncompleteSessionFinalizationRequested>().Which;
        signal.SessionId.Should().Be("session-2");
        signal.ExpectedLastProgressSequence.Should().Be(0);
        agent.State.Sessions["session-2"].Completed.Should().BeFalse(
            "activation only dispatches an actor continuation");
        (await store.GetEventsAsync(actorId)).Should().NotContain(stateEvent =>
            stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor));

        await agent.HandleIncompleteSessionFinalizationRequestedAsync(signal);

        provider.StreamCallCount.Should().Be(0);
        var terminalState = agent.State.Sessions["session-2"];
        terminalState.Completed.Should().BeTrue();
        terminalState.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
        terminalState.FailureCode.Should().Be("SESSION_ORPHANED");
        var completion = (await store.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should().ContainSingle().Which;
        completion.TerminalProgress.Should().ContainSingle(progress =>
            progress.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Terminal &&
            progress.Terminal.Outcome == RoleChatSessionOutcome.Failed &&
            progress.Terminal.FailureCode == "SESSION_ORPHANED");
        committedPublisher.Published.Should().Contain(published =>
            published.StateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor));
        var notification = publisher.Sends.Should().ContainSingle().Which.Event
            .Should().BeOfType<RoleChatSessionCompletedEvent>().Which;
        notification.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
        notification.FailureCode.Should().Be("SESSION_ORPHANED");
    }

    [Theory]
    [InlineData("model-streaming")]
    [InlineData("tool-started")]
    [InlineData("tool-completed")]
    public async Task ActivationSignal_ProgressedSession_ShouldCommitOutcomeUncertainWithoutExecution(
        string progressStage)
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("must not run");
        var services = BuildServices(store);
        var actorId = $"role-session-uncertain-{progressStage}";
        var progress = new RoleChatSessionProgressedEvent
        {
            SessionId = "session-1",
            Sequence = 1,
        };
        switch (progressStage)
        {
            case "model-streaming":
                progress.TextDelta = new RoleChatTextDeltaProgress { Delta = "partial" };
                break;
            case "tool-started":
                progress.ToolStarted = new RoleChatToolStartedProgress
                {
                    CallId = "call-1",
                    ToolName = "side_effecting_tool",
                };
                break;
            case "tool-completed":
                progress.ToolCompleted = new RoleChatToolCompletedProgress
                {
                    ToolName = "side_effecting_tool",
                    Result = new ToolResultEvent
                    {
                        CallId = "call-1",
                        ResultJson = "{\"ok\":true}",
                        Success = true,
                    },
                };
                break;
            default:
                throw new InvalidOperationException($"Unsupported progress stage: {progressStage}");
        }
        await store.AppendAsync(
            actorId,
            [
                StateEventFor(actorId, 1, new RoleChatSessionStartedEvent
                {
                    SessionId = "session-1",
                    Prompt = "perform work",
                }),
                StateEventFor(actorId, 2, progress),
            ],
            expectedVersion: 0);
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, actorId, provider);
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        var signal = publisher.Published
            .OfType<RoleChatIncompleteSessionFinalizationRequested>()
            .Should().ContainSingle().Which;

        await agent.HandleIncompleteSessionFinalizationRequestedAsync(signal);

        provider.StreamCallCount.Should().Be(0);
        agent.State.Sessions["session-1"].Outcome.Should().Be(RoleChatSessionOutcome.OutcomeUncertain);
        agent.State.Sessions["session-1"].FailureCode.Should().Be("SESSION_OUTCOME_UNCERTAIN");
        var completion = (await store.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should().ContainSingle().Which;
        completion.Outcome.Should().Be(RoleChatSessionOutcome.OutcomeUncertain);
        completion.SafeMessage.Should().Contain("outcome could not be confirmed");
        completion.TerminalProgress.Should().ContainSingle(progress =>
            progress.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Terminal &&
            progress.Terminal.Outcome == RoleChatSessionOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task IncompleteSessionSignal_ShouldIgnoreStaleAndDuplicateDelivery()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        const string actorId = "role-session-signal-fence";
        await store.AppendAsync(
            actorId,
            [
                StateEventFor(actorId, 1, new RoleChatSessionStartedEvent
                {
                    SessionId = "session-1",
                    Prompt = "hello",
                }),
                StateEventFor(actorId, 2, new RoleChatSessionProgressedEvent
                {
                    SessionId = "session-1",
                    Sequence = 1,
                    TextStarted = new RoleChatTextStartedProgress { AgentId = actorId },
                }),
            ],
            expectedVersion: 0);
        var agent = CreateAgent(services, actorId, new CountingLlmProviderFactory("must not run"));
        await agent.ActivateAsync();

        await agent.HandleIncompleteSessionFinalizationRequestedAsync(
            new RoleChatIncompleteSessionFinalizationRequested
            {
                SessionId = "session-1",
                ExpectedLastProgressSequence = 0,
            });
        agent.State.Sessions["session-1"].Completed.Should().BeFalse();

        var current = new RoleChatIncompleteSessionFinalizationRequested
        {
            SessionId = "session-1",
            ExpectedLastProgressSequence = 1,
        };
        await agent.HandleIncompleteSessionFinalizationRequestedAsync(current);
        await agent.HandleIncompleteSessionFinalizationRequestedAsync(current.Clone());

        (await store.GetEventsAsync(actorId))
            .Count(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Should().Be(1);
    }

    [Fact]
    public async Task Activation_ShouldNotFinalizeSessionWaitingForApprovalContinuation()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        const string actorId = "role-session-pending-approval";
        await store.AppendAsync(
            actorId,
            [
                StateEventFor(actorId, 1, new RoleChatSessionStartedEvent
                {
                    SessionId = "session-1",
                    Prompt = "perform approved work",
                    RecoveryCheckpoint = new RoleChatRecoveryCheckpoint
                    {
                        Generation = 3,
                        Stage = RoleChatRecoveryCheckpointStage.WaitingApproval,
                        PendingOperationId = "operation-1",
                        PayloadExpiresAtUnixMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
                    },
                }),
                StateEventFor(actorId, 2, new PendingToolApprovalPersistedEvent
                {
                    Pending = new PendingToolApprovalState
                    {
                        RequestId = "approval-1",
                        SessionId = "session-1",
                        ToolName = "destructive_tool",
                        ToolCallId = "call-1",
                        ArgumentsJson = "{}",
                        OperationId = "operation-1",
                    },
                }),
            ],
            expectedVersion: 0);
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, actorId);
        agent.EventPublisher = publisher;

        await agent.ActivateAsync();

        agent.State.Sessions["session-1"].Completed.Should().BeFalse();
        publisher.Published.Should().NotContain(message =>
            message is RoleChatIncompleteSessionFinalizationRequested);
        (await store.GetEventsAsync(actorId)).Should().NotContain(stateEvent =>
            stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor));
    }

    [Fact]
    public async Task CallerRetry_IncompleteSession_ShouldFinalizeWithoutResumingProvider()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("must not run");
        var services = BuildServices(store);
        const string actorId = "role-session-caller-retry";
        await store.AppendAsync(
            actorId,
            [StateEventFor(actorId, 1, new RoleChatSessionStartedEvent
            {
                SessionId = "session-1",
                Prompt = "hello",
            })],
            expectedVersion: 0);
        var agent = CreateAgent(services, actorId, provider);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-1",
            Prompt = "hello",
        });

        provider.StreamCallCount.Should().Be(0);
        agent.State.Sessions["session-1"].Completed.Should().BeTrue();
        agent.State.Sessions["session-1"].FailureCode.Should().Be("SESSION_ORPHANED");
        publisher.Published.OfType<TextMessageEndEvent>().Should().BeEmpty();
        publisher.Published.OfType<RoleChatSessionErrorEvent>()
            .Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new RoleChatSessionErrorEvent
            {
                SessionId = "session-1",
                Outcome = RoleChatSessionOutcome.Failed,
                Reason = "SESSION_ORPHANED",
                Message = "The chat session was interrupted before execution started. Please try again.",
            });
    }

    [Theory]
    [InlineData(RoleChatSessionOutcome.Failed, "SESSION_FAILED", "Safe failure", "Safe failure")]
    [InlineData(RoleChatSessionOutcome.OutcomeUncertain, "SESSION_OUTCOME_UNCERTAIN", "", "SESSION_OUTCOME_UNCERTAIN")]
    public async Task CallerRetry_TerminalFailure_ShouldPublishTypedLiveError(
        RoleChatSessionOutcome outcome,
        string failureCode,
        string safeMessage,
        string expectedMessage)
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        const string actorId = "role-session-error-replay";
        await store.AppendAsync(
            actorId,
            [
                StateEventFor(actorId, 1, new RoleChatSessionStartedEvent
                {
                    SessionId = "session-1",
                    Prompt = "hello",
                }),
                StateEventFor(actorId, 2, new RoleChatSessionCompletedEvent
                {
                    SessionId = "session-1",
                    Prompt = "hello",
                    Outcome = outcome,
                    FailureCode = failureCode,
                    SafeMessage = safeMessage,
                    TerminalTime = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
                }),
            ],
            expectedVersion: 0);
        var agent = CreateAgent(services, actorId);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-1",
            Prompt = "hello",
        });

        publisher.Published.OfType<TextMessageEndEvent>().Should().BeEmpty();
        var error = publisher.Published.OfType<RoleChatSessionErrorEvent>()
            .Should().ContainSingle().Which;
        error.SessionId.Should().Be("session-1");
        error.Outcome.Should().Be(outcome);
        error.Reason.Should().Be(failureCode);
        error.Message.Should().Be(expectedMessage);
    }

    [Fact]
    public async Task HandleChatRequest_ShouldCommitCompletionBeforePublishingTerminalFrame()
    {
        var inner = new InMemoryEventStoreForTests();
        var operationLog = new List<string>();
        var store = new RecordingCompletionEventStore(inner, operationLog);
        var provider = new CountingLlmProviderFactory("ordered answer");
        var services = BuildServices(store);

        var publisher = new RecordingEventPublisher(operationLog);
        var agent = CreateAgent(services, "role-completion-order", provider);
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "role-ordered",
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-completion-order",
        });

        operationLog.Should().ContainInOrder(
            "commit:RoleChatSessionCompletedEvent:session-completion-order",
            "publish:TextMessageEndEvent:session-completion-order");
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-completion-order" &&
                x.Content == "ordered answer");
    }

    [Fact]
    public async Task RoleChatSessions_ShouldRetainOnlyRecentBoundedCache()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("bounded");
        var services = BuildServices(store);

        var agent = CreateAgent(services, "role-session-retention", provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        for (var i = 1; i <= 130; i++)
        {
            await agent.HandleChatRequest(new ChatRequestEvent
            {
                Prompt = $"prompt-{i}",
                SessionId = $"session-{i}",
            });
        }

        agent.State.Sessions.Count.Should().Be(128);
        agent.State.Sessions.ContainsKey("session-1").Should().BeFalse();
        agent.State.Sessions.ContainsKey("session-2").Should().BeFalse();
        agent.State.Sessions.ContainsKey("session-130").Should().BeTrue();
    }

    [Fact]
    public async Task RoleChatSessions_ShouldRetainEveryIncompleteSessionBeyondCacheLimit()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        const string actorId = "role-incomplete-session-retention";
        var startedEvents = Enumerable.Range(1, 130)
            .Select(index => StateEventFor(actorId, index, new RoleChatSessionStartedEvent
            {
                SessionId = $"session-{index}",
                Prompt = $"prompt-{index}",
            }))
            .ToArray();
        await store.AppendAsync(actorId, startedEvents, expectedVersion: 0);
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, actorId);
        agent.EventPublisher = publisher;

        await agent.ActivateAsync();

        agent.State.Sessions.Should().HaveCount(130);
        agent.State.Sessions.Values.Should().OnlyContain(session => !session.Completed);
        var finalizationSignals = publisher.Published
            .OfType<RoleChatIncompleteSessionFinalizationRequested>()
            .ToArray();
        finalizationSignals.Should().HaveCount(130);

        foreach (var signal in finalizationSignals)
            await agent.HandleIncompleteSessionFinalizationRequestedAsync(signal);

        agent.State.Sessions.Should().HaveCount(130);
        agent.State.Sessions.Values.Should().OnlyContain(session => session.Completed);
        var provider = new CountingLlmProviderFactory("must not run");
        var retryAgent = CreateAgent(services, actorId, provider);
        await retryAgent.ActivateAsync();
        await retryAgent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-1",
            Prompt = "prompt-1",
        });
        provider.StreamCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleChatRequest_WhenCapacityIsFullOfIncompleteSessions_ShouldCommitTypedAdmissionRejection()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        const string actorId = "role-session-capacity-rejection";
        var startedEvents = Enumerable.Range(1, 128)
            .Select(index => StateEventFor(actorId, index, new RoleChatSessionStartedEvent
            {
                SessionId = $"session-{index}",
                Prompt = $"prompt-{index}",
            }))
            .ToArray();
        await store.AppendAsync(actorId, startedEvents, expectedVersion: 0);
        var provider = new CountingLlmProviderFactory("must not run");
        var agent = CreateAgent(services, actorId, provider);
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "session-overflow",
            CommandAttemptId = "attempt-overflow",
            Prompt = "must be rejected",
        });

        provider.StreamCallCount.Should().Be(0);
        agent.State.Sessions.Should().HaveCount(128);
        agent.State.Sessions.Should().NotContainKey("session-overflow");
        var rejection = (await store.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatCommandAttemptRejectedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatCommandAttemptRejectedEvent>())
            .Should().ContainSingle().Which;
        rejection.RequestedSessionId.Should().Be("session-overflow");
        rejection.CommandAttemptId.Should().Be("attempt-overflow");
        rejection.Reason.Should().Be(RoleChatCommandAttemptRejectionReason.CapacityExhausted);
    }

    [Fact]
    public async Task HandleChatRequest_WhenProviderThrowsWithTimeout_ShouldPublishWorkflowFailureMarker()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new ThrowingLlmProviderFactory("throwing-timeout", new InvalidOperationException("  provider exploded  "));
        var services = BuildServices(store);

        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, "role-timeout-failure", provider);
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "role-timeout",
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-timeout-failure",
            TimeoutMs = 1000,
        });

        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .BeEmpty();
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-timeout-failure" &&
                x.Content == "[[AEVATAR_LLM_ERROR]] provider exploded");

        var completed = (await store.GetEventsAsync("role-timeout-failure"))
            .Single(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal))
            .EventData
            .Unpack<RoleChatSessionCompletedEvent>();
        completed.Content.Should().Be("[[AEVATAR_LLM_ERROR]] provider exploded");
        completed.ContentEmitted.Should().BeFalse();
        completed.RoleId.Should().Be("role-timeout");
    }

    [Fact]
    public async Task HandleChatRequest_WhenProviderThrowsWithoutTimeout_ShouldIncludeToolNamesInFailureMessage()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new ThrowingLlmProviderFactory("throwing-tools", new InvalidOperationException("  provider exploded  "));
        var services = BuildServices(store, services =>
        {
            services.AddSingleton<IAgentToolSource>(
                new StaticToolSource(
                [
                    new DelegateTool("dangerous_tool", _ => "{}")
                ]));
        });

        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, "role-tool-failure", provider);
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-tool-failure",
        });

        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .BeEmpty();
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-tool-failure" &&
                x.Content == "LLM request failed [tools=dangerous_tool]: provider exploded");

        var completed = (await store.GetEventsAsync("role-tool-failure"))
            .Single(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal))
            .EventData
            .Unpack<RoleChatSessionCompletedEvent>();
        completed.ContentEmitted.Should().BeFalse();
    }

    [Fact]
    public async Task HandleChatRequest_WithoutSessionId_ShouldSkipSessionPersistence()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("stateless answer");
        var services = BuildServices(store);

        var agent = CreateAgent(services, "role-no-session", provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello without session",
        });

        var persisted = await store.GetEventsAsync("role-no-session");
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(InitializeRoleAgentEvent), StringComparison.Ordinal));
        persisted.Should().NotContain(x => x.EventType.Contains(nameof(RoleChatSessionStartedEvent), StringComparison.Ordinal));
        persisted.Should().NotContain(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletedSessionReplay_ShouldEmitReasoningToolCallsAndMedia_WhenContentWasNotStreamed()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-rich-session-replay");
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "counting",
            SystemPrompt = "system",
        });
        await agent1.DeactivateAsync();

        await store.AppendAsync(
            "role-rich-session-replay",
            [
                StateEventFor(
                    "role-rich-session-replay",
                    2,
                    new RoleChatSessionCompletedEvent
                    {
                        SessionId = "session-rich",
                        Prompt = "hello rich",
                        Content = "final answer",
                        ReasoningContent = "because",
                        ContentEmitted = false,
                        ToolCalls =
                        {
                            new ToolCallEvent
                            {
                                CallId = "call-1",
                                ToolName = "lookup",
                                ArgumentsJson = "{\"x\":1}",
                            },
                        },
                        ToolReceipts =
                        {
                            new AgentToolReceipt
                            {
                                CallId = "call-approval",
                                ToolName = "dangerous_tool",
                                Status = AgentToolReceiptStatus.ApprovalRequired,
                                ApprovalRequestId = "approval-1",
                                ResultJson = "{\"status\":\"approval_required\"}",
                            },
                        },
                        OutputParts =
                        {
                            new ChatContentPart
                            {
                                Kind = ChatContentPartKind.Image,
                                Name = "photo.png",
                            },
                        },
                    }),
            ],
            expectedVersion: 1);

        var replayPublisher = new RecordingEventPublisher();
        var agent2 = CreateAgent(services, "role-rich-session-replay");
        agent2.EventPublisher = replayPublisher;
        await agent2.ActivateAsync();

        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello rich",
            SessionId = "session-rich",
        });

        replayPublisher.Published
            .OfType<TextMessageStartEvent>()
            .Should()
            .ContainSingle(x => x.SessionId == "session-rich");
        replayPublisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .ContainSingle(x => x.SessionId == "session-rich" && x.Delta == "final answer");
        replayPublisher.Published
            .OfType<TextMessageReasoningEvent>()
            .Should()
            .ContainSingle(x => x.SessionId == "session-rich" && x.Delta == "because");
        replayPublisher.Published
            .OfType<ToolCallEvent>()
            .Should()
            .ContainSingle(x => x.CallId == "call-1" && x.ToolName == "lookup");
        replayPublisher.Published
            .OfType<ToolResultEvent>()
            .Should()
            .ContainSingle(x =>
                x.CallId == "call-approval" &&
                !x.Success &&
                x.Receipt.Status == AgentToolReceiptStatus.ApprovalRequired);
        replayPublisher.Published
            .OfType<MediaContentEvent>()
            .Should()
            .ContainSingle(x => x.SessionId == "session-rich");
        replayPublisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x => x.SessionId == "session-rich" && x.Content == "final answer");
    }

    [Fact]
    public async Task HandleChatRequest_WhenPersistCompletionFails_ShouldNotPublishTerminalFrames()
    {
        var inner = new InMemoryEventStoreForTests();
        var store = new FailOnCompletionEventStore(inner);
        var provider = new ThrowingLlmProviderFactory(
            "throwing-persist-fail",
            new InvalidOperationException("provider failed before commit"));
        var services = BuildServices(store);

        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, "role-persist-fail", provider);
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        var act = () => agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-persist-fail",
        });

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Simulated persistence failure for session completion.");

        // Refactor (iter164/cluster-001-role-completion):
        //   Old pattern: RoleGAgent published the terminal TextMessageEndEvent before completion commit.
        //   New principle: completion commit failure prevents terminal presentation frames from being published.
        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .BeEmpty();
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .BeEmpty();

        var persisted = await inner.GetEventsAsync("role-persist-fail");
        persisted.Should().NotContain(x =>
            x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletedSessionReplay_WhenFailureContentWasNotStreamed_ShouldNotPublishDisplayContent()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-failure-replay");
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "counting",
            SystemPrompt = "system",
        });
        await agent1.DeactivateAsync();

        await store.AppendAsync(
            "role-failure-replay",
            [
                StateEventFor(
                    "role-failure-replay",
                    2,
                    new RoleChatSessionCompletedEvent
                    {
                        SessionId = "session-failure-replay",
                        Prompt = "hello",
                        Content = "LLM request failed [tools=none]: upstream",
                        ContentEmitted = false,
                    }),
            ],
            expectedVersion: 1);

        var publisher = new RecordingEventPublisher();
        var agent2 = CreateAgent(services, "role-failure-replay");
        agent2.EventPublisher = publisher;
        await agent2.ActivateAsync();

        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-failure-replay",
        });

        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .BeEmpty();
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-failure-replay" &&
                x.Content == "LLM request failed [tools=none]: upstream");
    }

    [Fact]
    public async Task CompletedSessionReplay_WhenMarkerFailureContentWasNotStreamed_ShouldNotPublishDisplayContent()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-marker-failure-replay");
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "counting",
            SystemPrompt = "system",
        });
        await agent1.DeactivateAsync();

        await store.AppendAsync(
            "role-marker-failure-replay",
            [
                StateEventFor(
                    "role-marker-failure-replay",
                    2,
                    new RoleChatSessionCompletedEvent
                    {
                        SessionId = "session-marker-failure-replay",
                        Prompt = "hello",
                        Content = "[[AEVATAR_LLM_ERROR]] upstream",
                        ContentEmitted = false,
                    }),
            ],
            expectedVersion: 1);

        var publisher = new RecordingEventPublisher();
        var agent2 = CreateAgent(services, "role-marker-failure-replay");
        agent2.EventPublisher = publisher;
        await agent2.ActivateAsync();

        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-marker-failure-replay",
        });

        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .BeEmpty();
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-marker-failure-replay" &&
                x.Content == "[[AEVATAR_LLM_ERROR]] upstream");
    }

    [Fact]
    public async Task PublishMissingDisplayContentWithDeadlineAsync_WhenCompletionWasNotEmitted_ShouldPublishContentAndMarkEmitted()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, "role-missing-display-content");
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();

        var replayRecord = CreateSessionReplayRecord("final answer", contentEmitted: false);
        var method = typeof(RoleGAgent).GetMethod(
            "PublishMissingDisplayContentWithDeadlineAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var task = method!.Invoke(
                agent,
                ["session-missing-display", replayRecord, CancellationToken.None])
            .Should()
            .BeAssignableTo<Task>()
            .Subject;
        await task;

        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-missing-display" &&
                x.Delta == "final answer");
        GetSessionReplayRecordContentEmitted(task).Should().BeTrue();
    }

    [Fact]
    public async Task HandleChatRequest_WhenReplayHasFinalOnlyContent_ShouldPublishDisplayContentBeforeEnd()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-final-only-replay");
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "counting",
            SystemPrompt = "system",
        });
        await agent1.DeactivateAsync();

        await store.AppendAsync(
            "role-final-only-replay",
            [
                StateEventFor(
                    "role-final-only-replay",
                    2,
                    new RoleChatSessionCompletedEvent
                    {
                        SessionId = "session-final-only",
                        Prompt = "hello",
                        Content = "final-only answer",
                        ContentEmitted = false,
                    }),
            ],
            expectedVersion: 1);

        var publisher = new RecordingEventPublisher();
        var agent2 = CreateAgent(services, "role-final-only-replay");
        agent2.EventPublisher = publisher;
        await agent2.ActivateAsync();

        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-final-only",
        });

        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-final-only" &&
                x.Delta == "final-only answer");
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-final-only" &&
                x.Content == "final-only answer");

        var persisted = await store.GetEventsAsync("role-final-only-replay");
        persisted
            .Where(x =>
                x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor) &&
                x.EventData.Unpack<RoleChatSessionCompletedEvent>().SessionId == "session-final-only")
            .Should()
            .ContainSingle();
        persisted
            .Where(x => x.EventData.Is(RoleChatSessionProgressedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<RoleChatSessionProgressedEvent>())
            .Should()
            .ContainSingle(progress =>
                progress.SessionId == "session-final-only" &&
                progress.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Replay &&
                progress.Replay.Snapshot.Content == "final-only answer");
    }

    private static IServiceProvider BuildServices(
        InMemoryEventStoreForTests store,
        Action<IServiceCollection>? configure = null) =>
        BuildServices((IEventStore)store, configure);

    private static IServiceProvider BuildServices(
        IEventStore store,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<ISecretVault, InMemorySecretVault>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IAuditTrailAppender, AppendedAuditTrail>()
            .AddSingleton<IAuditActorIdentityHasher, StableIdentityHasher>()
            .AddSingleton<IAgentToolAdmissionLedger>(AlwaysStartingAgentToolAdmissionLedger.Instance)
            .AddSingleton<IAgentToolExecutionPort, AdmittedAgentToolExecutor>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }

    private static RoleGAgent CreateAgent(
        IServiceProvider services,
        string actorId,
        ILLMProviderFactory? providerFactory = null,
        TimeProvider? timeProvider = null,
        RoleChatExecutionOptions? chatExecutionOptions = null)
    {
        var agent = new RoleGAgent(
            services.GetRequiredService<IAgentToolExecutionPort>(),
            providerFactory,
            toolSources: services.GetServices<IAgentToolSource>(),
            timeProvider: timeProvider,
            chatExecutionOptions: chatExecutionOptions,
            chatToolRecoverySecretVault: services.GetRequiredService<ISecretVault>())
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static RoleGAgent CreateExplicitToolAgent(
        IServiceProvider services,
        string actorId,
        ILLMProviderFactory providerFactory,
        IReadOnlyList<IAgentTool> exactTools)
    {
        var agent = new ExplicitToolRoleGAgent(
            services.GetRequiredService<IAgentToolExecutionPort>(),
            providerFactory,
            exactTools,
            services.GetRequiredService<ISecretVault>())
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static ProfiledRoleGAgent CreateProfiledAgent(
        IServiceProvider services,
        string actorId,
        string intentId = "intent-a",
        string exactSkillGuid = "skill-a",
        List<string>? operationLog = null,
        ILLMProviderFactory? providerFactory = null,
        ReconcileProposalMutation reconcileProposalMutation = ReconcileProposalMutation.None,
        InitialAuthorityMutation initialAuthorityMutation = InitialAuthorityMutation.None)
    {
        var agent = new ProfiledRoleGAgent(
            providerFactory ?? new CountingLlmProviderFactory("done"),
            intentId,
            exactSkillGuid,
            operationLog,
            reconcileProposalMutation,
            initialAuthorityMutation)
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static AgentProfileTurnAuthorityState MutateReconcileProposal(
        AgentProfileTurnAuthorityState authority,
        ReconcileProposalMutation mutation)
    {
        var proposal = authority.Clone();
        switch (mutation)
        {
            case ReconcileProposalMutation.None:
                break;
            case ReconcileProposalMutation.WidenCeiling:
                proposal.AuthorityCeilingToolNames.Add("outside-frozen-ceiling");
                break;
            case ReconcileProposalMutation.RecoveryWithEmptyCeiling:
                proposal.AuthorityKind = AgentProfileTurnAuthorityKind.Recovery;
                proposal.AuthorityCeilingToolNames.Clear();
                break;
            case ReconcileProposalMutation.RestrictedEmptyWithNonEmptyCeiling:
                proposal.AuthorityKind = AgentProfileTurnAuthorityKind.RestrictedEmpty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        return proposal;
    }

    private static AgentProfileTurnAuthorityState MutateInitialAuthority(
        AgentProfileTurnAuthorityState authority,
        InitialAuthorityMutation mutation)
    {
        var initial = authority.Clone();
        switch (mutation)
        {
            case InitialAuthorityMutation.None:
                break;
            case InitialAuthorityMutation.WrongSession:
                initial.ReconciliationKey.SessionId = "session-other";
                break;
            case InitialAuthorityMutation.AttemptNotOne:
                initial.ReconciliationKey.Attempt = 2;
                break;
            case InitialAuthorityMutation.RecoveryWithEmptyCeiling:
                initial.AuthorityKind = AgentProfileTurnAuthorityKind.Recovery;
                initial.AuthorityCeilingToolNames.Clear();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        return initial;
    }

    private static AgentProfileTurnAuthorityState TurnAuthority(
        string sessionId,
        int attempt,
        string intentId,
        string exactSkillGuid) =>
        new()
        {
            ReconciliationKey = new AgentProfileTurnReconciliationKey
            {
                SessionId = sessionId,
                Attempt = attempt,
            },
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
            {
                ProfileId = "profile-a",
                ProfileVersion = "v1",
                PolicyRevision = "policy-a",
                IntentId = intentId,
            },
            SelectedExactSkillRef = new ExactRemoteSkillRef
            {
                Guid = exactSkillGuid,
                LiteralVersion = "1.0.0",
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
            AuthorityCeilingToolNames = { "recovery", "task" },
        };

    private static RoleGAgentState ApplyTurnAuthority(
        RoleGAgentState current,
        AgentProfileTurnAuthorityCommittedEvent evt)
    {
        var method = typeof(RoleGAgent).GetMethod(
            "ApplyAgentProfileTurnAuthorityCommitted",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        return (RoleGAgentState)method!.Invoke(null, [current, evt])!;
    }

    private static bool IsSessionFact(StateEvent stateEvent, string sessionId) =>
        stateEvent.EventData.Is(RoleChatSessionStartedEvent.Descriptor)
            ? string.Equals(
                stateEvent.EventData.Unpack<RoleChatSessionStartedEvent>().SessionId,
                sessionId,
                StringComparison.Ordinal)
            : stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor) &&
              string.Equals(
                  stateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>()
                      .Authority.ReconciliationKey.SessionId,
                  sessionId,
                  StringComparison.Ordinal);

    private static RecordingCommittedPublisherProxy AttachCommittedPublisher(RoleGAgent agent)
    {
        var publisherType = typeof(GAgentBase).Assembly.GetType(
            "Aevatar.Foundation.Core.EventSourcing.ICommittedStateEventPublisher",
            throwOnError: true)!;
        var proxy = (RecordingCommittedPublisherProxy)DispatchProxy.Create(
            publisherType,
            typeof(RecordingCommittedPublisherProxy));
        typeof(GAgentBase).GetProperty(
                "CommittedStateEventPublisher",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(agent, proxy);
        return proxy;
    }

    private static StateEvent StateEventFor(string agentId, long version, IMessage evt) =>
        new()
        {
            EventId = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Version = version,
            EventType = evt.Descriptor.FullName,
            EventData = Any.Pack(evt),
            AgentId = agentId,
        };

    private static void AssignActorId(RoleGAgent agent, string actorId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [actorId]);
    }

    private static object CreateSessionReplayRecord(string content, bool contentEmitted)
    {
        var replayRecordType = typeof(RoleGAgent).GetNestedType(
            "SessionReplayRecord",
            BindingFlags.NonPublic);
        replayRecordType.Should().NotBeNull();

        return Activator.CreateInstance(
            replayRecordType!,
            content,
            string.Empty,
            Array.Empty<ToolCall>(),
            Array.Empty<ContentPart>(),
            Array.Empty<AgentToolReceipt>(),
            Array.Empty<ToolResultEvent>(),
            null, // Usage (added by #1700)
            null, // Model (added by #1700)
            contentEmitted,
            RoleChatSessionOutcome.Completed,
            string.Empty,
            string.Empty,
            null)!;
    }

    private static bool GetSessionReplayRecordContentEmitted(Task task)
    {
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var property = result.GetType().GetProperty("ContentEmitted")!;
        return (bool)property.GetValue(result)!;
    }

    private sealed class RecordingTerminalBatchEventStore : IEventStore
    {
        private readonly InMemoryEventStoreForTests _inner = new();

        public List<IReadOnlyList<StateEvent>> Appends { get; } = [];

        public async Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.Select(static evt => evt.Clone()).ToArray();
            var result = await _inner.AppendAsync(agentId, batch, expectedVersion, ct);
            Appends.Add(batch);
            return result;
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            _inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            _inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private sealed class BlockingRoleSuccessCompletionEventStore(IEventStore inner) : IEventStore
    {
        private readonly TaskCompletionSource _appendStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _neverCompletes =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SuccessCompletionAppendStarted => _appendStarted.Task;
        public Task CancellationObserved => _cancellationObserved.Task;

        public async Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.Select(static stateEvent => stateEvent.Clone()).ToArray();
            if (batch.Any(IsSuccessfulRoleCompletion))
            {
                _appendStarted.TrySetResult();
                try
                {
                    await _neverCompletes.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _cancellationObserved.TrySetResult();
                    throw;
                }
            }

            return await inner.AppendAsync(agentId, batch, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);

        private static bool IsSuccessfulRoleCompletion(StateEvent stateEvent) =>
            stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor) &&
            stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>().Outcome ==
            RoleChatSessionOutcome.Completed;
    }

    private sealed class LateReturningCommittedRoleSuccessEventStore(IEventStore inner) : IEventStore
    {
        private readonly TaskCompletionSource _successCommitCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _deadlineObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SuccessCommitCompleted => _successCommitCompleted.Task;
        public Task DeadlineObserved => _deadlineObserved.Task;

        public async Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.Select(static stateEvent => stateEvent.Clone()).ToArray();
            if (!batch.Any(IsSuccessfulRoleCompletion))
                return await inner.AppendAsync(agentId, batch, expectedVersion, ct);

            var committed = await inner.AppendAsync(
                agentId,
                batch,
                expectedVersion,
                CancellationToken.None);
            _successCommitCompleted.TrySetResult();
            using var registration = ct.Register(() => _deadlineObserved.TrySetResult());
            await _deadlineObserved.Task;
            return committed;
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);

        private static bool IsSuccessfulRoleCompletion(StateEvent stateEvent) =>
            stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor) &&
            stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>().Outcome ==
            RoleChatSessionOutcome.Completed;
    }

    private sealed class RecordingEventPublisher(List<string>? operationLog = null) : IEventPublisher
    {
        public bool FailSends { get; init; }
        public Func<IMessage, CancellationToken, Task>? BeforePublishAsync { get; set; }

        public List<IMessage> Published { get; } = [];

        public List<(IMessage Event, TopologyAudience Audience)> Publications { get; } = [];

        public List<(string TargetActorId, IMessage Event, EventEnvelopePublishOptions? Options)> Sends { get; } = [];

        public async Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = sourceEnvelope;
            _ = options;
            if (BeforePublishAsync is not null)
                await BeforePublishAsync(evt, ct);
            ct.ThrowIfCancellationRequested();
            Published.Add(evt);
            Publications.Add((evt, direction));
            if (evt is TextMessageEndEvent textMessageEnd)
                operationLog?.Add($"publish:TextMessageEndEvent:{textMessageEnd.SessionId}");
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Sends.Add((targetActorId, evt, options));
            if (FailSends)
                throw new InvalidOperationException("simulated completion notification failure");

            return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
        }

        public Task PublishCommittedStateEventAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            _ = audience;
            return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
        }
    }

    private sealed class PostTurnPublicationProbe
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _neverCompletes =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task HangIgnoringCancellationAsync()
        {
            _started.TrySetResult();
            return _neverCompletes.Task;
        }
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TimeoutRequests.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                TimeoutRequests.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowOnceEventPublisher(
        Func<IMessage, bool> shouldThrow,
        Exception exception) : IEventPublisher
    {
        private bool _thrown;

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (!_thrown && shouldThrow(evt))
            {
                _thrown = true;
                throw exception;
            }

            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);

        public Task PublishCommittedStateEventAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null) =>
            PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
    }

    private class RecordingCommittedPublisherProxy : DispatchProxy
    {
        public List<CommittedStateEventPublished> Published { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is { Length: > 0 } && args[0] is CommittedStateEventPublished published)
                Published.Add(published.Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class CountingLlmProviderFactory(string response) : ILLMProviderFactory, ILLMProvider
    {
        public int StreamCallCount { get; private set; }
        public List<LLMRequest> StreamRequests { get; } = [];

        public string Name => "counting";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StreamCallCount++;
            StreamRequests.Add(request);
            yield return new LLMStreamChunk
            {
                DeltaContent = response,
            };
            await Task.CompletedTask;
            yield return new LLMStreamChunk
            {
                IsLast = true,
                Usage = new TokenUsage(1, 1, 2),
            };
        }
    }

    private sealed class FragmentedLlmProviderFactory(
        int textChunkCount,
        int reasoningChunkCount) : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "fragmented";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            for (var i = 0; i < textChunkCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return new LLMStreamChunk { DeltaContent = "t" };
            }

            for (var i = 0; i < reasoningChunkCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return new LLMStreamChunk { DeltaReasoningContent = "r" };
            }

            await Task.CompletedTask;
            yield return new LLMStreamChunk
            {
                IsLast = true,
                Usage = new TokenUsage(1, 1, 2),
            };
        }
    }

    private sealed class PacedLlmProviderFactory(
        ManualDeadlineTimeProvider timeProvider) : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "paced";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = "a" };
            timeProvider.Advance(TimeSpan.FromMilliseconds(250));
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = "b" };
            timeProvider.Advance(TimeSpan.FromMilliseconds(250));
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = "c" };
            await Task.CompletedTask;
        }
    }

    private sealed class CancellationAwareHangingProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        private readonly TaskCompletionSource _firstStreamStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _neverCompletes =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _streamCallCount;

        public string Name => "cancellation-aware-hanging";
        public Task FirstStreamStarted => _firstStreamStarted.Task;
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            if (Interlocked.Increment(ref _streamCallCount) == 1)
            {
                _firstStreamStarted.TrySetResult();
                await _neverCompletes.Task.WaitAsync(ct);
                yield break;
            }

            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = "next turn completed" };
        }
    }

    private sealed class LateCompletionAfterCancellationProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        private readonly TaskCompletionSource _streamStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _neverCompletes =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseLateCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _streamCallCount;

        public string Name => "late-completion-after-cancellation";
        public Task StreamStarted => _streamStarted.Task;
        public Task CancellationObserved => _cancellationObserved.Task;
        public int StreamCallCount => _streamCallCount;
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public void ReleaseLateCompletion() => _releaseLateCompletion.TrySetResult();

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            Interlocked.Increment(ref _streamCallCount);
            _streamStarted.TrySetResult();
            try
            {
                await _neverCompletes.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
            }

            await _releaseLateCompletion.Task;
            yield return new LLMStreamChunk { DeltaContent = "late provider completion" };
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class AuthorizationThenSuccessLlmProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public int StreamCallCount { get; private set; }
        public string Name => "authorization-then-success";
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StreamCallCount++;
            if (StreamCallCount == 1)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call-auth",
                        Name = "authorization_required_test_tool",
                        ArgumentsJson = "{}",
                    },
                };
            }
            else
            {
                yield return new LLMStreamChunk { DeltaContent = "follow-up answer" };
            }

            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class AuthorizationRequiredTool : IAgentTool
    {
        public string Name => "authorization_required_test_tool";
        public string Description => "Returns a typed authorization blocker.";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("""{"error":true,"status":401}""");

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.AuthorizationRequired,
                AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
                {
                    ServiceSlug = "api-github",
                    ResourceUri = "/repos/private",
                    ReasonCode = "NYXID_UNAUTHORIZED",
                    SafeMessage = "Connect or reauthorize api-github to continue.",
                },
            };
    }

    private sealed class ProfiledRoleGAgent(
        ILLMProviderFactory providerFactory,
        string intentId,
        string exactSkillGuid,
        List<string>? operationLog,
        ReconcileProposalMutation reconcileProposalMutation,
        InitialAuthorityMutation initialAuthorityMutation)
        : RoleGAgent(TestAgentToolExecutionPort.Instance, providerFactory)
    {
        public int PrepareCallCount { get; private set; }
        public int MaterializeCallCount { get; private set; }
        public List<AgentProfileTurnAuthorityState> MaterializedAuthorities { get; } = [];

        protected override Task<AgentProfileTurnAuthorityPreparation?> PrepareAgentProfileTurnAuthorityAsync(
            ChatRequestEvent request,
            AgentToolExecutionContext toolContext,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            PrepareCallCount++;
            return Task.FromResult<AgentProfileTurnAuthorityPreparation?>(
                AgentProfileTurnAuthorityPreparation.Create(
                    MutateInitialAuthority(
                        TurnAuthority(request.SessionId, 1, intentId, exactSkillGuid),
                        initialAuthorityMutation)));
        }

        protected override Task<AgentTurnToolCatalogMaterialization?> MaterializeCommittedAgentTurnToolCatalogAsync(
            ChatRequestEvent request,
            AgentToolExecutionContext toolContext,
            AgentProfileTurnAuthorityState committedAuthority,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            operationLog?.Add($"materialize:{committedAuthority.ReconciliationKey.Attempt}");
            MaterializeCallCount++;
            MaterializedAuthorities.Add(committedAuthority.Clone());
            var reconcileProposal = MutateReconcileProposal(committedAuthority, reconcileProposalMutation);
            var catalog = new AgentTurnToolCatalog(
                reconcileProposal.AuthorityCeilingToolNames,
                profilePromptLayer: null,
                selectedSkillPromptLayer: null,
                selectedIntentId: committedAuthority.CandidateRoute?.IntentId,
                candidateIntentId: committedAuthority.CandidateRoute?.IntentId);
            return Task.FromResult<AgentTurnToolCatalogMaterialization?>(
                AgentTurnToolCatalogMaterialization.Create(catalog, reconcileProposal));
        }
    }

    private sealed class ExplicitToolRoleGAgent(
        IAgentToolExecutionPort toolExecutionPort,
        ILLMProviderFactory providerFactory,
        IReadOnlyList<IAgentTool> exactTools,
        ISecretVault secretVault)
        : RoleGAgent(
            toolExecutionPort,
            providerFactory,
            toolSources: [new StaticToolSource(exactTools)],
            chatToolRecoverySecretVault: secretVault)
    {
        protected override Task<AgentProfileTurnAuthorityPreparation?> PrepareAgentProfileTurnAuthorityAsync(
            ChatRequestEvent request,
            AgentToolExecutionContext toolContext,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var authority = new AgentProfileTurnAuthorityState
            {
                ReconciliationKey = new AgentProfileTurnReconciliationKey
                {
                    SessionId = request.SessionId,
                    Attempt = 1,
                },
                CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
                {
                    ProfileId = "profile-explicit-tools",
                    ProfileVersion = "v1",
                    PolicyRevision = "policy-v1",
                    IntentId = "explicit-tools",
                },
                AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
            };
            authority.AuthorityCeilingToolNames.Add(exactTools.Select(static tool => tool.Name));
            return Task.FromResult<AgentProfileTurnAuthorityPreparation?>(
                AgentProfileTurnAuthorityPreparation.Create(authority));
        }

        protected override Task<AgentTurnToolCatalogMaterialization?> MaterializeCommittedAgentTurnToolCatalogAsync(
            ChatRequestEvent request,
            AgentToolExecutionContext toolContext,
            AgentProfileTurnAuthorityState committedAuthority,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var catalog = new AgentTurnToolCatalog(
                committedAuthority.AuthorityCeilingToolNames,
                profilePromptLayer: null,
                selectedSkillPromptLayer: null,
                selectedIntentId: committedAuthority.CandidateRoute?.IntentId,
                candidateIntentId: committedAuthority.CandidateRoute?.IntentId,
                exactTools: exactTools);
            return Task.FromResult<AgentTurnToolCatalogMaterialization?>(
                AgentTurnToolCatalogMaterialization.Create(catalog, committedAuthority.Clone()));
        }
    }

    public enum ReconcileProposalMutation
    {
        None,
        WidenCeiling,
        RecoveryWithEmptyCeiling,
        RestrictedEmptyWithNonEmptyCeiling,
    }

    public enum InitialAuthorityMutation
    {
        None,
        WrongSession,
        AttemptNotOne,
        RecoveryWithEmptyCeiling,
    }

    private sealed class CountingEventModuleFactory : IEventModuleFactory<IEventHandlerContext>
    {
        public int TryCreateCallCount { get; private set; }

        public bool TryCreate(string name, out IEventModule<IEventHandlerContext>? module)
        {
            TryCreateCallCount++;
            module = name switch
            {
                "routable" => new CountingRoutableModule(),
                "bypass" => new CountingBypassModule(),
                "lifecycle" => new CountingLifecycleModule(),
                _ => null,
            };
            return module != null;
        }
    }

    private sealed class CountingRoutableModule : IEventModule<IEventHandlerContext>
    {
        public string Name => "routable";
        public int Priority => 0;
        public bool CanHandle(EventEnvelope envelope) => envelope != null;
        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CountingLifecycleModule : ILifecycleAwareEventModule
    {
        public string Name => "lifecycle";
        public int Priority => 0;
        public int InitializeCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }
        public bool CanHandle(EventEnvelope envelope) => envelope != null;
        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task InitializeAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            InitializeCallCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingBypassModule : IEventModule<IEventHandlerContext>, IRouteBypassModule
    {
        public string Name => "bypass";
        public int Priority => 0;
        public bool CanHandle(EventEnvelope envelope) => envelope != null;
        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingLlmProviderFactory(string name, Exception exception) : ILLMProviderFactory, ILLMProvider
    {
        public string Name => name;

        public ILLMProvider GetProvider(string providerName)
        {
            _ = providerName;
            return this;
        }

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            throw exception;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class LateFailingPostCommitToolSource : IAgentToolSource
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseLateFailure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockNext;

        public Task Started => _started.Task;
        public Task CancellationObserved => _cancellationObserved.Task;

        public void BlockNextDiscovery() => Interlocked.Exchange(ref _blockNext, 1);

        public void ReleaseLateFailure() => _releaseLateFailure.TrySetResult();

        public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(
            CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _blockNext, 0) == 0)
                return [];

            _started.TrySetResult();
            var cancellation = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => cancellation.TrySetResult());
            await cancellation.Task;
            _cancellationObserved.TrySetResult();
            await _releaseLateFailure.Task;
            throw new InvalidOperationException("late tool discovery failure");
        }
    }

    private sealed class DelegateTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name => name;
        public string Description => $"{name} test tool";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(execute(argumentsJson));
        }
    }

    /// <summary>
    /// Wraps an inner store but throws on appends that contain a
    /// <see cref="RoleChatSessionCompletedEvent"/>, simulating a
    /// persistence failure during session completion.
    /// </summary>
    private sealed class FailOnCompletionEventStore(InMemoryEventStoreForTests inner) : IEventStore
    {
        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var list = events.ToList();
            if (list.Any(e => e.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal)))
                throw new InvalidOperationException("Simulated persistence failure for session completion.");

            return inner.AppendAsync(agentId, list, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId, long? fromVersion = null, CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private sealed class FailOnCompletionNotificationDispatchedEventStore(
        InMemoryEventStoreForTests inner) : IEventStore
    {
        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var pending = events.ToArray();
            if (pending.Any(stateEvent =>
                    stateEvent.EventData.Is(RoleChatCompletionNotificationDispatchedEvent.Descriptor)))
            {
                throw new InvalidOperationException("Simulated completion notification checkpoint failure.");
            }

            return inner.AppendAsync(agentId, pending, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private sealed class RecordingCompletionEventStore(
        InMemoryEventStoreForTests inner,
        List<string> operationLog) : IEventStore
    {
        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var list = events.ToList();
            var result = inner.AppendAsync(agentId, list, expectedVersion, ct);

            foreach (var evt in list)
            {
                if (!evt.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
                    continue;

                var completed = evt.EventData.Unpack<RoleChatSessionCompletedEvent>();
                operationLog.Add($"commit:RoleChatSessionCompletedEvent:{completed.SessionId}");
            }

            return result;
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId, long? fromVersion = null, CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private sealed class RecordingBatchEventStore(
        InMemoryEventStoreForTests inner,
        List<string>? operationLog = null) : IEventStore
    {
        public List<IReadOnlyList<StateEvent>> Batches { get; } = [];

        public async Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.ToArray();
            var result = await inner.AppendAsync(agentId, batch, expectedVersion, ct);
            Batches.Add(batch);
            foreach (var authorityEvent in batch
                         .Where(stateEvent => stateEvent.EventData.Is(
                             AgentProfileTurnAuthorityCommittedEvent.Descriptor))
                         .Select(stateEvent => stateEvent.EventData
                             .Unpack<AgentProfileTurnAuthorityCommittedEvent>()))
            {
                operationLog?.Add(
                    $"commit:{AuthorityCommitName(authorityEvent.CommitKind)}:{authorityEvent.Authority.ReconciliationKey.Attempt}");
            }
            return result;
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId, long? fromVersion = null, CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private sealed class FailOnceOnInitialAuthorityBatchEventStore(InMemoryEventStoreForTests inner) : IEventStore
    {
        private bool _shouldFail = true;

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.ToArray();
            if (_shouldFail && batch.Any(stateEvent =>
                    stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor) &&
                    stateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>().CommitKind ==
                    AgentProfileTurnAuthorityCommitKind.Initial))
            {
                _shouldFail = false;
                throw new InvalidOperationException("Simulated authority batch failure.");
            }

            return inner.AppendAsync(agentId, batch, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId, long? fromVersion = null, CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private sealed class FailOnceOnReconcileEventStore(InMemoryEventStoreForTests inner) : IEventStore
    {
        private bool _shouldFail = true;

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.ToArray();
            if (_shouldFail && batch.Any(stateEvent =>
                    stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor) &&
                    stateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>().CommitKind ==
                    AgentProfileTurnAuthorityCommitKind.Reconcile))
            {
                _shouldFail = false;
                throw new InvalidOperationException("Simulated reconcile failure.");
            }

            return inner.AppendAsync(agentId, batch, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId, long? fromVersion = null, CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private sealed class FailOnceOnRetryStartedEventStore(InMemoryEventStoreForTests inner) : IEventStore
    {
        private bool _shouldFail = true;

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.ToArray();
            if (_shouldFail && batch.Any(stateEvent =>
                    stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor) &&
                    stateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>().CommitKind ==
                    AgentProfileTurnAuthorityCommitKind.RetryStarted))
            {
                _shouldFail = false;
                throw new InvalidOperationException("Simulated retry started failure.");
            }

            return inner.AppendAsync(agentId, batch, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId, long? fromVersion = null, CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private static string AuthorityCommitName(AgentProfileTurnAuthorityCommitKind commitKind) => commitKind switch
    {
        AgentProfileTurnAuthorityCommitKind.Initial => "INITIAL",
        AgentProfileTurnAuthorityCommitKind.RetryStarted => "RETRY_STARTED",
        AgentProfileTurnAuthorityCommitKind.Reconcile => "RECONCILE",
        _ => "UNSPECIFIED",
    };
}
