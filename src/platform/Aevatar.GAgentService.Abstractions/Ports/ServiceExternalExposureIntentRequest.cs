using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public sealed record ServiceExternalExposureIntentRequest(
    ServiceIdentity Identity,
    bool ExposureDesired,
    ServiceDefinitionSpec? DesiredDefinition = null,
    ServiceCatalogSnapshot? ExistingService = null);
