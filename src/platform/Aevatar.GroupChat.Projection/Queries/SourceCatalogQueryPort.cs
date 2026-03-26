using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Abstractions.Queries;
using Aevatar.GroupChat.Projection.ReadModels;

namespace Aevatar.GroupChat.Projection.Queries;

public sealed class SourceCatalogQueryPort : ISourceRegistryQueryPort
{
    private readonly IProjectionDocumentReader<SourceCatalogReadModel, string> _documentReader;

    public SourceCatalogQueryPort(IProjectionDocumentReader<SourceCatalogReadModel, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<GroupSourceCatalogSnapshot?> GetSourceAsync(string sourceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        var readModel = await _documentReader.GetAsync(GroupChatActorIds.Source(sourceId), ct);
        return readModel == null ? null : Map(readModel);
    }

    private static GroupSourceCatalogSnapshot Map(SourceCatalogReadModel readModel)
    {
        var sourceKind = Enum.IsDefined(typeof(GroupSourceKind), readModel.SourceKindValue)
            ? (GroupSourceKind)readModel.SourceKindValue
            : GroupSourceKind.Unspecified;
        var authorityClass = Enum.IsDefined(typeof(GroupSourceAuthorityClass), readModel.AuthorityClassValue)
            ? (GroupSourceAuthorityClass)readModel.AuthorityClassValue
            : GroupSourceAuthorityClass.Unspecified;
        var verificationStatus = Enum.IsDefined(typeof(GroupSourceVerificationStatus), readModel.VerificationStatusValue)
            ? (GroupSourceVerificationStatus)readModel.VerificationStatusValue
            : GroupSourceVerificationStatus.Unspecified;
        return new GroupSourceCatalogSnapshot(
            readModel.ActorId,
            readModel.SourceId,
            sourceKind,
            readModel.CanonicalLocator,
            authorityClass,
            verificationStatus,
            readModel.StateVersion,
            readModel.LastEventId,
            readModel.UpdatedAt);
    }
}
