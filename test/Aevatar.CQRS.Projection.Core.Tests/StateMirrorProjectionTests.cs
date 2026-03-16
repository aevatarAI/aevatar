using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.StateMirror.Abstractions;
using Aevatar.CQRS.Projection.StateMirror.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class StateMirrorProjectionTests
{
    [Fact]
    public void AddJsonStateMirrorProjection_ShouldProjectStateByDefault()
    {
        var services = new ServiceCollection();
        services.AddJsonStateMirrorProjection<SampleState, SampleReadModel>();

        using var provider = services.BuildServiceProvider();
        var projection = provider.GetRequiredService<IStateMirrorProjection<SampleState, SampleReadModel>>();
        var projected = projection.Project(new SampleState
        {
            ActorId = "actor-1",
            Count = 3,
            InternalNote = "note",
        });

        projected.ActorId.Should().Be("actor-1");
        projected.Count.Should().Be(3);
        projected.InternalNote.Should().Be("note");
    }

    [Fact]
    public void AddJsonStateMirrorProjection_WithRenameAndIgnore_ShouldApplyOptions()
    {
        var services = new ServiceCollection();
        services.AddJsonStateMirrorProjection<SampleState, RenamedReadModel>(options =>
        {
            options.IgnoredFields.Add(nameof(SampleState.InternalNote));
            options.RenamedFields[nameof(SampleState.ActorId)] = nameof(RenamedReadModel.Id);
        });

        using var provider = services.BuildServiceProvider();
        var projection = provider.GetRequiredService<IStateMirrorProjection<SampleState, RenamedReadModel>>();
        var projected = projection.Project(new SampleState
        {
            ActorId = "actor-2",
            Count = 9,
            InternalNote = "secret",
        });

        projected.Id.Should().Be("actor-2");
        projected.Count.Should().Be(9);
        projected.InternalNote.Should().BeNull();
    }

    [Fact]
    public void AddJsonStateMirrorProjection_MultipleRegistrations_ShouldKeepOptionsIsolated()
    {
        var services = new ServiceCollection();
        services.AddJsonStateMirrorProjection<SampleState, RenamedReadModel>(options =>
        {
            options.IgnoredFields.Add(nameof(SampleState.InternalNote));
            options.RenamedFields[nameof(SampleState.ActorId)] = nameof(RenamedReadModel.Id);
        });
        services.AddJsonStateMirrorProjection<SampleState, SampleReadModel>();

        using var provider = services.BuildServiceProvider();
        var renamedProjection = provider.GetRequiredService<IStateMirrorProjection<SampleState, RenamedReadModel>>();
        var defaultProjection = provider.GetRequiredService<IStateMirrorProjection<SampleState, SampleReadModel>>();
        var state = new SampleState
        {
            ActorId = "actor-3",
            Count = 5,
            InternalNote = "shared",
        };

        var renamed = renamedProjection.Project(state);
        var defaultProjected = defaultProjection.Project(state);

        renamed.Id.Should().Be("actor-3");
        renamed.InternalNote.Should().BeNull();
        defaultProjected.ActorId.Should().Be("actor-3");
        defaultProjected.InternalNote.Should().Be("shared");
    }

    [Fact]
    public async Task AddJsonStateMirrorReadModelProjector_ShouldProjectAndDispatchToStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<InMemoryProjectionReadModelStore>();
        services.AddSingleton<IProjectionDocumentReader<ProjectionReadModel, string>>(sp =>
            sp.GetRequiredService<InMemoryProjectionReadModelStore>());
        services.AddSingleton<IProjectionWriteDispatcher<ProjectionReadModel>>(sp =>
            sp.GetRequiredService<InMemoryProjectionReadModelStore>());
        services.AddJsonStateMirrorReadModelProjector<SampleState, ProjectionReadModel>(options =>
        {
            options.RenamedFields[nameof(SampleState.ActorId)] = nameof(ProjectionReadModel.Id);
        });

        using var provider = services.BuildServiceProvider();
        var projector = provider.GetRequiredService<IStateMirrorReadModelProjector<SampleState, ProjectionReadModel, string>>();

        var projected = await projector.ProjectAndUpsertAsync(new SampleState
        {
            ActorId = "actor-4",
            Count = 8,
            InternalNote = "memo",
        });
        projected.Id.Should().Be("actor-4");

        var stored = await projector.GetAsync("actor-4");
        stored.Should().NotBeNull();
        stored!.Count.Should().Be(8);
        stored.InternalNote.Should().Be("memo");

        var items = await projector.QueryAsync(new ProjectionDocumentQuery
        {
            Take = 10,
        });
        items.Items.Should().ContainSingle(x => x.Id == "actor-4" && x.Count == 8);
    }

    public sealed class SampleState
    {
        public string ActorId { get; set; } = "";

        public int Count { get; set; }

        public string InternalNote { get; set; } = "";
    }

    public sealed class SampleReadModel
    {
        public string ActorId { get; set; } = "";

        public int Count { get; set; }

        public string InternalNote { get; set; } = "";
    }

    public sealed class RenamedReadModel
    {
        public string Id { get; set; } = "";

        public int Count { get; set; }

        public string? InternalNote { get; set; }
    }

    public sealed class ProjectionReadModel : IProjectionReadModel
    {
        public string Id { get; set; } = "";

        public string ActorId => Id;

        public long StateVersion { get; set; }

        public string LastEventId { get; set; } = "";

        public DateTimeOffset UpdatedAt { get; set; }

        public int Count { get; set; }

        public string InternalNote { get; set; } = "";
    }

    private sealed class InMemoryProjectionReadModelStore
        : IProjectionDocumentReader<ProjectionReadModel, string>,
          IProjectionWriteDispatcher<ProjectionReadModel>
    {
        private readonly Dictionary<string, ProjectionReadModel> _items = new(StringComparer.Ordinal);

        public Task<ProjectionWriteResult> UpsertAsync(ProjectionReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _items[readModel.Id] = Clone(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_items.TryGetValue(key, out var model)
                ? Clone(model)
                : null);
        }

        public Task<ProjectionDocumentQueryResult<ProjectionReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var normalizedTake = Math.Clamp(query.Take <= 0 ? 50 : query.Take, 1, 500);
            var items = _items.Values
                .Take(normalizedTake)
                .Select(Clone)
                .ToList();
            return Task.FromResult(new ProjectionDocumentQueryResult<ProjectionReadModel>
            {
                Items = items,
            });
        }

        private static ProjectionReadModel Clone(ProjectionReadModel source)
        {
            return new ProjectionReadModel
            {
                Id = source.Id,
                StateVersion = source.StateVersion,
                LastEventId = source.LastEventId,
                UpdatedAt = source.UpdatedAt,
                Count = source.Count,
                InternalNote = source.InternalNote,
            };
        }
    }
}
