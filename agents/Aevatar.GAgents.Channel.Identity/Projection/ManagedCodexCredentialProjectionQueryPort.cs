using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

public sealed class ManagedCodexCredentialProjectionQueryPort(
    IProjectionDocumentReader<ManagedCodexCredentialDocument, string> reader)
    : IManagedCodexCredentialQueryPort
{
    private readonly IProjectionDocumentReader<ManagedCodexCredentialDocument, string> _reader =
        reader ?? throw new ArgumentNullException(nameof(reader));

    public async Task<ManagedCodexCredentialSnapshot?> ResolveAsync(
        ExternalSubjectRef owner,
        CancellationToken ct = default)
    {
        var document = await _reader.GetAsync(ManagedCodexCredentialActorIdentity.From(owner), ct);
        if (document?.Credential is null)
            return null;

        return new ManagedCodexCredentialSnapshot(
            document.Credential.Clone(),
            document.PendingRevocations.Select(static item => item.Clone()).ToArray(),
            document.StateVersion);
    }
}
