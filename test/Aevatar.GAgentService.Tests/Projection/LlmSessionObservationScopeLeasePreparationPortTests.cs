using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Orchestration;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class LlmSessionObservationScopeLeasePreparationPortTests
{
    private sealed class CapturingActivationService
        : IProjectionScopeActivationService<LlmSessionObservationRuntimeLease>
    {
        private readonly bool _throwOnEnsure;

        public CapturingActivationService(bool throwOnEnsure = false)
        {
            _throwOnEnsure = throwOnEnsure;
        }

        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<LlmSessionObservationRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            if (_throwOnEnsure)
                throw new InvalidOperationException("activation failed");

            return Task.FromResult(new LlmSessionObservationRuntimeLease(
                new LlmSessionObservationProjectionContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                    SessionId = request.SessionId,
                }));
        }
    }

    [Fact]
    public void Constructor_NullActivationService_Throws()
    {
        var act = () => new LlmSessionObservationScopeLeasePreparationPort(
            null!,
            new RecordingProjectionReleaseService<LlmSessionObservationRuntimeLease>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullReleaseService_Throws()
    {
        var act = () => new LlmSessionObservationScopeLeasePreparationPort(
            new CapturingActivationService(),
            null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task PrepareAsync_TrimsIdentifiers_ActivatesSessionScope_ReturnsPreparation()
    {
        var activation = new CapturingActivationService();
        var release = new RecordingProjectionReleaseService<LlmSessionObservationRuntimeLease>();
        var port = new LlmSessionObservationScopeLeasePreparationPort(activation, release);

        var preparation = await port.PrepareAsync("  actor-1  ", "  response-1  ");

        preparation.Should().NotBeNull();
        preparation!.ActorId.Should().Be("actor-1");
        preparation.ResponseId.Should().Be("response-1");

        activation.Requests.Should().ContainSingle();
        var request = activation.Requests[0];
        request.RootActorId.Should().Be("actor-1");
        request.SessionId.Should().Be("response-1");
        request.ProjectionKind.Should().Be("llm-session-observation");
        request.Mode.Should().Be(ProjectionRuntimeMode.SessionObservation);

        release.Released.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "response")]
    [InlineData("   ", "response")]
    [InlineData("actor", "")]
    [InlineData("actor", "   ")]
    public async Task PrepareAsync_BlankIdentifiers_ReturnsNull_WithoutActivating(string actorId, string responseId)
    {
        var activation = new CapturingActivationService();
        var port = new LlmSessionObservationScopeLeasePreparationPort(
            activation,
            new RecordingProjectionReleaseService<LlmSessionObservationRuntimeLease>());

        var preparation = await port.PrepareAsync(actorId, responseId);

        preparation.Should().BeNull();
        activation.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_WhenActivationThrows_ReturnsNull()
    {
        var activation = new CapturingActivationService(throwOnEnsure: true);
        var port = new LlmSessionObservationScopeLeasePreparationPort(
            activation,
            new RecordingProjectionReleaseService<LlmSessionObservationRuntimeLease>());

        var preparation = await port.PrepareAsync("actor", "response");

        preparation.Should().BeNull();
        activation.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ReleaseAsync_NullPreparation_Throws()
    {
        var port = new LlmSessionObservationScopeLeasePreparationPort(
            new CapturingActivationService(),
            new RecordingProjectionReleaseService<LlmSessionObservationRuntimeLease>());

        var act = async () => await port.ReleaseAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReleaseAsync_WhenCancelled_Throws()
    {
        var port = new LlmSessionObservationScopeLeasePreparationPort(
            new CapturingActivationService(),
            new RecordingProjectionReleaseService<LlmSessionObservationRuntimeLease>());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await port.ReleaseAsync(
            new LlmSessionObservationScopeLeasePreparation("actor", "response"),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReleaseAsync_ReleasesLeaseBuiltFromPreparation()
    {
        var release = new RecordingProjectionReleaseService<LlmSessionObservationRuntimeLease>();
        var port = new LlmSessionObservationScopeLeasePreparationPort(
            new CapturingActivationService(),
            release);

        await port.ReleaseAsync(new LlmSessionObservationScopeLeasePreparation("actor-9", "response-9"));

        release.Released.Should().ContainSingle();
        release.Released[0].ActorId.Should().Be("actor-9");
        release.Released[0].ResponseId.Should().Be("response-9");
    }
}
