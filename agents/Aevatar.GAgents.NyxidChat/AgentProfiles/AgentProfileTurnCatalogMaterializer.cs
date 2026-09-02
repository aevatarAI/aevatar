using System.Text;
using System.Security.Cryptography;
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

    public async Task<AgentProfileTurnAuthorityPreparation> PrepareAsync(
        AgentProfileSnapshot profile,
        string sessionId,
        string userMessage,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registeredTools);
        ArgumentNullException.ThrowIfNull(toolContext);
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("A profiled turn requires a session id.", nameof(sessionId));

        var diagnostics = new List<AgentProfileTurnDiagnostic>();
        if (!AgentProfileSnapshotCodec.Verify(profile))
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ProfileInvalid,
                "snapshot_digest_invalid"));
            return CreatePreparation(
                sessionId,
                candidate: null,
                selectedExactSkillRef: null,
                AgentProfileTurnAuthorityKind.RestrictedEmpty,
                [],
                diagnostics);
        }

        var routeTools = await DiscoverToolSetAsync(
            profile.RouteToolSetRef,
            toolContext,
            AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable,
            diagnostics,
            ct);
        if (routeTools.HadFailure)
        {
            return CreatePreparation(
                sessionId,
                candidate: null,
                selectedExactSkillRef: null,
                AgentProfileTurnAuthorityKind.RestrictedEmpty,
                [],
                diagnostics);
        }

        var availableTools = MergeExactTools(
            routeTools.Tools,
            registeredTools,
            toolContext,
            diagnostics,
            out var hadMergeFailure);
        var available = new HashSet<string>(availableTools.Keys, StringComparer.OrdinalIgnoreCase);
        available.RemoveWhere(name => !toolContext.ToolVisibility.Allows(name));

        var maximum = await ResolvePolicyAsync(profile.MaximumToolPolicy, toolContext, diagnostics, ct);
        available.IntersectWith(maximum.Names);
        var recovery = await ResolvePolicyAsync(profile.RecoveryToolPolicy, toolContext, diagnostics, ct);
        var recoveryNames = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        recoveryNames.IntersectWith(recovery.Names);
        if (routeTools.HadFailure || hadMergeFailure ||
            maximum.HadFailure || recovery.HadFailure)
        {
            return CreatePreparation(
                sessionId,
                candidate: null,
                selectedExactSkillRef: null,
                recoveryNames.Count == 0
                    ? AgentProfileTurnAuthorityKind.RestrictedEmpty
                    : AgentProfileTurnAuthorityKind.Recovery,
                recoveryNames,
                diagnostics);
        }

        var candidate = await SelectCandidateAsync(profile, userMessage, diagnostics, ct);
        if (candidate is null)
        {
            return CreatePreparation(
                sessionId,
                candidate: null,
                selectedExactSkillRef: null,
                AgentProfileTurnAuthorityKind.Recovery,
                recoveryNames,
                diagnostics);
        }

        var candidateIdentity = new AgentProfileTurnCandidateRouteIdentity
        {
            ProfileId = profile.ProfileId,
            ProfileVersion = profile.ProfileVersion,
            PolicyRevision = profile.PolicyRevision,
            IntentId = candidate.IntentId,
        };
        if (profile.ActivationMode != AgentProfileActivationMode.Enforced)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ShadowCandidate,
                candidate.IntentId));
            return CreatePreparation(
                sessionId,
                candidateIdentity,
                selectedExactSkillRef: null,
                AgentProfileTurnAuthorityKind.Recovery,
                recoveryNames,
                diagnostics);
        }

        var taskPolicy = await ResolvePolicyAsync(candidate.TaskToolPolicy, toolContext, diagnostics, ct);
        if (taskPolicy.HadFailure)
        {
            return CreatePreparation(
                sessionId,
                candidateIdentity,
                candidate.SkillRef,
                AgentProfileTurnAuthorityKind.Recovery,
                recoveryNames,
                diagnostics);
        }

        var selectedPolicy = new HashSet<string>(recovery.Names, StringComparer.OrdinalIgnoreCase);
        selectedPolicy.UnionWith(taskPolicy.Names);
        var ceiling = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        ceiling.IntersectWith(selectedPolicy);
        return CreatePreparation(
            sessionId,
            candidateIdentity,
            candidate.SkillRef,
            AgentProfileTurnAuthorityKind.Selected,
            ceiling,
            diagnostics);
    }

    public async Task<AgentProfileTurnCatalogMaterialization> MaterializeCommittedAsync(
        AgentProfileSnapshot profile,
        AgentProfileTurnAuthorityState committedAuthority,
        string? accessToken,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(committedAuthority);
        ArgumentNullException.ThrowIfNull(registeredTools);
        ArgumentNullException.ThrowIfNull(toolContext);

        var diagnostics = DiagnosticsFromAuthority(committedAuthority);
        if (!AgentProfileSnapshotCodec.Verify(profile) ||
            !MatchesCommittedProfile(profile, committedAuthority.CandidateRoute))
        {
            if (diagnostics.All(static diagnostic =>
                    diagnostic.Code != AgentProfileTurnDiagnosticCode.ProfileInvalid))
            {
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.ProfileInvalid,
                    "committed_profile_mismatch"));
            }
            return BuildMaterialization(
                profile,
                committedAuthority,
                AgentProfileTurnAuthorityKind.RestrictedEmpty,
                [],
                selectedIntentId: null,
                selectedSkillPromptLayer: null,
                diagnostics,
                routeOwnedTools: []);
        }

        var routeTools = await DiscoverToolSetAsync(
            profile.RouteToolSetRef,
            toolContext,
            AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable,
            diagnostics,
            ct);
        if (routeTools.HadFailure)
        {
            return BuildMaterialization(
                profile,
                committedAuthority,
                AgentProfileTurnAuthorityKind.RestrictedEmpty,
                [],
                selectedIntentId: null,
                selectedSkillPromptLayer: null,
                diagnostics,
                routeOwnedTools: []);
        }

        var availableTools = MergeExactTools(
            routeTools.Tools,
            registeredTools,
            toolContext,
            diagnostics,
            out var hadMergeFailure);
        var eligible = new HashSet<string>(availableTools.Keys, StringComparer.OrdinalIgnoreCase);
        eligible.RemoveWhere(name => !toolContext.ToolVisibility.Allows(name));
        var maximum = await ResolvePolicyAsync(profile.MaximumToolPolicy, toolContext, diagnostics, ct);
        eligible.IntersectWith(maximum.Names);
        eligible.IntersectWith(committedAuthority.AuthorityCeilingToolNames);
        var recovery = await ResolvePolicyAsync(profile.RecoveryToolPolicy, toolContext, diagnostics, ct);
        var recoveryNames = new HashSet<string>(eligible, StringComparer.OrdinalIgnoreCase);
        recoveryNames.IntersectWith(recovery.Names);

        if (routeTools.HadFailure || hadMergeFailure ||
            maximum.HadFailure || recovery.HadFailure)
        {
            return BuildMaterialization(
                profile,
                committedAuthority,
                NarrowAuthority(
                    committedAuthority.AuthorityKind,
                    recoveryNames.Count == 0
                        ? AgentProfileTurnAuthorityKind.RestrictedEmpty
                        : AgentProfileTurnAuthorityKind.Recovery),
                recoveryNames,
                selectedIntentId: null,
                selectedSkillPromptLayer: null,
                diagnostics,
                SelectTools(routeTools.Tools, recoveryNames));
        }

        if (committedAuthority.SelectedExactSkillRef is null)
        {
            return BuildMaterialization(
                profile,
                committedAuthority,
                committedAuthority.AuthorityKind,
                eligible,
                selectedIntentId: null,
                selectedSkillPromptLayer: null,
                diagnostics,
                SelectTools(routeTools.Tools, eligible));
        }

        var candidate = ResolveCommittedCandidate(profile, committedAuthority);
        if (candidate is null)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch,
                "committed_candidate_mismatch"));
            return BuildMaterialization(
                profile,
                committedAuthority,
                AgentProfileTurnAuthorityKind.RestrictedEmpty,
                [],
                selectedIntentId: null,
                selectedSkillPromptLayer: null,
                diagnostics,
                routeOwnedTools: []);
        }

        var fetched = await FetchSelectedSkillAsync(profile, candidate, accessToken, diagnostics, ct);
        if (fetched is null)
        {
            return BuildMaterialization(
                profile,
                committedAuthority,
                NarrowAuthority(committedAuthority.AuthorityKind, AgentProfileTurnAuthorityKind.Recovery),
                recoveryNames,
                selectedIntentId: null,
                selectedSkillPromptLayer: null,
                diagnostics,
                SelectTools(routeTools.Tools, recoveryNames));
        }

        var selectedLayer = new SelectedSkillPromptLayer(
            fetched,
            new SelectedSkillPromptProvenance(
                $"ornn:{committedAuthority.SelectedExactSkillRef.Guid}@{committedAuthority.SelectedExactSkillRef.LiteralVersion}"),
            new PromptLayerBounds(profile.MaxSelectedSkillBytes, Math.Max(1, profile.MaxSelectedSkillBytes / 4)));
        return BuildMaterialization(
            profile,
            committedAuthority,
            committedAuthority.AuthorityKind,
            eligible,
            candidate.IntentId,
            selectedLayer,
            diagnostics,
            SelectTools(routeTools.Tools, eligible));
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
                            member.RoutingDescription,
                            member.SideEffectClass))
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
            fetchResult.SkillSha256 is null ||
            fetchResult.SkillSha256.Length != 32 ||
            candidate.SealedSkillSha256.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(
                fetchResult.SkillSha256.Span,
                candidate.SealedSkillSha256.Span))
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

    private async Task<ToolDiscoveryResolution> DiscoverToolSetAsync(
        string? toolSetName,
        AgentToolExecutionContext toolContext,
        AgentProfileTurnDiagnosticCode unavailableCode,
        List<AgentProfileTurnDiagnostic> diagnostics,
        CancellationToken ct)
    {
        ToolSetResolveResult resolved;
        try
        {
            resolved = _toolSetRegistry.Resolve(toolSetName);
        }
        catch (Exception)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(unavailableCode, toolSetName ?? string.Empty));
            return new ToolDiscoveryResolution(
                new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase),
                true);
        }

        if (!resolved.IsSuccess)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                unavailableCode,
                resolved.Error?.Code ?? "resolve_failed"));
            return new ToolDiscoveryResolution(
                new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase),
                true);
        }

        var discovered = new List<IAgentTool>();
        using var toolContextScope = AgentToolContextScope.Push(toolContext);
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
                return new ToolDiscoveryResolution(
                    new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase),
                    true);
            }
        }

        return ToEligibleTools(discovered, toolContext, diagnostics);
    }

    private static ToolDiscoveryResolution ToEligibleTools(
        IReadOnlyList<IAgentTool> tools,
        AgentToolExecutionContext toolContext,
        List<AgentProfileTurnDiagnostic> diagnostics)
    {
        var eligibleTools = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        var hadFailure = false;
        foreach (var group in tools
                     .Where(static tool => !string.IsNullOrWhiteSpace(tool.Name))
                     .GroupBy(static tool => tool.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var tool = group.First();
            if (group.Any(candidate => !ReferenceEquals(candidate, tool)))
            {
                hadFailure = true;
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.ToolNameCollision,
                    group.Key));
                continue;
            }

            if (DeclaresCapability(tool, AgentToolCapabilities.ExcludeFromNyxIdChat))
                continue;

            if (!IsEligible(tool, toolContext))
            {
                hadFailure = true;
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.ToolCapabilityRejected,
                    group.Key));
                continue;
            }

            eligibleTools.Add(group.Key, tool);
        }

        return new ToolDiscoveryResolution(eligibleTools, hadFailure);
    }

    private static IReadOnlyDictionary<string, IAgentTool> MergeExactTools(
        IReadOnlyDictionary<string, IAgentTool> routeTools,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        List<AgentProfileTurnDiagnostic> diagnostics,
        out bool hadFailure)
    {
        var available = new Dictionary<string, IAgentTool>(routeTools, StringComparer.OrdinalIgnoreCase);
        hadFailure = false;
        foreach (var group in registeredTools
                     .Where(tool =>
                         !string.IsNullOrWhiteSpace(tool.Name) && routeTools.ContainsKey(tool.Name.Trim()))
                     .GroupBy(static tool => tool.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var registeredTool = group.First();
            var name = group.Key;
            if (group.Any(candidate => !ReferenceEquals(candidate, registeredTool)))
            {
                hadFailure = true;
                available.Remove(name);
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.ToolNameCollision,
                    name));
                continue;
            }

            if (!IsEligible(registeredTool, toolContext))
            {
                hadFailure = true;
                available.Remove(name);
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.ToolCapabilityRejected,
                    name));
                continue;
            }

            if (!ReferenceEquals(routeTools[name], registeredTool))
            {
                hadFailure = true;
                available.Remove(name);
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.ToolNameCollision,
                    name));
            }
        }

        return available;
    }

    private static IEnumerable<IAgentTool> SelectTools(
        IReadOnlyDictionary<string, IAgentTool> tools,
        IReadOnlySet<string> selectedNames) =>
        tools.Where(pair => selectedNames.Contains(pair.Key)).Select(static pair => pair.Value);

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

    private static bool DeclaresCapability(IAgentTool tool, string capability) =>
        tool is IAgentToolCapabilityDescriptor descriptor &&
        descriptor.Capabilities.Contains(capability, StringComparer.Ordinal);

    private static AgentProfileTurnAuthorityPreparation CreatePreparation(
        string sessionId,
        AgentProfileTurnCandidateRouteIdentity? candidate,
        ExactRemoteSkillRef? selectedExactSkillRef,
        AgentProfileTurnAuthorityKind authorityKind,
        IEnumerable<string> ceilingToolNames,
        IReadOnlyList<AgentProfileTurnDiagnostic> diagnostics)
    {
        var canonicalCeilingToolNames = CanonicalToolNames(ceilingToolNames);
        var authority = new AgentProfileTurnAuthorityState
        {
            ReconciliationKey = new AgentProfileTurnReconciliationKey
            {
                SessionId = sessionId,
                Attempt = 1,
            },
            CandidateRoute = candidate?.Clone(),
            SelectedExactSkillRef = selectedExactSkillRef?.Clone(),
            AuthorityKind = ResolveAuthorityKindForCeiling(authorityKind, canonicalCeilingToolNames),
        };
        authority.AuthorityCeilingToolNames.Add(canonicalCeilingToolNames);
        authority.DegradationReasons.Add(
            diagnostics
                .Select(static diagnostic => ToDegradationReason(diagnostic))
                .Where(static reason => reason != AgentProfileTurnDegradationReason.Unspecified)
                .Distinct()
                .OrderBy(static reason => (int)reason));
        return AgentProfileTurnAuthorityPreparation.Create(authority, diagnostics);
    }

    private static AgentProfileTurnCatalogMaterialization BuildMaterialization(
        AgentProfileSnapshot profile,
        AgentProfileTurnAuthorityState committedAuthority,
        AgentProfileTurnAuthorityKind authorityKind,
        IEnumerable<string> ceilingToolNames,
        string? selectedIntentId,
        SelectedSkillPromptLayer? selectedSkillPromptLayer,
        IReadOnlyList<AgentProfileTurnDiagnostic> diagnostics,
        IEnumerable<IAgentTool> routeOwnedTools)
    {
        var canonicalCeilingToolNames = CanonicalToolNames(ceilingToolNames);
        var proposal = committedAuthority.Clone();
        proposal.AuthorityKind = ResolveAuthorityKindForCeiling(authorityKind, canonicalCeilingToolNames);
        proposal.AuthorityCeilingToolNames.Clear();
        proposal.AuthorityCeilingToolNames.Add(canonicalCeilingToolNames);
        proposal.DegradationReasons.Clear();
        proposal.DegradationReasons.Add(
            committedAuthority.DegradationReasons
                .Concat(diagnostics.Select(static diagnostic => ToDegradationReason(diagnostic)))
                .Where(static reason => reason != AgentProfileTurnDegradationReason.Unspecified)
                .Distinct()
                .OrderBy(static reason => (int)reason));
        var catalog = BuildCatalog(
            profile,
            proposal.AuthorityCeilingToolNames,
            selectedIntentId,
            committedAuthority.CandidateRoute?.IntentId,
            selectedSkillPromptLayer,
            diagnostics,
            routeOwnedTools);
        return AgentProfileTurnCatalogMaterialization.Create(catalog, proposal);
    }

    private static IReadOnlyList<string> CanonicalToolNames(IEnumerable<string> names) =>
        names
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

    private static AgentProfileTurnAuthorityKind ResolveAuthorityKindForCeiling(
        AgentProfileTurnAuthorityKind authorityKind,
        IReadOnlyCollection<string> canonicalCeilingToolNames) =>
        authorityKind == AgentProfileTurnAuthorityKind.Recovery && canonicalCeilingToolNames.Count == 0
            ? AgentProfileTurnAuthorityKind.RestrictedEmpty
            : authorityKind;

    private static bool MatchesCommittedProfile(
        AgentProfileSnapshot profile,
        AgentProfileTurnCandidateRouteIdentity? candidate) =>
        candidate is null ||
        string.Equals(profile.ProfileId, candidate.ProfileId, StringComparison.Ordinal) &&
        string.Equals(profile.ProfileVersion, candidate.ProfileVersion, StringComparison.Ordinal) &&
        string.Equals(profile.PolicyRevision, candidate.PolicyRevision, StringComparison.Ordinal);

    private static AgentProfileSkillMember? ResolveCommittedCandidate(
        AgentProfileSnapshot profile,
        AgentProfileTurnAuthorityState committedAuthority)
    {
        var candidate = committedAuthority.CandidateRoute;
        var exactRef = committedAuthority.SelectedExactSkillRef;
        if (candidate is null || exactRef is null)
            return null;

        return profile.Members.SingleOrDefault(member =>
            string.Equals(member.IntentId, candidate.IntentId, StringComparison.Ordinal) &&
            member.SkillRef is not null &&
            string.Equals(member.SkillRef.Guid, exactRef.Guid, StringComparison.Ordinal) &&
            string.Equals(member.SkillRef.LiteralVersion, exactRef.LiteralVersion, StringComparison.Ordinal));
    }

    private static AgentProfileTurnAuthorityKind NarrowAuthority(
        AgentProfileTurnAuthorityKind current,
        AgentProfileTurnAuthorityKind proposed)
    {
        if (current == AgentProfileTurnAuthorityKind.RestrictedEmpty ||
            proposed == AgentProfileTurnAuthorityKind.RestrictedEmpty)
        {
            return AgentProfileTurnAuthorityKind.RestrictedEmpty;
        }

        if (current == AgentProfileTurnAuthorityKind.Recovery ||
            proposed == AgentProfileTurnAuthorityKind.Recovery)
        {
            return AgentProfileTurnAuthorityKind.Recovery;
        }

        return AgentProfileTurnAuthorityKind.Selected;
    }

    private static List<AgentProfileTurnDiagnostic> DiagnosticsFromAuthority(
        AgentProfileTurnAuthorityState authority) =>
        authority.DegradationReasons
            .Where(static reason => reason != AgentProfileTurnDegradationReason.Unspecified)
            .Distinct()
            .OrderBy(static reason => (int)reason)
            .Select(static reason => ToDiagnostic(reason))
            .OfType<AgentProfileTurnDiagnostic>()
            .ToList();

    private static AgentProfileTurnDegradationReason ToDegradationReason(
        AgentProfileTurnDiagnostic diagnostic) => (diagnostic.Code, diagnostic.Detail) switch
    {
        (AgentProfileTurnDiagnosticCode.ProfileInvalid, _) => AgentProfileTurnDegradationReason.ProfileInvalid,
        (AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable, _) =>
            AgentProfileTurnDegradationReason.RouteToolSetUnavailable,
        (AgentProfileTurnDiagnosticCode.ToolSetUnavailable, _) =>
            AgentProfileTurnDegradationReason.ToolSetUnavailable,
        (AgentProfileTurnDiagnosticCode.ToolDiscoveryFailed, _) =>
            AgentProfileTurnDegradationReason.ToolDiscoveryFailed,
        (AgentProfileTurnDiagnosticCode.ToolNameCollision, _) =>
            AgentProfileTurnDegradationReason.ToolNameCollision,
        (AgentProfileTurnDiagnosticCode.ToolCapabilityRejected, _) =>
            AgentProfileTurnDegradationReason.ToolCapabilityRejected,
        (AgentProfileTurnDiagnosticCode.ClassifierNoMatch, _) =>
            AgentProfileTurnDegradationReason.ClassifierNoMatch,
        (AgentProfileTurnDiagnosticCode.ClassifierFailed, _) =>
            AgentProfileTurnDegradationReason.ClassifierFailed,
        (AgentProfileTurnDiagnosticCode.ShadowCandidate, _) => AgentProfileTurnDegradationReason.ShadowMode,
        (AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed, _) =>
            AgentProfileTurnDegradationReason.ExactSkillFetchFailed,
        (AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch, _) =>
            AgentProfileTurnDegradationReason.ExactSkillIdentityMismatch,
        (AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid, _) =>
            AgentProfileTurnDegradationReason.SelectedSkillBodyInvalid,
        _ => AgentProfileTurnDegradationReason.Unspecified,
    };

    private static AgentProfileTurnDiagnostic? ToDiagnostic(
        AgentProfileTurnDegradationReason reason) => reason switch
    {
        AgentProfileTurnDegradationReason.ProfileInvalid => new AgentProfileTurnDiagnostic(
            AgentProfileTurnDiagnosticCode.ProfileInvalid,
            reason.ToString()),
        AgentProfileTurnDegradationReason.RouteToolSetUnavailable =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable, reason.ToString()),
        AgentProfileTurnDegradationReason.ToolSetUnavailable =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.ToolSetUnavailable, reason.ToString()),
        AgentProfileTurnDegradationReason.ToolDiscoveryFailed =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.ToolDiscoveryFailed, reason.ToString()),
        AgentProfileTurnDegradationReason.ToolNameCollision =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.ToolNameCollision, reason.ToString()),
        AgentProfileTurnDegradationReason.ToolCapabilityRejected =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.ToolCapabilityRejected, reason.ToString()),
        AgentProfileTurnDegradationReason.ClassifierNoMatch =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.ClassifierNoMatch, reason.ToString()),
        AgentProfileTurnDegradationReason.ClassifierFailed =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.ClassifierFailed, reason.ToString()),
        AgentProfileTurnDegradationReason.ShadowMode =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.ShadowCandidate, reason.ToString()),
        AgentProfileTurnDegradationReason.ExactSkillFetchFailed =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed, reason.ToString()),
        AgentProfileTurnDegradationReason.ExactSkillIdentityMismatch =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch, reason.ToString()),
        AgentProfileTurnDegradationReason.SelectedSkillBodyInvalid =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid, reason.ToString()),
        AgentProfileTurnDegradationReason.Unspecified or
            AgentProfileTurnDegradationReason.LegacyAuthorityMissing or
            AgentProfileTurnDegradationReason.MaterializerUnavailable or
            AgentProfileTurnDegradationReason.MaterializationFailed => null,
        _ => null,
    };

    private static AgentProfileTurnCatalog BuildCatalog(
        AgentProfileSnapshot profile,
        IEnumerable<string> finalNames,
        string? selectedIntentId,
        string? candidateIntentId,
        SelectedSkillPromptLayer? selectedSkillPromptLayer,
        IReadOnlyList<AgentProfileTurnDiagnostic> diagnostics,
        IEnumerable<IAgentTool> routeOwnedTools)
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
            diagnostics,
            routeOwnedTools);
    }

    private static bool MatchesAlias(string? userMessage, string? alias)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(alias))
            return false;

        var message = userMessage.Trim();
        var normalizedAlias = alias.Trim();
        return string.Equals(message, normalizedAlias, StringComparison.OrdinalIgnoreCase) ||
               (message.StartsWith(normalizedAlias, StringComparison.OrdinalIgnoreCase) &&
                message.Length > normalizedAlias.Length &&
                char.IsWhiteSpace(message[normalizedAlias.Length]));
    }

    private sealed record ToolPolicyResolution(IReadOnlySet<string> Names, bool HadFailure);

    private sealed record ToolDiscoveryResolution(IReadOnlyDictionary<string, IAgentTool> Tools, bool HadFailure)
    {
        public IReadOnlySet<string> Names { get; } = Tools.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
