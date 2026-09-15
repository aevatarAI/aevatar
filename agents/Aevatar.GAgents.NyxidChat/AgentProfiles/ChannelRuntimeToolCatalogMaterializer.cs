using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

public interface IChannelRuntimeToolCatalogMaterializer
{
    Task<AgentTurnToolCatalog> MaterializeAsync(
        ChannelRuntimeConfigProof runtimeConfig,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default);
}

public sealed class ChannelRuntimeToolCatalogMaterializer : IChannelRuntimeToolCatalogMaterializer
{
    private readonly IToolSetRegistry _toolSetRegistry;
    private readonly IAgentToolDiscoveryService _toolDiscoveryService;
    private readonly ILogger<ChannelRuntimeToolCatalogMaterializer> _logger;

    public ChannelRuntimeToolCatalogMaterializer(
        IToolSetRegistry toolSetRegistry,
        IAgentToolDiscoveryService? toolDiscoveryService = null,
        ILogger<ChannelRuntimeToolCatalogMaterializer>? logger = null)
    {
        _toolSetRegistry = toolSetRegistry ?? throw new ArgumentNullException(nameof(toolSetRegistry));
        _toolDiscoveryService = toolDiscoveryService ?? AgentToolDiscoveryService.Instance;
        _logger = logger ?? NullLogger<ChannelRuntimeToolCatalogMaterializer>.Instance;
    }

    public async Task<AgentTurnToolCatalog> MaterializeAsync(
        ChannelRuntimeConfigProof runtimeConfig,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeConfig);
        ArgumentNullException.ThrowIfNull(registeredTools);
        ArgumentNullException.ThrowIfNull(toolContext);

        var diagnostics = new List<AgentProfileTurnDiagnostic>();
        var sources = new List<IAgentToolSource>();
        var selectorSlugs = runtimeConfig.NyxidServiceSelectors
            .Select(static selector => Normalize(selector.ServiceSlug))
            .Where(static serviceSlug => serviceSlug is not null)
            .Select(static serviceSlug => serviceSlug!)
            .ToArray();
        _logger.LogInformation(
            "Channel runtime tool catalog materialization started. registration={RegistrationId} configRevision={ConfigRevision} credentialSourceMode={CredentialSourceMode} toolSetRefs={ToolSetRefs} extraToolCount={ExtraToolCount} selectorCount={SelectorCount} selectorSlugs={SelectorSlugs} visibilityRestricted={VisibilityRestricted} visibilityAllowedToolCount={VisibilityAllowedToolCount}",
            runtimeConfig.RegistrationId,
            runtimeConfig.ConfigRevision,
            runtimeConfig.CredentialSourceMode,
            string.Join(',', runtimeConfig.ToolSetRefs),
            runtimeConfig.ExtraToolNames.Count,
            runtimeConfig.NyxidServiceSelectors.Count,
            string.Join(',', selectorSlugs),
            toolContext.ToolVisibility.IsRestricted,
            toolContext.ToolVisibility.AllowedToolNames?.Count ?? -1);
        foreach (var toolSetRef in runtimeConfig.ToolSetRefs)
        {
            var resolved = ResolveToolSet(toolSetRef, diagnostics);
            if (resolved is null)
                return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);

