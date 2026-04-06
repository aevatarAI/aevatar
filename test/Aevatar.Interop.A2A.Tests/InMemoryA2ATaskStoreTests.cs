using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;
using Aevatar.Interop.A2A.Application;
using FluentAssertions;

namespace Aevatar.Interop.A2A.Tests;

public class InMemoryA2ATaskStoreTests
{
    private readonly InMemoryA2ATaskStore _store = new();

    private static Message MakeMessage(string text) => new()
    {
        Role = "user",
        Parts = [new TextPart { Text = text }],
    };

    [Fact]
    public async Task CreateTask_SetsSubmittedState()
    {
        var task = await _store.CreateTaskAsync("t1", "s1", MakeMessage("hello"));

        task.Id.Should().Be("t1");
        task.SessionId.Should().Be("s1");
        task.Status.State.Should().Be(TaskState.Submitted);
        task.History.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateTask_DuplicateId_Throws()
    {
        await _store.CreateTaskAsync("t1", null, MakeMessage("a"));

        var act = () => _store.CreateTaskAsync("t1", null, MakeMessage("b"));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetTask_Existing_ReturnsTask()
    {
        await _store.CreateTaskAsync("t1", null, MakeMessage("hello"));
        var task = await _store.GetTaskAsync("t1");
        task.Should().NotBeNull();
        task!.Id.Should().Be("t1");
    }

    [Fact]
    public async Task GetTask_NonExistent_ReturnsNull()
    {
        var task = await _store.GetTaskAsync("missing");
        task.Should().BeNull();
    }

    [Fact]
    public async Task UpdateTaskState_ChangesState()
    {
        await _store.CreateTaskAsync("t1", null, MakeMessage("hello"));

        var updated = await _store.UpdateTaskStateAsync("t1", TaskState.Working);
        updated.Status.State.Should().Be(TaskState.Working);

        var agentMsg = MakeMessage("Done!");
        var completed = await _store.UpdateTaskStateAsync("t1", TaskState.Completed, new Message
        {
            Role = "agent",
            Parts = [new TextPart { Text = "Done!" }],
        });
        completed.Status.State.Should().Be(TaskState.Completed);
        completed.History.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateTaskState_NonExistent_Throws()
    {
        var act = () => _store.UpdateTaskStateAsync("missing", TaskState.Working);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task AddArtifact_AppendsToList()
    {
        await _store.CreateTaskAsync("t1", null, MakeMessage("hello"));

        var artifact = new Artifact
        {
            Parts = [new TextPart { Text = "result" }],
            Index = 0,
        };
        var task = await _store.AddArtifactAsync("t1", artifact);
        task.Artifacts.Should().HaveCount(1);
        task.Artifacts![0].Parts.Should().HaveCount(1);
    }

    [Fact]
    public async Task DeleteTask_RemovesTask()
    {
        await _store.CreateTaskAsync("t1", null, MakeMessage("hello"));
        var deleted = await _store.DeleteTaskAsync("t1");
        deleted.Should().BeTrue();

        var task = await _store.GetTaskAsync("t1");
        task.Should().BeNull();
    }

    [Fact]
    public async Task DeleteTask_NonExistent_ReturnsFalse()
    {
        var deleted = await _store.DeleteTaskAsync("missing");
        deleted.Should().BeFalse();
    }

    // ─── Subscription tests ───

    [Fact]
    public async Task Subscribe_ReceivesUpdates()
    {
        await _store.CreateTaskAsync("t1", null, MakeMessage("hello"));
        var reader = _store.SubscribeAsync("t1");

        await _store.UpdateTaskStateAsync("t1", TaskState.Working);

        var canRead = reader.TryRead(out var update);
        canRead.Should().BeTrue();
        update!.Status.State.Should().Be(TaskState.Working);
        update.IsFinal.Should().BeFalse();
    }

    [Fact]
    public async Task Subscribe_FinalUpdate_CompletesChannel()
    {
        await _store.CreateTaskAsync("t1", null, MakeMessage("hello"));
        var reader = _store.SubscribeAsync("t1");

        await _store.UpdateTaskStateAsync("t1", TaskState.Completed);

        var updates = new List<TaskStateUpdate>();
        await foreach (var u in reader.ReadAllAsync())
            updates.Add(u);

        updates.Should().HaveCount(1);
        updates[0].Status.State.Should().Be(TaskState.Completed);
        updates[0].IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task Subscribe_AfterTerminalState_ImmediatelyCompletes()
    {
        await _store.CreateTaskAsync("t1", null, MakeMessage("hello"));
        await _store.UpdateTaskStateAsync("t1", TaskState.Completed);

        // Subscribe AFTER task is already completed
        var reader = _store.SubscribeAsync("t1");

        var updates = new List<TaskStateUpdate>();
        await foreach (var u in reader.ReadAllAsync())
            updates.Add(u);

        updates.Should().HaveCount(1);
        updates[0].Status.State.Should().Be(TaskState.Completed);
        updates[0].IsFinal.Should().BeTrue();
    }
}
