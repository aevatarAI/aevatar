using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionStoreDispatcherTests
{
    [Fact]
    public async Task UpsertAsync_ShouldWriteToAllBindings()
    {
        var queryBinding = new TestQueryableBinding();
        var graphBinding = new RecordingBinding("graph");
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel, string>(
            [queryBinding, graphBinding]);

        var readModel = new TestReadModel
        {
            Id = "id-1",
            Value = "v1",
        };

        await dispatcher.UpsertAsync(readModel);

        queryBinding.UpsertCount.Should().Be(1);
        graphBinding.UpsertCount.Should().Be(1);
    }

    [Fact]
    public async Task MutateAsync_ShouldMutateQueryableStore_AndRefreshWriteOnlyBindings()
    {
        var queryBinding = new TestQueryableBinding();
        var graphBinding = new RecordingBinding("graph");
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel, string>(
            [queryBinding, graphBinding]);

        await dispatcher.UpsertAsync(new TestReadModel
        {
            Id = "id-1",
            Value = "before",
        });

        await dispatcher.MutateAsync("id-1", model => model.Value = "after");

        var fetched = await dispatcher.GetAsync("id-1");
        fetched.Should().NotBeNull();
        fetched!.Value.Should().Be("after");
        graphBinding.UpsertCount.Should().Be(2);
        graphBinding.LastValue.Should().Be("after");
    }

    [Fact]
    public async Task UpsertAsync_WhenQueryableBindingMissing_ShouldWriteWriteOnlyBindings()
    {
        var writeOnly = new RecordingBinding("write-only");
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel, string>(
            [writeOnly]);

        await dispatcher.UpsertAsync(new TestReadModel
        {
            Id = "id-1",
            Value = "v1",
        });

        writeOnly.UpsertCount.Should().Be(1);
    }

    [Fact]
    public async Task MutateAsync_WhenQueryableBindingMissing_ShouldThrow()
    {
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel, string>(
            [new RecordingBinding("write-only")]);

        Func<Task> act = () => dispatcher.MutateAsync("id-1", model => model.Value = "v2");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Queryable projection store binding is not configured*");
    }

    [Fact]
    public async Task GetAndList_WhenQueryableBindingMissing_ShouldThrow()
    {
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel, string>(
            [new RecordingBinding("write-only")]);

        Func<Task> getAct = async () => _ = await dispatcher.GetAsync("id-1");
        Func<Task> listAct = async () => _ = await dispatcher.ListAsync();

        await getAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Queryable projection store binding is not configured*");
        await listAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Queryable projection store binding is not configured*");
    }

    [Fact]
    public void Ctor_WhenNoConfiguredBindings_ShouldThrow()
    {
        var unconfiguredDocumentBinding = new ProjectionDocumentStoreBinding<TestReadModel, string>();

        Action act = () => new ProjectionStoreDispatcher<TestReadModel, string>(
            [unconfiguredDocumentBinding]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No configured projection store bindings*");
    }

    [Fact]
    public void Ctor_WhenNoConfiguredBindings_ShouldIncludeAvailabilityReason()
    {
        var unconfiguredDocumentBinding = new ProjectionDocumentStoreBinding<TestReadModel, string>();

        Action act = () => new ProjectionStoreDispatcher<TestReadModel, string>(
            [unconfiguredDocumentBinding]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Document projection store service is not registered*");
    }

    [Fact]
    public void Ctor_WhenMultipleQueryableBindings_ShouldThrow()
    {
        Action act = () => new ProjectionStoreDispatcher<TestReadModel, string>(
            [new TestQueryableBinding(), new TestQueryableBinding()]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*At most one queryable projection store binding is allowed*");
    }

    [Fact]
    public void ProjectionDocumentBinding_WhenStoreMissing_ShouldExposeAvailabilityReason()
    {
        var binding = new ProjectionDocumentStoreBinding<TestReadModel, string>();

        binding.IsConfigured.Should().BeFalse();
        binding.AvailabilityReason.Should().Contain("not registered");
    }

    [Fact]
    public async Task UpsertAsync_WhenBindingFailsInitially_ShouldRetry()
    {
        var queryBinding = new TestQueryableBinding();
        var flakyGraphBinding = new FlakyBinding("graph", failCountBeforeSuccess: 1);
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel, string>(
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
        var queryBinding = new TestQueryableBinding();
        var failingBinding = new FlakyBinding("graph", failCountBeforeSuccess: int.MaxValue);
        var compensator = new RecordingCompensator();
        var dispatcher = new ProjectionStoreDispatcher<TestReadModel, string>(
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

        public string Value { get; set; } = "";
    }

    private sealed class RecordingBinding : IProjectionStoreBinding<TestReadModel, string>
    {
        public RecordingBinding(string name)
        {
            StoreName = name;
        }

        public string StoreName { get; }

        public int UpsertCount { get; private set; }

        public string LastValue { get; private set; } = "";

        public Task UpsertAsync(TestReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UpsertCount++;
            LastValue = readModel.Value;
            return Task.CompletedTask;
        }
    }

    private sealed class FlakyBinding : IProjectionStoreBinding<TestReadModel, string>
    {
        private readonly int _failCountBeforeSuccess;
        private int _remainingFailures;

        public FlakyBinding(string storeName, int failCountBeforeSuccess)
        {
            StoreName = storeName;
            _failCountBeforeSuccess = failCountBeforeSuccess;
            _remainingFailures = failCountBeforeSuccess;
        }

        public string StoreName { get; }

        public int AttemptCount { get; private set; }

        public int UpsertCount { get; private set; }

        public Task UpsertAsync(TestReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AttemptCount++;
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                throw new InvalidOperationException(
                    $"Binding '{StoreName}' failed. remainingFailures={_remainingFailures} failCountBeforeSuccess={_failCountBeforeSuccess}");
            }

            UpsertCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCompensator : IProjectionStoreDispatchCompensator<TestReadModel, string>
    {
        public ProjectionStoreDispatchCompensationContext<TestReadModel, string>? LastContext { get; private set; }

        public Task CompensateAsync(
            ProjectionStoreDispatchCompensationContext<TestReadModel, string> context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastContext = context;
            return Task.CompletedTask;
        }
    }

    private sealed class TestQueryableBinding : IProjectionQueryableStoreBinding<TestReadModel, string>
    {
        private readonly Dictionary<string, TestReadModel> _items = new(StringComparer.Ordinal);

        public string StoreName => "document";

        public int UpsertCount { get; private set; }

        public Task UpsertAsync(TestReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _items[readModel.Id] = Clone(readModel);
            UpsertCount++;
            return Task.CompletedTask;
        }

        public Task MutateAsync(string key, Action<TestReadModel> mutate, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_items.TryGetValue(key, out var model))
                throw new InvalidOperationException($"Missing read model '{key}'.");

            mutate(model);
            return Task.CompletedTask;
        }

        public Task<TestReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_items.TryGetValue(key, out var model))
                return Task.FromResult<TestReadModel?>(null);

            return Task.FromResult<TestReadModel?>(Clone(model));
        }

        public Task<IReadOnlyList<TestReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var items = _items.Values
                .Take(Math.Clamp(take, 1, 200))
                .Select(Clone)
                .ToList();
            return Task.FromResult<IReadOnlyList<TestReadModel>>(items);
        }

        private static TestReadModel Clone(TestReadModel source)
        {
            return new TestReadModel
            {
                Id = source.Id,
                Value = source.Value,
            };
        }
    }
}
