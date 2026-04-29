using System.Collections.Concurrent;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// In-process test fake for <see cref="INyxIdCapabilityBroker"/>. Holds binding
/// state in a process-local dictionary; suitable only for unit and integration
/// tests. Production wiring uses <c>NyxIdRemoteCapabilityBroker</c> (added in
/// a follow-up PR once ChronoAIProject/NyxID#549 ships).
/// </summary>
internal sealed class InMemoryCapabilityBroker : INyxIdCapabilityBroker
{
    private readonly ConcurrentDictionary<string, BindingId> _bindings = new(StringComparer.Ordinal);
    private readonly Func<ExternalSubjectRef, BindingChallenge> _challengeFactory;
    private readonly Func<ExternalSubjectRef, BindingId, CapabilityScope, CapabilityHandle> _handleFactory;
    private readonly HashSet<string> _revokedBindings = new(StringComparer.Ordinal);

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
        _revokedBindings.Add(bindingId.Value);
    }

    public Task<BindingChallenge> StartExternalBindingAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);
        return Task.FromResult(_challengeFactory(externalSubject));
    }

    public Task<BindingId?> ResolveBindingAsync(
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
            _revokedBindings.Add(removed.Value);
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
            throw new BindingRevokedException(externalSubject, "No active binding for the given external subject.");

        if (_revokedBindings.Contains(bindingId.Value))
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
