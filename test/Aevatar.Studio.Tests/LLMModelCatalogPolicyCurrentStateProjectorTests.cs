using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class LLMModelCatalogPolicyCurrentStateProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldCopyTypedPolicyAtAuthoritativeCommittedVersion()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new LLMModelCatalogPolicyCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-15T08:00:00Z")));
        var state = new LLMModelCatalogPolicyGAgentState
        {
            OwnerType = LLMModelCatalogPolicyOwnerType.Scope,
            ScopeId = "scope-alpha",
            Mode = LLMModelCatalogPolicyMode.Custom,
            LastMutationId = "mutation-alpha",
        };
        state.Sources.Add(UserSource());

        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = "llm-model-catalog-policy-scope-scope-alpha",
                ProjectionKind = LLMModelCatalogPolicyGAgent.ProjectionKind,
            },
            WrapCommitted(state, version: 23));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.StateVersion.Should().Be(23);
        document.OwnerType.Should().Be(LLMModelCatalogPolicyOwnerType.Scope);
        document.ScopeId.Should().Be("scope-alpha");
        document.Mode.Should().Be(LLMModelCatalogPolicyMode.Custom);
        document.LastMutationId.Should().Be("mutation-alpha");
        document.Sources.Should().ContainSingle();
        document.Sources[0].Should().NotBeSameAs(state.Sources[0]);
        document.Sources[0].Source.UserServiceId.Should().Be("user-svc-alpha");
    }

    private static Aevatar.GAgents.UserConfig.LLMModelCatalogPolicySource UserSource()
    {
        var models = new ExplicitLLMModelIDs();
        models.UpstreamModelIds.Add("gpt-5.5");
        return new Aevatar.GAgents.UserConfig.LLMModelCatalogPolicySource
        {
            Source = new NyxIDModelSourceReference
            {
                UserServiceId = "user-svc-alpha",
                ServiceSlugSnapshot = "chrono-llm",
            },
            ExplicitModels = models,
        };
    }

    private static EventEnvelope WrapCommitted(
        LLMModelCatalogPolicyGAgentState state,
        long version) => new()
        {
            Id = "evt-23",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-15T08:00:00Z")),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(
                "llm-model-catalog-policy-scope-scope-alpha"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-23",
                    Version = version,
                    EventData = Any.Pack(new LLMModelCatalogPolicyReplacedEvent
                    {
                        OwnerType = state.OwnerType,
                        ScopeId = state.ScopeId,
                        Mode = state.Mode,
                        MutationId = state.LastMutationId,
                    }),
                    Timestamp = Timestamp.FromDateTimeOffset(
                        DateTimeOffset.Parse("2026-08-15T08:00:00Z")),
                },
                StateRoot = Any.Pack(state),
            }),
        };

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<LLMModelCatalogPolicyCurrentStateDocument>
    {
        public List<LLMModelCatalogPolicyCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            LLMModelCatalogPolicyCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
