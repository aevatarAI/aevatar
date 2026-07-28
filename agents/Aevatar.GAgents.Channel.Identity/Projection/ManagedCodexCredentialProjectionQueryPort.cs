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

        var snapshot = new ManagedCodexCredentialSnapshot
        {
            Credential = document.Credential.Clone(),
            StateVersion = document.StateVersion,
            LastEventId = document.LastEventId,
        };
        snapshot.PendingRevocations.Add(
            document.PendingRevocations.Select(static item => item.Clone()));
        return snapshot;
    }
}
