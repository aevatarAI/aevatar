using Aevatar.GAgents.ContentArtifacts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class ContentArtifactPinService : IContentArtifactPinService
{
    private readonly IContentArtifactQueryPort _artifactQueryPort;
    private readonly IContentArtifactPinQueryPort _pinQueryPort;
    private readonly IContentArtifactPinCommandPort _pinCommandPort;

    public ContentArtifactPinService(
        IContentArtifactQueryPort artifactQueryPort,
        IContentArtifactPinQueryPort pinQueryPort,
        IContentArtifactPinCommandPort pinCommandPort)
    {
        _artifactQueryPort = artifactQueryPort ?? throw new ArgumentNullException(nameof(artifactQueryPort));
        _pinQueryPort = pinQueryPort ?? throw new ArgumentNullException(nameof(pinQueryPort));
        _pinCommandPort = pinCommandPort ?? throw new ArgumentNullException(nameof(pinCommandPort));
    }

    public async Task<ContentArtifactPinCurrentStateResponse> GetAsync(
        string scopeId,
        string pinKey,
        CancellationToken ct = default)
    {
        var (normalizedScopeId, normalizedPinKey) = NormalizeIdentity(scopeId, pinKey);
        var current = await GetCurrentAsync(normalizedScopeId, normalizedPinKey, ct);
        // Fix (review round 1, F1):
        //   Empty pointers hid committed pin_version and last-mutation observations behind 404.
        //   Any existing current-state document is now returned; only a never-mutated pin is absent.
        if (current == null)
            throw new ContentArtifactPinNotFoundException(normalizedScopeId, normalizedPinKey);
        return current;
    }

    // Implement (issue #3527):
    //   Behavior: set validates that the target is ACTIVE, in-scope, and owned by the caller.
    //   Why this shape: target authorization is advisory application policy; pointer CAS remains actor-owned.
    public async Task<ContentArtifactPinAcceptedReceipt> SetAsync(
        string scopeId,
        string pinKey,
        SetContentArtifactPinRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (normalizedScopeId, normalizedPinKey) = NormalizeIdentity(scopeId, pinKey);
        var normalizedRequester = NormalizePrincipal(requester);
        var artifactId = ContentArtifactConventions.NormalizeArtifactId(request.ArtifactId);
        var mutationId = ContentArtifactConventions.NormalizeRequired(request.MutationId, nameof(request.MutationId));
        ValidateExpectedVersion(request.ExpectedPinVersion);

        var artifact = await _artifactQueryPort.GetAsync(normalizedScopeId, artifactId, ct);
        if (artifact == null ||
            !string.Equals(artifact.ScopeId, normalizedScopeId, StringComparison.Ordinal) ||
            !string.Equals(artifact.LifecycleStatus, ContentArtifactLifecycleStatusNames.Active, StringComparison.Ordinal) ||
            !PrincipalEquals(artifact.Owner, normalizedRequester))
        {
            throw new ContentArtifactNotFoundException(normalizedScopeId, artifactId);
        }

        return await _pinCommandPort.SetAsync(
            normalizedScopeId,
            normalizedPinKey,
            request with { ArtifactId = artifactId, MutationId = mutationId },
            normalizedRequester,
            ct);
    }

    public async Task<ContentArtifactPinAcceptedReceipt> ClearAsync(
        string scopeId,
        string pinKey,
        ClearContentArtifactPinRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (normalizedScopeId, normalizedPinKey) = NormalizeIdentity(scopeId, pinKey);
        var normalizedRequester = NormalizePrincipal(requester);
        var mutationId = ContentArtifactConventions.NormalizeRequired(request.MutationId, nameof(request.MutationId));
        ValidateExpectedVersion(request.ExpectedPinVersion);
        var current = await GetCurrentAsync(normalizedScopeId, normalizedPinKey, ct);
        // Fix (review round 1, F1):
        //   Successful clear removed pinned_by, so an identical mutation_id replay never reached the actor.
        //   Live pointers use pinned_by; empty pointers allow only the last requester's exact mutation replay.
        if (current == null || !CanClear(current, normalizedRequester, mutationId))
        {
            throw new ContentArtifactPinNotFoundException(normalizedScopeId, normalizedPinKey);
        }
        return await _pinCommandPort.ClearAsync(
            normalizedScopeId,
            normalizedPinKey,
            request with { MutationId = mutationId },
            normalizedRequester,
            ct);
    }

    private async Task<ContentArtifactPinCurrentStateResponse?> GetCurrentAsync(
        string scopeId,
        string pinKey,
        CancellationToken ct)
    {
        var current = await _pinQueryPort.GetAsync(scopeId, pinKey, ct);
        return current != null &&
               string.Equals(current.ScopeId, scopeId, StringComparison.Ordinal) &&
               string.Equals(current.PinKey, pinKey, StringComparison.Ordinal)
            ? current
            : null;
    }

    private static bool CanClear(
        ContentArtifactPinCurrentStateResponse current,
        ContentArtifactPrincipalContract requester,
        string mutationId)
    {
        if (!string.IsNullOrWhiteSpace(current.PinnedArtifactId))
            return current.PinnedBy != null && PrincipalEquals(current.PinnedBy, requester);

        return string.Equals(current.LastMutationId, mutationId, StringComparison.Ordinal) &&
               current.LastMutationRequestedBy != null &&
               PrincipalEquals(current.LastMutationRequestedBy, requester);
    }

    private static (string ScopeId, string PinKey) NormalizeIdentity(string scopeId, string pinKey) =>
        (ContentArtifactConventions.NormalizeScopeId(scopeId),
            ContentArtifactConventions.NormalizeLabelKey(pinKey, nameof(pinKey)));

    private static ContentArtifactPrincipalContract NormalizePrincipal(ContentArtifactPrincipalContract requester)
    {
        ArgumentNullException.ThrowIfNull(requester);
        return new ContentArtifactPrincipalContract(
            ContentArtifactConventions.NormalizeRequired(requester.PrincipalId, "requester.principalId"),
            ContentArtifactConventions.NormalizeRequired(requester.PrincipalKind, "requester.principalKind"));
    }

    private static bool PrincipalEquals(
        ContentArtifactPrincipalContract left,
        ContentArtifactPrincipalContract right) =>
        string.Equals(left.PrincipalId, right.PrincipalId, StringComparison.Ordinal);

    private static void ValidateExpectedVersion(long expectedPinVersion)
    {
        if (expectedPinVersion < 0)
            throw new InvalidOperationException("expectedPinVersion must be non-negative.");
    }
}
