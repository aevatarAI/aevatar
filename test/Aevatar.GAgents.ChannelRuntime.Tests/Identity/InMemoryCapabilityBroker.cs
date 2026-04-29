using System.Collections.Concurrent;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// In-process test fake for both <see cref="INyxIdCapabilityBroker"/> (write-
/// side) and <see cref="IExternalIdentityBindingQueryPort"/> (read-side).
/// Combining the two seams in a single fake keeps test wiring tight: the
/// in-memory dictionary is the single source of truth that the broker writes
/// to and the query port reads from. Production wiring uses
/// <c>NyxIdRemoteCapabilityBroker</c> for the broker seam plus a
/// projection-backed query port — added in a follow-up PR once
/// ChronoAIProject/NyxID#549 is consumed end-to-end.
/// </summary>
internal sealed class InMemoryCapabilityBroker : INyxIdCapabilityBroker, IExternalIdentityBindingQueryPort
{
    private readonly ConcurrentDictionary<string, BindingId> _bindings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _revokedBindings = new(StringComparer.Ordinal);
    private readonly Func<ExternalSubjectRef, BindingChallenge> _challengeFactory;
    private readonly Func<ExternalSubjectRef, BindingId, CapabilityScope, CapabilityHandle> _handleFactory;

    public InMemoryCapabilityBroker(
        Func<ExternalSubjectRef, BindingChallenge>? challengeFactory = null,
        Func<ExternalSubjectRef, BindingId, CapabilityScope, CapabilityHandle>? handleFactory = null)
    {
        _challengeFactory = challengeFactory ?? DefaultChallenge;
        _handleFactory = handleFactory ?? DefaultHandle;
    }

    /// <summary>
    /// Test helper to seed a binding directly without running the OAuth flow.
    /// </summary>
    public void SeedBinding(ExternalSubjectRef externalSubject, BindingId bindingId)
    {
        ArgumentNullException.ThrowIfNull(bindingId);
        _bindings[externalSubject.ToActorId()] = bindingId;
    }

    /// <summary>
    /// Test helper that flips a previously-seeded binding into the
    /// "NyxID-side revoked" state so <see cref="IssueShortLivedAsync"/>
    /// throws <see cref="BindingRevokedException"/>.
    /// </summary>
    public void MarkRevokedOnNyxId(BindingId bindingId)
    {
        ArgumentNullException.ThrowIfNull(bindingId);
        _revokedBindings.TryAdd(bindingId.Value, 0);
    }

    public Task<BindingChallenge> StartExternalBindingAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);
        return Task.FromResult(_challengeFactory(externalSubject));
    }

    public Task<BindingId?> ResolveAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);
        _bindings.TryGetValue(externalSubject.ToActorId(), out var bindingId);
        return Task.FromResult<BindingId?>(bindingId);
    }

    public Task RevokeBindingAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);
        if (_bindings.TryRemove(externalSubject.ToActorId(), out var removed))
            _revokedBindings.TryAdd(removed.Value, 0);
        return Task.CompletedTask;
    }

    public Task<CapabilityHandle> IssueShortLivedAsync(
        ExternalSubjectRef externalSubject,
        CapabilityScope scope,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);
        ArgumentNullException.ThrowIfNull(scope);

        if (!_bindings.TryGetValue(externalSubject.ToActorId(), out var bindingId))
            throw new BindingNotFoundException(externalSubject);

        if (_revokedBindings.ContainsKey(bindingId.Value))
            throw new BindingRevokedException(externalSubject, "Binding revoked at NyxID.");

        return Task.FromResult(_handleFactory(externalSubject, bindingId, scope));
    }

    private static BindingChallenge DefaultChallenge(ExternalSubjectRef externalSubject) =>
        new()
        {
            AuthorizeUrl = $"https://test-nyxid.local/oauth/authorize?subject={externalSubject.ToActorId()}",
            ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        };

    private static CapabilityHandle DefaultHandle(
        ExternalSubjectRef _,
        BindingId bindingId,
        CapabilityScope scope) =>
        new()
        {
            AccessToken = $"test-access-token-for-{bindingId.Value}",
            ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            Scope = scope.Value,
        };
}
