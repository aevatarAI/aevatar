using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Runtime.Observability;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Diagnostics;

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
    public async Task DeleteAsync_WithMarker_ShouldDelegateMarkerToSingleBinding()
    {
        var binding = new RecordingBinding("document");
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel>([binding]);
        var marker = NewDeleteMarker();

        await dispatcher.DeleteAsync(marker);

        binding.DeleteCount.Should().Be(1);
        binding.LastDeletedId.Should().Be(marker.Id);
        binding.LastDeleteMarker.Should().BeSameAs(marker);
    }

    [Fact]
    public async Task ObservedProjectionWriteDispatcher_DeleteAsync_WithMarker_ShouldForwardMarkerThroughStoreBindingToWriter()
    {
        var writer = new RecordingDocumentWriter();
        var binding = new ProjectionDocumentStoreBinding<TestReadModel>(writer);
        var inner = new ProjectionStoreDispatcher<TestReadModel>([binding]);
        var dispatcher = new ObservedProjectionWriteDispatcher<TestReadModel>(inner);
        var marker = NewDeleteMarker();

        var result = await dispatcher.DeleteAsync(marker);

        result.IsApplied.Should().BeTrue();
        writer.Deletes.Should().BeEmpty();
        writer.DeleteMarkers.Should().ContainSingle().Which.Should().BeSameAs(marker);
    }

    [Fact]
    public async Task DeleteAsync_WithMarker_WhenSinkDoesNotSupportMarkers_ShouldFailClosed()
    {
        var binding = new IdOnlyBinding("document");
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel>([binding]);
        var marker = NewDeleteMarker();

        Func<Task> act = () => dispatcher.DeleteAsync(marker);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*versioned read-model deletes*");
        binding.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task ObservedProjectionWriteDispatcher_ShouldEmitUpsertAndDeleteActivities()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var binding = new RecordingBinding("document");
        var inner = new ProjectionStoreDispatcher<TestReadModel>([binding]);
        var dispatcher = new ObservedProjectionWriteDispatcher<TestReadModel>(inner);

        await dispatcher.UpsertAsync(new TestReadModel
        {
            Id = "id-1",
            StateVersion = 11,
            Value = "v1",
        });
        await dispatcher.DeleteAsync("id-1");

        var upsert = stopped.ShouldContainActivity(
            AevatarActivitySource.ReadModelUpsertActivityName,
            AevatarActivitySource.ReadModelNameTag,
            nameof(TestReadModel));
        upsert.GetTagItem(AevatarActivitySource.ReadModelStateVersionTag).Should().Be(11L);

        var delete = stopped.ShouldContainActivity(
            AevatarActivitySource.ReadModelDeleteActivityName,
            AevatarActivitySource.ReadModelNameTag,
            nameof(TestReadModel));
        delete.GetTagItem(AevatarActivitySource.ReadModelIdTag).Should().Be("id-1");
    }

    [Fact]
    public async Task ObservedProjectionWriteDispatcher_ShouldMarkUpsertActivityError_WhenInnerThrows()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var dispatcher = new ObservedProjectionWriteDispatcher<TestReadModel>(
            new ProjectionStoreDispatcher<TestReadModel>([new ThrowingBinding("document", throwOnUpsert: true)]));

        Func<Task> act = () => dispatcher.UpsertAsync(new TestReadModel
        {
            Id = "id-error",
            StateVersion = 17,
            Value = "v-error",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("upsert boom");
        stopped
            .Where(activity => activity.DisplayName == AevatarActivitySource.ReadModelUpsertActivityName)
            .Should()
            .ContainSingle()
            .Which
            .Status
            .Should()
            .Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task ObservedProjectionWriteDispatcher_ShouldMarkDeleteActivityError_WhenInnerThrows()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var dispatcher = new ObservedProjectionWriteDispatcher<TestReadModel>(
            new ProjectionStoreDispatcher<TestReadModel>([new ThrowingBinding("document", throwOnDelete: true)]));

        Func<Task> act = () => dispatcher.DeleteAsync("id-error");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("delete boom");
        stopped
            .Where(activity => activity.DisplayName == AevatarActivitySource.ReadModelDeleteActivityName)
            .Should()
            .ContainSingle()
            .Which
            .Status
            .Should()
            .Be(ActivityStatusCode.Error);
    }

    [Fact]
    public void AddProjectionReadModelRuntime_ShouldResolveObservedDispatcherAroundStoreDispatcher()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionWriteSink<TestReadModel>>(new RecordingBinding("document"));

        services.AddProjectionReadModelRuntime();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ProjectionStoreDispatcher<TestReadModel>>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionWriteDispatcher<TestReadModel>>()
            .Should().BeOfType<ObservedProjectionWriteDispatcher<TestReadModel>>();
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
    public async Task ProjectionDocumentBinding_DeleteAsync_WithMarker_ShouldForwardMarkerToWriter()
    {
        var writer = new RecordingDocumentWriter();
        var binding = new ProjectionDocumentStoreBinding<TestReadModel>(writer);
        var marker = NewDeleteMarker();

        var result = await binding.DeleteAsync(marker);

        result.IsApplied.Should().BeTrue();
        writer.Deletes.Should().BeEmpty();
        writer.DeleteMarkers.Should().ContainSingle().Which.Should().BeSameAs(marker);
    }

    [Fact]
    public async Task ProjectionDocumentBinding_DeleteAsync_WithMarker_WhenWriterDoesNotSupportMarkers_ShouldFailClosed()
    {
        var writer = new IdOnlyDocumentWriter();
        var binding = new ProjectionDocumentStoreBinding<TestReadModel>(writer);
        var marker = NewDeleteMarker();

        Func<Task> act = () => binding.DeleteAsync(marker);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*versioned read-model deletes*");
        writer.Deletes.Should().BeEmpty();
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

    private static ProjectionDocumentDeleteMarker NewDeleteMarker() => new(
        "id-to-delete",
        "actor-1",
        42,
        "event-42",
        new DateTimeOffset(2026, 7, 29, 1, 2, 3, TimeSpan.Zero));

    private sealed class IdOnlyBinding(string sinkName) : IProjectionWriteSink<TestReadModel>
    {
        public string SinkName { get; } = sinkName;

        public bool IsEnabled => true;

        public string DisabledReason => "enabled";

        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TestReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Deletes.Add(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
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

        public ProjectionDocumentDeleteMarker? LastDeleteMarker { get; private set; }

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

        public Task<ProjectionWriteResult> DeleteAsync(
            ProjectionDocumentDeleteMarker marker,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DeleteCount++;
            LastDeletedId = marker.Id;
            LastDeleteMarker = marker;
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class IdOnlyDocumentWriter : IProjectionDocumentWriter<TestReadModel>
    {
        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TestReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Deletes.Add(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class RecordingDocumentWriter : IProjectionDocumentWriter<TestReadModel>
    {
        public List<TestReadModel> Upserts { get; } = [];

        public List<string> Deletes { get; } = [];

        public List<ProjectionDocumentDeleteMarker> DeleteMarkers { get; } = [];

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

        public Task<ProjectionWriteResult> DeleteAsync(
            ProjectionDocumentDeleteMarker marker,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DeleteMarkers.Add(marker);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class ThrowingBinding(
        string sinkName,
        bool throwOnUpsert = false,
        bool throwOnDelete = false) : IProjectionWriteSink<TestReadModel>
    {
        public string SinkName { get; } = sinkName;

        public bool IsEnabled => true;

        public string DisabledReason => "enabled";

        public Task<ProjectionWriteResult> UpsertAsync(TestReadModel readModel, CancellationToken ct = default)
        {
            _ = readModel;
            ct.ThrowIfCancellationRequested();
            if (throwOnUpsert)
                throw new InvalidOperationException("upsert boom");
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            _ = id;
            ct.ThrowIfCancellationRequested();
            if (throwOnDelete)
                throw new InvalidOperationException("delete boom");
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

}

file static class ProjectionActivityAssertions
{
    public static Activity ShouldContainActivity(
        this ConcurrentQueue<Activity> activities,
        string displayName,
        string tagName,
        string tagValue)
    {
        return activities
            .Where(activity =>
                activity.DisplayName == displayName &&
                string.Equals(
                    activity.GetTagItem(tagName) as string,
                    tagValue,
                    StringComparison.Ordinal))
            .Should()
            .ContainSingle()
            .Which;
    }
}
