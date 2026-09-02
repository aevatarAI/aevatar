using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// Reconciles the platform's member-first invoke / runs / binding routes
/// with the StudioMember authority introduced in PR #428.
///
/// The legacy <see cref="DefaultMemberPublishedServiceResolver"/> returns
/// <c>publishedServiceId == memberId</c>. Studio's bind path persists
/// <c>publishedServiceId = "member-{memberId}"</c> on the member actor
/// (per <c>StudioMemberConventions.BuildPublishedServiceId</c>). Without this
/// resolver, contract reads / activate / retire would target
/// <c>member-{memberId}</c> while invoke would target <c>{memberId}</c>, so
/// the URL we hand the frontend would 404 against the same binding it just
/// committed.
///
/// Resolution rule:
///   1. If the StudioMember authority knows about (scope, member), return its
///      stable <c>publishedServiceId</c> — this is the Studio-bound case.
///   2. Missing member authority or a blank published service identity is an
///      invalid normal-business state and fails closed.
///
/// Registered with <c>Replace</c> in Studio's capability so Studio-enabled
/// hosts use the member authority instead of the platform's deterministic
/// resolver.
/// </summary>
public sealed class StudioAwareMemberPublishedServiceResolver : IMemberPublishedServiceResolver
{
    private static readonly char[] DisallowedMemberIdChars = [':', '/', '\\', '?', '#'];

    private readonly IStudioMemberQueryPort _memberQueryPort;

    public StudioAwareMemberPublishedServiceResolver(IStudioMemberQueryPort memberQueryPort)
    {
        _memberQueryPort = memberQueryPort
            ?? throw new ArgumentNullException(nameof(memberQueryPort));
    }

    public async Task<MemberPublishedServiceResolution> ResolveAsync(
        MemberPublishedServiceResolveRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        // Reproduces the legacy resolver's normalization rules so a malformed
        // member id (separator chars, empty after trim) fails fast in the
        // same way regardless of whether StudioMember authority is touched.
        // Centralizing the rule in a shared helper would mean a project
        // reference into platform Application; the tradeoff isn't worth it.
        var normalizedScopeId = NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var normalizedMemberId = NormalizeMemberId(request.MemberId);

        var detail = await _memberQueryPort.GetAsync(normalizedScopeId, normalizedMemberId, ct);
        if (detail == null)
        {
            throw new InvalidOperationException(
                $"Member '{normalizedMemberId}' was not found in scope '{normalizedScopeId}'.");
        }

        var publishedServiceId = detail?.Summary.PublishedServiceId;
        if (string.IsNullOrWhiteSpace(publishedServiceId))
        {
            throw new InvalidOperationException(
                $"Member '{normalizedMemberId}' has no published service in scope '{normalizedScopeId}'.");
        }

        return new MemberPublishedServiceResolution(
            normalizedScopeId,
            normalizedMemberId,
            publishedServiceId.Trim(),
            IsMemberAuthorityBacked: true);
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");
        return normalized;
    }

    private static string NormalizeMemberId(string? memberId)
    {
        var normalized = NormalizeRequired(memberId, nameof(MemberPublishedServiceResolveRequest.MemberId));
        if (normalized.IndexOfAny(DisallowedMemberIdChars) >= 0)
            throw new InvalidOperationException("memberId must not contain ':', '/', '\\', '?' or '#'.");
        return normalized;
    }
}
