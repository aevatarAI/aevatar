namespace Aevatar.AI.ToolProviders.Skills;

public enum RemoteSkillFetchFailureKind
{
    Unspecified = 0,
    AccessDenied = 1,
    Unavailable = 2,
}

public sealed class RemoteSkillFetchException : InvalidOperationException
{
    public RemoteSkillFetchException(
        string message,
        RemoteSkillFetchFailureKind failureKind,
        string skillName,
        int httpStatus = 0)
        : base(message)
    {
        FailureKind = failureKind;
        SkillName = skillName;
        HttpStatus = httpStatus;
    }

    public RemoteSkillFetchFailureKind FailureKind { get; }

    public string SkillName { get; }

    public int HttpStatus { get; }

    public static RemoteSkillFetchException AccessDenied(string skillName, string detail, int httpStatus) =>
        new(
            string.IsNullOrWhiteSpace(detail)
                ? $"Remote skill '{skillName}' access denied."
                : detail,
            RemoteSkillFetchFailureKind.AccessDenied,
            skillName,
            httpStatus);

    public static RemoteSkillFetchException Unavailable(string skillName, string detail, int httpStatus = 0) =>
        new(
            string.IsNullOrWhiteSpace(detail)
                ? $"Remote skill '{skillName}' is temporarily unavailable."
                : detail,
            RemoteSkillFetchFailureKind.Unavailable,
            skillName,
            httpStatus);
}
