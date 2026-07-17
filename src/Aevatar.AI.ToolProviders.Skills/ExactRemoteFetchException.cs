namespace Aevatar.AI.ToolProviders.Skills;

public enum ExactRemoteFetchFailureKind
{
    Unavailable,
    InvalidResponse,
    IntegrityMismatch,
}

public enum ExactRemoteResourceKind
{
    Skill,
    Skillset,
}

public sealed class ExactRemoteFetchException : Exception
{
    public ExactRemoteFetchFailureKind FailureKind { get; }
    public ExactRemoteResourceKind ResourceKind { get; }
    public string Guid { get; }
    public string LiteralVersion { get; }
    public int? HttpStatus { get; }

    public ExactRemoteFetchException(
        ExactRemoteFetchFailureKind failureKind,
        ExactRemoteResourceKind resourceKind,
        string guid,
        string literalVersion,
        string message,
        int? httpStatus = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
        ResourceKind = resourceKind;
        Guid = guid;
        LiteralVersion = literalVersion;
        HttpStatus = httpStatus;
    }

    public static ExactRemoteFetchException Unavailable(
        ExactRemoteResourceKind resourceKind,
        string guid,
        string literalVersion,
        string detail,
        int? httpStatus = null) =>
        new(
            ExactRemoteFetchFailureKind.Unavailable,
            resourceKind,
            guid,
            literalVersion,
            $"Exact remote {ResourceName(resourceKind)} '{guid}@{literalVersion}' is unavailable: {detail}",
            httpStatus);

    public static ExactRemoteFetchException InvalidResponse(
        ExactRemoteResourceKind resourceKind,
        string guid,
        string literalVersion,
        string detail,
        Exception? innerException = null) =>
        new(
            ExactRemoteFetchFailureKind.InvalidResponse,
            resourceKind,
            guid,
            literalVersion,
            $"Exact remote {ResourceName(resourceKind)} '{guid}@{literalVersion}' returned an invalid response: {detail}",
            innerException: innerException);

    public static ExactRemoteFetchException IntegrityMismatch(
        ExactRemoteResourceKind resourceKind,
        string guid,
        string literalVersion,
        string detail) =>
        new(
            ExactRemoteFetchFailureKind.IntegrityMismatch,
            resourceKind,
            guid,
            literalVersion,
            $"Exact remote {ResourceName(resourceKind)} '{guid}@{literalVersion}' failed integrity verification: {detail}");

    private static string ResourceName(ExactRemoteResourceKind resourceKind) =>
        resourceKind == ExactRemoteResourceKind.Skill ? "skill" : "skillset";
}