            sources.AddRange(resolved.Sources);
        }
        _logger.LogInformation(
            "Channel runtime tool set sources resolved. registration={RegistrationId} configRevision={ConfigRevision} toolSetRefs={ToolSetRefs} sourceCount={SourceCount}",
            runtimeConfig.RegistrationId,
            runtimeConfig.ConfigRevision,
            string.Join(',', runtimeConfig.ToolSetRefs),
            sources.Count);

        var discoveryToolContext = WithRuntimeConnectedServicesContext(runtimeConfig, toolContext);
        var availableTools = sources.Count == 0
            ? new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase)
            : await DiscoverToolsAsync(sources, discoveryToolContext, diagnostics, ct).ConfigureAwait(false);
        if (availableTools is null)
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);

        foreach (var tool in registeredTools)
            AddRegisteredTool(tool, availableTools, toolContext, diagnostics);

        var selectedNames = availableTools
            .Where(pair => IsSelectableRouteTool(pair.Key, pair.Value, toolContext))
            .Select(static pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var toolName in runtimeConfig.ExtraToolNames)
        {
            var normalized = Normalize(toolName);
            if (normalized is not null &&
                availableTools.TryGetValue(normalized, out var tool) &&
                IsSelectableRouteTool(normalized, tool, toolContext))
            {
                selectedNames.Add(normalized);
            }
        }

        var connectedNames = SelectConnectedOperationNames(runtimeConfig, availableTools, toolContext)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        LogConnectedServiceSelection(
            runtimeConfig,
            availableTools,
            toolContext,
            selectorSlugs,
            connectedNames);
        selectedNames.UnionWith(connectedNames);
        var selectedTools = availableTools
            .Where(pair => selectedNames.Contains(pair.Key))
            .Select(pair => new AgentTurnToolSelection(
                pair.Value,
                connectedNames.Contains(pair.Key)
                    ? AgentTurnToolOrigin.ConnectedService
                    : AgentTurnToolOrigin.RouteToolSet))
            .ToArray();
        return new AgentTurnToolCatalog(
            selectedNames,
            BuildPromptLayer(runtimeConfig),
            selectedSkillPromptLayer: null,
            selectedIntentId: null,
            candidateIntentId: null,
            diagnostics,
            selectedTools,
            hasUnresolvedConnectedServiceSelectors: false,
            requiredToolInvocation: null,
            AgentTurnToolCatalogBudget.Ordinary);
    }

    private static ProfileRoutingPromptLayer? BuildPromptLayer(ChannelRuntimeConfigProof runtimeConfig)
    {
        var instructions = Normalize(runtimeConfig.Instructions);
        if (instructions is null)
            return null;

        var builder = new StringBuilder()
            .Append("Channel registration: ").Append(runtimeConfig.RegistrationId)
            .Append("\nConfig revision: ").Append(runtimeConfig.ConfigRevision)
            .Append("\nConfig digest: ").Append(runtimeConfig.ConfigDigest);
        if (!string.IsNullOrWhiteSpace(runtimeConfig.DefaultSkillName))
        {
            builder.Append("\nDefault skill: ").Append(runtimeConfig.DefaultSkillName);
            if (!string.IsNullOrWhiteSpace(runtimeConfig.DefaultSkillVersion))
                builder.Append('@').Append(runtimeConfig.DefaultSkillVersion);
        }

        builder.Append("\nInstructions:\n").Append(instructions);
        return new ProfileRoutingPromptLayer(
            builder.ToString(),
            new ProfileRoutingPromptProvenance(
                $"channel-registration:{runtimeConfig.RegistrationId}@{runtimeConfig.ConfigRevision}"),
            new PromptLayerBounds(8 * 1024, 2 * 1024));
    }

    private static AgentToolExecutionContext WithRuntimeConnectedServicesContext(
        ChannelRuntimeConfigProof runtimeConfig,
        AgentToolExecutionContext toolContext)
    {
        var contextJson = runtimeConfig.NyxidServiceSelectors.Count == 0
            ? toolContext.ConnectedServices.ContextJson
            : AddRuntimeSelectors(
                toolContext.ConnectedServices.ContextJson,
                runtimeConfig.NyxidServiceSelectors);
        var evidence = BuildAgentKeyEvidence(runtimeConfig.AgentKeyGrant) ??
                       toolContext.ConnectedServices.AgentKeyAuthorizationEvidence;
        if (string.Equals(
                contextJson,
                toolContext.ConnectedServices.ContextJson,
                StringComparison.Ordinal) &&
            Equals(evidence, toolContext.ConnectedServices.AgentKeyAuthorizationEvidence))
        {
            return toolContext;
        }

        return toolContext with
        {
            ConnectedServices = new AgentToolConnectedServicesContext(contextJson, evidence),
        };
    }

    private static AgentKeyServiceAuthorizationEvidence? BuildAgentKeyEvidence(
        ChannelAgentKeyGrantSnapshot? grant)
    {
        if (grant is null)
            return null;

        return AgentKeyServiceAuthorizationEvidence.FromAllowedServices(
            grant.AllowedServiceIds,
            grant.ScopePlanDigest,
            grant.HasAllowAllServices ? grant.AllowAllServices : null);
    }

    private void LogConnectedServiceSelection(
        ChannelRuntimeConfigProof runtimeConfig,
        IReadOnlyDictionary<string, IAgentTool> availableTools,
        AgentToolExecutionContext toolContext,
        IReadOnlyList<string> selectorSlugs,
        IReadOnlySet<string> connectedNames)
    {
        var connectedOperationCount = 0;
        var visibilityRejectedCount = 0;
        var slugRejectedCount = 0;
        var endpointRejectedCount = 0;
        var eligibleCandidateCount = 0;
        var connectedOperationSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedConnectedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endpointFilters = runtimeConfig.NyxidServiceSelectors
            .Select(static selector => new
            {
                ServiceSlug = Normalize(selector.ServiceSlug),
                Endpoints = selector.EndpointNames
                    .Select(Normalize)
                    .Where(static endpoint => endpoint is not null)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
            })
            .Where(static selector => selector.ServiceSlug is not null)
            .ToArray();

        foreach (var pair in availableTools)
        {
            if (pair.Value is not IAgentToolOperationAdmissionOwner owner)
                continue;

            connectedOperationCount++;
            connectedOperationSlugs.Add(owner.OperationAdmission.CatalogServiceSlug);
            connectedOperationSlugs.Add(owner.OperationAdmission.ServiceSlug);
            if (!toolContext.ToolVisibility.Allows(pair.Key))
            {
                visibilityRejectedCount++;
                continue;
            }

            var matchedSelector = endpointFilters.FirstOrDefault(selector =>
                MatchesServiceSlug(owner.OperationAdmission, selector.ServiceSlug!));
            if (matchedSelector is null)
            {
                slugRejectedCount++;
                continue;
            }

            if (matchedSelector.Endpoints.Count > 0 &&
                (owner.OperationAdmission.Identity is not AgentToolOperationIdentity.PublishedEndpoint published ||
                 !matchedSelector.Endpoints.Contains(published.EndpointId)))
            {
                endpointRejectedCount++;
                continue;
            }

            eligibleCandidateCount++;
            selectedConnectedSlugs.Add(owner.OperationAdmission.CatalogServiceSlug);
            selectedConnectedSlugs.Add(owner.OperationAdmission.ServiceSlug);
        }

        _logger.LogInformation(
            "Channel runtime connected-service selection completed. registration={RegistrationId} configRevision={ConfigRevision} selectorCount={SelectorCount} selectorSlugs={SelectorSlugs} availableToolCount={AvailableToolCount} connectedOperationCount={ConnectedOperationCount} connectedOperationSlugs={ConnectedOperationSlugs} visibilityRejectedCount={VisibilityRejectedCount} slugRejectedCount={SlugRejectedCount} endpointRejectedCount={EndpointRejectedCount} eligibleConnectedCandidateCount={EligibleConnectedCandidateCount} selectedConnectedToolCount={SelectedConnectedToolCount} selectedConnectedSlugs={SelectedConnectedSlugs} visibilityRestricted={VisibilityRestricted} visibilityAllowedToolCount={VisibilityAllowedToolCount}",
            runtimeConfig.RegistrationId,
            runtimeConfig.ConfigRevision,
            runtimeConfig.NyxidServiceSelectors.Count,
            string.Join(',', selectorSlugs),
            availableTools.Count,
            connectedOperationCount,
            string.Join(',', connectedOperationSlugs.Where(static slug => !string.IsNullOrWhiteSpace(slug)).Order(StringComparer.OrdinalIgnoreCase)),
            visibilityRejectedCount,
            slugRejectedCount,
            endpointRejectedCount,
            eligibleCandidateCount,
            connectedNames.Count,
            string.Join(',', selectedConnectedSlugs.Where(static slug => !string.IsNullOrWhiteSpace(slug)).Order(StringComparer.OrdinalIgnoreCase)),
            toolContext.ToolVisibility.IsRestricted,
            toolContext.ToolVisibility.AllowedToolNames?.Count ?? -1);
    }

    private static string AddRuntimeSelectors(
        string? contextJson,
        IReadOnlyList<ChannelBotRuntimeNyxIdServiceSelector> selectors)
    {
        using var document = TryParseObject(contextJson);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            if (document is not null)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "nyxid_service_selectors", StringComparison.Ordinal))
                        property.WriteTo(writer);
                }
            }

            writer.WritePropertyName("nyxid_service_selectors");
            writer.WriteStartArray();
            foreach (var selector in selectors)
            {
                var serviceSlug = Normalize(selector.ServiceSlug);
                if (serviceSlug is null)
                    continue;

                writer.WriteStartObject();
                writer.WriteString("service_slug", serviceSlug);
                writer.WritePropertyName("endpoint_names");
                writer.WriteStartArray();
                foreach (var endpointName in selector.EndpointNames)
                {
                    var normalized = Normalize(endpointName);
                    if (normalized is not null)
                        writer.WriteStringValue(normalized);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static JsonDocument? TryParseObject(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
            return null;

        try
        {
            var document = JsonDocument.Parse(contextJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ToolSetResolveResult? ResolveToolSet(
        string toolSetRef,
        List<AgentProfileTurnDiagnostic> diagnostics)
    {
        ToolSetResolveResult resolved;
        try
        {
            resolved = _toolSetRegistry.Resolve(toolSetRef);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Channel runtime tool set resolve failed for {ToolSetRef}",
                toolSetRef);
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ToolSetUnavailable,
                toolSetRef));
            return null;
        }

        if (resolved.IsSuccess)
            return resolved;

        diagnostics.Add(new AgentProfileTurnDiagnostic(
            AgentProfileTurnDiagnosticCode.ToolSetUnavailable,
            resolved.Error?.Code ?? toolSetRef));
        return null;
    }

    private async Task<Dictionary<string, IAgentTool>?> DiscoverToolsAsync(
        IReadOnlyList<IAgentToolSource> sources,
        AgentToolExecutionContext toolContext,
        List<AgentProfileTurnDiagnostic> diagnostics,
        CancellationToken ct)
    {
        var discovery = await _toolDiscoveryService
            .DiscoverAsync(sources, toolContext, ct)
            .ConfigureAwait(false);
        if (!discovery.IsSuccess)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                discovery.Failure!.Code == AgentToolDiscoveryFailureCode.ToolNameCollision
                    ? AgentProfileTurnDiagnosticCode.ToolNameCollision
                    : AgentProfileTurnDiagnosticCode.ToolDiscoveryFailed,
                discovery.Failure.ToolName));
            return null;
        }

        var tools = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in discovery.Tools)
            AddDiscoveredTool(tool, tools, toolContext, diagnostics);
        return tools;
    }

    private static void AddDiscoveredTool(
        IAgentTool tool,
        Dictionary<string, IAgentTool> availableTools,
        AgentToolExecutionContext toolContext,
        List<AgentProfileTurnDiagnostic> diagnostics)
    {
        var name = Normalize(tool.Name);
        if (name is null)
            return;
        if (!IsEligible(tool, toolContext))
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ToolCapabilityRejected,
                name));
            return;
        }

        availableTools[name] = tool;
    }

    private static void AddRegisteredTool(
        IAgentTool tool,
        Dictionary<string, IAgentTool> availableTools,
        AgentToolExecutionContext toolContext,
        List<AgentProfileTurnDiagnostic> diagnostics)
    {
        var name = Normalize(tool.Name);
        if (name is null || !availableTools.ContainsKey(name))
            return;
        if (!IsEligible(tool, toolContext) || !ReferenceEquals(availableTools[name], tool))
        {
            availableTools.Remove(name);
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ToolNameCollision,
                name));
        }
    }

    private static IEnumerable<string> SelectConnectedOperationNames(
        ChannelRuntimeConfigProof runtimeConfig,
        IReadOnlyDictionary<string, IAgentTool> availableTools,
        AgentToolExecutionContext toolContext)
    {
        if (runtimeConfig.NyxidServiceSelectors.Count == 0)
        {
            if (runtimeConfig.AuthorizationMode != ChannelRegistrationAuthorizationMode.NyxidDefault)
                yield break;

            foreach (var pair in availableTools)
            {
                if (toolContext.ToolVisibility.Allows(pair.Key) &&
                    pair.Value is IAgentToolOperationAdmissionOwner)
                {
                    yield return pair.Key;
                }
            }

            yield break;
        }

        foreach (var selector in runtimeConfig.NyxidServiceSelectors)
        {
            var serviceSlug = Normalize(selector.ServiceSlug);
            if (serviceSlug is null)
                continue;
            var endpoints = selector.EndpointNames
                .Select(Normalize)
                .Where(static endpoint => endpoint is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in availableTools)
            {
                if (!toolContext.ToolVisibility.Allows(pair.Key) ||
                    pair.Value is not IAgentToolOperationAdmissionOwner owner ||
                    !MatchesServiceSlug(owner.OperationAdmission, serviceSlug))
                {
                    continue;
                }

                if (endpoints.Count == 0 ||
                    owner.OperationAdmission.Identity is AgentToolOperationIdentity.PublishedEndpoint published &&
                    endpoints.Contains(published.EndpointId))
                {
                    yield return pair.Key;
                }
            }
        }
    }

    private static bool MatchesServiceSlug(AgentToolOperationAdmission admission, string serviceSlug) =>
        string.Equals(admission.CatalogServiceSlug, serviceSlug, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(admission.ServiceSlug, serviceSlug, StringComparison.OrdinalIgnoreCase);

    private static bool IsSelectableRouteTool(
        string name,
        IAgentTool tool,
        AgentToolExecutionContext toolContext) =>
        tool is not IAgentToolOperationAdmissionOwner && toolContext.ToolVisibility.Allows(name);

    private static string? Normalize(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool IsEligible(IAgentTool tool, AgentToolExecutionContext toolContext)
    {
        if (tool is not IAgentToolCapabilityDescriptor descriptor)
            return true;

        if (descriptor.Capabilities.Contains(
                AgentToolCapabilities.ExcludeFromDirectChannelChat,
                StringComparer.Ordinal))
        {
            return false;
        }

        return !descriptor.Capabilities.Contains(
                   AgentToolCapabilities.RequiresHumanSession,
                   StringComparer.Ordinal) ||
               !string.IsNullOrWhiteSpace(
                   AgentToolHumanSessionNyxIdCredential.ResolveBearerToken(toolContext));
    }
}
