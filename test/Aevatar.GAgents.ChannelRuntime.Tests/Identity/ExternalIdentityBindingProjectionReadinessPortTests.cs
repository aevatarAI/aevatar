using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Tests for <see cref="ExternalIdentityBindingProjectionReadinessPort"/>.
/// Pins the polling loop, the binding-id match success path, the revoke
/// (binding-id null) match, and the timeout error path. The implementation
/// is the only thing standing between the OAuth callback handler returning
/// success and the next inbound message resolving the binding, so the
/// behaviour needs explicit coverage. Earlier review (kimi-k2p6 L1) flagged
/// the gap.
/// </summary>
public class ExternalIdentityBindingProjectionReadinessPortTests
{
    private static ExternalSubjectRef SampleSubject() => new()
    {
        Platform = "lark",
        Tenant = "ou_tenant_x",
        ExternalUserId = "ou_user_y",
    };

    [Fact]
    public async Task WaitForBindingStateAsync_ReturnsImmediatelyWhenAlreadyMaterialized()
    {
        var subject = SampleSubject();
        var actorId = subject.ToActorId();
        var reader = new InMemoryReader();
        reader.Documents[actorId] = new ExternalIdentityBindingDocument
        {
            Id = actorId,
            BindingId = "bnd_x",
        };
        var port = new ExternalIdentityBindingProjectionReadinessPort(
            reader,
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await port.WaitForBindingStateAsync(subject, "bnd_x", TimeSpan.FromSeconds(1));

        reader.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task WaitForBindingStateAsync_PollsUntilMatchObserved()
    {
        var subject = SampleSubject();
        var actorId = subject.ToActorId();
        var reader = new InMemoryReader();
        var port = new ExternalIdentityBindingProjectionReadinessPort(
            reader,
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        // Materialize the document after the first poll so the loop has to
        // observe the change.
        reader.OnGet = (calls) =>
        {
            if (calls >= 2)
                reader.Documents[actorId] = new ExternalIdentityBindingDocument
                {
                    Id = actorId,
                    BindingId = "bnd_x",
                };
        };

        await port.WaitForBindingStateAsync(subject, "bnd_x", TimeSpan.FromSeconds(2));

        reader.GetCalls.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task WaitForBindingStateAsync_RevokeCaseMatchesEmptyBindingId()
    {
        var subject = SampleSubject();
        var actorId = subject.ToActorId();
        var reader = new InMemoryReader();
        // Document exists but with a cleared BindingId — represents a revoked
        // binding (the projector writes binding_id = "" on revoke).
        reader.Documents[actorId] = new ExternalIdentityBindingDocument
        {
            Id = actorId,
            BindingId = string.Empty,
        };
        var port = new ExternalIdentityBindingProjectionReadinessPort(
            reader,
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await port.WaitForBindingStateAsync(subject, expectedBindingId: null, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WaitForBindingStateAsync_ThrowsTimeoutWhenNoMatch()
    {
        var subject = SampleSubject();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-29T10:00:00Z"));
        var reader = new InMemoryReader();
        // Document never materializes — the loop must time out rather than
        // hang forever.
        var port = new ExternalIdentityBindingProjectionReadinessPort(reader, clock);

        // Nudge the clock past the deadline immediately so the wait gives up
        // on the first failed poll.
        reader.OnGet = (_) => clock.Advance(TimeSpan.FromMilliseconds(2000));

        var act = () => port.WaitForBindingStateAsync(
            subject,
            expectedBindingId: "bnd_x",
            timeout: TimeSpan.FromMilliseconds(500));

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*did not observe binding_id=bnd_x*");
    }

    [Fact]
    public async Task WaitForBindingStateAsync_TreatsLaterEventAsMatch()
    {
        // The reviewer (kimi-k2p6 L47) flagged that exact LastEventId equality
        // is fragile when the projection processes a later event. The new
        // signature (binding_id state) avoids that pitfall — even if the
        // projection has advanced past several events, the wait succeeds as
        // long as the readmodel reports the expected binding_id.
        var subject = SampleSubject();
        var actorId = subject.ToActorId();
        var reader = new InMemoryReader();
        reader.Documents[actorId] = new ExternalIdentityBindingDocument
        {
            Id = actorId,
            BindingId = "bnd_x",
            StateVersion = 42, // already past the version the caller emitted
            LastEventId = "evt-future",
        };
        var port = new ExternalIdentityBindingProjectionReadinessPort(
            reader,
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await port.WaitForBindingStateAsync(subject, "bnd_x", TimeSpan.FromSeconds(1));
    }

    private sealed class InMemoryReader : IProjectionDocumentReader<ExternalIdentityBindingDocument, string>
    {
        public Dictionary<string, ExternalIdentityBindingDocument> Documents { get; } =
            new(StringComparer.Ordinal);

        public int GetCalls { get; private set; }

        public Action<int>? OnGet { get; set; }

        public Task<ExternalIdentityBindingDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            GetCalls++;
            OnGet?.Invoke(GetCalls);
            Documents.TryGetValue(key, out var doc);
            return Task.FromResult<ExternalIdentityBindingDocument?>(doc);
        }

        public Task<ProjectionDocumentQueryResult<ExternalIdentityBindingDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionDocumentQueryResult<ExternalIdentityBindingDocument>.Empty);
    }
}
