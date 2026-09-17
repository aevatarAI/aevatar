using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Studio.Tests;

public sealed class ActorDispatchLLMModelCatalogPolicyCommandServiceTests
{
    [Fact]
    public async Task ReplaceAsync_ShouldMapTypedOneOfsAndHonestAdmissionReceipt()
    {
        var bootstrap = new RecordingBootstrap();
        var ackedAt = DateTimeOffset.Parse("2026-08-15T08:00:00Z");
        var dispatch = new RecordingDispatchPort(new DispatchAdmission(
            Accepted: true,
            CommandId: "command-alpha",
            AckedAt: ackedAt,
            ActorId: "llm-model-catalog-policy-scope-scope-alpha",
            CorrelationId: "correlation-alpha"));
        var service = new ActorDispatchLLMModelCatalogPolicyCommandService(bootstrap, dispatch);

        var receipt = await service.ReplaceAsync(new ReplaceLLMModelCatalogPolicy(
            LLMModelCatalogPolicyOwner.ForScope("scope-alpha"),
            Aevatar.Studio.Application.Studio.Abstractions.LLMModelCatalogPolicyMode.Custom,
            [
                new(
                    new NyxIDUserServiceModelSourceIdentity("user-svc-beta"),
                    "chrono-llm-public",
                    new ExplicitLLMModels(["gpt-5.5", "o3"])),
            ],
            ExpectedStateVersion: 7,
            MutationId: "mutation-alpha"));

        bootstrap.ActorIds.Should().ContainSingle()
            .Which.Should().Be("llm-model-catalog-policy-scope-scope-alpha");
        var dispatched = dispatch.Dispatches.Should().ContainSingle().Subject;
        dispatched.Envelope.Route.PublisherActorId.Should()
            .Be("aevatar.studio.projection.llm-model-catalog-policy");
        var payload = dispatched.Envelope.Payload.Unpack<ReplaceLLMModelCatalogPolicyCommand>();
        payload.OwnerType.Should().Be(LLMModelCatalogPolicyOwnerType.Scope);
        payload.ScopeId.Should().Be("scope-alpha");
        payload.ExpectedStateVersion.Should().Be(7);
        payload.MutationId.Should().Be("mutation-alpha");
        payload.Sources.Should().ContainSingle();
        payload.Sources[0].Source.UserServiceId.Should().Be("user-svc-beta");
        payload.Sources[0].ExplicitModels.UpstreamModelIds.Should()
            .Equal("gpt-5.5", "o3");
        receipt.Should().Be(new UserConfigSaveReceipt(
            true,
            "command-alpha",
            UserConfigCommandAckStage.Accepted,
            "llm-model-catalog-policy-scope-scope-alpha",
            "correlation-alpha",
            ackedAt));
    }

    [Fact]
    public async Task ReplaceAsync_ShouldMapPlatformCatalogSource()
    {
        var dispatch = RecordingDispatchPort.Accepting();
        var service = new ActorDispatchLLMModelCatalogPolicyCommandService(
            new RecordingBootstrap(),
            dispatch);

        await service.ReplaceAsync(new ReplaceLLMModelCatalogPolicy(
            LLMModelCatalogPolicyOwner.Platform,
            Aevatar.Studio.Application.Studio.Abstractions.LLMModelCatalogPolicyMode.Custom,
            [
                new(
                    new NyxIDCatalogServiceModelSourceIdentity("catalog-svc-alpha"),
                    "chrono-llm",
                    new ExplicitLLMModels(["gpt-5.5"])),
            ],
            ExpectedStateVersion: 3,
            MutationId: "platform-source-mutation"));

        var payload = dispatch.Dispatches.Should().ContainSingle().Subject.Envelope.Payload
            .Unpack<ReplaceLLMModelCatalogPolicyCommand>();
        payload.OwnerType.Should().Be(LLMModelCatalogPolicyOwnerType.Platform);
        payload.Sources.Should().ContainSingle();
        payload.Sources[0].Source.CatalogServiceId.Should().Be("catalog-svc-alpha");
        payload.Sources[0].ExplicitModels.UpstreamModelIds.Should().Equal("gpt-5.5");
    }

    [Fact]
    public async Task ReplaceAsync_WithScopeCatalogSource_ShouldRejectBeforeBootstrap()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = RecordingDispatchPort.Accepting();
        var service = new ActorDispatchLLMModelCatalogPolicyCommandService(bootstrap, dispatch);
        var command = new ReplaceLLMModelCatalogPolicy(
            LLMModelCatalogPolicyOwner.ForScope("scope-alpha"),
            Aevatar.Studio.Application.Studio.Abstractions.LLMModelCatalogPolicyMode.Custom,
            [
                new(
                    new NyxIDCatalogServiceModelSourceIdentity("catalog-svc-alpha"),
                    "chrono-llm",
                    new ExplicitLLMModels(["gpt-5.5"])),
            ],
            ExpectedStateVersion: 0,
            MutationId: "invalid-scope-source");

        var act = () => service.ReplaceAsync(command);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*exact user services*");
        bootstrap.ActorIds.Should().BeEmpty();
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplaceAsync_WithEmptyExplicitSelection_ShouldRejectBeforeBootstrap()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = RecordingDispatchPort.Accepting();
        var service = new ActorDispatchLLMModelCatalogPolicyCommandService(bootstrap, dispatch);
        var command = new ReplaceLLMModelCatalogPolicy(
            LLMModelCatalogPolicyOwner.ForScope("scope-alpha"),
            Aevatar.Studio.Application.Studio.Abstractions.LLMModelCatalogPolicyMode.Custom,
            [
                new(
                    new NyxIDUserServiceModelSourceIdentity("user-svc-alpha"),
                    "chrono-llm",
                    new ExplicitLLMModels([])),
            ],
            ExpectedStateVersion: 0,
            MutationId: "invalid-empty-models");

        var act = () => service.ReplaceAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires at least one upstream_model_id*");
        bootstrap.ActorIds.Should().BeEmpty();
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplaceAsync_ShouldKeepPlatformAndScopeActorIdsDistinct()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = RecordingDispatchPort.Accepting();
        var service = new ActorDispatchLLMModelCatalogPolicyCommandService(bootstrap, dispatch);

        await service.ReplaceAsync(new ReplaceLLMModelCatalogPolicy(
            LLMModelCatalogPolicyOwner.Platform,
            Aevatar.Studio.Application.Studio.Abstractions.LLMModelCatalogPolicyMode.Custom,
            [], 0, "platform-mutation"));
        await service.ReplaceAsync(new ReplaceLLMModelCatalogPolicy(
            LLMModelCatalogPolicyOwner.ForScope("platform"),
            Aevatar.Studio.Application.Studio.Abstractions.LLMModelCatalogPolicyMode.Custom,
            [], 0, "scope-mutation"));

        bootstrap.ActorIds.Should().Equal(
            "llm-model-catalog-policy-platform",
            "llm-model-catalog-policy-scope-platform");
    }

    private sealed class RecordingBootstrap : IStudioActorBootstrap
    {
        public List<string> ActorIds { get; } = [];

        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor
        {
            ActorIds.Add(actorId);
            return Task.FromResult<IActor>(new StubActor(actorId));
        }
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingDispatchPort(DispatchAdmission admission) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public static RecordingDispatchPort Accepting() => new(new DispatchAdmission(
            true,
            "command-alpha",
            DateTimeOffset.Parse("2026-08-15T08:00:00Z"),
            "actor-alpha",
            "correlation-alpha"));

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            return Task.FromResult(admission);
        }
    }
}
