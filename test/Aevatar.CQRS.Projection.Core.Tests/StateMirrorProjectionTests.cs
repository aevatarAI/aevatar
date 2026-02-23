using Aevatar.CQRS.Projection.StateMirror.Abstractions;
using Aevatar.CQRS.Projection.StateMirror.DependencyInjection;
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
}
