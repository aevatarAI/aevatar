using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

public sealed class AgentProfileTurnCatalogMaterializer
{
    private readonly IToolSetRegistry _toolSetRegistry;
    private readonly IAgentProfileTurnClassifier _classifier;
    private readonly IExactRemoteSkillFetcher? _exactRemoteSkillFetcher;
    private readonly SkillFrontmatterParser _frontmatterParser;
    private readonly ILogger<AgentProfileTurnCatalogMaterializer> _logger;
    private readonly TimeProvider _timeProvider;

    public AgentProfileTurnCatalogMaterializer(
        IToolSetRegistry toolSetRegistry,
        IAgentProfileTurnClassifier classifier,
        IExactRemoteSkillFetcher? exactRemoteSkillFetcher = null,
        SkillFrontmatterParser? frontmatterParser = null,
        ILogger<AgentProfileTurnCatalogMaterializer>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _toolSetRegistry = toolSetRegistry ?? throw new ArgumentNullException(nameof(toolSetRegistry));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _exactRemoteSkillFetcher = exactRemoteSkillFetcher;
        _frontmatterParser = frontmatterParser ?? new SkillFrontmatterParser();
        _logger = logger ?? NullLogger<AgentProfileTurnCatalogMaterializer>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AgentProfileTurnCatalog> MaterializeAsync(
        AgentProfileSnapshot profile,
        string userMessage,
        string? accessToken,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registeredTools);
        ArgumentNullException.ThrowIfNull(toolContext);

        var diagnostics = new List<AgentProfileTurnDiagnostic>();
        if (!AgentProfileSnapshotCodec.Verify(profile))
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ProfileInvalid,
                "snapshot_digest_invalid"));
            return BuildCatalog(profile, [], null, null, null, diagnostics);
        }

        var routeTools = await DiscoverToolSetAsync(
            profile.RouteToolSetRef,
            toolContext,
            AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable,
            diagnostics,
            ct);
        var registered = ToEligibleToolNames(registeredTools, toolContext, diagnostics);
        var available = new HashSet<string>(routeTools.Names, StringComparer.OrdinalIgnoreCase);
        available.IntersectWith(registered.Names);
        available.RemoveWhere(name => !toolContext.ToolVisibility.Allows(name));

        var maximum = await ResolvePolicyAsync(profile.MaximumToolPolicy, toolContext, diagnostics, ct);
        available.IntersectWith(maximum.Names);
        var recovery = await ResolvePolicyAsync(profile.RecoveryToolPolicy, toolContext, diagnostics, ct);
        var recoveryNames = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        recoveryNames.IntersectWith(recovery.Names);
        if (routeTools.HadFailure || registered.HadFailure || maximum.HadFailure || recovery.HadFailure)
            return BuildCatalog(profile, recoveryNames, null, null, null, diagnostics);

        var candidate = await SelectCandidateAsync(profile, userMessage, diagnostics, ct);
        if (candidate is null)
            return BuildCatalog(profile, recoveryNames, null, null, null, diagnostics);

        if (profile.ActivationMode != AgentProfileActivationMode.Enforced)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ShadowCandidate,
                candidate.IntentId));
            return BuildCatalog(profile, recoveryNames, null, candidate.IntentId, null, diagnostics);
        }

        var fetched = await FetchSelectedSkillAsync(profile, candidate, accessToken, diagnostics, ct);
        if (fetched is null)
            return BuildCatalog(profile, recoveryNames, null, candidate.IntentId, null, diagnostics);

        var taskPolicy = await ResolvePolicyAsync(candidate.TaskToolPolicy, toolContext, diagnostics, ct);
        if (taskPolicy.HadFailure)
            return BuildCatalog(profile, recoveryNames, null, candidate.IntentId, null, diagnostics);

        var selectedPolicy = new HashSet<string>(recovery.Names, StringComparer.OrdinalIgnoreCase);
        selectedPolicy.UnionWith(taskPolicy.Names);
        var finalNames = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        finalNames.IntersectWith(selectedPolicy);

        var selectedLayer = new SelectedSkillPromptLayer(
            fetched,
            new SelectedSkillPromptProvenance(
                $"ornn:{candidate.SkillRef.Guid}@{candidate.SkillRef.LiteralVersion}"),
            new PromptLayerBounds(profile.MaxSelectedSkillBytes, Math.Max(1, profile.MaxSelectedSkillBytes / 4)));
        return BuildCatalog(
            profile,
            finalNames,
            candidate.IntentId,
            candidate.IntentId,
            selectedLayer,
            diagnostics);
    }

    private async Task<AgentProfileSkillMember?> SelectCandidateAsync(
        AgentProfileSnapshot profile,
        string userMessage,
        List<AgentProfileTurnDiagnostic> diagnostics,
        CancellationToken ct)
    {
        var aliasMatches = profile.Members
            .Where(member => member.ExplicitTriggerAliases.Any(alias => MatchesAlias(userMessage, alias)))
            .ToArray();
        if (aliasMatches.Length == 1)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.AliasMatched,
                aliasMatches[0].IntentId));
            return aliasMatches[0];
        }
        if (aliasMatches.Length > 1)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ClassifierFailed,
                "alias_collision"));
            return null;
        }

        if (profile.Members.Count == 0 || profile.ClassifierTimeoutMs <= 0)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ClassifierFailed,
                "classifier_not_configured"));
            return null;
        }

        AgentProfileTurnClassificationResult result;
        try
        {
            result = await _classifier.ClassifyAsync(
                new AgentProfileTurnClassificationRequest(
                    userMessage ?? string.Empty,
                    profile.Members.Take(32)
                        .Select(static member => new AgentProfileTurnClassificationCandidate(
                            member.IntentId,
                            member.RoutingDescription))
                        .ToArray(),
                    TimeSpan.FromMilliseconds(profile.ClassifierTimeoutMs)),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            result = AgentProfileTurnClassificationResult.Failed("classifier_exception");
        }

        if (result.Status == AgentProfileTurnClassificationStatus.NoMatch)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ClassifierNoMatch,
                "no_match"));
            return null;
        }
        if (result.Status != AgentProfileTurnClassificationStatus.Matched ||
            string.IsNullOrWhiteSpace(result.IntentId))
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ClassifierFailed,
                result.FailureCode ?? "failed"));
            return null;
        }

        var member = profile.Members.SingleOrDefault(candidate =>
            string.Equals(candidate.IntentId, result.IntentId, StringComparison.Ordinal));
        if (member is null)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ClassifierFailed,
                "unknown_intent"));
            return null;
        }

        diagnostics.Add(new AgentProfileTurnDiagnostic(
            AgentProfileTurnDiagnosticCode.ClassifierMatched,
            member.IntentId));
        return member;
    }

    private async Task<string?> FetchSelectedSkillAsync(
        AgentProfileSnapshot profile,
        AgentProfileSkillMember candidate,
        string? accessToken,
        List<AgentProfileTurnDiagnostic> diagnostics,
        CancellationToken ct)
    {
        if (_exactRemoteSkillFetcher is null || string.IsNullOrWhiteSpace(accessToken) ||
            candidate.SkillRef is null || profile.ExactSkillFetchTimeoutMs <= 0)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed,
                "exact_fetch_unavailable"));
            return null;
        }

        ExactRemoteSkillFetchResult fetchResult;
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(profile.ExactSkillFetchTimeoutMs),
            _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            fetchResult = await _exactRemoteSkillFetcher.FetchAsync(
                accessToken,
                candidate.SkillRef,
                linkedCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed,
                "timeout"));
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exact remote skill fetch failed for intent {IntentId}.", candidate.IntentId);
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed,
                "fetch_exception"));
            return null;
        }

        if (!fetchResult.IsSuccess)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed,
                fetchResult.FailureCode?.ToString() ?? "failed"));
            return null;
        }

        if (!string.Equals(fetchResult.Guid, candidate.SkillRef.Guid, StringComparison.Ordinal) ||
            !string.Equals(fetchResult.LiteralVersion, candidate.SkillRef.LiteralVersion, StringComparison.Ordinal) ||
            !string.Equals(fetchResult.Name, candidate.ExpectedSkillName, StringComparison.Ordinal) ||
            !string.Equals(fetchResult.PublisherId, candidate.ReviewedPublisherId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(fetchResult.SkillHash))
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch,
                candidate.IntentId));
            return null;
        }

        var skillMarkdown = fetchResult.SkillMarkdown ?? string.Empty;
        if (profile.MaxSelectedSkillBytes <= 0 ||
            Encoding.UTF8.GetByteCount(skillMarkdown) > profile.MaxSelectedSkillBytes)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid,
                "body_out_of_bounds"));
            return null;
        }

        var parsed = _frontmatterParser.Parse(skillMarkdown);
        if (string.IsNullOrWhiteSpace(parsed.Body) ||
            (!string.IsNullOrWhiteSpace(parsed.Name) &&
             !string.Equals(parsed.Name, candidate.ExpectedSkillName, StringComparison.Ordinal)))
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid,
                "frontmatter_identity_invalid"));
            return null;
        }

        return parsed.Body;
    }

    private async Task<ToolPolicyResolution> ResolvePolicyAsync(
        AgentProfileToolPolicy? policy,
        AgentToolExecutionContext toolContext,
        List<AgentProfileTurnDiagnostic> diagnostics,
        CancellationToken ct)
    {
        if (policy is null)
            return new ToolPolicyResolution(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                false);

        var names = new HashSet<string>(
            policy.ToolNames.Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var hadFailure = false;
        foreach (var toolSetRef in policy.ToolSetRefs)
        {
            var resolution = await DiscoverToolSetAsync(
                toolSetRef,
                toolContext,
                AgentProfileTurnDiagnosticCode.ToolSetUnavailable,
                diagnostics,
                ct);
            names.UnionWith(resolution.Names);
            hadFailure |= resolution.HadFailure;
        }

        return new ToolPolicyResolution(names, hadFailure);
    }

    private async Task<ToolPolicyResolution> DiscoverToolSetAsync(
        string? toolSetName,
        AgentToolExecutionContext toolContext,
        AgentProfileTurnDiagnosticCode unavailableCode,
        List<AgentProfileTurnDiagnostic> diagnostics,
        CancellationToken ct)
    {
        ToolSetResolveResult resolved;
        try
        {
            resolved = _toolSetRegistry.Resolve(new ChatRouteToolSetRef { Name = toolSetName ?? string.Empty });
        }
        catch (Exception)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(unavailableCode, toolSetName ?? string.Empty));
            return new ToolPolicyResolution(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                true);
        }

        if (!resolved.IsSuccess)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                unavailableCode,
                resolved.Error?.Code ?? "resolve_failed"));
            return new ToolPolicyResolution(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                true);
        }

        var discovered = new List<IAgentTool>();
        foreach (var source in resolved.Sources)
        {
            try
            {
                discovered.AddRange(await source.DiscoverToolsAsync(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.ToolDiscoveryFailed,
                    resolved.Name ?? string.Empty));
                return new ToolPolicyResolution(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    true);
            }
        }

        return ToEligibleToolNames(discovered, toolContext, diagnostics);
    }

    private static ToolPolicyResolution ToEligibleToolNames(
        IReadOnlyList<IAgentTool> tools,
        AgentToolExecutionContext toolContext,
        List<AgentProfileTurnDiagnostic> diagnostics)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hadFailure = false;
        foreach (var group in tools
                     .Where(static tool => !string.IsNullOrWhiteSpace(tool.Name))
                     .GroupBy(static tool => tool.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1)
            {
                hadFailure = true;
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.ToolNameCollision,
                    group.Key));
                continue;
            }

            var tool = group.Single();
            if (!IsEligible(tool, toolContext))
            {
                hadFailure = true;
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.ToolCapabilityRejected,
                    group.Key));
                continue;
            }

            names.Add(group.Key);
        }

        return new ToolPolicyResolution(names, hadFailure);
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
               !string.IsNullOrWhiteSpace(toolContext.Credentials.NyxIdAccessToken);
    }

    private static AgentProfileTurnCatalog BuildCatalog(
        AgentProfileSnapshot profile,
        IEnumerable<string> finalNames,
        string? selectedIntentId,
        string? candidateIntentId,
        SelectedSkillPromptLayer? selectedSkillPromptLayer,
        IReadOnlyList<AgentProfileTurnDiagnostic> diagnostics)
    {
        var profileText = new StringBuilder()
            .Append("Agent profile: ").Append(profile.ProfileId)
            .Append("\nProfile version: ").Append(profile.ProfileVersion)
            .Append("\nPolicy revision: ").Append(profile.PolicyRevision);
        if (!string.IsNullOrWhiteSpace(candidateIntentId))
            profileText.Append("\nCandidate intent: ").Append(candidateIntentId);
        if (!string.IsNullOrWhiteSpace(selectedIntentId))
            profileText.Append("\nSelected intent: ").Append(selectedIntentId);

        var profileLayer = new ProfileRoutingPromptLayer(
            profileText.ToString(),
            new ProfileRoutingPromptProvenance(
                $"agent-profile:{profile.ProfileId}@{profile.ProfileVersion}"),
            new PromptLayerBounds(8 * 1024, 2 * 1024));
        return new AgentProfileTurnCatalog(
            finalNames,
            profileLayer,
            selectedSkillPromptLayer,
            selectedIntentId,
            candidateIntentId,
            diagnostics);
    }

    private static bool MatchesAlias(string? userMessage, string? alias)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return true;
        if (string.IsNullOrWhiteSpace(alias))
            return false;

        var message = userMessage.Trim();
        var normalizedAlias = alias.Trim();
        return string.Equals(message, normalizedAlias, StringComparison.OrdinalIgnoreCase) ||
               (message.StartsWith(normalizedAlias, StringComparison.OrdinalIgnoreCase) &&
                message.Length > normalizedAlias.Length &&
                char.IsWhiteSpace(message[normalizedAlias.Length]));
    }

    private sealed record ToolPolicyResolution(IReadOnlySet<string> Names, bool HadFailure);
}
