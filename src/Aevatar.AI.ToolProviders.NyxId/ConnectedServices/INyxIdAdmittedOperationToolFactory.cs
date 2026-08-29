using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>
/// Rehydrates only a server-sealed admitted read operation. It does not discover
/// by name or route, and it never creates an effect-capable recovery tool.
/// </summary>
public interface INyxIdAdmittedOperationToolFactory
{
    IAgentTool? CreateRead(AgentToolOperationAdmissionPayload admission);
}

internal sealed class NyxIdAdmittedOperationToolFactory : INyxIdAdmittedOperationToolFactory
{
    private readonly NyxIdApiClient _apiClient;
    private readonly NyxIdToolOptions _options;
    private readonly NyxIdDelegationTokenLease _delegationTokenLease;
    private readonly ILogger<NyxIdAdmittedOperationToolFactory> _logger;
    private readonly INyxIdProxyFileArtifactIngress? _fileArtifactIngress;

    public NyxIdAdmittedOperationToolFactory(
        NyxIdApiClient apiClient,
        NyxIdToolOptions options,
        ILogger<NyxIdAdmittedOperationToolFactory> logger,
        INyxIdProxyFileArtifactIngress? fileArtifactIngress = null,
        NyxIdDelegationTokenLease? delegationTokenLease = null)
    {
        _apiClient = apiClient;
        _options = options;
        _delegationTokenLease = delegationTokenLease ?? new NyxIdDelegationTokenLease(apiClient);
        _logger = logger;
        _fileArtifactIngress = fileArtifactIngress;
    }

    public IAgentTool? CreateRead(AgentToolOperationAdmissionPayload admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var mapped = AgentToolOperationAdmissionPayloadMapper.FromPayload(admission);
        if (mapped is null ||
            mapped.ExecutionPolicy.Risk != AgentToolOperationRisk.ReadOnly ||
            mapped.ExecutionPolicy.Approval != AgentToolOperationApproval.None ||
            mapped.ExecutionPolicy.EnforcementOwner != AgentToolOperationEnforcementOwner.Aevatar ||
            mapped.Identity is not AgentToolOperationIdentity.PublishedEndpoint ||
            mapped.HttpMethod is not ("GET" or "HEAD" or "OPTIONS") ||
            admission.ReadBack is not null)
        {
            return null;
        }

        var proxy = new NyxIdProxyTool(
            _apiClient,
            _logger,
            _fileArtifactIngress,
            _options.EffectiveProxyFileArtifactMaxBytes,
            _options.ManagedWorkflowAdmissionMode,
            _delegationTokenLease);
        return new NyxIdConnectedServiceOperationTool(
            proxy,
            mapped,
            "Connected service",
            "Verification read",
            mapped.ServiceSlug,
            null,
            NyxIdServiceAccessTokenSource.User);
    }
}
