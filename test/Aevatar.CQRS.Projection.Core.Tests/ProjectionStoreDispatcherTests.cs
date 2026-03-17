using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionStoreDispatcherTests
{
    [Fact]
    public async Task UpsertAsync_ShouldWriteToAllBindings()
    {
        var documentBinding = new RecordingBinding("document");
        var graphBinding = new RecordingBinding("graph");
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel>(
            [documentBinding, graphBinding]);

        var readModel = new TestReadModel
        {
            Id = "id-1",
            Value = "v1",
        };

        await dispatcher.UpsertAsync(readModel);

        documentBinding.UpsertCount.Should().Be(1);
        graphBinding.UpsertCount.Should().Be(1);
    }

    [Fact]
    public async Task UpsertAsync_WhenOnlyWriteBindingsAreRegistered_ShouldStillWrite()
    {
        var writeOnly = new RecordingBinding("write-only");
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel>(
            [writeOnly]);

        await dispatcher.UpsertAsync(new TestReadModel
        {
            Id = "id-1",
            Value = "v1",
        });

        writeOnly.UpsertCount.Should().Be(1);
    }

    [Fact]
    public async Task UpsertAsync_ShouldPreserveRegisteredBindingOrder()
    {
        var writes = new List<string>();
        var graphBinding = new RecordingBinding("graph", writes);
        var documentBinding = new RecordingBinding("document", writes);
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel>(
            [graphBinding, documentBinding]);

        await dispatcher.UpsertAsync(new TestReadModel
        {
            Id = "id-1",
            Value = "v1",
        });

        writes.Should().Equal("graph", "document");
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
    public void Ctor_WhenNoConfiguredBindings_ShouldIncludeAvailabilityReason()
    {
        var unconfiguredDocumentBinding = new ProjectionDocumentStoreBinding<TestReadModel>();

        Action act = () => new ProjectionStoreDispatcher<TestReadModel>(
            [unconfiguredDocumentBinding]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Document projection store service is not registered*");
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
    public async Task UpsertAsync_WhenBindingFailsInitially_ShouldRetry()
    {
        var queryBinding = new RecordingBinding("document");
        var flakyGraphBinding = new FlakyBinding("graph", failCountBeforeSuccess: 1);
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel>(
            [queryBinding, flakyGraphBinding],
            options: new ProjectionStoreDispatchOptions
            {
                MaxWriteAttempts = 2,
            });

        await dispatcher.UpsertAsync(new TestReadModel
        {
            Id = "id-1",
            Value = "v1",
        });

        flakyGraphBinding.AttemptCount.Should().Be(2);
        flakyGraphBinding.UpsertCount.Should().Be(1);
    }

    [Fact]
    public async Task UpsertAsync_WhenBindingFailsAfterRetries_ShouldInvokeCompensator()
    {
        var queryBinding = new RecordingBinding("document");
        var failingBinding = new FlakyBinding("graph", failCountBeforeSuccess: int.MaxValue);
        var compensator = new RecordingCompensator();
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel>(
            [queryBinding, failingBinding],
            compensator: compensator,
            options: new ProjectionStoreDispatchOptions
            {
                MaxWriteAttempts = 2,
            });

        Func<Task> act = () => dispatcher.UpsertAsync(new TestReadModel
        {
            Id = "id-1",
            Value = "v1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*after 2 attempt*");
        compensator.LastContext.Should().NotBeNull();
        compensator.LastContext!.Operation.Should().Be("upsert");
        compensator.LastContext.FailedStore.Should().Be("graph");
        compensator.LastContext.SucceededStores.Should().ContainSingle("document");
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
        private readonly ICollection<string>? _writes;

        public RecordingBinding(string name, ICollection<string>? writes = null)
        {
            SinkName = name;
            _writes = writes;
        }

        public string SinkName { get; }

        public bool IsEnabled => true;

        public string DisabledReason => "enabled";

        public int UpsertCount { get; private set; }

        public string LastValue { get; private set; } = "";

        public Task<ProjectionWriteResult> UpsertAsync(TestReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UpsertCount++;
            LastValue = readModel.Value;
            _writes?.Add(SinkName);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FlakyBinding : IProjectionWriteSink<TestReadModel>
    {
        private readonly int _failCountBeforeSuccess;
        private int _remainingFailures;

        public FlakyBinding(string storeName, int failCountBeforeSuccess)
        {
            SinkName = storeName;
            _failCountBeforeSuccess = failCountBeforeSuccess;
            _remainingFailures = failCountBeforeSuccess;
        }

        public string SinkName { get; }

        public bool IsEnabled => true;

        public string DisabledReason => "enabled";

        public int AttemptCount { get; private set; }

        public int UpsertCount { get; private set; }

        public Task<ProjectionWriteResult> UpsertAsync(TestReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AttemptCount++;
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                throw new InvalidOperationException(
                    $"Binding '{SinkName}' failed. remainingFailures={_remainingFailures} failCountBeforeSuccess={_failCountBeforeSuccess}");
            }

            UpsertCount++;
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class RecordingCompensator : IProjectionStoreDispatchCompensator<TestReadModel>
    {
        public ProjectionStoreDispatchCompensationContext<TestReadModel>? LastContext { get; private set; }

        public Task CompensateAsync(
            ProjectionStoreDispatchCompensationContext<TestReadModel> context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastContext = context;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDocumentWriter : IProjectionDocumentWriter<TestReadModel>
    {
        public List<TestReadModel> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TestReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

}
