namespace Aevatar.Studio.Domain.Studio.Models;

public sealed record StepCapability
{
    public NyxIdOperationCapability? NyxIdOperation { get; init; }

    public NyxIdRequestCapability? NyxIdRequest { get; init; }
}

public sealed record NyxIdOperationCapability
{
    public string UserServiceId { get; init; } = string.Empty;

    public string EndpointId { get; init; } = string.Empty;
}

public sealed record NyxIdRequestCapability
{
    public string UserServiceId { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public string PathTemplate { get; init; } = string.Empty;

    public List<string> QueryParameters { get; init; } = [];

    public List<string> HeaderParameters { get; init; } = [];

    public bool BodyRequired { get; init; }

    public string BodyMode { get; init; } = string.Empty;

    public string ResponseMode { get; init; } = string.Empty;
}
