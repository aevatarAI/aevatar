namespace Aevatar.GAgentService.Abstractions.Ports;

public sealed record MemberPublishedServiceResolveRequest(
    string ScopeId,
    string MemberId);

public sealed record MemberPublishedServiceResolution(
    string ScopeId,
    string MemberId,
    string PublishedServiceId);

public interface IMemberPublishedServiceResolver
{
    Task<MemberPublishedServiceResolution> ResolveAsync(
        MemberPublishedServiceResolveRequest request,
        CancellationToken ct = default);
}
