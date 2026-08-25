using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.UserMemory;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class UserMemoryGAgentCommandTests
{
    private const string ActorId = "user-memory-user-gamma";

    [Fact]
    public async Task TypedCommands_ShouldCommitUserMemoryFactsAndChangeOnlyOwnerState()
    {
        const string conversationId = "conversation-alpha";
        const string sessionId = "session-beta";
        var eventStore = new InMemoryEventStore();
        var agent = await CreateAgentAsync(eventStore);

        await agent.HandleEventAsync(Envelope(new AddUserMemoryEntryCommand
        {
            Entry = new UserMemoryEntryProto
            {
                Id = "memory-delta",
                Category = UserMemoryCategory.Preference,
                Content = "Prefer concise status updates",
                Source = UserMemorySource.Explicit,
                CreatedAtMs = 1_750_000_000_000,
                UpdatedAtMs = 1_750_000_001_000,
            },
        }));

        ActorId.Should().NotBe(conversationId);
        ActorId.Should().NotBe(sessionId);
        var entry = agent.State.Entries.Should().ContainSingle().Subject;
        entry.Id.Should().Be("memory-delta");
        entry.Category.Should().Be(UserMemoryCategory.Preference);
        entry.Source.Should().Be(UserMemorySource.Explicit);
        var added = (await eventStore.GetEventsAsync(ActorId)).Should().ContainSingle().Subject;
        added.EventData.Is(MemoryEntryAddedEvent.Descriptor).Should().BeTrue();

        await agent.HandleEventAsync(Envelope(new RemoveUserMemoryEntryCommand
        {
            EntryId = "memory-delta",
        }));

        agent.State.Entries.Should().BeEmpty();
        var events = await eventStore.GetEventsAsync(ActorId);
        events.Should().HaveCount(2);
        events[1].EventData.Is(MemoryEntryRemovedEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task AddCommand_WithUntypedCategory_ShouldFailClosedWithoutCommitting()
    {
        var eventStore = new InMemoryEventStore();
        var agent = await CreateAgentAsync(eventStore);
        var command = new AddUserMemoryEntryCommand
        {
            Entry = new UserMemoryEntryProto
            {
                Id = "memory-delta",
                Category = UserMemoryCategory.Unspecified,
                Content = "Do not accept an untyped category",
                Source = UserMemorySource.Explicit,
            },
        };

        var act = () => agent.HandleEventAsync(Envelope(command));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("user_memory_category_invalid");
        agent.State.Entries.Should().BeEmpty();
        (await eventStore.GetEventsAsync(ActorId)).Should().BeEmpty();
    }

    [Fact]
    public async Task AddCommand_WithUnreadableTimestamp_ShouldFailClosedWithoutCommitting()
    {
        var eventStore = new InMemoryEventStore();
        var agent = await CreateAgentAsync(eventStore);
        var command = new AddUserMemoryEntryCommand
        {
            Entry = new UserMemoryEntryProto
            {
                Id = "memory-delta",
                Category = UserMemoryCategory.Preference,
                Content = "Do not persist timestamps that the read model cannot map",
                Source = UserMemorySource.Explicit,
                CreatedAtMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds(),
                UpdatedAtMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() + 1,
            },
        };

        var act = () => agent.HandleEventAsync(Envelope(command));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("user_memory_timestamp_invalid");
        agent.State.Entries.Should().BeEmpty();
        (await eventStore.GetEventsAsync(ActorId)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReplayWithoutPolicy_ShouldPreserveLegacyStateBytes()
    {
        var agent = await CreateAgentAsync(new InMemoryEventStore());
        var context = Entry("context-oldest", UserMemoryCategory.Context, 1);
        var preference = Entry("preference-old", UserMemoryCategory.Preference, 10);
        await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand { Entry = context });
        await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand { Entry = preference });
        var instructions = Enumerable.Range(0, 48)
            .Select(index => Entry($"instruction-{index}", UserMemoryCategory.Instruction, 100 + index))
            .ToArray();
        foreach (var instruction in instructions)
            await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand { Entry = instruction });

        var added = Entry("preference-new", UserMemoryCategory.Preference, 1_000);
        await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand { Entry = added });

        var expected = new UserMemoryState();
        expected.Entries.Add(context);
        expected.Entries.AddRange(instructions);
        expected.Entries.Add(added);
        agent.State.RetentionPolicy.Should().BeNull();
        agent.State.ToByteArray().Should().Equal(expected.ToByteArray());
    }

    [Fact]
    public async Task PolicyEviction_ShouldEnforceAddedCategoryCap()
    {
        var agent = await CreateAgentAsync(new InMemoryEventStore());
        await agent.HandleReplaceRetentionPolicy(PolicyCommand(
            0,
            "policy-cap",
            Rule(UserMemoryCategory.Preference, maxEntries: 2, evictionRank: 100)));

        await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand
        {
            Entry = Entry("preference-1", UserMemoryCategory.Preference, 1),
        });
        await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand
        {
            Entry = Entry("preference-2", UserMemoryCategory.Preference, 2),
        });
        await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand
        {
            Entry = Entry("preference-3", UserMemoryCategory.Preference, 3),
        });

        agent.State.Entries.Select(static entry => entry.Id)
            .Should().Equal("preference-2", "preference-3");
    }

    [Fact]
    public async Task PolicyEviction_ShouldEvictHigherRankBeforeOlderLowRankEntry()
    {
        var agent = await CreateAgentAsync(new InMemoryEventStore());
        await agent.HandleReplaceRetentionPolicy(PolicyCommand(
            0,
            "policy-rank",
            Rule(UserMemoryCategory.Preference, 0, 0),
            Rule(UserMemoryCategory.Context, 0, 900)));
        await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand
        {
            Entry = Entry("context-newer", UserMemoryCategory.Context, 1_000),
        });
        for (var index = 0; index < 49; index++)
        {
            await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand
            {
                Entry = Entry($"preference-{index}", UserMemoryCategory.Preference, index + 1),
            });
        }

        await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand
        {
            Entry = Entry("instruction-added", UserMemoryCategory.Instruction, 2_000),
        });

        agent.State.Entries.Should().HaveCount(50);
        agent.State.Entries.Should().NotContain(entry => entry.Id == "context-newer");
        agent.State.Entries.Should().Contain(entry => entry.Id == "preference-0");
    }

    [Fact]
    public async Task PolicyEviction_WhenRanksTie_ShouldUseLegacySameCategoryOrder()
    {
        var agent = await CreateAgentAsync(new InMemoryEventStore());
        await agent.HandleReplaceRetentionPolicy(PolicyCommand(0, "policy-tie"));
        await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand
        {
            Entry = Entry("context-oldest", UserMemoryCategory.Context, 1),
        });
        await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand
        {
            Entry = Entry("preference-old", UserMemoryCategory.Preference, 100),
        });
        for (var index = 0; index < 48; index++)
        {
            await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand
            {
                Entry = Entry($"instruction-{index}", UserMemoryCategory.Instruction, 200 + index),
            });
        }

        await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand
        {
            Entry = Entry("preference-added", UserMemoryCategory.Preference, 2_000),
        });

        agent.State.Entries.Should().Contain(entry => entry.Id == "context-oldest");
        agent.State.Entries.Should().NotContain(entry => entry.Id == "preference-old");
        agent.State.Entries.Should().Contain(entry => entry.Id == "preference-added");
    }

    [Fact]
    public async Task PolicyEviction_ShouldNeverEvictEntryBeingAdded()
    {
        var agent = await CreateAgentAsync(new InMemoryEventStore());
        await agent.HandleReplaceRetentionPolicy(PolicyCommand(
            0,
            "policy-new-entry",
            Rule(UserMemoryCategory.Preference, 0, 0),
            Rule(UserMemoryCategory.Context, 0, 1_000)));
        for (var index = 0; index < 50; index++)
        {
            await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand
            {
                Entry = Entry($"preference-{index}", UserMemoryCategory.Preference, 100 + index),
            });
        }

        await agent.HandleAddUserMemoryEntry(new AddUserMemoryEntryCommand
        {
            Entry = Entry("context-added", UserMemoryCategory.Context, 1),
        });

        agent.State.Entries.Should().Contain(entry => entry.Id == "context-added");
        agent.State.Entries.Should().NotContain(entry => entry.Id == "preference-0");
    }

    [Fact]
    public async Task ReplacePolicy_ShouldApplyCasRevisionAndMutationIdempotency()
    {
        var agent = await CreateAgentAsync(new InMemoryEventStore());
        var initial = PolicyCommand(
            0,
            " policy-alpha ",
            Rule(UserMemoryCategory.Context, 5, 10),
            Rule(UserMemoryCategory.Preference, 2, 900));

        await agent.HandleReplaceRetentionPolicy(initial);

        agent.EventSourcing!.CurrentVersion.Should().Be(1);
        agent.State.RetentionPolicy.PolicyRevision.Should().Be(1);
        agent.State.RetentionPolicy.Rules.Select(static rule => rule.Category)
            .Should().Equal(UserMemoryCategory.Preference, UserMemoryCategory.Context);
        agent.State.LastRetentionPolicyMutationId.Should().Be("policy-alpha");

        var retry = PolicyCommand(
            0,
            "policy-alpha",
            Rule(UserMemoryCategory.Preference, 2, 900),
            Rule(UserMemoryCategory.Context, 5, 10));
        await agent.HandleReplaceRetentionPolicy(retry);
        agent.EventSourcing.CurrentVersion.Should().Be(1);

        var conflictingMutation = () => agent.HandleReplaceRetentionPolicy(PolicyCommand(
            0,
            "policy-alpha",
            Rule(UserMemoryCategory.Context, 4, 10)));
        await conflictingMutation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("user_memory_policy_mutation_conflict");

        var staleVersion = () => agent.HandleReplaceRetentionPolicy(PolicyCommand(
            0,
            "policy-beta",
            Rule(UserMemoryCategory.Context, 4, 10)));
        await staleVersion.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("user_memory_expected_state_version_conflict");
        agent.EventSourcing.CurrentVersion.Should().Be(1);

        await agent.HandleReplaceRetentionPolicy(PolicyCommand(
            1,
            "policy-beta",
            Rule(UserMemoryCategory.Context, 4, 10)));
        agent.State.RetentionPolicy.PolicyRevision.Should().Be(2);
        agent.EventSourcing.CurrentVersion.Should().Be(2);
    }

    [Theory]
    [InlineData(UserMemoryCategory.Unspecified, 0, 0, "user_memory_policy_category_invalid")]
    [InlineData(UserMemoryCategory.Preference, -1, 0, "user_memory_policy_max_entries_invalid")]
    [InlineData(UserMemoryCategory.Preference, 51, 0, "user_memory_policy_max_entries_invalid")]
    [InlineData(UserMemoryCategory.Preference, 0, -1, "user_memory_policy_eviction_rank_invalid")]
    [InlineData(UserMemoryCategory.Preference, 0, 1001, "user_memory_policy_eviction_rank_invalid")]
    public async Task ReplacePolicy_WithInvalidRule_ShouldRejectWithoutCommitting(
        UserMemoryCategory category,
        int maxEntries,
        int evictionRank,
        string error)
    {
        var agent = await CreateAgentAsync(new InMemoryEventStore());
        var act = () => agent.HandleReplaceRetentionPolicy(PolicyCommand(
            0,
            "policy-invalid",
            Rule(category, maxEntries, evictionRank)));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(error);
        agent.EventSourcing!.CurrentVersion.Should().Be(0);
    }

    [Fact]
    public async Task ReplacePolicy_WithDuplicateCategory_ShouldRejectWithoutCommitting()
    {
        var agent = await CreateAgentAsync(new InMemoryEventStore());
        var act = () => agent.HandleReplaceRetentionPolicy(PolicyCommand(
            0,
            "policy-duplicate",
            Rule(UserMemoryCategory.Preference, 2, 10),
            Rule(UserMemoryCategory.Preference, 3, 20)));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("user_memory_policy_category_duplicate");
        agent.EventSourcing!.CurrentVersion.Should().Be(0);
    }

    private static ReplaceUserMemoryRetentionPolicyCommand PolicyCommand(
        long expectedStateVersion,
        string mutationId,
        params UserMemoryCategoryRetentionRule[] rules)
    {
        var command = new ReplaceUserMemoryRetentionPolicyCommand
        {
            ExpectedStateVersion = expectedStateVersion,
            MutationId = mutationId,
        };
        command.Rules.AddRange(rules);
        return command;
    }

    private static UserMemoryCategoryRetentionRule Rule(
        UserMemoryCategory category,
        int maxEntries,
        int evictionRank) => new()
        {
            Category = category,
            MaxEntries = maxEntries,
            EvictionRank = evictionRank,
        };

    private static UserMemoryEntryProto Entry(
        string id,
        UserMemoryCategory category,
        long createdAtMs) => new()
        {
            Id = id,
            Category = category,
            Content = $"content-{id}",
            Source = UserMemorySource.Explicit,
            CreatedAtMs = createdAtMs,
            UpdatedAtMs = createdAtMs,
        };

    private static EventEnvelope Envelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", ActorId),
        };

    private static async Task<UserMemoryGAgent> CreateAgentAsync(IEventStore eventStore)
    {
        var services = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<IActorDispatchPort, RecordingActorDispatchPort>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new UserMemoryGAgent
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<UserMemoryState>>(),
        };
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(agent, [ActorId]);
        await agent.ActivateAsync();
        return agent;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) =>
            Task.FromResult(new DispatchAdmission(
                true,
                actorId,
                DateTimeOffset.UtcNow,
                envelope.Id,
                envelope.Propagation?.CorrelationId ?? string.Empty));
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(Lease(request.ActorId, request.CallbackId));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(Lease(request.ActorId, request.CallbackId));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;

        private static RuntimeCallbackLease Lease(string actorId, string callbackId) =>
            new(actorId, callbackId, 1, RuntimeCallbackBackend.InMemory);
    }
}
