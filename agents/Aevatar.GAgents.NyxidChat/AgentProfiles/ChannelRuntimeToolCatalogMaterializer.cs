using System.Text;
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

        var availableTools = sources.Count == 0
            ? new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase)
            : await DiscoverToolsAsync(sources, toolContext, diagnostics, ct).ConfigureAwait(false);
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

        var selectedTools = availableTools
            .Where(pair => selectedNames.Contains(pair.Key))
            .Select(pair => new AgentTurnToolSelection(
                pair.Value,
                AgentTurnToolOrigin.RouteToolSet))
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
            AgentTurnToolCatalogBudget.ChannelReply);
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
