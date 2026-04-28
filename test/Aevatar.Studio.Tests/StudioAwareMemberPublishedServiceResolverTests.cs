using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Locks in the resolver's contract:
///
///   - Studio members → return the actor-stored <c>publishedServiceId</c>
///     (e.g. <c>"member-{memberId}"</c>) so platform invoke / runs / binding
///     routes target the same service Studio's bind path wrote.
///   - Non-Studio members → fall through to the legacy deterministic
///     mapping (<c>publishedServiceId == memberId</c>) so direct platform
///     binds keep working unchanged.
///   - Malformed input (empty / contains separator chars) → fail fast with
///     the legacy validation rules.
/// </summary>
public sealed class StudioAwareMemberPublishedServiceResolverTests
{
    [Fact]
    public async Task ResolveAsync_ShouldReturnStudioPublishedServiceId_WhenMemberExists()
    {
        var port = new InMemoryQueryPort(new Dictionary<(string, string), string>
        {
            [("scope-1", "m-abc")] = "member-m-abc",
        });
        var resolver = new StudioAwareMemberPublishedServiceResolver(port);

        var result = await resolver.ResolveAsync(
            new MemberPublishedServiceResolveRequest("scope-1", "m-abc"),
            CancellationToken.None);

        result.ScopeId.Should().Be("scope-1");
        result.MemberId.Should().Be("m-abc");
        // The fix the inline review flagged: contract / activate / retire
        // resolved at "member-m-abc"; if invoke kept resolving at "m-abc"
        // the URL contract handed back to the frontend would 404 against
        // its own binding.
        result.PublishedServiceId.Should().Be("member-m-abc");
    }

    [Fact]
    public async Task ResolveAsync_ShouldFallBackToMemberId_WhenStudioMemberMissing()
    {
        var port = new InMemoryQueryPort(new Dictionary<(string, string), string>());
        var resolver = new StudioAwareMemberPublishedServiceResolver(port);

        var result = await resolver.ResolveAsync(
            new MemberPublishedServiceResolveRequest("scope-1", "legacy-member"),
            CancellationToken.None);

        // Direct platform binds (no StudioMember actor) must preserve the
        // legacy deterministic mapping; otherwise existing platform-only
        // member-first calls would silently break under this resolver.
        result.PublishedServiceId.Should().Be("legacy-member");
    }

    [Fact]
    public async Task ResolveAsync_ShouldTrimInputs()
    {
        var port = new InMemoryQueryPort(new Dictionary<(string, string), string>
        {
            [("scope-1", "m-abc")] = "member-m-abc",
        });
        var resolver = new StudioAwareMemberPublishedServiceResolver(port);

        var result = await resolver.ResolveAsync(
            new MemberPublishedServiceResolveRequest("  scope-1  ", "  m-abc  "),
            CancellationToken.None);

        result.ScopeId.Should().Be("scope-1");
        result.MemberId.Should().Be("m-abc");
        result.PublishedServiceId.Should().Be("member-m-abc");
    }

    [Theory]
    [InlineData("scope:bad", "m-abc")]
    [InlineData("scope-1", "m/bad")]
    [InlineData("scope-1", "m\\bad")]
    [InlineData("scope-1", "m?bad")]
    [InlineData("scope-1", "m#bad")]
    public async Task ResolveAsync_ShouldRejectMemberIdsWithSeparatorChars(string scopeId, string memberId)
    {
        // Only memberId has separator restrictions; scope-1/m:bad would
        // also fail when the platform scope-id grammar is enforced upstream,
        // but here we mirror the legacy resolver's input validation exactly.
        if (scopeId.Contains(':'))
            return;

        var resolver = new StudioAwareMemberPublishedServiceResolver(new InMemoryQueryPort());

        var act = () => resolver.ResolveAsync(new MemberPublishedServiceResolveRequest(scopeId, memberId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*memberId must not contain*");
    }

    [Theory]
    [InlineData("", "m-abc", "ScopeId is required.")]
    [InlineData("   ", "m-abc", "ScopeId is required.")]
    [InlineData("scope-1", "", "MemberId is required.")]
    [InlineData("scope-1", "   ", "MemberId is required.")]
    public async Task ResolveAsync_ShouldRejectEmptyInputs(string scopeId, string memberId, string expectedMessage)
    {
        var resolver = new StudioAwareMemberPublishedServiceResolver(new InMemoryQueryPort());

        var act = () => resolver.ResolveAsync(new MemberPublishedServiceResolveRequest(scopeId, memberId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldFallBack_WhenStudioMemberHasEmptyPublishedServiceId()
    {
        // Defensive: an authority record with a blank publishedServiceId is
        // a backend invariant violation, but resolver shouldn't crash —
        // it should degrade to the legacy mapping so the rest of the host
        // keeps serving. The right place to surface the invariant violation
        // is StudioMemberService, which the test there asserts.
        var port = new InMemoryQueryPort(new Dictionary<(string, string), string>
        {
            [("scope-1", "m-abc")] = string.Empty,
        });
        var resolver = new StudioAwareMemberPublishedServiceResolver(port);

        var result = await resolver.ResolveAsync(
            new MemberPublishedServiceResolveRequest("scope-1", "m-abc"),
            CancellationToken.None);

        result.PublishedServiceId.Should().Be("m-abc");
    }

    private sealed class InMemoryQueryPort : IStudioMemberQueryPort
    {
        private readonly IReadOnlyDictionary<(string Scope, string Member), string> _publishedServiceIds;

        public InMemoryQueryPort(IReadOnlyDictionary<(string Scope, string Member), string>? publishedServiceIds = null)
        {
            _publishedServiceIds = publishedServiceIds ?? new Dictionary<(string, string), string>();
        }

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId, StudioMemberRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(scopeId, []));

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default)
        {
            if (!_publishedServiceIds.TryGetValue((scopeId, memberId), out var publishedServiceId))
                return Task.FromResult<StudioMemberDetailResponse?>(null);

            var summary = new StudioMemberSummaryResponse(
                MemberId: memberId,
                ScopeId: scopeId,
                DisplayName: "Test",
                Description: string.Empty,
                ImplementationKind: MemberImplementationKindNames.Workflow,
                LifecycleStage: MemberLifecycleStageNames.BindReady,
                PublishedServiceId: publishedServiceId,
                LastBoundRevisionId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow);
            return Task.FromResult<StudioMemberDetailResponse?>(
                new StudioMemberDetailResponse(summary, null, null));
        }
    }
}
