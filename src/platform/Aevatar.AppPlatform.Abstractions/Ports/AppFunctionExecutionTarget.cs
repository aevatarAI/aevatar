namespace Aevatar.AppPlatform.Abstractions.Ports;

public sealed record AppFunctionExecutionTarget(
    AppDefinitionSnapshot App,
    AppReleaseSnapshot Release,
    AppEntryRef Entry,
    AppServiceRef ServiceRef,
    string PrimaryActorId,
    string DeploymentId,
    string ActiveRevisionId);
