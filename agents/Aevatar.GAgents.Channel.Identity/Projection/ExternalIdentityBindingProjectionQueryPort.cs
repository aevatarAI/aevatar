using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Reads <see cref="ExternalIdentityBindingDocument"/> through the projection
/// document reader (Elasticsearch / in-memory provider). No event-store replay,
/// no actor state mirror, no query-time priming — see ADR-0018 §Projection
/// Readiness. A miss returns <c>null</c>; binding-required command handlers can
/// prompt <c>/init</c>, while normal LLM turns may fall back to bot-owner
/// credentials.
/// </summary>
public sealed class ExternalIdentityBindingProjectionQueryPort
    : IExternalIdentityBindingQueryPort, IOwnerScopeResolver
{
    private readonly IProjectionDocumentReader<ExternalIdentityBindingDocument, string> _reader;

    public ExternalIdentityBindingProjectionQueryPort(
        IProjectionDocumentReader<ExternalIdentityBindingDocument, string> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<BindingId?> ResolveAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);
        var document = await _reader.GetAsync(externalSubject.ToActorId(), ct);
        if (document is null || !document.IsActive)
            return null;
        return new BindingId { Value = document.BindingId };
    }

    async Task<OwnerScopeId?> IOwnerScopeResolver.ResolveAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);
        var document = await _reader.GetAsync(externalSubject.ToActorId(), ct);
        if (document is null || !document.IsActive || string.IsNullOrWhiteSpace(document.OwnerScopeId))
            return null;

        return new OwnerScopeId { Value = document.OwnerScopeId.Trim() };
    }
}
