using Aevatar.GAgents.ChatHistory;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.GAgents.Registry;
using Aevatar.GAgents.RoleCatalog;
using Aevatar.GAgents.StreamingProxyParticipant;
using Aevatar.GAgents.UserConfig;
using Aevatar.GAgents.UserMemory;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Tools.Cli.Tests;

/// <summary>
/// Unit tests for actor-backed GAgent state transition semantics.
///
/// Each GAgent's <c>TransitionState</c> is <c>protected override</c> with
/// <c>private static Apply*</c> helpers, so these tests replicate the exact
/// transition logic using the public <see cref="StateTransitionMatcher"/> to
/// verify proto definitions and state-change semantics are correct.
/// </summary>
public sealed class ActorBackedGAgentStateTransitionTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  Helpers — replicate each GAgent's TransitionState using the
    //  same StateTransitionMatcher + Apply* pattern.
    // ═══════════════════════════════════════════════════════════════════

    #region GAgentRegistry helpers

    private static GAgentRegistryState ApplyRegistry(GAgentRegistryState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ActorRegisteredEvent>(ApplyRegistered)
            .On<ActorUnregisteredEvent>(ApplyUnregistered)
            .OrCurrent();

    private static GAgentRegistryState ApplyRegistered(
        GAgentRegistryState state, ActorRegisteredEvent evt)
    {
        var next = state.Clone();
        var group = next.Groups.FirstOrDefault(g =>
            string.Equals(g.GagentType, evt.GagentType, StringComparison.Ordinal));

        if (group is null)
        {
            group = new GAgentRegistryEntry { GagentType = evt.GagentType };
            next.Groups.Add(group);
        }

        if (!group.ActorIds.Contains(evt.ActorId))
            group.ActorIds.Add(evt.ActorId);

        return next;
    }

    private static GAgentRegistryState ApplyUnregistered(
        GAgentRegistryState state, ActorUnregisteredEvent evt)
    {
        var next = state.Clone();
        var group = next.Groups.FirstOrDefault(g =>
            string.Equals(g.GagentType, evt.GagentType, StringComparison.Ordinal));

        if (group is null)
            return next;

        group.ActorIds.Remove(evt.ActorId);

        if (group.ActorIds.Count == 0)
            next.Groups.Remove(group);

        return next;
    }

    #endregion

    #region StreamingProxyParticipant helpers

    private static StreamingProxyParticipantGAgentState ApplyParticipant(
        StreamingProxyParticipantGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ParticipantAddedEvent>(ApplyParticipantAdded)
            .On<ParticipantRemovedEvent>(ApplyParticipantRemoved)
            .On<RoomParticipantsRemovedEvent>(ApplyRoomRemoved)
            .OrCurrent();

    private static StreamingProxyParticipantGAgentState ApplyParticipantAdded(
        StreamingProxyParticipantGAgentState state, ParticipantAddedEvent evt)
    {
        var next = state.Clone();

        if (!next.Rooms.TryGetValue(evt.RoomId, out var list))
        {
            list = new ParticipantList();
            next.Rooms[evt.RoomId] = list;
        }

        var existing = list.Participants.FirstOrDefault(p =>
            string.Equals(p.AgentId, evt.AgentId, StringComparison.Ordinal));
        if (existing is not null)
            list.Participants.Remove(existing);

        list.Participants.Add(new ParticipantEntry
        {
            AgentId = evt.AgentId,
            DisplayName = evt.DisplayName,
            JoinedAt = evt.JoinedAt,
        });

        return next;
    }

    private static StreamingProxyParticipantGAgentState ApplyRoomRemoved(
        StreamingProxyParticipantGAgentState state, RoomParticipantsRemovedEvent evt)
    {
        var next = state.Clone();
        next.Rooms.Remove(evt.RoomId);
        return next;
    }

    private static StreamingProxyParticipantGAgentState ApplyParticipantRemoved(
        StreamingProxyParticipantGAgentState state, ParticipantRemovedEvent evt)
    {
        var next = state.Clone();
        if (!next.Rooms.TryGetValue(evt.RoomId, out var list))
            return next;

        for (var index = list.Participants.Count - 1; index >= 0; index--)
        {
            if (string.Equals(list.Participants[index].AgentId, evt.AgentId, StringComparison.Ordinal))
                list.Participants.RemoveAt(index);
        }

        if (list.Participants.Count == 0)
            next.Rooms.Remove(evt.RoomId);

        return next;
    }

    #endregion

    #region UserConfig helpers

    private static UserConfigGAgentState ApplyConfig(
        UserConfigGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<UserConfigUpdatedEvent>(ApplyConfigUpdated)
            .OrCurrent();

    private static UserConfigGAgentState ApplyConfigUpdated(
        UserConfigGAgentState state, UserConfigUpdatedEvent evt) =>
        new()
        {
            DefaultModel = evt.DefaultModel,
            PreferredLlmRoute = evt.PreferredLlmRoute,
            RuntimeMode = evt.RuntimeMode,
            LocalRuntimeBaseUrl = evt.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl = evt.RemoteRuntimeBaseUrl,
            MaxToolRounds = evt.MaxToolRounds,
        };

    #endregion

    #region UserMemory helpers

    private const int MaxEntries = 50; // mirrors UserMemoryGAgent.MaxEntries

    private static UserMemoryState ApplyMemory(
        UserMemoryState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<MemoryEntryAddedEvent>(ApplyMemoryAdded)
            .On<MemoryEntryRemovedEvent>(ApplyMemoryRemoved)
            .On<MemoryEntriesClearedEvent>(ApplyMemoryCleared)
            .OrCurrent();

    private static UserMemoryState ApplyMemoryAdded(
        UserMemoryState state, MemoryEntryAddedEvent evt)
    {
        var next = state.Clone();
        next.Entries.Add(evt.Entry.Clone());

        while (next.Entries.Count > MaxEntries)
        {
            var category = evt.Entry.Category;
            var oldestSameCategory = next.Entries
                .Where(e => string.Equals(e.Category, category, StringComparison.Ordinal)
                            && !string.Equals(e.Id, evt.Entry.Id, StringComparison.Ordinal))
                .OrderBy(e => e.CreatedAt)
                .FirstOrDefault();

            if (oldestSameCategory is not null)
            {
                next.Entries.Remove(oldestSameCategory);
            }
            else
            {
                var globallyOldest = next.Entries
                    .Where(e => !string.Equals(e.Id, evt.Entry.Id, StringComparison.Ordinal))
                    .OrderBy(e => e.CreatedAt)
                    .FirstOrDefault();

                if (globallyOldest is not null)
                    next.Entries.Remove(globallyOldest);
                else
                    break;
            }
        }

        return next;
    }

    private static UserMemoryState ApplyMemoryRemoved(
        UserMemoryState state, MemoryEntryRemovedEvent evt)
    {
        var next = state.Clone();
        var entry = next.Entries.FirstOrDefault(e =>
            string.Equals(e.Id, evt.EntryId, StringComparison.Ordinal));

        if (entry is not null)
            next.Entries.Remove(entry);

        return next;
    }

    private static UserMemoryState ApplyMemoryCleared(
        UserMemoryState state, MemoryEntriesClearedEvent _)
    {
        var next = state.Clone();
        next.Entries.Clear();
        return next;
    }

    #endregion

    #region ChatConversation helpers

    private const int MaxMessages = 500; // mirrors ChatConversationGAgent.MaxMessages

    private static ChatConversationState ApplyConversation(
        ChatConversationState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<MessagesReplacedEvent>(ApplyMessagesReplaced)
            .On<ConversationDeletedEvent>(ApplyConversationDeleted)
            .OrCurrent();

    private static ChatConversationState ApplyMessagesReplaced(
        ChatConversationState state, MessagesReplacedEvent evt)
    {
        var next = new ChatConversationState { Meta = evt.Meta?.Clone() };
        next.Messages.AddRange(evt.Messages);
        return next;
    }

    private static ChatConversationState ApplyConversationDeleted(
        ChatConversationState state, ConversationDeletedEvent evt) =>
        new();

    #endregion

    #region ChatHistoryIndex helpers

    private static ChatHistoryIndexState ApplyHistoryIndex(
        ChatHistoryIndexState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ConversationUpsertedEvent>(ApplyConversationUpserted)
            .On<ConversationRemovedEvent>(ApplyConversationRemoved)
            .OrCurrent();

    private static ChatHistoryIndexState ApplyConversationUpserted(
        ChatHistoryIndexState state, ConversationUpsertedEvent evt)
    {
        var next = state.Clone();

        var existing = next.Conversations.FirstOrDefault(c =>
            string.Equals(c.Id, evt.Meta.Id, StringComparison.Ordinal));
        if (existing is not null)
            next.Conversations.Remove(existing);

        next.Conversations.Add(evt.Meta.Clone());
        return next;
    }

    private static ChatHistoryIndexState ApplyConversationRemoved(
        ChatHistoryIndexState state, ConversationRemovedEvent evt)
    {
        var next = state.Clone();

        var existing = next.Conversations.FirstOrDefault(c =>
            string.Equals(c.Id, evt.ConversationId, StringComparison.Ordinal));
        if (existing is not null)
            next.Conversations.Remove(existing);

        return next;
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    //  1. GAgentRegistryGAgent
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Registry_RegisterActor_AddsToNewGroup()
    {
        var state = new GAgentRegistryState();
        var evt = new ActorRegisteredEvent { GagentType = "TypeA", ActorId = "a1" };

        var next = ApplyRegistry(state, evt);

        next.Groups.Should().HaveCount(1);
        next.Groups[0].GagentType.Should().Be("TypeA");
        next.Groups[0].ActorIds.Should().ContainSingle().Which.Should().Be("a1");
    }

    [Fact]
    public void Registry_RegisterActor_AddsToExistingGroup()
    {
        var state = new GAgentRegistryState();
        state.Groups.Add(new GAgentRegistryEntry
        {
            GagentType = "TypeA",
            ActorIds = { "a1" },
        });

        var evt = new ActorRegisteredEvent { GagentType = "TypeA", ActorId = "a2" };

        var next = ApplyRegistry(state, evt);

        next.Groups.Should().HaveCount(1);
        next.Groups[0].ActorIds.Should().BeEquivalentTo(new[] { "a1", "a2" });
    }

    [Fact]
    public void Registry_RegisterActor_Idempotent_DoesNotDuplicate()
    {
        var state = new GAgentRegistryState();
        state.Groups.Add(new GAgentRegistryEntry
        {
            GagentType = "TypeA",
            ActorIds = { "a1" },
        });

        var evt = new ActorRegisteredEvent { GagentType = "TypeA", ActorId = "a1" };

        var next = ApplyRegistry(state, evt);

        next.Groups.Should().HaveCount(1);
        next.Groups[0].ActorIds.Should().ContainSingle().Which.Should().Be("a1");
    }

    [Fact]
    public void Registry_RegisterActor_MultipleGroups()
    {
        var state = new GAgentRegistryState();

        var s1 = ApplyRegistry(state, new ActorRegisteredEvent { GagentType = "TypeA", ActorId = "a1" });
        var s2 = ApplyRegistry(s1, new ActorRegisteredEvent { GagentType = "TypeB", ActorId = "b1" });

        s2.Groups.Should().HaveCount(2);
        s2.Groups.Should().Contain(g => g.GagentType == "TypeA");
        s2.Groups.Should().Contain(g => g.GagentType == "TypeB");
    }

    [Fact]
    public void Registry_UnregisterActor_RemovesFromGroup()
    {
        var state = new GAgentRegistryState();
        state.Groups.Add(new GAgentRegistryEntry
        {
            GagentType = "TypeA",
            ActorIds = { "a1", "a2" },
        });

        var evt = new ActorUnregisteredEvent { GagentType = "TypeA", ActorId = "a1" };

        var next = ApplyRegistry(state, evt);

        next.Groups.Should().HaveCount(1);
        next.Groups[0].ActorIds.Should().ContainSingle().Which.Should().Be("a2");
    }

    [Fact]
    public void Registry_UnregisterActor_RemovesEmptyGroup()
    {
        var state = new GAgentRegistryState();
        state.Groups.Add(new GAgentRegistryEntry
        {
            GagentType = "TypeA",
            ActorIds = { "a1" },
        });

        var evt = new ActorUnregisteredEvent { GagentType = "TypeA", ActorId = "a1" };

        var next = ApplyRegistry(state, evt);

        next.Groups.Should().BeEmpty();
    }

    [Fact]
    public void Registry_UnregisterActor_NonexistentGroup_ReturnsUnchanged()
    {
        var state = new GAgentRegistryState();

        var evt = new ActorUnregisteredEvent { GagentType = "NoSuchType", ActorId = "a1" };

        var next = ApplyRegistry(state, evt);

        next.Groups.Should().BeEmpty();
    }

    [Fact]
    public void Registry_UnregisterActor_NonexistentId_ReturnsUnchanged()
    {
        var state = new GAgentRegistryState();
        state.Groups.Add(new GAgentRegistryEntry
        {
            GagentType = "TypeA",
            ActorIds = { "a1" },
        });

        var evt = new ActorUnregisteredEvent { GagentType = "TypeA", ActorId = "no-such-id" };

        var next = ApplyRegistry(state, evt);

        next.Groups.Should().HaveCount(1);
        next.Groups[0].ActorIds.Should().ContainSingle().Which.Should().Be("a1");
    }

    [Fact]
    public void Registry_EmptyState_IsValid()
    {
        var state = new GAgentRegistryState();

        state.Groups.Should().BeEmpty();
    }

    [Fact]
    public void Registry_UnknownEvent_ReturnsCurrentState()
    {
        var state = new GAgentRegistryState();
        state.Groups.Add(new GAgentRegistryEntry { GagentType = "T", ActorIds = { "x" } });

        // ParticipantAddedEvent is unrelated to the registry matcher
        var unrelated = new ParticipantAddedEvent { RoomId = "r1", AgentId = "ag1" };

        var next = ApplyRegistry(state, unrelated);

        next.Should().BeSameAs(state);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  2. StreamingProxyParticipantGAgent
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Participant_Add_CreatesRoom()
    {
        var state = new StreamingProxyParticipantGAgentState();
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var evt = new ParticipantAddedEvent
        {
            RoomId = "room1",
            AgentId = "agent1",
            DisplayName = "Agent One",
            JoinedAt = now,
        };

        var next = ApplyParticipant(state, evt);

        next.Rooms.Should().ContainKey("room1");
        next.Rooms["room1"].Participants.Should().HaveCount(1);
        next.Rooms["room1"].Participants[0].AgentId.Should().Be("agent1");
        next.Rooms["room1"].Participants[0].DisplayName.Should().Be("Agent One");
        next.Rooms["room1"].Participants[0].JoinedAt.Should().Be(now);
    }

    [Fact]
    public void Participant_Add_MultipleToSameRoom()
    {
        var state = new StreamingProxyParticipantGAgentState();
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var s1 = ApplyParticipant(state, new ParticipantAddedEvent
        {
            RoomId = "room1", AgentId = "agent1", DisplayName = "A1", JoinedAt = now,
        });
        var s2 = ApplyParticipant(s1, new ParticipantAddedEvent
        {
            RoomId = "room1", AgentId = "agent2", DisplayName = "A2", JoinedAt = now,
        });

        s2.Rooms["room1"].Participants.Should().HaveCount(2);
    }

    [Fact]
    public void Participant_Add_DuplicateAgentId_UpsertsEntry()
    {
        var state = new StreamingProxyParticipantGAgentState();
        var time1 = Timestamp.FromDateTimeOffset(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var time2 = Timestamp.FromDateTimeOffset(new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var s1 = ApplyParticipant(state, new ParticipantAddedEvent
        {
            RoomId = "room1", AgentId = "agent1", DisplayName = "OldName", JoinedAt = time1,
        });
        var s2 = ApplyParticipant(s1, new ParticipantAddedEvent
        {
            RoomId = "room1", AgentId = "agent1", DisplayName = "NewName", JoinedAt = time2,
        });

        s2.Rooms["room1"].Participants.Should().HaveCount(1);
        s2.Rooms["room1"].Participants[0].DisplayName.Should().Be("NewName");
        s2.Rooms["room1"].Participants[0].JoinedAt.Should().Be(time2);
    }

    [Fact]
    public void Participant_RemoveRoom_RemovesAllParticipants()
    {
        var state = new StreamingProxyParticipantGAgentState();
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var s1 = ApplyParticipant(state, new ParticipantAddedEvent
        {
            RoomId = "room1", AgentId = "agent1", DisplayName = "A1", JoinedAt = now,
        });
        var s2 = ApplyParticipant(s1, new ParticipantAddedEvent
        {
            RoomId = "room1", AgentId = "agent2", DisplayName = "A2", JoinedAt = now,
        });

        var s3 = ApplyParticipant(s2, new RoomParticipantsRemovedEvent { RoomId = "room1" });

        s3.Rooms.Should().NotContainKey("room1");
    }

    [Fact]
    public void Participant_RemoveRoom_NonexistentRoom_ReturnsUnchanged()
    {
        var state = new StreamingProxyParticipantGAgentState();

        var next = ApplyParticipant(state, new RoomParticipantsRemovedEvent { RoomId = "no-room" });

        next.Rooms.Should().BeEmpty();
    }

    [Fact]
    public void Participant_RemoveRoom_DoesNotAffectOtherRooms()
    {
        var state = new StreamingProxyParticipantGAgentState();
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var s1 = ApplyParticipant(state, new ParticipantAddedEvent
        {
            RoomId = "room1", AgentId = "a1", DisplayName = "A1", JoinedAt = now,
        });
        var s2 = ApplyParticipant(s1, new ParticipantAddedEvent
        {
            RoomId = "room2", AgentId = "a2", DisplayName = "A2", JoinedAt = now,
        });

        var s3 = ApplyParticipant(s2, new RoomParticipantsRemovedEvent { RoomId = "room1" });

        s3.Rooms.Should().ContainKey("room2");
        s3.Rooms.Should().NotContainKey("room1");
    }

    [Fact]
    public void Participant_RemoveParticipant_RemovesOnlyTargetParticipant()
    {
        var state = new StreamingProxyParticipantGAgentState();
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var s1 = ApplyParticipant(state, new ParticipantAddedEvent
        {
            RoomId = "room1", AgentId = "a1", DisplayName = "A1", JoinedAt = now,
        });
        var s2 = ApplyParticipant(s1, new ParticipantAddedEvent
        {
            RoomId = "room1", AgentId = "a2", DisplayName = "A2", JoinedAt = now,
        });

        var s3 = ApplyParticipant(s2, new ParticipantRemovedEvent { RoomId = "room1", AgentId = "a1" });

        s3.Rooms.Should().ContainKey("room1");
        s3.Rooms["room1"].Participants.Should().ContainSingle(p => p.AgentId == "a2");
    }

    [Fact]
    public void Participant_RemoveParticipant_LastParticipant_RemovesRoom()
    {
        var state = new StreamingProxyParticipantGAgentState();
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var s1 = ApplyParticipant(state, new ParticipantAddedEvent
        {
            RoomId = "room1", AgentId = "a1", DisplayName = "A1", JoinedAt = now,
        });

        var s2 = ApplyParticipant(s1, new ParticipantRemovedEvent { RoomId = "room1", AgentId = "a1" });

        s2.Rooms.Should().NotContainKey("room1");
    }

    [Fact]
    public void Participant_EmptyState_IsValid()
    {
        var state = new StreamingProxyParticipantGAgentState();

        state.Rooms.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  3. UserConfigGAgent
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void UserConfig_Update_ReplacesAllFields()
    {
        var state = new UserConfigGAgentState
        {
            DefaultModel = "old-model",
            PreferredLlmRoute = "old-route",
            RuntimeMode = "remote",
            LocalRuntimeBaseUrl = "http://old-local",
            RemoteRuntimeBaseUrl = "http://old-remote",
            MaxToolRounds = 5,
        };

        var evt = new UserConfigUpdatedEvent
        {
            DefaultModel = "new-model",
            PreferredLlmRoute = "new-route",
            RuntimeMode = "local",
            LocalRuntimeBaseUrl = "http://new-local",
            RemoteRuntimeBaseUrl = "http://new-remote",
            MaxToolRounds = 10,
        };

        var next = ApplyConfig(state, evt);

        next.DefaultModel.Should().Be("new-model");
        next.PreferredLlmRoute.Should().Be("new-route");
        next.RuntimeMode.Should().Be("local");
        next.LocalRuntimeBaseUrl.Should().Be("http://new-local");
        next.RemoteRuntimeBaseUrl.Should().Be("http://new-remote");
        next.MaxToolRounds.Should().Be(10);
    }

    [Fact]
    public void UserConfig_Update_FromEmptyState()
    {
        var state = new UserConfigGAgentState();

        var evt = new UserConfigUpdatedEvent
        {
            DefaultModel = "gpt-4",
            PreferredLlmRoute = "azure",
            RuntimeMode = "remote",
            LocalRuntimeBaseUrl = "",
            RemoteRuntimeBaseUrl = "https://api.example.com",
            MaxToolRounds = 3,
        };

        var next = ApplyConfig(state, evt);

        next.DefaultModel.Should().Be("gpt-4");
        next.MaxToolRounds.Should().Be(3);
    }

    [Fact]
    public void UserConfig_Update_FullReplacement_DoesNotRetainOldFields()
    {
        var state = new UserConfigGAgentState
        {
            DefaultModel = "old-model",
            MaxToolRounds = 99,
        };

        // Event with zero/empty values for some fields
        var evt = new UserConfigUpdatedEvent
        {
            DefaultModel = "new-model",
            PreferredLlmRoute = "",
            RuntimeMode = "",
            LocalRuntimeBaseUrl = "",
            RemoteRuntimeBaseUrl = "",
            MaxToolRounds = 0,
        };

        var next = ApplyConfig(state, evt);

        next.DefaultModel.Should().Be("new-model");
        next.MaxToolRounds.Should().Be(0);
        next.PreferredLlmRoute.Should().BeEmpty();
    }

    [Fact]
    public void UserConfig_UnknownEvent_ReturnsCurrentState()
    {
        var state = new UserConfigGAgentState { DefaultModel = "keep-me" };

        var unrelated = new ActorRegisteredEvent { GagentType = "T", ActorId = "x" };

        var next = ApplyConfig(state, unrelated);

        next.Should().BeSameAs(state);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  4. UserMemoryGAgent
    // ═══════════════════════════════════════════════════════════════════

    private static UserMemoryEntryProto MakeEntry(string id, string category, long createdAt) =>
        new()
        {
            Id = id,
            Category = category,
            Content = $"content-{id}",
            Source = "test",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

    [Fact]
    public void Memory_AddEntry_ToEmptyState()
    {
        var state = new UserMemoryState();
        var entry = MakeEntry("e1", "cat-a", 1000);
        var evt = new MemoryEntryAddedEvent { Entry = entry };

        var next = ApplyMemory(state, evt);

        next.Entries.Should().HaveCount(1);
        next.Entries[0].Id.Should().Be("e1");
        next.Entries[0].Category.Should().Be("cat-a");
    }

    [Fact]
    public void Memory_AddEntry_MultipleEntries()
    {
        var state = new UserMemoryState();

        var s1 = ApplyMemory(state, new MemoryEntryAddedEvent { Entry = MakeEntry("e1", "cat-a", 1000) });
        var s2 = ApplyMemory(s1, new MemoryEntryAddedEvent { Entry = MakeEntry("e2", "cat-b", 2000) });

        s2.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void Memory_AddEntry_EvictsOldestSameCategoryAtCap()
    {
        var state = new UserMemoryState();

        // Fill to capacity with entries in "cat-a"
        for (var i = 0; i < MaxEntries; i++)
        {
            state = ApplyMemory(state, new MemoryEntryAddedEvent
            {
                Entry = MakeEntry($"e{i}", "cat-a", i * 100),
            });
        }

        state.Entries.Should().HaveCount(MaxEntries);

        // Add one more in same category — should evict the oldest (e0)
        var next = ApplyMemory(state, new MemoryEntryAddedEvent
        {
            Entry = MakeEntry("new-entry", "cat-a", MaxEntries * 100),
        });

        next.Entries.Should().HaveCount(MaxEntries);
        next.Entries.Should().NotContain(e => e.Id == "e0");
        next.Entries.Should().Contain(e => e.Id == "new-entry");
    }

    [Fact]
    public void Memory_AddEntry_EvictsGloballyOldestWhenNoSameCategory()
    {
        var state = new UserMemoryState();

        // Fill to capacity: all entries in "cat-a"
        for (var i = 0; i < MaxEntries; i++)
        {
            state = ApplyMemory(state, new MemoryEntryAddedEvent
            {
                Entry = MakeEntry($"e{i}", "cat-a", i * 100),
            });
        }

        // Add in a different category — no same-category to evict,
        // so globally oldest (e0) should be evicted
        var next = ApplyMemory(state, new MemoryEntryAddedEvent
        {
            Entry = MakeEntry("diff-cat", "cat-b", MaxEntries * 100),
        });

        next.Entries.Should().HaveCount(MaxEntries);
        next.Entries.Should().NotContain(e => e.Id == "e0");
        next.Entries.Should().Contain(e => e.Id == "diff-cat");
    }

    [Fact]
    public void Memory_AddEntry_ExactlyAtCap_NoEviction()
    {
        var state = new UserMemoryState();

        for (var i = 0; i < MaxEntries; i++)
        {
            state = ApplyMemory(state, new MemoryEntryAddedEvent
            {
                Entry = MakeEntry($"e{i}", "cat-a", i * 100),
            });
        }

        state.Entries.Should().HaveCount(MaxEntries);
        // All entries still present — no eviction at exactly cap
        state.Entries.Should().Contain(e => e.Id == "e0");
        state.Entries.Should().Contain(e => e.Id == $"e{MaxEntries - 1}");
    }

    [Fact]
    public void Memory_RemoveEntry_RemovesById()
    {
        var state = new UserMemoryState();
        state = ApplyMemory(state, new MemoryEntryAddedEvent { Entry = MakeEntry("e1", "cat-a", 1000) });
        state = ApplyMemory(state, new MemoryEntryAddedEvent { Entry = MakeEntry("e2", "cat-a", 2000) });

        var next = ApplyMemory(state, new MemoryEntryRemovedEvent { EntryId = "e1" });

        next.Entries.Should().HaveCount(1);
        next.Entries[0].Id.Should().Be("e2");
    }

    [Fact]
    public void Memory_RemoveEntry_NonexistentId_ReturnsUnchanged()
    {
        var state = new UserMemoryState();
        state = ApplyMemory(state, new MemoryEntryAddedEvent { Entry = MakeEntry("e1", "cat-a", 1000) });

        var next = ApplyMemory(state, new MemoryEntryRemovedEvent { EntryId = "no-such-id" });

        next.Entries.Should().HaveCount(1);
        next.Entries[0].Id.Should().Be("e1");
    }

    [Fact]
    public void Memory_ClearEntries_RemovesAll()
    {
        var state = new UserMemoryState();
        state = ApplyMemory(state, new MemoryEntryAddedEvent { Entry = MakeEntry("e1", "cat-a", 1000) });
        state = ApplyMemory(state, new MemoryEntryAddedEvent { Entry = MakeEntry("e2", "cat-b", 2000) });

        var next = ApplyMemory(state, new MemoryEntriesClearedEvent());

        next.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Memory_ClearEntries_OnEmptyState_ReturnsEmpty()
    {
        var state = new UserMemoryState();

        var next = ApplyMemory(state, new MemoryEntriesClearedEvent());

        next.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Memory_EmptyState_IsValid()
    {
        var state = new UserMemoryState();

        state.Entries.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  5a. ChatConversationGAgent
    // ═══════════════════════════════════════════════════════════════════

    private static StoredChatMessageProto MakeMessage(string id, string role, string content) =>
        new()
        {
            Id = id,
            Role = role,
            Content = content,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    [Fact]
    public void Conversation_ReplaceMessages_SetsState()
    {
        var state = new ChatConversationState();
        var meta = new ConversationMetaProto
        {
            Id = "conv1",
            Title = "Test Conversation",
            MessageCount = 2,
        };
        var evt = new MessagesReplacedEvent { Meta = meta };
        evt.Messages.Add(MakeMessage("m1", "user", "Hello"));
        evt.Messages.Add(MakeMessage("m2", "assistant", "Hi"));

        var next = ApplyConversation(state, evt);

        next.Meta.Should().NotBeNull();
        next.Meta!.Id.Should().Be("conv1");
        next.Meta.Title.Should().Be("Test Conversation");
        next.Messages.Should().HaveCount(2);
        next.Messages[0].Role.Should().Be("user");
        next.Messages[1].Role.Should().Be("assistant");
    }

    [Fact]
    public void Conversation_ReplaceMessages_OverwritesPreviousState()
    {
        var state = new ChatConversationState
        {
            Meta = new ConversationMetaProto { Id = "conv1", Title = "Old Title" },
        };
        state.Messages.Add(MakeMessage("old1", "user", "old content"));

        var newMeta = new ConversationMetaProto { Id = "conv1", Title = "New Title", MessageCount = 1 };
        var evt = new MessagesReplacedEvent { Meta = newMeta };
        evt.Messages.Add(MakeMessage("new1", "assistant", "new content"));

        var next = ApplyConversation(state, evt);

        next.Meta!.Title.Should().Be("New Title");
        next.Messages.Should().HaveCount(1);
        next.Messages[0].Id.Should().Be("new1");
    }

    [Fact]
    public void Conversation_Delete_ClearsState()
    {
        var state = new ChatConversationState
        {
            Meta = new ConversationMetaProto { Id = "conv1", Title = "Will be deleted" },
        };
        state.Messages.Add(MakeMessage("m1", "user", "content"));

        var evt = new ConversationDeletedEvent { ConversationId = "conv1" };

        var next = ApplyConversation(state, evt);

        next.Meta.Should().BeNull();
        next.Messages.Should().BeEmpty();
    }

    [Fact]
    public void Conversation_Delete_OnEmptyState_ReturnsEmpty()
    {
        var state = new ChatConversationState();

        var evt = new ConversationDeletedEvent { ConversationId = "conv1" };

        var next = ApplyConversation(state, evt);

        next.Meta.Should().BeNull();
        next.Messages.Should().BeEmpty();
    }

    [Fact]
    public void Conversation_EmptyState_IsValid()
    {
        var state = new ChatConversationState();

        state.Meta.Should().BeNull();
        state.Messages.Should().BeEmpty();
    }

    [Fact]
    public void Conversation_UnknownEvent_ReturnsCurrentState()
    {
        var state = new ChatConversationState
        {
            Meta = new ConversationMetaProto { Id = "keep" },
        };

        var unrelated = new ActorRegisteredEvent { GagentType = "T", ActorId = "x" };

        var next = ApplyConversation(state, unrelated);

        next.Should().BeSameAs(state);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  5b. ChatHistoryIndexGAgent
    // ═══════════════════════════════════════════════════════════════════

    private static ConversationMetaProto MakeMeta(string id, string title) =>
        new()
        {
            Id = id,
            Title = title,
            CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    [Fact]
    public void HistoryIndex_UpsertConversation_AddsNew()
    {
        var state = new ChatHistoryIndexState();
        var evt = new ConversationUpsertedEvent { Meta = MakeMeta("c1", "First Chat") };

        var next = ApplyHistoryIndex(state, evt);

        next.Conversations.Should().HaveCount(1);
        next.Conversations[0].Id.Should().Be("c1");
        next.Conversations[0].Title.Should().Be("First Chat");
    }

    [Fact]
    public void HistoryIndex_UpsertConversation_UpdatesExisting()
    {
        var state = new ChatHistoryIndexState();
        state.Conversations.Add(MakeMeta("c1", "Old Title"));

        var evt = new ConversationUpsertedEvent { Meta = MakeMeta("c1", "New Title") };

        var next = ApplyHistoryIndex(state, evt);

        next.Conversations.Should().HaveCount(1);
        next.Conversations[0].Title.Should().Be("New Title");
    }

    [Fact]
    public void HistoryIndex_UpsertConversation_MultipleConversations()
    {
        var state = new ChatHistoryIndexState();

        var s1 = ApplyHistoryIndex(state, new ConversationUpsertedEvent { Meta = MakeMeta("c1", "Chat 1") });
        var s2 = ApplyHistoryIndex(s1, new ConversationUpsertedEvent { Meta = MakeMeta("c2", "Chat 2") });

        s2.Conversations.Should().HaveCount(2);
    }

    [Fact]
    public void HistoryIndex_RemoveConversation_RemovesById()
    {
        var state = new ChatHistoryIndexState();
        state.Conversations.Add(MakeMeta("c1", "Chat 1"));
        state.Conversations.Add(MakeMeta("c2", "Chat 2"));

        var evt = new ConversationRemovedEvent { ConversationId = "c1" };

        var next = ApplyHistoryIndex(state, evt);

        next.Conversations.Should().HaveCount(1);
        next.Conversations[0].Id.Should().Be("c2");
    }

    [Fact]
    public void HistoryIndex_RemoveConversation_NonexistentId_ReturnsUnchanged()
    {
        var state = new ChatHistoryIndexState();
        state.Conversations.Add(MakeMeta("c1", "Chat 1"));

        var evt = new ConversationRemovedEvent { ConversationId = "no-such-id" };

        var next = ApplyHistoryIndex(state, evt);

        next.Conversations.Should().HaveCount(1);
        next.Conversations[0].Id.Should().Be("c1");
    }

    [Fact]
    public void HistoryIndex_RemoveConversation_FromEmptyState_ReturnsEmpty()
    {
        var state = new ChatHistoryIndexState();

        var evt = new ConversationRemovedEvent { ConversationId = "c1" };

        var next = ApplyHistoryIndex(state, evt);

        next.Conversations.Should().BeEmpty();
    }

    [Fact]
    public void HistoryIndex_EmptyState_IsValid()
    {
        var state = new ChatHistoryIndexState();

        state.Conversations.Should().BeEmpty();
    }

    [Fact]
    public void HistoryIndex_UnknownEvent_ReturnsCurrentState()
    {
        var state = new ChatHistoryIndexState();
        state.Conversations.Add(MakeMeta("c1", "Keep"));

        var unrelated = new ActorRegisteredEvent { GagentType = "T", ActorId = "x" };

        var next = ApplyHistoryIndex(state, unrelated);

        next.Should().BeSameAs(state);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  8. ConnectorCatalogGAgent
    // ═══════════════════════════════════════════════════════════════════

    #region ConnectorCatalog helpers

    private static ConnectorCatalogState ApplyConnectorCatalog(
        ConnectorCatalogState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ConnectorCatalogSavedEvent>(ApplyConnectorCatalogSaved)
            .On<ConnectorDraftSavedEvent>(ApplyConnectorDraftSaved)
            .On<ConnectorDraftDeletedEvent>(ApplyConnectorDraftDeleted)
            .OrCurrent();

    private static ConnectorCatalogState ApplyConnectorCatalogSaved(
        ConnectorCatalogState state, ConnectorCatalogSavedEvent evt)
    {
        var next = state.Clone();
        next.Connectors.Clear();
        next.Connectors.AddRange(evt.Connectors);
        return next;
    }

    private static ConnectorCatalogState ApplyConnectorDraftSaved(
        ConnectorCatalogState state, ConnectorDraftSavedEvent evt)
    {
        var next = state.Clone();
        next.Draft = new ConnectorDraftEntry
        {
            Draft = evt.Draft?.Clone(),
            UpdatedAtUtc = evt.UpdatedAtUtc,
        };
        return next;
    }

    private static ConnectorCatalogState ApplyConnectorDraftDeleted(
        ConnectorCatalogState state, ConnectorDraftDeletedEvent _)
    {
        var next = state.Clone();
        next.Draft = null;
        return next;
    }

    private static ConnectorDefinitionEntry MakeConnector(string name, string type = "http") =>
        new()
        {
            Name = name,
            Type = type,
            Enabled = true,
            TimeoutMs = 30000,
            Retry = 3,
        };

    #endregion

    [Fact]
    public void ConnectorCatalog_SaveCatalog_ReplacesAll()
    {
        var state = new ConnectorCatalogState();
        state.Connectors.Add(MakeConnector("old-conn"));

        var evt = new ConnectorCatalogSavedEvent();
        evt.Connectors.Add(MakeConnector("new-conn-1"));
        evt.Connectors.Add(MakeConnector("new-conn-2", "mcp"));

        var next = ApplyConnectorCatalog(state, evt);

        next.Connectors.Should().HaveCount(2);
        next.Connectors[0].Name.Should().Be("new-conn-1");
        next.Connectors[1].Name.Should().Be("new-conn-2");
        next.Connectors[1].Type.Should().Be("mcp");
    }

    [Fact]
    public void ConnectorCatalog_SaveCatalog_EmptyList_ClearsAll()
    {
        var state = new ConnectorCatalogState();
        state.Connectors.Add(MakeConnector("existing"));

        var evt = new ConnectorCatalogSavedEvent();

        var next = ApplyConnectorCatalog(state, evt);

        next.Connectors.Should().BeEmpty();
    }

    [Fact]
    public void ConnectorCatalog_SaveDraft_SetsNewDraft()
    {
        var state = new ConnectorCatalogState();
        var ts = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var evt = new ConnectorDraftSavedEvent
        {
            Draft = MakeConnector("draft-conn", "cli"),
            UpdatedAtUtc = ts,
        };

        var next = ApplyConnectorCatalog(state, evt);

        next.Draft.Should().NotBeNull();
        next.Draft!.Draft.Name.Should().Be("draft-conn");
        next.Draft.Draft.Type.Should().Be("cli");
        next.Draft.UpdatedAtUtc.Should().Be(ts);
    }

    [Fact]
    public void ConnectorCatalog_SaveDraft_NullDraft_SetsEntryWithNullPayload()
    {
        var state = new ConnectorCatalogState();
        var ts = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var evt = new ConnectorDraftSavedEvent
        {
            Draft = null,
            UpdatedAtUtc = ts,
        };

        var next = ApplyConnectorCatalog(state, evt);

        next.Draft.Should().NotBeNull();
        next.Draft!.Draft.Should().BeNull();
        next.Draft.UpdatedAtUtc.Should().Be(ts);
    }

    [Fact]
    public void ConnectorCatalog_SaveDraft_OverwritesPreviousDraft()
    {
        var state = new ConnectorCatalogState
        {
            Draft = new ConnectorDraftEntry
            {
                Draft = MakeConnector("old-draft"),
                UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(-1)),
            },
        };
        var ts = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var evt = new ConnectorDraftSavedEvent
        {
            Draft = MakeConnector("new-draft", "mcp"),
            UpdatedAtUtc = ts,
        };

        var next = ApplyConnectorCatalog(state, evt);

        next.Draft!.Draft.Name.Should().Be("new-draft");
        next.Draft.UpdatedAtUtc.Should().Be(ts);
    }

    [Fact]
    public void ConnectorCatalog_DeleteDraft_ClearsDraft()
    {
        var state = new ConnectorCatalogState
        {
            Draft = new ConnectorDraftEntry
            {
                Draft = MakeConnector("to-delete"),
                UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

        var next = ApplyConnectorCatalog(state, new ConnectorDraftDeletedEvent());

        next.Draft.Should().BeNull();
    }

    [Fact]
    public void ConnectorCatalog_DeleteDraft_NoDraft_ReturnsNullDraft()
    {
        var state = new ConnectorCatalogState();

        var next = ApplyConnectorCatalog(state, new ConnectorDraftDeletedEvent());

        next.Draft.Should().BeNull();
    }

    [Fact]
    public void ConnectorCatalog_SaveCatalog_DoesNotAffectDraft()
    {
        var draft = new ConnectorDraftEntry
        {
            Draft = MakeConnector("my-draft"),
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        var state = new ConnectorCatalogState { Draft = draft };

        var evt = new ConnectorCatalogSavedEvent();
        evt.Connectors.Add(MakeConnector("catalog-conn"));

        var next = ApplyConnectorCatalog(state, evt);

        next.Connectors.Should().HaveCount(1);
        next.Draft.Should().NotBeNull();
        next.Draft!.Draft.Name.Should().Be("my-draft");
    }

    [Fact]
    public void ConnectorCatalog_EmptyState_IsValid()
    {
        var state = new ConnectorCatalogState();

        state.Connectors.Should().BeEmpty();
        state.Draft.Should().BeNull();
    }

    [Fact]
    public void ConnectorCatalog_UnknownEvent_ReturnsCurrentState()
    {
        var state = new ConnectorCatalogState();
        state.Connectors.Add(MakeConnector("keep"));

        var unrelated = new ActorRegisteredEvent { GagentType = "T", ActorId = "x" };

        var next = ApplyConnectorCatalog(state, unrelated);

        next.Should().BeSameAs(state);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  9. RoleCatalogGAgent
    // ═══════════════════════════════════════════════════════════════════

    #region RoleCatalog helpers

    private static RoleCatalogState ApplyRoleCatalog(
        RoleCatalogState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<RoleCatalogSavedEvent>(ApplyRoleCatalogSaved)
            .On<RoleDraftSavedEvent>(ApplyRoleDraftSaved)
            .On<RoleDraftDeletedEvent>(ApplyRoleDraftDeleted)
            .OrCurrent();

    private static RoleCatalogState ApplyRoleCatalogSaved(
        RoleCatalogState state, RoleCatalogSavedEvent evt)
    {
        var next = state.Clone();
        next.Roles.Clear();
        next.Roles.AddRange(evt.Roles);
        return next;
    }

    private static RoleCatalogState ApplyRoleDraftSaved(
        RoleCatalogState state, RoleDraftSavedEvent evt)
    {
        var next = state.Clone();
        next.Draft = new RoleDraftEntry
        {
            Draft = evt.Draft?.Clone(),
            UpdatedAtUtc = evt.UpdatedAtUtc,
        };
        return next;
    }

    private static RoleCatalogState ApplyRoleDraftDeleted(
        RoleCatalogState state, RoleDraftDeletedEvent _)
    {
        var next = state.Clone();
        next.Draft = null;
        return next;
    }

    private static RoleDefinitionEntry MakeRole(string id, string name = "Test Role") =>
        new()
        {
            Id = id,
            Name = name,
            SystemPrompt = $"You are {name}",
            Provider = "anthropic",
            Model = "claude-opus",
        };

    #endregion

    [Fact]
    public void RoleCatalog_SaveCatalog_ReplacesAll()
    {
        var state = new RoleCatalogState();
        state.Roles.Add(MakeRole("old-role"));

        var evt = new RoleCatalogSavedEvent();
        evt.Roles.Add(MakeRole("role-1", "Assistant"));
        evt.Roles.Add(MakeRole("role-2", "Translator"));

        var next = ApplyRoleCatalog(state, evt);

        next.Roles.Should().HaveCount(2);
        next.Roles[0].Id.Should().Be("role-1");
        next.Roles[0].Name.Should().Be("Assistant");
        next.Roles[1].Id.Should().Be("role-2");
    }

    [Fact]
    public void RoleCatalog_SaveCatalog_EmptyList_ClearsAll()
    {
        var state = new RoleCatalogState();
        state.Roles.Add(MakeRole("existing"));

        var evt = new RoleCatalogSavedEvent();

        var next = ApplyRoleCatalog(state, evt);

        next.Roles.Should().BeEmpty();
    }

    [Fact]
    public void RoleCatalog_SaveDraft_SetsNewDraft()
    {
        var state = new RoleCatalogState();
        var ts = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var evt = new RoleDraftSavedEvent
        {
            Draft = MakeRole("draft-role", "Draft Assistant"),
            UpdatedAtUtc = ts,
        };

        var next = ApplyRoleCatalog(state, evt);

        next.Draft.Should().NotBeNull();
        next.Draft!.Draft.Id.Should().Be("draft-role");
        next.Draft.Draft.Name.Should().Be("Draft Assistant");
        next.Draft.UpdatedAtUtc.Should().Be(ts);
    }

    [Fact]
    public void RoleCatalog_SaveDraft_NullDraft_SetsEntryWithNullPayload()
    {
        var state = new RoleCatalogState();
        var ts = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var evt = new RoleDraftSavedEvent
        {
            Draft = null,
            UpdatedAtUtc = ts,
        };

        var next = ApplyRoleCatalog(state, evt);

        next.Draft.Should().NotBeNull();
        next.Draft!.Draft.Should().BeNull();
        next.Draft.UpdatedAtUtc.Should().Be(ts);
    }

    [Fact]
    public void RoleCatalog_SaveDraft_OverwritesPreviousDraft()
    {
        var state = new RoleCatalogState
        {
            Draft = new RoleDraftEntry
            {
                Draft = MakeRole("old-draft"),
                UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(-1)),
            },
        };
        var ts = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var evt = new RoleDraftSavedEvent
        {
            Draft = MakeRole("new-draft", "Updated Role"),
            UpdatedAtUtc = ts,
        };

        var next = ApplyRoleCatalog(state, evt);

        next.Draft!.Draft.Name.Should().Be("Updated Role");
        next.Draft.UpdatedAtUtc.Should().Be(ts);
    }

    [Fact]
    public void RoleCatalog_DeleteDraft_ClearsDraft()
    {
        var state = new RoleCatalogState
        {
            Draft = new RoleDraftEntry
            {
                Draft = MakeRole("to-delete"),
                UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

        var next = ApplyRoleCatalog(state, new RoleDraftDeletedEvent());

        next.Draft.Should().BeNull();
    }

    [Fact]
    public void RoleCatalog_DeleteDraft_NoDraft_ReturnsNullDraft()
    {
        var state = new RoleCatalogState();

        var next = ApplyRoleCatalog(state, new RoleDraftDeletedEvent());

        next.Draft.Should().BeNull();
    }

    [Fact]
    public void RoleCatalog_SaveCatalog_DoesNotAffectDraft()
    {
        var draft = new RoleDraftEntry
        {
            Draft = MakeRole("my-draft", "Keep This"),
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        var state = new RoleCatalogState { Draft = draft };

        var evt = new RoleCatalogSavedEvent();
        evt.Roles.Add(MakeRole("catalog-role"));

        var next = ApplyRoleCatalog(state, evt);

        next.Roles.Should().HaveCount(1);
        next.Draft.Should().NotBeNull();
        next.Draft!.Draft.Name.Should().Be("Keep This");
    }

    [Fact]
    public void RoleCatalog_SaveDraft_WithConnectors()
    {
        var state = new RoleCatalogState();
        var role = MakeRole("with-conn", "Connected Role");
        role.Connectors.Add("web-search");
        role.Connectors.Add("code-runner");
        var ts = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var evt = new RoleDraftSavedEvent { Draft = role, UpdatedAtUtc = ts };

        var next = ApplyRoleCatalog(state, evt);

        next.Draft!.Draft.Connectors.Should().BeEquivalentTo(["web-search", "code-runner"]);
    }

    [Fact]
    public void RoleCatalog_EmptyState_IsValid()
    {
        var state = new RoleCatalogState();

        state.Roles.Should().BeEmpty();
        state.Draft.Should().BeNull();
    }

    [Fact]
    public void RoleCatalog_UnknownEvent_ReturnsCurrentState()
    {
        var state = new RoleCatalogState();
        state.Roles.Add(MakeRole("keep"));

        var unrelated = new ActorRegisteredEvent { GagentType = "T", ActorId = "x" };

        var next = ApplyRoleCatalog(state, unrelated);

        next.Should().BeSameAs(state);
    }
}
