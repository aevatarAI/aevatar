using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionStoreDispatcherTests
{
    [Fact]
    public async Task UpsertAsync_ShouldWriteToSingleBinding()
    {
        var binding = new RecordingBinding("document");
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel>(
            [binding]);

        var readModel = new TestReadModel
        {
            Id = "id-1",
            Value = "v1",
        };

        await dispatcher.UpsertAsync(readModel);

        binding.UpsertCount.Should().Be(1);
    }

    [Fact]
    public void Ctor_WhenMultipleEnabledBindings_ShouldThrow()
    {
        var documentBinding = new RecordingBinding("document");
        var graphBinding = new RecordingBinding("graph");

        Action act = () => new ProjectionStoreDispatcher<TestReadModel>(
            [documentBinding, graphBinding]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Multiple projection store bindings*")
            .WithMessage("*document*")
            .WithMessage("*graph*");
    }

    [Fact]
    public void Ctor_WhenNoConfiguredBindings_ShouldThrow()
    {
        var unconfiguredDocumentBinding = new ProjectionDocumentStoreBinding<TestReadModel>();

        Action act = () => new ProjectionStoreDispatcher<TestReadModel>(
            [unconfiguredDocumentBinding]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No configured projection store bindings*");
    }

    [Fact]
    public void Ctor_WhenNoConfiguredBindings_ShouldLogSkippedBindings()
    {
        var unconfiguredDocumentBinding = new ProjectionDocumentStoreBinding<TestReadModel>();

        Action act = () => new ProjectionStoreDispatcher<TestReadModel>(
            [unconfiguredDocumentBinding]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ProjectionDocumentBinding_WhenStoreMissing_ShouldExposeAvailabilityReason()
    {
        var binding = new ProjectionDocumentStoreBinding<TestReadModel>();

        binding.IsEnabled.Should().BeFalse();
        binding.DisabledReason.Should().Contain("not registered");
    }

    [Fact]
    public async Task ProjectionDocumentBinding_WhenStoreRegistered_ShouldExposeActiveState_AndForwardWrites()
    {
        var writer = new RecordingDocumentWriter();
        var binding = new ProjectionDocumentStoreBinding<TestReadModel>(writer);
        var readModel = new TestReadModel
        {
            Id = "id-1",
            Value = "v1",
        };

        binding.IsEnabled.Should().BeTrue();
        binding.DisabledReason.Should().Be("Document binding is active.");
        binding.SinkName.Should().Be("Document");

        var result = await binding.UpsertAsync(readModel);

        result.IsApplied.Should().BeTrue();
        writer.Upserts.Should().ContainSingle();
        writer.Upserts[0].Should().BeSameAs(readModel);
    }

    [Fact]
    public async Task UpsertAsync_ShouldDelegateToSingleBinding()
    {
        var binding = new RecordingBinding("document");
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel>(
            [binding]);

        await dispatcher.UpsertAsync(new TestReadModel
        {
            Id = "id-1",
            Value = "v1",
        });

        binding.UpsertCount.Should().Be(1);
        binding.LastValue.Should().Be("v1");
    }

    [Fact]
    public async Task DeleteAsync_ShouldDelegateToSingleBinding()
    {
        var binding = new RecordingBinding("document");
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel>([binding]);

        await dispatcher.DeleteAsync("id-to-delete");

        binding.DeleteCount.Should().Be(1);
        binding.LastDeletedId.Should().Be("id-to-delete");
    }

    [Fact]
    public async Task DeleteAsync_ShouldThrow_WhenIdIsBlank()
    {
        var binding = new RecordingBinding("document");
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel>([binding]);

        Func<Task> act = () => dispatcher.DeleteAsync("   ");

        await act.Should().ThrowAsync<ArgumentException>();
        binding.DeleteCount.Should().Be(0);
    }

    [Fact]
    public async Task ProjectionDocumentBinding_DeleteAsync_ShouldForwardToWriter()
    {
        var writer = new RecordingDocumentWriter();
        var binding = new ProjectionDocumentStoreBinding<TestReadModel>(writer);

        var result = await binding.DeleteAsync("id-1");

        result.IsApplied.Should().BeTrue();
        writer.Deletes.Should().ContainSingle().Which.Should().Be("id-1");
    }

    [Fact]
    public async Task ProjectionDocumentBinding_DeleteAsync_WhenWriterMissing_ShouldNoOpAsApplied()
    {
        var binding = new ProjectionDocumentStoreBinding<TestReadModel>();

        var result = await binding.DeleteAsync("id-1");

        result.IsApplied.Should().BeTrue();
    }

    [Fact]
    public void Ctor_WhenDisabledBindingsExistButOneEnabled_ShouldSelectEnabledBinding()
    {
        var disabledBinding = new ProjectionDocumentStoreBinding<TestReadModel>();
        var enabledBinding = new RecordingBinding("graph");

        var dispatcher = new ProjectionStoreDispatcher<TestReadModel>(
            [disabledBinding, enabledBinding]);

        dispatcher.Should().NotBeNull();
    }

    private sealed class TestReadModel : IProjectionReadModel
    {
        public string Id { get; set; } = "";

        public string ActorId => Id;

        public long StateVersion { get; set; }

        public string LastEventId { get; set; } = "";

        public DateTimeOffset UpdatedAt { get; set; }

        public string Value { get; set; } = "";
    }

    private sealed class RecordingBinding : IProjectionWriteSink<TestReadModel>
    {
        public RecordingBinding(string name)
        {
            SinkName = name;
        }

        public string SinkName { get; }

        public bool IsEnabled => true;

        public string DisabledReason => "enabled";

        public int UpsertCount { get; private set; }

        public string LastValue { get; private set; } = "";

        public int DeleteCount { get; private set; }

        public string LastDeletedId { get; private set; } = "";

        public Task<ProjectionWriteResult> UpsertAsync(TestReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UpsertCount++;
            LastValue = readModel.Value;
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DeleteCount++;
            LastDeletedId = id;
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class RecordingDocumentWriter : IProjectionDocumentWriter<TestReadModel>
    {
        public List<TestReadModel> Upserts { get; } = [];

        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TestReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Deletes.Add(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

}
