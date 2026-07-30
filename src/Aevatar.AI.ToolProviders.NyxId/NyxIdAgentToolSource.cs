using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// NyxID tool source. Provides tools for managing services, credentials,
/// nodes, approvals, and making proxied requests through NyxID.
/// </summary>
public sealed class NyxIdAgentToolSource : IAgentToolSource
{
    private readonly NyxIdToolOptions _options;
    private readonly NyxIdApiClient _client;
    private readonly ILogger _logger;
    private readonly INyxIdProxyFileArtifactIngress? _fileArtifactIngress;

    public NyxIdAgentToolSource(
        NyxIdToolOptions options,
        NyxIdApiClient client,
        INyxIdProxyFileArtifactIngress? fileArtifactIngress = null,
        ILogger<NyxIdAgentToolSource>? logger = null)
    {
        _options = options;
        _client = client;
        _fileArtifactIngress = fileArtifactIngress;
        _logger = logger ?? NullLogger<NyxIdAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-025-nyxid-tool-discovery-actor-cache):
        //   Old pattern: NyxIdSpecCatalog + SpecFetchToken + IServiceDiscoveryCache 在仓库内建第二 catalog(NyxID 真实源的影子)
        //   New principle: NyxID 是唯一真实源;删除 in-process catalog 假权威面; routing 和 spec hints 请求时读取 live NyxID surface;保留 typed tools + live nyxid_proxy
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogDebug("NyxID base URL not configured, skipping NyxID tools");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        var tools = new List<IAgentTool>
        {
            new NyxIdAccountTool(_client),
            new NyxIdStatusTool(_client),
            new NyxIdProfileTool(_client),
            new NyxIdMfaTool(_client),
            new NyxIdSessionsTool(_client),
            new NyxIdCatalogTool(_client),
            new NyxIdServicesTool(_client),
            new NyxIdProxyTool(_client, _logger, _fileArtifactIngress, _options.EffectiveProxyFileArtifactMaxBytes),
            new NyxIdCodeExecuteTool(_client, _logger),
            new NyxIdApiKeysTool(_client),
            new NyxIdNodesTool(_client),
            new NyxIdApprovalsTool(_client),
            new NyxIdEndpointsTool(_client),
            new NyxIdExternalKeysTool(_client),
            new NyxIdNotificationsTool(_client),
            new NyxIdLlmStatusTool(_client),
            new NyxIdProvidersTool(_client),
            new NyxIdChannelBotsTool(_client),
            new NyxIdOrgTool(_client),
            new NyxIdChannelEventsTool(_client),
            new NyxIdAdminTool(_client),
        };

        if (_options.EnableSshExecTool)
        {
            var sshExecutor = new NyxIdSshCommandExecutor(_client, _logger);
            tools.Add(new NyxIdSshExecTool(sshExecutor, _options));
            tools.Add(new NyxIdCodexExecTool(sshExecutor, _options));
        }

        _logger.LogInformation(
            "NyxID tools registered ({Count} tools, base URL: {BaseUrl}, ssh_exec={SshEnabled})",
            tools.Count, _options.BaseUrl, _options.EnableSshExecTool);

        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}
