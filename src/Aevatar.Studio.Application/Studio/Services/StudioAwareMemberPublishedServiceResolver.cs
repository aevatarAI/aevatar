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
///   2. Otherwise fall through to the deterministic legacy mapping
///      (<c>publishedServiceId == memberId</c>) so direct platform binds
///      keep working unchanged.
///
/// Registered with <c>AddSingleton</c> in Studio's capability so it wins over
/// the platform's <c>TryAddSingleton</c> default; only Studio-enabled hosts
/// take this branch — pure platform integration tests still see the legacy
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
        var publishedServiceId = detail?.Summary.PublishedServiceId;
        var resolvedServiceId = string.IsNullOrWhiteSpace(publishedServiceId)
            ? normalizedMemberId  // legacy deterministic mapping for direct platform binds
            : publishedServiceId;

        return new MemberPublishedServiceResolution(
            normalizedScopeId,
            normalizedMemberId,
            resolvedServiceId);
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
