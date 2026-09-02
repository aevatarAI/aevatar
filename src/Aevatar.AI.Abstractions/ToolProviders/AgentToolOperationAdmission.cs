namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Server-generated proof that the current tool call site may invoke exactly one admitted
/// connected-service request. It is a provider-neutral projection of an admission decision made
/// before dispatch; a tool must build its request from this contract instead of caller-supplied
/// route fields. Absent proof means the caller is not an admitted call site.
/// </summary>
public sealed record AgentToolOperationAdmission(
    string ServiceInstanceId,
    string ServiceSlug,
    AgentToolOperationIdentity Identity,
    AgentToolOperationAuthorizationBasis AuthorizationBasis,
    string HttpMethod,
    string PathTemplate,
    string ContractDigest,
    IReadOnlyList<AgentToolOperationParameter> Parameters,
    AgentToolOperationRequestBody? RequestBody,
    AgentToolOperationResponsePolicy ResponsePolicy,
    AgentToolOperationExecutionPolicy ExecutionPolicy)
{
    public IEnumerable<AgentToolOperationParameter> PathParameters =>
        Parameters.Where(static parameter => parameter.Location == AgentToolOperationParameterLocation.Path);

    public IEnumerable<AgentToolOperationParameter> QueryParameters =>
        Parameters.Where(static parameter => parameter.Location == AgentToolOperationParameterLocation.Query);

    public IEnumerable<AgentToolOperationParameter> HeaderParameters =>
        Parameters.Where(static parameter => parameter.Location == AgentToolOperationParameterLocation.Header);
}

public abstract record AgentToolOperationIdentity
{
    private AgentToolOperationIdentity()
    {
    }

    public sealed record PublishedEndpoint(string EndpointId) : AgentToolOperationIdentity;

    public sealed record AuthoredRequest(string RequestContractDigest) : AgentToolOperationIdentity;
}

public enum AgentToolOperationAuthorizationBasis
{
    PublishedContract = 1,
    ExplicitRequest = 2,
}

public enum AgentToolOperationRisk
{
    Unspecified = 0,
    ReadOnly = 1,
    Write = 2,
    Destructive = 3,
}

public enum AgentToolOperationApproval
{
    Unspecified = 0,
    None = 1,
    Required = 2,
}

public enum AgentToolOperationEnforcementOwner
{
    Unspecified = 0,
    Aevatar = 1,
    NyxId = 2,
}

public enum AgentToolOperationExecutionMode
{
    Unspecified = 0,
    Interactive = 1,
    Durable = 2,
}

public sealed record AgentToolOperationExecutionPolicy(
    AgentToolOperationRisk Risk,
    AgentToolOperationApproval Approval,
    AgentToolOperationEnforcementOwner EnforcementOwner,
    IReadOnlyList<AgentToolOperationExecutionMode> AllowedExecutionModes)
{
    public static AgentToolOperationExecutionPolicy Unspecified { get; } = new(
        AgentToolOperationRisk.Unspecified,
        AgentToolOperationApproval.Unspecified,
        AgentToolOperationEnforcementOwner.Unspecified,
        []);
}

public enum AgentToolOperationParameterLocation
{
    Unspecified = 0,
    Path = 1,
    Query = 2,
    Header = 3,
}

public enum AgentToolOperationValueKind
{
    Unspecified = 0,
    String = 1,
    Integer = 2,
    Number = 3,
    Boolean = 4,
    Object = 5,
    Array = 6,
}

public sealed record AgentToolOperationParameter(
    string Name,
    AgentToolOperationParameterLocation Location,
    bool Required,
    AgentToolOperationValueSchema Schema);

public sealed record AgentToolOperationRequestBody(
    bool Required,
    string MediaType,
    AgentToolOperationValueSchema Schema);

public sealed record AgentToolOperationResponsePolicy(
    bool TextAllowed,
    bool FileArtifactAllowed,
    IReadOnlyList<string> MediaTypes)
{
    public static AgentToolOperationResponsePolicy TextOnly { get; } = new(true, false, []);
}

public sealed record AgentToolOperationValueSchema(
    AgentToolOperationValueKind Kind,
    IReadOnlyList<AgentToolOperationSchemaProperty> Properties,
    IReadOnlySet<string> RequiredProperties,
    AgentToolOperationValueSchema? Items,
    IReadOnlyList<string> AllowedValues,
    bool AdditionalPropertiesAllowed)
{
    public static AgentToolOperationValueSchema Text { get; } = new(
        AgentToolOperationValueKind.String,
        [],
        new HashSet<string>(StringComparer.Ordinal),
        null,
        [],
        false);

    public AgentToolOperationValueSchema? FindProperty(string name) =>
        Properties.FirstOrDefault(property =>
            string.Equals(property.Name, name, StringComparison.Ordinal))?.Schema;
}

public sealed record AgentToolOperationSchemaProperty(
    string Name,
    AgentToolOperationValueSchema Schema);
