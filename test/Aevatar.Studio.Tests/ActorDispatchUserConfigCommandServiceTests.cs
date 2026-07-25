using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Studio.Tests;

public sealed class ActorDispatchUserConfigCommandServiceTests
{
    [Fact]
    public async Task UpdateAsync_ShouldMapDispatchAdmissionReceiptAndEnvelope()
    {
        var ackedAt = new DateTimeOffset(2026, 5, 26, 10, 30, 0, TimeSpan.Zero);
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort(new DispatchAdmission(
            Accepted: true,
            CommandId: "admission-command",
            AckedAt: ackedAt,
            ActorId: "user-config-scope-1",
            CorrelationId: "corr-1"));
        var service = new ActorDispatchUserConfigCommandService(
            bootstrap,
            dispatch);

        var receipt = await service.UpdateAsync(
            UserConfigResourceKey.ForOwnerScope("scope-1"),
            new UserConfigUpdate(
            DefaultModel: "gpt-5.5",
            LlmSelection: new UserLlmSelectionValue(
                UserLlmSelectionKind.NyxIdUserService,
                "/api/v1/proxy/s/openai-work",
                "us-openai",
                "openai-work"),
            RuntimeMode: "remote",
            LocalRuntimeBaseUrl: "http://127.0.0.1:5080",
            RemoteRuntimeBaseUrl: "https://runtime.example.com",
            GithubUsername: "octocat",
            MaxToolRounds: 7));

        bootstrap.EnsuredActorIds.Should().ContainSingle().Which.Should().Be("user-config-scope-1");
        dispatch.Dispatches.Should().ContainSingle();
        var dispatched = dispatch.Dispatches[0];
        dispatched.ActorId.Should().Be("user-config-scope-1");
        dispatched.Envelope.Route.Direct.TargetActorId.Should().Be("user-config-scope-1");
        dispatched.Envelope.Route.PublisherActorId.Should().Be("aevatar.studio.projection.user-config");
        dispatched.Envelope.Payload.Is(UpdateUserConfigCommand.Descriptor).Should().BeTrue();

        var payload = dispatched.Envelope.Payload.Unpack<UpdateUserConfigCommand>();
        payload.DefaultModel.Should().Be("gpt-5.5");
        payload.LlmSelection.RouteValue.Should().Be("/api/v1/proxy/s/openai-work");
        payload.LlmSelection.NyxIdUserServiceId.Should().Be("us-openai");
        payload.RuntimeMode.Should().Be(UserConfigRuntimeDefaults.RemoteMode);
        payload.LocalRuntimeBaseUrl.Should().Be("http://127.0.0.1:5080");
        payload.RemoteRuntimeBaseUrl.Should().Be("https://runtime.example.com");
        payload.GithubUsername.Should().Be("octocat");
        payload.MaxToolRounds.Should().Be(7);

        receipt.Accepted.Should().BeTrue();
        receipt.CommandId.Should().Be("admission-command");
        receipt.AckStage.Should().Be(UserConfigCommandAckStage.Accepted);
        receipt.ActorId.Should().Be("user-config-scope-1");
        receipt.CorrelationId.Should().Be("corr-1");
        receipt.AckedAtUtc.Should().Be(ackedAt);
    }

    [Fact]
    public async Task UpdateAsync_WhenDispatchAdmissionIsNotAccepted_ShouldReturnRejectedReceipt()
    {
        var ackedAt = new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);
        var service = new ActorDispatchUserConfigCommandService(
            new RecordingBootstrap(),
            new RecordingDispatchPort(new DispatchAdmission(
                Accepted: false,
                CommandId: "rejected-command",
                AckedAt: ackedAt,
                ActorId: "user-config-scope-1",
                CorrelationId: "corr-1")));

        var receipt = await service.UpdateAsync(
            UserConfigResourceKey.ForOwnerScope("scope-1"),
            new UserConfigUpdate(DefaultModel: "gpt-5.5"));

        receipt.Accepted.Should().BeFalse();
        receipt.CommandId.Should().Be("rejected-command");
        receipt.AckStage.Should().Be(UserConfigCommandAckStage.AdmissionRejected);
        receipt.ActorId.Should().Be("user-config-scope-1");
        receipt.CorrelationId.Should().Be("corr-1");
        receipt.AckedAtUtc.Should().Be(ackedAt);
    }

    [Fact]
    public async Task UpdateAsync_ShouldMapOnlyPresentFieldsAndKeepResourceKindsDistinct()
    {
        var dispatch = RecordingDispatchPort.Accepting();
        var service = CreateService(dispatch);
        var selection = new UserLlmSelectionValue(
            UserLlmSelectionKind.NyxIdUserService,
            "/api/v1/proxy/s/chrono-llm-public",
            "us-alpha",
            "chrono-llm-public");

        await service.UpdateAsync(
            UserConfigResourceKey.ForOwnerScope("binding-alpha"),
            new UserConfigUpdate(DefaultModel: "gpt-5.5", LlmSelection: selection));
        await service.UpdateAsync(
            UserConfigResourceKey.ForChannelBinding("alpha"),
            new UserConfigUpdate(DefaultModel: "claude-4"));

        dispatch.Dispatches.Select(x => x.ActorId).Should().Equal(
            "user-config-binding-alpha",
            "channel-user-config-alpha");
        var command = dispatch.Dispatches[0].Envelope.Payload.Unpack<UpdateUserConfigCommand>();
        command.HasDefaultModel.Should().BeTrue();
        command.HasRuntimeMode.Should().BeFalse();
        command.LlmSelection.NyxIdUserServiceId.Should().Be("us-alpha");
    }

    [Fact]
    public async Task UpdateAsync_ShouldRejectUnknownResourceKind()
    {
        var dispatch = RecordingDispatchPort.Accepting();
        var service = CreateService(dispatch);
        var resource = new UserConfigResourceKey((UserConfigResourceKind)99, "unknown-alpha");

        var act = () => service.UpdateAsync(resource, new UserConfigUpdate(DefaultModel: "gpt-5.5"));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        dispatch.Dispatches.Should().BeEmpty();
    }

    private static ActorDispatchUserConfigCommandService CreateService(RecordingDispatchPort dispatch) =>
        new(new RecordingBootstrap(), dispatch);

    private sealed class RecordingBootstrap : IStudioActorBootstrap
    {
        public List<string> EnsuredActorIds { get; } = [];

        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor
        {
            EnsuredActorIds.Add(actorId);
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
        public List<DispatchedCommand> Dispatches { get; } = [];

        public static RecordingDispatchPort Accepting() =>
            new(new DispatchAdmission(
                Accepted: true,
                CommandId: "command-alpha",
                AckedAt: DateTimeOffset.Parse("2026-07-22T08:00:00Z"),
                ActorId: "user-config-binding-alpha",
                CorrelationId: "correlation-alpha"));

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add(new DispatchedCommand(actorId, envelope));
            return Task.FromResult(admission);
        }

        public sealed record DispatchedCommand(string ActorId, EventEnvelope Envelope);
    }

    private sealed class StubScopeResolver(string scopeId) : IAppScopeResolver
    {
        public AppScopeContext? Resolve(Microsoft.AspNetCore.Http.HttpContext? httpContext = null) =>
            new(scopeId, "test");

        public bool HasAuthenticatedRequestWithoutScope(Microsoft.AspNetCore.Http.HttpContext? httpContext = null) =>
            false;

        public bool HasHttpRequestContext(Microsoft.AspNetCore.Http.HttpContext? httpContext = null) => false;
    }
}
