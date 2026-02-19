namespace Aevatar.Platform.Sagas.States;

public sealed class PlatformCommandSagaState : SagaStateBase
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
}
