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
    public void Ctor_WhenQueryableBindingMissing_ShouldThrow()
    {
        Action act = () => new ProjectionStoreDispatcher<TestReadModel, string>(
            [new RecordingBinding("write-only")]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Exactly one queryable projection store binding is required*");
    }

    [Fact]
    public void Ctor_WhenMultipleQueryableBindings_ShouldThrow()
    {
        Action act = () => new ProjectionStoreDispatcher<TestReadModel, string>(
            [new TestQueryableBinding(), new TestQueryableBinding()]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Exactly one queryable projection store binding is required*");
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
