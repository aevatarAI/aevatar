using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

public sealed class AgentTurnToolCatalogMaterializer : IAgentProfileTurnToolCatalogPlanner
{
    private const string NyxIdRequireServiceToolName = "nyxid_require_service";
    private const int DefaultConnectedOperationSelectorTimeoutMs = 15_000;
    internal const string ProfileTaskRouteIntentId = "nyxid_profile_task_route";
    internal const string ProfileTaskRouteRoutingDescription =
        "Perform an ordinary NyxID Assistant task, including invoking, reading from, or " +
        "writing through an already-connected exact UserService. This route does not " +
        "establish, add, reauthorize, or repair a missing service connection.";
    internal static AgentProfileTurnClassificationCandidate ProfileTaskRouteCandidate { get; } =
        new(
            ProfileTaskRouteIntentId,
            ProfileTaskRouteRoutingDescription,
            AgentProfileSideEffectClass.ExternalHandoff);
    internal const string UnprofiledBaselineIntentId = "nyxid_chat_unprofiled_baseline";
    internal const string RouteToolChoiceHintIntentId = "nyxid_chat_route_tool_choice_hint";

    // The reviewed baseline surface for ordinary, unprofiled NyxID chat turns:
    // the Class-R management reads (#3298), the service readiness gate, typed
    // user input, and explicit skill discovery/loading. Request-local
    // connected operations stay behind the readiness gate's verified
    // authorization continuation and never enter the unprofiled baseline.
    private static readonly IReadOnlySet<string> UnprofiledBaselineToolNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nyxid_services",
            "nyxid_api_keys",
            "nyxid_nodes",
            "nyxid_account",
            "nyxid_status",
            "nyxid_catalog",
            "nyxid_require_service",
            "ask_user",
            "use_skill",
            "ornn_search_skills",
        };

    private static readonly IReadOnlySet<string> ServiceConnectToolNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nyxid_catalog",
            "nyxid_require_service",
        };
    private static readonly IReadOnlySet<string> KeyCreateToolNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nyxid_services",
            "nyxid_request_key_create",
        };
    private static readonly IReadOnlySet<string> KeyRotateToolNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nyxid_api_keys",
            "nyxid_request_key_rotate",
        };
    private static readonly IReadOnlySet<string> WorkflowAuthoringToolNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "list_external_workflow_capabilities",
            "inspect_external_workflow_capability_readiness",
            "preview_workflow_explicit_requests",
        };

    private readonly IToolSetRegistry _toolSetRegistry;
    private readonly IAgentProfileTurnClassifier _classifier;
    private readonly IExactRemoteSkillFetcher? _exactRemoteSkillFetcher;
    private readonly SkillFrontmatterParser _frontmatterParser;
    private readonly IAgentToolDiscoveryService _toolDiscoveryService;
    private readonly IAgentProfileConnectedOperationSelector? _connectedOperationSelector;
    private readonly ILogger<AgentTurnToolCatalogMaterializer> _logger;
    private readonly TimeProvider _timeProvider;

    public AgentTurnToolCatalogMaterializer(
        IToolSetRegistry toolSetRegistry,
        IAgentProfileTurnClassifier classifier,
        IExactRemoteSkillFetcher? exactRemoteSkillFetcher = null,
        SkillFrontmatterParser? frontmatterParser = null,
        ILogger<AgentTurnToolCatalogMaterializer>? logger = null,
        TimeProvider? timeProvider = null,
        IAgentToolDiscoveryService? toolDiscoveryService = null,
        IAgentProfileConnectedOperationSelector? connectedOperationSelector = null)
    {
        _toolSetRegistry = toolSetRegistry ?? throw new ArgumentNullException(nameof(toolSetRegistry));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _exactRemoteSkillFetcher = exactRemoteSkillFetcher;
        _frontmatterParser = frontmatterParser ?? new SkillFrontmatterParser();
        _toolDiscoveryService = toolDiscoveryService ?? AgentToolDiscoveryService.Instance;
        _connectedOperationSelector = connectedOperationSelector;
        _logger = logger ?? NullLogger<AgentTurnToolCatalogMaterializer>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<AgentProfileTurnAuthorityPreparation> PrepareAsync(
        AgentProfileSnapshot profile,
        string sessionId,
        string userMessage,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default) =>
        PrepareCoreAsync(
            profile,
            sessionId,
            userMessage,
            registeredTools,
            toolContext,
            includeBuiltInNyxIdIntents: false,
            llmControl: null,
            ct);

    internal Task<AgentProfileTurnAuthorityPreparation> PrepareNyxIdChatAsync(
        AgentProfileSnapshot profile,
        string sessionId,
        string userMessage,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        LLMControlContext? llmControl,
        CancellationToken ct = default) =>
        PrepareCoreAsync(
            profile,
            sessionId,
            userMessage,
            registeredTools,
            toolContext,
            includeBuiltInNyxIdIntents: true,
            llmControl,
            ct);

    private async Task<AgentProfileTurnAuthorityPreparation> PrepareCoreAsync(
        AgentProfileSnapshot profile,
        string sessionId,
        string userMessage,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        bool includeBuiltInNyxIdIntents,
        LLMControlContext? llmControl,
        CancellationToken ct)
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

        var maximum = await ResolvePolicyAsync(
            profile.MaximumToolPolicy,
            availableTools,
            toolContext,
            diagnostics,
            ct);
        ApplyMaximumPolicy(available, maximum.Names, availableTools, diagnostics);
        var recovery = await ResolvePolicyAsync(
            profile.RecoveryToolPolicy,
            availableTools,
            toolContext,
            diagnostics,
            ct,
            enforceConnectedOperationLimits: true,
            eligibleToolNames: available);
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

        var candidate = await SelectCandidateAsync(
            profile,
            userMessage,
            diagnostics,
            includeBuiltInNyxIdIntents,
            llmControl,
            ct);
        if (candidate is null)
        {
            if (diagnostics.Any(static diagnostic =>
                    diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierNoMatch))
            {
                // A NyxID chat message that matches no exact profile member is
                // still an ordinary assistant turn (#3532): it keeps the reviewed
                // ordinary baseline attenuated by the profile's eligible surface
                // (route set, visibility, maximum policy) instead of failing
                // closed to zero tools. Non-chat surfaces keep the strict empty
                // no-match contract.
                var noMatchNames = includeBuiltInNyxIdIntents
                    ? OrdinaryDegradedNames(available, recoveryNames)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return CreatePreparation(
                    sessionId,
                    candidate: null,
                    selectedExactSkillRef: null,
                    noMatchNames.Count == 0
                        ? AgentProfileTurnAuthorityKind.RestrictedEmpty
                        : AgentProfileTurnAuthorityKind.Recovery,
                    noMatchNames,
                    diagnostics);
            }

            if (diagnostics.Any(static diagnostic =>
                    diagnostic.Code == AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation))
            {
                var clarificationNames = available.Contains("ask_user")
                    ? new[] { "ask_user" }
                    : Array.Empty<string>();
                return CreatePreparation(
                    sessionId,
                    candidate: null,
                    selectedExactSkillRef: null,
                    clarificationNames.Length == 0
                        ? AgentProfileTurnAuthorityKind.RestrictedEmpty
                        : AgentProfileTurnAuthorityKind.Recovery,
                    clarificationNames,
                    diagnostics);
            }

            return CreatePreparation(
                sessionId,
                candidate: null,
                selectedExactSkillRef: null,
                AgentProfileTurnAuthorityKind.Recovery,
                includeBuiltInNyxIdIntents
                    ? OrdinaryDegradedNames(available, recoveryNames)
                    : recoveryNames,
                diagnostics);
        }

        var candidateIdentity = new AgentProfileTurnCandidateRouteIdentity
        {
            ProfileId = profile.ProfileId,
            ProfileVersion = profile.ProfileVersion,
            PolicyRevision = profile.PolicyRevision,
            IntentId = candidate.IntentId,
        };
        var taskPolicy = await ResolvePolicyAsync(
            candidate.TaskToolPolicy,
            availableTools,
            toolContext,
            diagnostics,
            ct,
            enforceConnectedOperationLimits: true,
            eligibleToolNames: available,
            selectionContext: new ConnectedOperationSelectionContext(
                userMessage ?? string.Empty,
                profile.ClassifierTimeoutMs,
                llmControl,
                $"{sessionId}:connected-operation-selector"));
        if (diagnostics.Any(static diagnostic =>
                diagnostic.Code == AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation))
        {
            var clarificationNames = available.Contains("ask_user")
                ? new[] { "ask_user" }
                : Array.Empty<string>();
            return CreatePreparation(
                sessionId,
                candidateIdentity,
                selectedExactSkillRef: null,
                clarificationNames.Length == 0
                    ? AgentProfileTurnAuthorityKind.RestrictedEmpty
                    : AgentProfileTurnAuthorityKind.Recovery,
                clarificationNames,
                diagnostics);
        }
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
        if (includeBuiltInNyxIdIntents && ceiling.Count == 0)
        {
            // A selected NyxID chat member whose resolved task and recovery
            // policies admit no currently-available tool (for example a
            // catch-all member sealed with an empty task policy) must not zero
            // the turn: the reviewed ordinary baseline survives wherever the
            // profile's eligible surface admits it (#3532). The maximum policy
            // stays the hard ceiling and non-chat surfaces keep the sealed
            // selection verbatim.
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.SelectedPolicyEmpty,
                candidate.IntentId));
            ceiling.UnionWith(OrdinaryDegradedNames(available, recoveryNames));
        }

        if (profile.ActivationMode != AgentProfileActivationMode.Enforced)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ShadowCandidate,
                candidate.IntentId));
            AgentTurnToolCatalogProof? shadowCandidateProof = null;
            try
            {
                shadowCandidateProof = AgentTurnToolCatalogProof.CreateShadowCandidate(
                    SelectTools(availableTools, ceiling),
                    AgentTurnToolCatalogFactory.ResolveProfileBudget(profile));
                AgentTurnToolCatalogTelemetry.RecordShadowCandidate(
                    shadowCandidateProof,
                    $"{profile.ProfileId}@{profile.PublishedRevision}",
                    candidate.IntentId);
            }
            catch (AgentTurnToolCatalogException exception)
            {
                diagnostics.Add(ToCatalogDiagnostic(exception.Failure));
            }

            return CreatePreparation(
                sessionId,
                candidateIdentity,
                selectedExactSkillRef: null,
                AgentProfileTurnAuthorityKind.Recovery,
                recoveryNames,
                diagnostics,
                shadowCandidateProof);
        }

        return CreatePreparation(
            sessionId,
            candidateIdentity,
            candidate.SkillRef,
            AgentProfileTurnAuthorityKind.Selected,
            ceiling,
            diagnostics);
    }

    public async Task<AgentTurnToolCatalogMaterialization> MaterializeCommittedAsync(
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
                exactTools: []);
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
                exactTools: []);
        }

        var availableTools = MergeExactTools(
            routeTools.Tools,
            registeredTools,
            toolContext,
            diagnostics,
            out var hadMergeFailure);
        var eligible = new HashSet<string>(availableTools.Keys, StringComparer.OrdinalIgnoreCase);
        eligible.RemoveWhere(name => !toolContext.ToolVisibility.Allows(name));
        var maximum = await ResolvePolicyAsync(
            profile.MaximumToolPolicy,
            availableTools,
            toolContext,
            diagnostics,
            ct);
        ApplyMaximumPolicy(eligible, maximum.Names, availableTools, diagnostics);
        eligible.IntersectWith(committedAuthority.AuthorityCeilingToolNames);
        var recovery = await ResolvePolicyAsync(
            profile.RecoveryToolPolicy,
            availableTools,
            toolContext,
            diagnostics,
            ct,
            enforceConnectedOperationLimits: true,
            eligibleToolNames: eligible);
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

        var candidate = ResolveCommittedCandidate(profile, committedAuthority, diagnostics);
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
                exactTools: []);
        }

        var fetched = await FetchSelectedSkillAsync(profile, candidate, accessToken, diagnostics, ct);
        if (fetched is null)
        {
            var fallbackNames = committedAuthority.DegradationReasons.Contains(
                AgentProfileTurnDegradationReason.SelectedPolicyEmpty)
                ? OrdinaryDegradedNames(eligible, recoveryNames)
                : recoveryNames;
            return BuildMaterialization(
                profile,
                committedAuthority,
                NarrowAuthority(committedAuthority.AuthorityKind, AgentProfileTurnAuthorityKind.Recovery),
                fallbackNames,
                selectedIntentId: null,
                selectedSkillPromptLayer: null,
                diagnostics,
                SelectTools(routeTools.Tools, fallbackNames));
        }

        var selectedLayer = new SelectedSkillPromptLayer(
            fetched,
            new SelectedSkillPromptProvenance(
                $"ornn:{committedAuthority.SelectedExactSkillRef.Guid}@{committedAuthority.SelectedExactSkillRef.LiteralVersion}"),
            new PromptLayerBounds(profile.MaxSelectedSkillBytes, Math.Max(1, profile.MaxSelectedSkillBytes / 4)));
        var readiness = ResolveConnectedServiceReadinessRequirement(
            candidate.TaskToolPolicy,
            availableTools,
            eligible);
        return BuildMaterialization(
            profile,
            committedAuthority,
            committedAuthority.AuthorityKind,
            eligible,
            candidate.IntentId,
            selectedLayer,
            diagnostics,
            SelectTools(routeTools.Tools, eligible),
            readiness.HasUnresolvedSelectors,
            readiness.RequiredToolInvocation);
    }

    public async Task<AgentTurnToolCatalog> MaterializeBuiltInIntentAsync(
        NyxIdChatTurnIntent intent,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toolContext);
        var builtIn = ResolveBuiltInIntent(intent);
        if (builtIn is null)
            return AgentTurnToolCatalogFactory.RestrictedEmpty();

        var diagnostics = new List<AgentProfileTurnDiagnostic>();
        var routeTools = await DiscoverToolSetAsync(
            builtIn.ToolSetName,
            toolContext,
            AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable,
            diagnostics,
            ct);
        if (routeTools.HadFailure)
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);

        var selectedTools = routeTools.Tools
            .Where(pair => builtIn.ToolNames.Contains(pair.Key) &&
                           toolContext.ToolVisibility.Allows(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (selectedTools.Count != builtIn.ToolNames.Count ||
            !builtIn.ToolNames.All(selectedTools.ContainsKey) ||
            (builtIn.RequiresReadOnly && selectedTools.Values.Any(static tool => !tool.IsReadOnly)))
        {
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);
        }

        return new AgentTurnToolCatalog(
            builtIn.ToolNames,
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            builtIn.IntentId,
            builtIn.IntentId,
            diagnostics,
            selectedTools.Values,
            budget: AgentTurnToolCatalogBudget.ConnectedOperations);
    }

    /// <summary>
    /// Materializes the reviewed baseline catalog for an ordinary, unprofiled
    /// NyxID chat turn from the dedicated nyxid.chat.baseline set. The baseline
    /// is narrowed to the pinned reviewed tool names and the caller's tool
    /// visibility; tools that are absent, ineligible, or collided degrade
    /// individually instead of failing the whole surface closed.
    /// </summary>
    public async Task<AgentTurnToolCatalog> MaterializeUnprofiledBaselineAsync(
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toolContext);
        var diagnostics = new List<AgentProfileTurnDiagnostic>();
        var routeTools = await DiscoverToolSetAsync(
            ToolSetNames.NyxIdChatBaseline,
            toolContext,
            AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable,
            diagnostics,
            ct);

        // The baseline is availability-intersected: a tool that is ineligible,
        // collided, or unavailable degrades on its own (with a diagnostic) and
        // the remaining reviewed tools still ship. Only an empty intersection
        // fails closed.
        var selectedTools = routeTools.Tools
            .Where(pair => UnprofiledBaselineToolNames.Contains(pair.Key) &&
                           toolContext.ToolVisibility.Allows(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (selectedTools.Count == 0)
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);

        return new AgentTurnToolCatalog(
            selectedTools.Keys,
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            UnprofiledBaselineIntentId,
            UnprofiledBaselineIntentId,
            diagnostics,
            selectedTools.Values,
            budget: AgentTurnToolCatalogBudget.Ordinary);
    }

    public async Task<AgentTurnToolCatalog> MaterializeRouteToolChoiceHintAsync(
        string toolSetName,
        string toolName,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toolContext);
        var normalizedToolSetName = toolSetName.Trim();
        var normalizedToolName = toolName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedToolSetName) || string.IsNullOrWhiteSpace(normalizedToolName))
            return AgentTurnToolCatalogFactory.RestrictedEmpty();

        var diagnostics = new List<AgentProfileTurnDiagnostic>();
        var routeTools = await DiscoverToolSetAsync(
            normalizedToolSetName,
            toolContext,
            AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable,
            diagnostics,
            ct);
        if (routeTools.HadFailure ||
            !toolContext.ToolVisibility.Allows(normalizedToolName) ||
            !routeTools.Tools.TryGetValue(normalizedToolName, out var selectedTool))
        {
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);
        }

        return new AgentTurnToolCatalog(
            [normalizedToolName],
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            RouteToolChoiceHintIntentId,
            RouteToolChoiceHintIntentId,
            diagnostics,
            [selectedTool],
            budget: AgentTurnToolCatalogBudget.ConnectedOperations);
    }

    internal async Task<AgentTurnToolCatalog> MaterializeVerifiedAuthorizationContinuationAsync(
        AgentProfileSnapshot? profile,
        AgentProfileTurnAuthorityState? committedAuthority,
        NyxIdChatVerifiedAuthorizationContinuation verifiedAuthorization,
        string userMessage,
        LLMControlContext? llmControl,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verifiedAuthorization);
        ArgumentNullException.ThrowIfNull(toolContext);
        if ((profile is null) != (committedAuthority is null))
            return AgentTurnToolCatalogFactory.RestrictedEmpty();

        var verifiedUserServiceId = verifiedAuthorization.VerifiedResource?.ResourceCase ==
                                    NyxIdChatSafeResourceRef.ResourceOneofCase.UserService
            ? verifiedAuthorization.VerifiedResource.UserService.UserServiceId?.Trim()
            : null;
        var verifiedServiceSlug = verifiedAuthorization.ServiceSlug?.Trim();
        if (string.IsNullOrWhiteSpace(verifiedUserServiceId) ||
            string.IsNullOrWhiteSpace(verifiedServiceSlug))
        {
            return AgentTurnToolCatalogFactory.RestrictedEmpty();
        }

        var diagnostics = committedAuthority is null
            ? []
            : DiagnosticsFromAuthority(committedAuthority);
        var routeToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet;
        if (profile is not null)
        {
            if (!AgentProfileSnapshotCodec.Verify(profile) ||
                committedAuthority!.CandidateRoute is null ||
                !MatchesCommittedProfile(profile, committedAuthority.CandidateRoute))
            {
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.ProfileInvalid,
                    "committed_profile_mismatch"));
                return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);
            }

            routeToolSetRef = profile.RouteToolSetRef;
        }

        var routeTools = await DiscoverToolSetAsync(
            routeToolSetRef,
            toolContext,
            AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable,
            diagnostics,
            ct);
        if (routeTools.HadFailure)
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);

        var maximumEligible = new HashSet<string>(routeTools.Names, StringComparer.OrdinalIgnoreCase);
        maximumEligible.RemoveWhere(name => !toolContext.ToolVisibility.Allows(name));
        if (profile is not null)
        {
            var maximum = await ResolvePolicyAsync(
                profile.MaximumToolPolicy,
                routeTools.Tools,
                toolContext,
                diagnostics,
                ct);
            if (maximum.HadFailure)
                return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);

            ApplyMaximumPolicy(maximumEligible, maximum.Names, routeTools.Tools, diagnostics);
        }

        var eligible = maximumEligible
            .Where(name => routeTools.Tools.TryGetValue(name, out var tool) &&
                           MatchesVerifiedUserService(
                               tool,
                               verifiedUserServiceId,
                               verifiedServiceSlug))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectionContext = new ConnectedOperationSelectionContext(
            userMessage ?? string.Empty,
            profile?.ClassifierTimeoutMs ?? DefaultConnectedOperationSelectorTimeoutMs,
            llmControl,
            $"{verifiedAuthorization.OriginTurnId}:verified-authorization-connected-operation-selector");

        if (profile is not null)
        {
            var committedCandidate = ResolveCommittedCandidate(profile, committedAuthority!, diagnostics);
            if (committedAuthority!.SelectedExactSkillRef is not null && committedCandidate is null)
            {
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch,
                    "committed_candidate_mismatch"));
                return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);
            }

            if (committedCandidate is not null)
            {
                var taskPolicy = await ResolvePolicyAsync(
                    committedCandidate.TaskToolPolicy,
                    routeTools.Tools,
                    toolContext,
                    diagnostics,
                    ct,
                    enforceConnectedOperationLimits: true,
                    eligibleToolNames: eligible,
                    selectionContext: selectionContext);
                if (taskPolicy.HadFailure)
                    return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);

                if (diagnostics.Any(static diagnostic =>
                        diagnostic.Code == AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation))
                {
                    return CreateVerifiedAuthorizationClarificationCatalog(
                        profile,
                        committedAuthority,
                        maximumEligible,
                        routeTools.Tools,
                        diagnostics);
                }

                eligible.IntersectWith(taskPolicy.Names);
            }
            else if (!await BoundVerifiedAuthorizationOperationsAsync(
                         eligible,
                         routeTools.Tools,
                         selectionContext,
                         diagnostics,
                         ct))
            {
                return CreateVerifiedAuthorizationClarificationCatalog(
                    profile,
                    committedAuthority,
                    maximumEligible,
                    routeTools.Tools,
                    diagnostics);
            }
        }
        else if (!await BoundVerifiedAuthorizationOperationsAsync(
                     eligible,
                     routeTools.Tools,
                     selectionContext,
                     diagnostics,
                     ct))
        {
            return CreateVerifiedAuthorizationClarificationCatalog(
                profile: null,
                committedAuthority: null,
                maximumEligible,
                routeTools.Tools,
                diagnostics);
        }

        if (eligible.Count == 0)
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);

        var selectedTools = SelectTools(routeTools.Tools, eligible);
        try
        {
            return profile is null
                ? new AgentTurnToolCatalog(
                    eligible,
                    profilePromptLayer: null,
                    selectedSkillPromptLayer: null,
                    selectedIntentId: null,
                    candidateIntentId: null,
                    diagnostics,
                    selectedTools,
                    budget: AgentTurnToolCatalogBudget.ConnectedOperations)
                : AgentTurnToolCatalogFactory.CreateForProfile(
                    profile,
                    eligible,
                    selectedIntentId: null,
                    candidateIntentId: committedAuthority!.CandidateRoute?.IntentId,
                    selectedSkillPromptLayer: null,
                    diagnostics,
                    selectedTools);
        }
        catch (AgentTurnToolCatalogException exception)
        {
            diagnostics.Add(ToCatalogDiagnostic(exception.Failure));
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);
        }
    }

    private async Task<bool> BoundVerifiedAuthorizationOperationsAsync(
        HashSet<string> eligible,
        IReadOnlyDictionary<string, IAgentTool> availableTools,
        ConnectedOperationSelectionContext selectionContext,
        List<AgentProfileTurnDiagnostic> diagnostics,
        CancellationToken ct)
    {
        if (!ConnectedOperationLimitExceeded(eligible, availableTools))
            return true;

        var counts = CountConnectedOperations(eligible, availableTools);
        if (counts.WriteCount > 1)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "connected_service_write_ambiguous"));
            return false;
        }

        if (_connectedOperationSelector is null)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "connected_service_selector_unavailable"));
            return false;
        }

        var selected = await SelectConnectedOperationsAsync(
            eligible,
            availableTools,
            AgentTurnToolCatalogBudget.ConnectedOperations.MaximumConnectedReadToolCount,
            AgentTurnToolCatalogBudget.ConnectedOperations.MaximumConnectedWriteToolCount,
            selectionContext,
            diagnostics,
            ct);
        if (selected is null || selected.Any(name => !eligible.Contains(name)))
            return false;

        eligible.IntersectWith(selected);
        return eligible.Count > 0 &&
               !ConnectedOperationLimitExceeded(eligible, availableTools);
    }

    private static AgentTurnToolCatalog CreateVerifiedAuthorizationClarificationCatalog(
        AgentProfileSnapshot? profile,
        AgentProfileTurnAuthorityState? committedAuthority,
        IReadOnlySet<string> maximumEligible,
        IReadOnlyDictionary<string, IAgentTool> availableTools,
        List<AgentProfileTurnDiagnostic> diagnostics)
    {
        if (!maximumEligible.Contains("ask_user") ||
            !availableTools.TryGetValue("ask_user", out var askUserTool))
        {
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);
        }

        try
        {
            return profile is null
                ? new AgentTurnToolCatalog(
                    ["ask_user"],
                    profilePromptLayer: null,
                    selectedSkillPromptLayer: null,
                    selectedIntentId: null,
                    candidateIntentId: null,
                    diagnostics,
                    [askUserTool],
                    budget: AgentTurnToolCatalogBudget.ConnectedOperations)
                : AgentTurnToolCatalogFactory.CreateForProfile(
                    profile,
                    ["ask_user"],
                    selectedIntentId: null,
                    candidateIntentId: committedAuthority?.CandidateRoute?.IntentId,
                    selectedSkillPromptLayer: null,
                    diagnostics,
                    [askUserTool]);
        }
        catch (AgentTurnToolCatalogException exception)
        {
            diagnostics.Add(ToCatalogDiagnostic(exception.Failure));
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: diagnostics);
        }
    }

    private static bool MatchesVerifiedUserService(
        IAgentTool tool,
        string verifiedUserServiceId,
        string verifiedServiceSlug) =>
        tool is IAgentToolOperationAdmissionOwner owner &&
        string.Equals(
            owner.OperationAdmission.ServiceInstanceId,
            verifiedUserServiceId,
            StringComparison.Ordinal) &&
        string.Equals(
            owner.OperationAdmission.ServiceSlug,
            verifiedServiceSlug,
            StringComparison.Ordinal);

    internal static AgentTurnToolCatalog NarrowToBuiltInIntent(
        NyxIdChatTurnIntent intent,
        AgentTurnToolCatalog catalog,
        IEnumerable<string>? authorityCeilingToolNames = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var builtIn = ResolveBuiltInIntent(intent);
        if (builtIn is null)
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: catalog.Diagnostics);

        var allowed = new HashSet<string>(builtIn.ToolNames, StringComparer.OrdinalIgnoreCase);
        allowed.IntersectWith(catalog.FinalAllowedToolNames);
        if (authorityCeilingToolNames is not null)
            allowed.IntersectWith(authorityCeilingToolNames);
        var selectedTools = catalog.ExactTools
            .Where(pair => allowed.Contains(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (allowed.Count != builtIn.ToolNames.Count ||
            selectedTools.Count != builtIn.ToolNames.Count ||
            !builtIn.ToolNames.All(name =>
                allowed.Contains(name) && selectedTools.ContainsKey(name)) ||
            (builtIn.RequiresReadOnly && selectedTools.Values.Any(static tool => !tool.IsReadOnly)))
        {
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: catalog.Diagnostics);
        }

        return new AgentTurnToolCatalog(
            allowed,
            catalog.ProfilePromptLayer,
            catalog.SelectedSkillPromptLayer,
            builtIn.IntentId,
            catalog.CandidateIntentId,
            catalog.Diagnostics,
            selectedTools.Values,
            budget: catalog.Budget);
    }

    internal static AgentTurnToolCatalog NarrowToVerifiedUserService(
        AgentTurnToolCatalog catalog,
        NyxIdChatVerifiedAuthorizationContinuation continuation)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(continuation);

        if (catalog.FinalAllowedToolNames.Count == 1 &&
            catalog.FinalAllowedToolNames.Contains("ask_user") &&
            catalog.ExactTools.ContainsKey("ask_user") &&
            catalog.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation))
        {
            return catalog;
        }

        var userServiceId = continuation.VerifiedResource?.ResourceCase ==
                            NyxIdChatSafeResourceRef.ResourceOneofCase.UserService
            ? continuation.VerifiedResource.UserService.UserServiceId?.Trim()
            : null;
        var serviceSlug = continuation.ServiceSlug?.Trim();
        if (string.IsNullOrWhiteSpace(userServiceId) ||
            string.IsNullOrWhiteSpace(serviceSlug))
        {
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: catalog.Diagnostics);
        }

        var selectedTools = catalog.ExactTools
            .Where(pair =>
                catalog.FinalAllowedToolNames.Contains(pair.Key) &&
                pair.Value is IAgentToolOperationAdmissionOwner owner &&
                string.Equals(
                    owner.OperationAdmission.ServiceInstanceId,
                    userServiceId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    owner.OperationAdmission.ServiceSlug,
                    serviceSlug,
                    StringComparison.Ordinal))
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        if (selectedTools.Count == 0)
            return AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: catalog.Diagnostics);

        return new AgentTurnToolCatalog(
            selectedTools.Keys,
            catalog.ProfilePromptLayer,
            catalog.SelectedSkillPromptLayer,
            catalog.SelectedIntentId,
            catalog.CandidateIntentId,
            catalog.Diagnostics,
            selectedTools.Values,
            budget: catalog.Budget);
    }

    private async Task<AgentProfileSkillMember?> SelectCandidateAsync(
        AgentProfileSnapshot profile,
        string userMessage,
        List<AgentProfileTurnDiagnostic> diagnostics,
        bool includeBuiltInNyxIdIntents,
        LLMControlContext? llmControl,
        CancellationToken ct)
    {
        var duplicateIntentId = profile.Members
            .GroupBy(static member => member.IntentId, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (duplicateIntentId is not null)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "intent_id_collision"));
            return null;
        }

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
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "alias_collision"));
            return null;
        }

        if (includeBuiltInNyxIdIntents)
        {
            var builtInMembers = new[]
            {
                profile.Members.FirstOrDefault(member => string.Equals(
                    member.IntentId,
                    NyxIdChatTurnIntentClassifier.ServiceConnectIntentId,
                    StringComparison.Ordinal)) ?? CreateBuiltInServiceConnectMember(),
                profile.Members.FirstOrDefault(member => string.Equals(
                    member.IntentId,
                    NyxIdChatTurnIntentClassifier.KeyCreateIntentId,
                    StringComparison.Ordinal)) ?? CreateBuiltInKeyCreateMember(),
                profile.Members.FirstOrDefault(member => string.Equals(
                    member.IntentId,
                    NyxIdChatTurnIntentClassifier.KeyRotateIntentId,
                    StringComparison.Ordinal)) ?? CreateBuiltInKeyRotateMember(),
                profile.Members.FirstOrDefault(member => string.Equals(
                    member.IntentId,
                    NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId,
                    StringComparison.Ordinal)) ?? CreateBuiltInWorkflowAuthoringMember(),
            };
            var builtInResult = await ClassifyAsync(
                userMessage,
                [
                    NyxIdChatTurnIntentClassifier.ServiceConnectCandidate,
                    NyxIdChatTurnIntentClassifier.KeyCreateCandidate,
                    NyxIdChatTurnIntentClassifier.KeyRotateCandidate,
                    NyxIdChatTurnIntentClassifier.WorkflowAuthoringCandidate,
                    ProfileTaskRouteCandidate,
                ],
                profile.ClassifierTimeoutMs,
                llmControl,
                ct);
            var builtInMember = builtInMembers.SingleOrDefault(member => string.Equals(
                member.IntentId,
                builtInResult.IntentId,
                StringComparison.Ordinal));
            if (builtInResult.Status == AgentProfileTurnClassificationStatus.Matched &&
                builtInMember is not null)
            {
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.ClassifierMatched,
                    builtInMember.IntentId));
                return builtInMember;
            }
            if (builtInResult.Status != AgentProfileTurnClassificationStatus.NoMatch &&
                !(builtInResult.Status == AgentProfileTurnClassificationStatus.Matched &&
                  string.Equals(
                      builtInResult.IntentId,
                      ProfileTaskRouteIntentId,
                      StringComparison.Ordinal)))
            {
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    IsDisambiguationFailure(builtInResult.FailureCode)
                        ? AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation
                        : AgentProfileTurnDiagnosticCode.ClassifierFailed,
                    builtInResult.FailureCode ?? "nyxid_builtin_classifier_failed"));
                return null;
            }
        }

        var candidates = profile.Members
            .Take(StreamingAgentProfileTurnClassifier.MaximumCandidates)
            .ToArray();
        if (candidates.Length == 0 || profile.ClassifierTimeoutMs <= 0)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ClassifierFailed,
                "classifier_not_configured"));
            return null;
        }

        var result = await ClassifyAsync(
            userMessage,
            candidates
                .Select(static member => new AgentProfileTurnClassificationCandidate(
                    member.IntentId,
                    member.RoutingDescription,
                    member.SideEffectClass))
                .ToArray(),
            profile.ClassifierTimeoutMs,
            llmControl,
            ct);

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
                IsDisambiguationFailure(result.FailureCode)
                    ? AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation
                    : AgentProfileTurnDiagnosticCode.ClassifierFailed,
                result.FailureCode ?? "failed"));
            return null;
        }

        var member = candidates.SingleOrDefault(candidate =>
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

    private async Task<AgentProfileTurnClassificationResult> ClassifyAsync(
        string userMessage,
        IReadOnlyList<AgentProfileTurnClassificationCandidate> candidates,
        int timeoutMs,
        LLMControlContext? llmControl,
        CancellationToken ct)
    {
        if (timeoutMs <= 0)
            return AgentProfileTurnClassificationResult.Failed("classifier_not_configured");

        try
        {
            return await _classifier.ClassifyAsync(
                new AgentProfileTurnClassificationRequest(
                    userMessage ?? string.Empty,
                    candidates,
                    TimeSpan.FromMilliseconds(timeoutMs),
                    llmControl),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return AgentProfileTurnClassificationResult.Failed("classifier_exception");
        }
    }

    private static AgentProfileSkillMember CreateBuiltInServiceConnectMember() =>
        new()
        {
            IntentId = NyxIdChatTurnIntentClassifier.ServiceConnectIntentId,
            RoutingDescription = NyxIdChatTurnIntentClassifier.ServiceConnectRoutingDescription,
            TaskToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "nyxid_catalog", "nyxid_require_service" },
            },
            SideEffectClass = AgentProfileSideEffectClass.ExternalHandoff,
        };

    private static AgentProfileSkillMember CreateBuiltInKeyCreateMember() =>
        new()
        {
            IntentId = NyxIdChatTurnIntentClassifier.KeyCreateIntentId,
            RoutingDescription = NyxIdChatTurnIntentClassifier.KeyCreateRoutingDescription,
            TaskToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "nyxid_services", "nyxid_request_key_create" },
            },
            SideEffectClass = AgentProfileSideEffectClass.ExternalHandoff,
        };

    private static AgentProfileSkillMember CreateBuiltInKeyRotateMember() =>
        new()
        {
            IntentId = NyxIdChatTurnIntentClassifier.KeyRotateIntentId,
            RoutingDescription = NyxIdChatTurnIntentClassifier.KeyRotateRoutingDescription,
            TaskToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "nyxid_api_keys", "nyxid_request_key_rotate" },
            },
            SideEffectClass = AgentProfileSideEffectClass.ExternalHandoff,
        };

    private static AgentProfileSkillMember CreateBuiltInWorkflowAuthoringMember() =>
        new()
        {
            IntentId = NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId,
            RoutingDescription = NyxIdChatTurnIntentClassifier.WorkflowAuthoringRoutingDescription,
            TaskToolPolicy = new AgentProfileToolPolicy
            {
                ToolSetRefs = { ToolSetNames.WorkflowExternalCapabilityAuthoring },
            },
            SideEffectClass = AgentProfileSideEffectClass.ReadOnly,
        };

    private static BuiltInIntent? ResolveBuiltInIntent(NyxIdChatTurnIntent intent) => intent switch
    {
        NyxIdChatTurnIntent.ServiceConnect => new BuiltInIntent(
            NyxIdChatTurnIntentClassifier.ServiceConnectIntentId,
            ToolSetNames.NyxIdAssistantAdmission,
            ServiceConnectToolNames,
            RequiresReadOnly: false),
        NyxIdChatTurnIntent.KeyCreate => new BuiltInIntent(
            NyxIdChatTurnIntentClassifier.KeyCreateIntentId,
            ToolSetNames.NyxIdAssistantAdmission,
            KeyCreateToolNames,
            RequiresReadOnly: false),
        NyxIdChatTurnIntent.KeyRotate => new BuiltInIntent(
            NyxIdChatTurnIntentClassifier.KeyRotateIntentId,
            ToolSetNames.NyxIdAssistantAdmission,
            KeyRotateToolNames,
            RequiresReadOnly: false),
        NyxIdChatTurnIntent.WorkflowAuthoring => new BuiltInIntent(
            NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId,
            ToolSetNames.WorkflowExternalCapabilityAuthoring,
            WorkflowAuthoringToolNames,
            RequiresReadOnly: true),
        _ => null,
    };

    private sealed record BuiltInIntent(
        string IntentId,
        string ToolSetName,
        IReadOnlySet<string> ToolNames,
        bool RequiresReadOnly);

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
        IReadOnlyDictionary<string, IAgentTool> availableTools,
        AgentToolExecutionContext toolContext,
        List<AgentProfileTurnDiagnostic> diagnostics,
        CancellationToken ct,
        bool enforceConnectedOperationLimits = false,
        IReadOnlySet<string>? eligibleToolNames = null,
        ConnectedOperationSelectionContext? selectionContext = null)
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

        var selectorKeys = new HashSet<string>(StringComparer.Ordinal);
        var exactConnectedMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var broadConnectedMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connectionAmbiguous = false;
        foreach (var selector in policy.ConnectedServiceSelectors)
        {
            if (!IsValidConnectedServiceSelector(selector) ||
                !selectorKeys.Add(string.Concat(
                    selector.CatalogServiceSlug,
                    "\0",
                    selector.EndpointId)))
            {
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.ProfileInvalid,
                    "connected_service_selector_invalid"));
                hadFailure = true;
                continue;
            }

            var rawMatches = availableTools
                .Where(pair => MatchesConnectedServiceSelector(pair.Value, selector))
                .Select(static pair => pair.Key)
                .ToArray();
            var matches = rawMatches
                .Where(toolContext.ToolVisibility.Allows)
                .Where(name => eligibleToolNames is null || eligibleToolNames.Contains(name))
                .ToArray();
            if (matches.Length == 0)
            {
                if (rawMatches.Length == 0 &&
                    selector.Readiness is not null &&
                    toolContext.ToolVisibility.Allows(NyxIdRequireServiceToolName) &&
                    availableTools.TryGetValue(NyxIdRequireServiceToolName, out var readinessTool) &&
                    readinessTool is INyxIdBuiltInTool)
                {
                    names.Add(NyxIdRequireServiceToolName);
                }

                continue;
            }

            if (enforceConnectedOperationLimits &&
                matches
                    .Select(name => ((IAgentToolOperationAdmissionOwner)availableTools[name])
                        .OperationAdmission.ServiceInstanceId)
                    .Distinct(StringComparer.Ordinal)
                    .Skip(1)
                    .Any())
            {
                connectionAmbiguous = true;
                continue;
            }

            if (string.IsNullOrEmpty(selector.EndpointId))
                broadConnectedMatches.UnionWith(matches);
            else
                exactConnectedMatches.UnionWith(matches);
        }

        broadConnectedMatches.ExceptWith(exactConnectedMatches);
        var connectedMatches = new HashSet<string>(exactConnectedMatches, StringComparer.OrdinalIgnoreCase);
        connectedMatches.UnionWith(broadConnectedMatches);
        if (!enforceConnectedOperationLimits)
        {
            names.UnionWith(connectedMatches);
            return new ToolPolicyResolution(names, hadFailure);
        }

        if (connectionAmbiguous)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "connected_service_connection_ambiguous"));
            return new ToolPolicyResolution(names, hadFailure);
        }

        if (!ConnectedOperationLimitExceeded(connectedMatches, availableTools))
        {
            names.UnionWith(connectedMatches);
            return new ToolPolicyResolution(names, hadFailure);
        }

        var exactCounts = CountConnectedOperations(exactConnectedMatches, availableTools);
        var broadCounts = CountConnectedOperations(broadConnectedMatches, availableTools);
        if (exactCounts.WriteCount == 0 &&
            broadCounts.WriteCount > 1 &&
            broadCounts.ReadCount == 0)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "connected_service_write_ambiguous"));
            return new ToolPolicyResolution(names, hadFailure);
        }

        if (ConnectedOperationLimitExceeded(exactConnectedMatches, availableTools) ||
            broadConnectedMatches.Count == 0 ||
            selectionContext is null ||
            _connectedOperationSelector is null)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "connected_service_selector_unavailable"));
            return new ToolPolicyResolution(names, hadFailure);
        }

        var maximumReadSelections = Math.Max(
            0,
            AgentTurnToolCatalogBudget.ConnectedOperations.MaximumConnectedReadToolCount -
            exactCounts.ReadCount);
        var maximumWriteSelections = Math.Max(
            0,
            AgentTurnToolCatalogBudget.ConnectedOperations.MaximumConnectedWriteToolCount -
            exactCounts.WriteCount);
        var selectionNames = broadConnectedMatches
            .Where(name => availableTools[name] is IAgentToolOperationAdmissionOwner owner &&
                           (owner.OperationAdmission.ExecutionPolicy.Risk == AgentToolOperationRisk.ReadOnly &&
                            maximumReadSelections > 0 ||
                            owner.OperationAdmission.ExecutionPolicy.Risk == AgentToolOperationRisk.Write &&
                            maximumWriteSelections > 0))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedConnectedNames = await SelectConnectedOperationsAsync(
            selectionNames,
            availableTools,
            maximumReadSelections,
            maximumWriteSelections,
            selectionContext,
            diagnostics,
            ct);
        if (selectedConnectedNames is null)
            return new ToolPolicyResolution(names, hadFailure);

        var revalidatedSelectedNames = selectedConnectedNames
            .Where(name => broadConnectedMatches.Contains(name) &&
                           availableTools.ContainsKey(name) &&
                           toolContext.ToolVisibility.Allows(name) &&
                           (eligibleToolNames is null || eligibleToolNames.Contains(name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (revalidatedSelectedNames.Count != selectedConnectedNames.Count)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "connected_operation_selector_authority_mismatch"));
            return new ToolPolicyResolution(names, hadFailure);
        }

        connectedMatches.Clear();
        connectedMatches.UnionWith(exactConnectedMatches);
        connectedMatches.UnionWith(revalidatedSelectedNames);
        if (ConnectedOperationLimitExceeded(connectedMatches, availableTools))
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "connected_service_selector_output_over_limit"));
            return new ToolPolicyResolution(names, hadFailure);
        }

        names.UnionWith(connectedMatches);
        return new ToolPolicyResolution(names, hadFailure);
    }

    private static bool ConnectedOperationLimitExceeded(
        IReadOnlySet<string> names,
        IReadOnlyDictionary<string, IAgentTool> availableTools)
    {
        var counts = CountConnectedOperations(names, availableTools);
        return counts.ReadCount >
               AgentTurnToolCatalogBudget.ConnectedOperations.MaximumConnectedReadToolCount ||
               counts.WriteCount >
               AgentTurnToolCatalogBudget.ConnectedOperations.MaximumConnectedWriteToolCount;
    }

    private static ConnectedOperationCounts CountConnectedOperations(
        IReadOnlySet<string> names,
        IReadOnlyDictionary<string, IAgentTool> availableTools)
    {
        var readCount = 0;
        var writeCount = 0;
        foreach (var name in names)
        {
            if (!availableTools.TryGetValue(name, out var tool) ||
                tool is not IAgentToolOperationAdmissionOwner owner)
            {
                continue;
            }

            switch (owner.OperationAdmission.ExecutionPolicy.Risk)
            {
                case AgentToolOperationRisk.ReadOnly:
                    readCount++;
                    break;
                case AgentToolOperationRisk.Write:
                    writeCount++;
                    break;
            }
        }

        return new ConnectedOperationCounts(readCount, writeCount);
    }

    private async Task<IReadOnlySet<string>?> SelectConnectedOperationsAsync(
        IReadOnlySet<string> names,
        IReadOnlyDictionary<string, IAgentTool> availableTools,
        int maximumReadSelections,
        int maximumWriteSelections,
        ConnectedOperationSelectionContext selectionContext,
        List<AgentProfileTurnDiagnostic> diagnostics,
        CancellationToken ct)
    {
        if (names.Count == 0 ||
            maximumReadSelections == 0 && maximumWriteSelections == 0 ||
            selectionContext.TimeoutMs <= 0)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "connected_operation_selector_not_configured"));
            return null;
        }

        var entries = new List<ConnectedOperationSelectionEntry>();
        foreach (var name in names)
        {
            if (!availableTools.TryGetValue(name, out var tool) ||
                !TryCreateConnectedOperationSelectionEntry(name, tool, out var entry))
            {
                diagnostics.Add(new AgentProfileTurnDiagnostic(
                    AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                    "connected_operation_presentation_mismatch"));
                return null;
            }

            entries.Add(entry);
        }

        entries.Sort(static (left, right) =>
        {
            var byCatalog = string.CompareOrdinal(
                left.Admission.CatalogServiceSlug,
                right.Admission.CatalogServiceSlug);
            if (byCatalog != 0)
                return byCatalog;
            var byService = string.CompareOrdinal(
                left.Admission.ServiceInstanceId,
                right.Admission.ServiceInstanceId);
            if (byService != 0)
                return byService;
            var leftEndpoint = ((AgentToolOperationIdentity.PublishedEndpoint)left.Admission.Identity)
                .EndpointId;
            var rightEndpoint = ((AgentToolOperationIdentity.PublishedEndpoint)right.Admission.Identity)
                .EndpointId;
            var byEndpoint = string.CompareOrdinal(leftEndpoint, rightEndpoint);
            return byEndpoint != 0
                ? byEndpoint
                : string.CompareOrdinal(left.ToolName, right.ToolName);
        });

        var candidates = entries
            .Select((entry, index) => entry with
            {
                Candidate = entry.Candidate with
                {
                    CandidateId = $"operation-{index + 1:D3}",
                },
            })
            .ToArray();
        AgentProfileConnectedOperationSelectionResult result;
        try
        {
            result = await _connectedOperationSelector!.SelectAsync(
                new AgentProfileConnectedOperationSelectionRequest(
                    selectionContext.UserMessage,
                    candidates.Select(static entry => entry.Candidate).ToArray(),
                    maximumReadSelections,
                    maximumWriteSelections,
                    TimeSpan.FromMilliseconds(selectionContext.TimeoutMs),
                    selectionContext.LlmControl,
                    selectionContext.RequestId),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Connected-operation selection failed closed during turn catalog materialization");
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "connected_operation_selector_failed"));
            return null;
        }

        if (result.Status == AgentProfileConnectedOperationSelectionStatus.NoMatch)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "connected_operation_selector_no_match"));
            return null;
        }
        if (result.Status != AgentProfileConnectedOperationSelectionStatus.Selected ||
            result.CandidateIds.Count == 0 ||
            result.CandidateIds.Distinct(StringComparer.Ordinal).Count() != result.CandidateIds.Count)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                string.Equals(
                    result.FailureCode,
                    "multiple_write_candidates",
                    StringComparison.Ordinal)
                    ? "connected_service_write_ambiguous"
                    : "connected_operation_selector_failed"));
            return null;
        }

        var entriesByCandidate = candidates.ToDictionary(
            static entry => entry.Candidate.CandidateId,
            StringComparer.Ordinal);
        if (result.CandidateIds.Any(id => !entriesByCandidate.ContainsKey(id)))
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "connected_operation_selector_unknown_candidate"));
            return null;
        }

        var selectedEntries = result.CandidateIds
            .Select(id => entriesByCandidate[id])
            .ToArray();
        var selectedRisks = selectedEntries
            .Select(static entry => entry.Admission.ExecutionPolicy.Risk)
            .Distinct()
            .ToArray();
        var validSelection = selectedRisks.Length == 1 && selectedRisks[0] switch
        {
            AgentToolOperationRisk.ReadOnly =>
                selectedEntries.Length <= maximumReadSelections,
            AgentToolOperationRisk.Write =>
                selectedEntries.Length == 1 && maximumWriteSelections == 1,
            _ => false,
        };
        if (!validSelection)
        {
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                "connected_operation_selector_output_invalid"));
            return null;
        }

        return selectedEntries
            .Select(static entry => entry.ToolName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryCreateConnectedOperationSelectionEntry(
        string toolName,
        IAgentTool tool,
        out ConnectedOperationSelectionEntry entry)
    {
        entry = null!;
        if (tool is not IAgentToolOperationAdmissionOwner owner ||
            owner.OperationAdmission.Identity is not AgentToolOperationIdentity.PublishedEndpoint endpoint ||
            owner.OperationAdmission.ExecutionPolicy.Risk is not (
                AgentToolOperationRisk.ReadOnly or AgentToolOperationRisk.Write))
        {
            return false;
        }

        var admission = owner.OperationAdmission;
        var presentation = tool.Presentation;
        if (presentation is null ||
            presentation.SourceRefCase != ToolPresentationDescriptor.SourceRefOneofCase.NyxIdOperation ||
            !string.Equals(presentation.InvocationName, toolName, StringComparison.Ordinal) ||
            !string.Equals(
                presentation.NyxIdOperation.ConnectedServiceId,
                admission.ServiceInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                presentation.NyxIdOperation.ServiceSlug,
                admission.ServiceSlug,
                StringComparison.Ordinal) ||
            !string.Equals(
                presentation.NyxIdOperation.CatalogServiceSlug,
                admission.CatalogServiceSlug,
                StringComparison.Ordinal) ||
            !string.Equals(
                presentation.NyxIdOperation.OperationId,
                endpoint.EndpointId,
                StringComparison.Ordinal) ||
            !string.Equals(
                presentation.NyxIdOperation.HttpMethod,
                admission.HttpMethod,
                StringComparison.Ordinal) ||
            !string.Equals(
                presentation.NyxIdOperation.PathTemplate,
                admission.PathTemplate,
                StringComparison.Ordinal))
        {
            return false;
        }

        entry = new ConnectedOperationSelectionEntry(
            toolName,
            admission,
            new AgentProfileConnectedOperationSelectionCandidate(
                string.Empty,
                admission.CatalogServiceSlug,
                NormalizeSelectionText(presentation.NyxIdOperation.ConnectorDisplayName, 160),
                NormalizeSelectionText(presentation.NyxIdOperation.ConnectionLabel, 160),
                NormalizeSelectionText(presentation.DisplayName, 256),
                NormalizeSelectionText(presentation.Description, 512),
                admission.HttpMethod,
                admission.PathTemplate,
                admission.ExecutionPolicy.Risk));
        return true;
    }

    private static string NormalizeSelectionText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    private static bool IsValidConnectedServiceSelector(
        AgentProfileConnectedServiceSelector selector) =>
        NyxIdServiceSlugPolicy.IsCanonical(selector.CatalogServiceSlug) &&
        (string.IsNullOrEmpty(selector.EndpointId) ||
         selector.EndpointId.Length <= 256 &&
         string.Equals(selector.EndpointId, selector.EndpointId.Trim(), StringComparison.Ordinal) &&
         !selector.EndpointId.Any(char.IsControl)) &&
        selector.AllowedRisks.Count > 0 &&
        selector.AllowedRisks.All(static risk =>
            risk is AgentToolOperationRiskPayload.ReadOnly or AgentToolOperationRiskPayload.Write) &&
        IsValidReadiness(selector.Readiness);

    private static bool IsValidReadiness(AgentProfileConnectedServiceReadiness? readiness)
    {
        if (readiness is null)
            return true;

        return readiness.RequestedScopes.Count > 0 &&
               readiness.RequestedScopes.Count <= 64 &&
               readiness.RequestedScopes.All(static scope =>
                   !string.IsNullOrWhiteSpace(scope) &&
                   string.Equals(scope, scope.Trim(), StringComparison.Ordinal) &&
                   scope.Length <= 256 &&
                   !scope.Any(char.IsControl)) &&
               readiness.RequestedScopes.Distinct(StringComparer.Ordinal).Count() ==
               readiness.RequestedScopes.Count;
    }

    private static ConnectedServiceReadinessResolution ResolveConnectedServiceReadinessRequirement(
        AgentProfileToolPolicy? policy,
        IReadOnlyDictionary<string, IAgentTool> availableTools,
        IReadOnlySet<string> finalAllowedToolNames)
    {
        if (policy is null || policy.ConnectedServiceSelectors.Count == 0)
            return ConnectedServiceReadinessResolution.None;

        var unmatched = policy.ConnectedServiceSelectors
            .Where(selector => !availableTools.Any(pair =>
                MatchesConnectedServiceSelector(pair.Value, selector)))
            .ToArray();
        if (unmatched.Length != policy.ConnectedServiceSelectors.Count)
            return ConnectedServiceReadinessResolution.None;

        if (unmatched.Length != 1 ||
            unmatched[0].Readiness is null ||
            !finalAllowedToolNames.Contains(NyxIdRequireServiceToolName))
        {
            return new ConnectedServiceReadinessResolution(true, null);
        }

        var argumentsJson = JsonSerializer.Serialize(new
        {
            service_slug = unmatched[0].CatalogServiceSlug,
            requested_scopes = unmatched[0].Readiness.RequestedScopes.ToArray(),
        });
        return new ConnectedServiceReadinessResolution(
            true,
            new AgentProfileRequiredToolInvocation(
                NyxIdRequireServiceToolName,
                argumentsJson));
    }

    private static bool MatchesConnectedServiceSelector(
        IAgentTool tool,
        AgentProfileConnectedServiceSelector selector)
    {
        if (tool is not IAgentToolOperationAdmissionOwner owner ||
            owner.OperationAdmission.Identity is not AgentToolOperationIdentity.PublishedEndpoint endpoint ||
            !string.Equals(
                owner.OperationAdmission.CatalogServiceSlug,
                selector.CatalogServiceSlug,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(selector.EndpointId) &&
            !string.Equals(endpoint.EndpointId, selector.EndpointId, StringComparison.Ordinal))
        {
            return false;
        }

        var risk = owner.OperationAdmission.ExecutionPolicy.Risk switch
        {
            AgentToolOperationRisk.ReadOnly => AgentToolOperationRiskPayload.ReadOnly,
            AgentToolOperationRisk.Write => AgentToolOperationRiskPayload.Write,
            _ => AgentToolOperationRiskPayload.Unspecified,
        };
        return selector.AllowedRisks.Contains(risk);
    }

    /// <summary>
    /// The degraded ceiling for an ordinary NyxID chat turn that selected no
    /// exact profile member: the profile's recovery tools plus the reviewed
    /// ordinary baseline, both already attenuated by the caller's eligible
    /// surface (route set, visibility, and maximum policy). The sealed profile
    /// ceiling is never widened; the baseline only survives where the profile
    /// admits it.
    /// </summary>
    private static HashSet<string> OrdinaryDegradedNames(
        IReadOnlySet<string> eligibleToolNames,
        IReadOnlySet<string> recoveryToolNames)
    {
        var names = new HashSet<string>(recoveryToolNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in eligibleToolNames)
        {
            if (UnprofiledBaselineToolNames.Contains(name))
                names.Add(name);
        }

        return names;
    }

    private static void ApplyMaximumPolicy(
        HashSet<string> eligible,
        IReadOnlySet<string> maximumNames,
        IReadOnlyDictionary<string, IAgentTool> availableTools,
        List<AgentProfileTurnDiagnostic> diagnostics)
    {
        var removed = eligible
            .Where(name => !maximumNames.Contains(name))
            .ToArray();
        eligible.IntersectWith(maximumNames);
        if (removed.Length == 0)
            return;

        var byKind = removed
            .Select(name => availableTools.TryGetValue(name, out var tool)
                ? DiagnosticKind(tool.Presentation.Kind)
                : "unknown")
            .GroupBy(static kind => kind, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => $"{group.Key}={group.Count()}");
        diagnostics.Add(new AgentProfileTurnDiagnostic(
            AgentProfileTurnDiagnosticCode.MaximumPolicyFilteredTools,
            $"removed={removed.Length};{string.Join(';', byKind)}"));
    }

    private static string DiagnosticKind(ToolPresentationKind kind) => kind switch
    {
        ToolPresentationKind.NyxIdOperation => "nyx_id_operation",
        ToolPresentationKind.Mcp => "mcp",
        ToolPresentationKind.Skill => "skill",
        ToolPresentationKind.BuiltIn => "built_in",
        ToolPresentationKind.Generic => "generic",
        _ => "unspecified",
    };

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

        var discovery = await _toolDiscoveryService
            .DiscoverAsync(resolved.Sources, toolContext, ct)
            .ConfigureAwait(false);
        if (!discovery.IsSuccess)
        {
            var diagnosticCode = discovery.Failure!.Code == AgentToolDiscoveryFailureCode.ToolNameCollision
                ? AgentProfileTurnDiagnosticCode.ToolNameCollision
                : AgentProfileTurnDiagnosticCode.ToolDiscoveryFailed;
            diagnostics.Add(new AgentProfileTurnDiagnostic(
                diagnosticCode,
                string.IsNullOrWhiteSpace(discovery.Failure.ToolName)
                    ? resolved.Name ?? string.Empty
                    : discovery.Failure.ToolName));
            return new ToolDiscoveryResolution(
                new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase),
                true);
        }

        return ToEligibleTools(discovery.Tools, toolContext, diagnostics);
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
                // Capability ineligibility is an availability intersection for
                // this caller, not a discovery failure: the tool drops with a
                // diagnostic while the rest of the surface stays usable.
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
               !string.IsNullOrWhiteSpace(
                   AgentToolHumanSessionNyxIdCredential.ResolveBearerToken(toolContext));
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
        IReadOnlyList<AgentProfileTurnDiagnostic> diagnostics,
        AgentTurnToolCatalogProof? shadowCandidateProof = null)
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
        return AgentProfileTurnAuthorityPreparation.Create(authority, diagnostics, shadowCandidateProof);
    }

    private static AgentTurnToolCatalogMaterialization BuildMaterialization(
        AgentProfileSnapshot profile,
        AgentProfileTurnAuthorityState committedAuthority,
        AgentProfileTurnAuthorityKind authorityKind,
        IEnumerable<string> ceilingToolNames,
        string? selectedIntentId,
        SelectedSkillPromptLayer? selectedSkillPromptLayer,
        IReadOnlyList<AgentProfileTurnDiagnostic> diagnostics,
        IEnumerable<IAgentTool> exactTools,
        bool hasUnresolvedConnectedServiceSelectors = false,
        AgentProfileRequiredToolInvocation? requiredToolInvocation = null)
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
        try
        {
            var catalog = AgentTurnToolCatalogFactory.CreateForProfile(
                profile,
                proposal.AuthorityCeilingToolNames,
                selectedIntentId,
                committedAuthority.CandidateRoute?.IntentId,
                selectedSkillPromptLayer,
                diagnostics,
                exactTools,
                hasUnresolvedConnectedServiceSelectors,
                requiredToolInvocation);
            return AgentTurnToolCatalogMaterialization.Create(catalog, proposal);
        }
        catch (AgentTurnToolCatalogException exception)
        {
            var failClosedDiagnostics = diagnostics
                .Append(ToCatalogDiagnostic(exception.Failure))
                .ToArray();
            proposal.AuthorityKind = AgentProfileTurnAuthorityKind.RestrictedEmpty;
            proposal.AuthorityCeilingToolNames.Clear();
            proposal.DegradationReasons.Clear();
            proposal.DegradationReasons.Add(
                committedAuthority.DegradationReasons
                    .Concat(failClosedDiagnostics.Select(static diagnostic => ToDegradationReason(diagnostic)))
                    .Where(static reason => reason != AgentProfileTurnDegradationReason.Unspecified)
                    .Distinct()
                    .OrderBy(static reason => (int)reason));
            return AgentTurnToolCatalogMaterialization.Create(
                AgentTurnToolCatalogFactory.RestrictedEmpty(diagnostics: failClosedDiagnostics),
                proposal);
        }
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
        AgentProfileTurnAuthorityState committedAuthority,
        ICollection<AgentProfileTurnDiagnostic> diagnostics)
    {
        var candidate = committedAuthority.CandidateRoute;
        var exactRef = committedAuthority.SelectedExactSkillRef;
        if (candidate is null)
            return null;

        var matches = profile.Members.Where(member =>
                string.Equals(member.IntentId, candidate.IntentId, StringComparison.Ordinal) &&
                (exactRef is null
                    ? member.SkillRef is null
                    : member.SkillRef is not null &&
                      string.Equals(member.SkillRef.Guid, exactRef.Guid, StringComparison.Ordinal) &&
                      string.Equals(
                          member.SkillRef.LiteralVersion,
                          exactRef.LiteralVersion,
                          StringComparison.Ordinal)))
            .Take(2)
            .ToArray();
        if (matches.Length <= 1)
            return matches.SingleOrDefault();

        diagnostics.Add(new AgentProfileTurnDiagnostic(
            AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
            "committed_intent_id_collision"));
        return null;
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
        (AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation, _) =>
            AgentProfileTurnDegradationReason.CatalogNeedsDisambiguation,
        (AgentProfileTurnDiagnosticCode.CatalogOverBudget, _) =>
            AgentProfileTurnDegradationReason.CatalogOverBudget,
        (AgentProfileTurnDiagnosticCode.SchemaInvalid, _) =>
            AgentProfileTurnDegradationReason.SchemaInvalid,
        (AgentProfileTurnDiagnosticCode.SelectedPolicyEmpty, _) =>
            AgentProfileTurnDegradationReason.SelectedPolicyEmpty,
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
        AgentProfileTurnDegradationReason.CatalogNeedsDisambiguation =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation, reason.ToString()),
        AgentProfileTurnDegradationReason.CatalogOverBudget =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.CatalogOverBudget, reason.ToString()),
        AgentProfileTurnDegradationReason.SchemaInvalid =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.SchemaInvalid, reason.ToString()),
        AgentProfileTurnDegradationReason.SelectedPolicyEmpty =>
            new AgentProfileTurnDiagnostic(AgentProfileTurnDiagnosticCode.SelectedPolicyEmpty, reason.ToString()),
        AgentProfileTurnDegradationReason.Unspecified or
            AgentProfileTurnDegradationReason.LegacyAuthorityMissing or
            AgentProfileTurnDegradationReason.MaterializerUnavailable or
            AgentProfileTurnDegradationReason.MaterializationFailed => null,
        _ => null,
    };

    private static AgentProfileTurnDiagnostic ToCatalogDiagnostic(AgentTurnToolCatalogFailure failure) =>
        failure.Code switch
        {
            AgentTurnToolCatalogFailureCode.CatalogOverBudget => new(
                AgentProfileTurnDiagnosticCode.CatalogOverBudget,
                failure.Detail),
            AgentTurnToolCatalogFailureCode.SchemaInvalid => new(
                AgentProfileTurnDiagnosticCode.SchemaInvalid,
                failure.ToolName ?? failure.Detail),
            AgentTurnToolCatalogFailureCode.CatalogNeedsDisambiguation => new(
                AgentProfileTurnDiagnosticCode.CatalogNeedsDisambiguation,
                failure.Detail),
            AgentTurnToolCatalogFailureCode.ToolNameCollision => new(
                AgentProfileTurnDiagnosticCode.ToolNameCollision,
                failure.ToolName ?? failure.Detail),
            _ => new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ProfileInvalid,
                failure.Code.ToString()),
        };

    private static bool IsDisambiguationFailure(string? failureCode) =>
        !string.IsNullOrWhiteSpace(failureCode) &&
        failureCode.Contains("ambig", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesAlias(string? userMessage, string? alias)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(alias))
            return false;

        var message = userMessage.Trim();
        var normalizedAlias = alias.Trim();
        var searchFrom = 0;
        while (searchFrom <= message.Length - normalizedAlias.Length)
        {
            var relativeIndex = message.IndexOf(
                normalizedAlias,
                searchFrom,
                StringComparison.OrdinalIgnoreCase);
            if (relativeIndex < 0)
                return false;

            var matchEnd = relativeIndex + normalizedAlias.Length;
            if (IsAliasBoundary(message, relativeIndex - 1) &&
                IsAliasBoundary(message, matchEnd))
            {
                return true;
            }

            searchFrom = relativeIndex + 1;
        }

        return false;
    }

    private static bool IsAliasBoundary(string message, int index) =>
        index < 0 ||
        index >= message.Length ||
        (!char.IsLetterOrDigit(message[index]) && message[index] != '_');

    private sealed record ToolPolicyResolution(IReadOnlySet<string> Names, bool HadFailure);

    private sealed record ConnectedOperationSelectionContext(
        string UserMessage,
        int TimeoutMs,
        LLMControlContext? LlmControl,
        string RequestId);

    private sealed record ConnectedOperationSelectionEntry(
        string ToolName,
        AgentToolOperationAdmission Admission,
        AgentProfileConnectedOperationSelectionCandidate Candidate);

    private sealed record ConnectedOperationCounts(int ReadCount, int WriteCount);

    private sealed record ConnectedServiceReadinessResolution(
        bool HasUnresolvedSelectors,
        AgentProfileRequiredToolInvocation? RequiredToolInvocation)
    {
        public static ConnectedServiceReadinessResolution None { get; } = new(false, null);
    }

    private sealed record ToolDiscoveryResolution(IReadOnlyDictionary<string, IAgentTool> Tools, bool HadFailure)
    {
        public IReadOnlySet<string> Names { get; } = Tools.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
