namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Server-generated proof that the current tool call site may invoke exactly one published
/// connected-service operation. It is a provider-neutral projection of an admission decision made
/// before dispatch; a tool must build its request from this contract instead of caller-supplied
/// route fields. Absent proof means the caller is not an admitted call site.
/// </summary>
public sealed record AgentToolOperationAdmission(
    string ServiceInstanceId,
    string ServiceSlug,
    string OperationId,
    string HttpMethod,
    string PathTemplate,
    string ContractDigest,
    IReadOnlyList<AgentToolOperationParameter> Parameters,
    AgentToolOperationRequestBody? RequestBody,
    AgentToolOperationResponsePolicy ResponsePolicy)
{
    public IEnumerable<AgentToolOperationParameter> PathParameters =>
        Parameters.Where(static parameter => parameter.Location == AgentToolOperationParameterLocation.Path);

    public IEnumerable<AgentToolOperationParameter> QueryParameters =>
        Parameters.Where(static parameter => parameter.Location == AgentToolOperationParameterLocation.Query);

    public IEnumerable<AgentToolOperationParameter> HeaderParameters =>
        Parameters.Where(static parameter => parameter.Location == AgentToolOperationParameterLocation.Header);
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
