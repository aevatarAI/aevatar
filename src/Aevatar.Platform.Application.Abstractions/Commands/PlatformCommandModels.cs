namespace Aevatar.Platform.Application.Abstractions.Commands;

public sealed record PlatformCommandRequest(
    string Subsystem,
    string Command,
    string Method = "POST",
    string? PayloadJson = null,
    string? ContentType = "application/json");

public enum PlatformCommandStartError
{
    None = 0,
    InvalidRequest = 1,
    SubsystemNotFound = 2,
}

public sealed record PlatformCommandStarted(
    string CommandId,
    string Subsystem,
    string Command,
    string Method,
    string TargetEndpoint,
    DateTimeOffset AcceptedAt);

public sealed record PlatformCommandEnqueueResult(
    PlatformCommandStartError Error,
    PlatformCommandStarted? Started)
{
    public bool Succeeded => Error == PlatformCommandStartError.None;
}

public sealed class PlatformCommandStatus
{
    public string CommandId { get; set; } = string.Empty;
    public string Subsystem { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string TargetEndpoint { get; set; } = string.Empty;
    public string State { get; set; } = "Accepted";
    public bool Succeeded { get; set; }
    public int? ResponseStatusCode { get; set; }
    public string ResponseContentType { get; set; } = string.Empty;
    public string ResponseBody { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
