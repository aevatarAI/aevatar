using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Hosting.AgentProfiles;

public sealed class NyxIdChatSystemAgentProfileBootstrapHostedService : IHostedService
{
    private const string AuditSubject = "system-agent-profile-bootstrap";

    private readonly AgentProfileApplicationService _profileService;
    private readonly IOptions<NyxIdChatSystemAgentProfileBootstrapOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NyxIdChatSystemAgentProfileBootstrapHostedService> _logger;

    public NyxIdChatSystemAgentProfileBootstrapHostedService(
        AgentProfileApplicationService profileService,
        IOptions<NyxIdChatSystemAgentProfileBootstrapOptions> options,
        TimeProvider timeProvider,
        ILogger<NyxIdChatSystemAgentProfileBootstrapHostedService> logger)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
            return;

        if (options.Members.Count == 0 && !HasAnyToolPolicy(options))
        {
            _logger.LogWarning(
                "System NyxID chat Agent Profile bootstrap is enabled but no exact Ornn skill members or tool policy are configured; skipping profile publish.");
            return;
        }

        if (!AgentProfilePolicies.IsReviewedRolloutCohort(options.CohortBasisPoints))
        {
            _logger.LogError(
                "System NyxID chat Agent Profile bootstrap has unsupported cohort basis points {CohortBasisPoints}.",
                options.CohortBasisPoints);
            return;
        }

        try
        {
            var owner = AgentProfileOwners.ForSystem();
            var profileSlug = Normalize(options.ProfileSlug);
            var desiredDraft = NyxIdChatSystemAgentProfileDraftFactory.Create(options);
            var desiredDraftSha256 = AgentProfileDeterminism.ComputeDraftDigest(desiredDraft);
            var detail = await EnsureProfileAsync(
                owner,
                profileSlug,
                desiredDraft,
                desiredDraftSha256,
                options,
                cancellationToken).ConfigureAwait(false);
            if (detail is null)
                return;

            await EnsureBindingAsync(
                owner,
                profileSlug,
                detail,
                AgentProfilePolicies.NyxIdChatAgentKind,
                "set-binding",
                options,
                cancellationToken).ConfigureAwait(false);

            if (options.EnableChannelReplyDefaultBinding)
            {
                var channelReplyProfileSlug = Normalize(options.ChannelReplyProfileSlug);
                var channelReplyDraft = NyxIdChatSystemAgentProfileDraftFactory.CreateChannelReply(options);
                var channelReplyDraftSha256 = AgentProfileDeterminism.ComputeDraftDigest(channelReplyDraft);
                var channelReplyDetail = await EnsureProfileAsync(
                    owner,
                    channelReplyProfileSlug,
                    channelReplyDraft,
                    channelReplyDraftSha256,
                    options,
                    cancellationToken).ConfigureAwait(false);
                if (channelReplyDetail is null)
                    return;

                await EnsureBindingAsync(
                    owner,
                    channelReplyProfileSlug,
                    channelReplyDetail,
                    AgentProfilePolicies.ChannelReplyAgentKind,
                    $"set-binding:{AgentProfilePolicies.ChannelReplyAgentKind}",
                    options,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentProfileSealingException ex)
        {
            _logger.LogError(
                ex,
                "System NyxID chat Agent Profile sealing failed: {Diagnostics}",
                string.Join(", ", ex.Diagnostics.Select(static x => $"{x.Code}:{x.Field}")));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "System NyxID chat Agent Profile bootstrap failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<AgentProfileManagementDetail?> EnsureProfileAsync(
        AgentProfileOwner owner,
        string profileSlug,
        AgentProfileDraft desiredDraft,
        ByteString desiredDraftSha256,
        NyxIdChatSystemAgentProfileBootstrapOptions options,
        CancellationToken ct)
    {
        var state = await GetProfileBootstrapStateAsync(owner, profileSlug, ct).ConfigureAwait(false);
        var detail = state.Detail;
        if (!state.Exists)
        {
            var receipt = await _profileService.CreateAsync(
                new AgentProfileCreateRequest(
                    owner,
                    profileSlug,
                    IdempotencyKey(options, profileSlug, "create"),
                    AuditSubject),
                ct).ConfigureAwait(false);
            _logger.LogInformation(
                "System Agent Profile bootstrap create accepted: profileSlug={ProfileSlug}, operationId={OperationId}, commandId={CommandId}",
                profileSlug,
                receipt.OperationId,
                receipt.CommandId);
        }

        if (detail is null)
        {
            detail = await WaitForProfileAsync(
                owner,
                profileSlug,
                static detail => detail is not null,
                options,
                ct).ConfigureAwait(false);
            if (detail is null)
            {
                _logger.LogWarning(
                    "System Agent Profile '{ProfileSlug}' did not materialize before timeout.",
                    profileSlug);
                return null;
            }
        }

        var draftChanged = false;
        if (!detail.Snapshot.DraftSha256.Equals(desiredDraftSha256))
        {
            var receipt = await _profileService.UpdateDraftAsync(
                new AgentProfileDraftUpdateRequest(
                    owner,
                    profileSlug,
                    desiredDraft,
                    detail.Snapshot.AuthorityStateVersion,
                    IdempotencyKey(options, profileSlug, "update-draft"),
                    AuditSubject),
                ct).ConfigureAwait(false);
            _logger.LogInformation(
                "System Agent Profile bootstrap draft update accepted: profileSlug={ProfileSlug}, operationId={OperationId}, commandId={CommandId}",
                profileSlug,
                receipt.OperationId,
                receipt.CommandId);

            detail = await WaitForProfileAsync(
                owner,
                profileSlug,
                candidate => candidate?.Snapshot.DraftSha256.Equals(desiredDraftSha256) == true,
                options,
                ct).ConfigureAwait(false);
            if (detail is null)
            {
                _logger.LogWarning(
                    "System NyxID chat Agent Profile '{ProfileSlug}' draft update did not materialize before timeout.",
                    profileSlug);
                return null;
            }

            draftChanged = true;
        }

        if (draftChanged || !PublishedExists(detail.Snapshot) || !detail.ExecutionAvailable)
        {
            var receipt = await _profileService.PublishAsync(
                new AgentProfilePublishRequest(
                    owner,
                    profileSlug,
                    detail.Snapshot.AuthorityStateVersion,
                    IdempotencyKey(options, profileSlug, "publish"),
                    AuditSubject,
                    NyxIdAccessToken: null),
                ct).ConfigureAwait(false);
            _logger.LogInformation(
                "System Agent Profile bootstrap publish accepted: profileSlug={ProfileSlug}, operationId={OperationId}, commandId={CommandId}",
                profileSlug,
                receipt.OperationId,
                receipt.CommandId);

            detail = await WaitForProfileAsync(
                owner,
                profileSlug,
                static candidate => candidate?.ExecutionAvailable == true,
                options,
                ct).ConfigureAwait(false);
            if (detail is null)
            {
                _logger.LogWarning(
                    "System NyxID chat Agent Profile '{ProfileSlug}' publish did not produce an execution read model before timeout.",
                    profileSlug);
                return null;
            }
        }

        return detail;
    }

    private async Task EnsureBindingAsync(
        AgentProfileOwner owner,
        string profileSlug,
        AgentProfileManagementDetail detail,
        string agentKind,
        string idempotencyOperation,
        NyxIdChatSystemAgentProfileBootstrapOptions options,
        CancellationToken ct)
    {
        var deadline = _timeProvider.GetUtcNow() + options.ProjectionWaitTimeout;
        var interval = options.ProjectionPollInterval <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(250)
            : options.ProjectionPollInterval;
        var setAccepted = false;
        var bindingIdempotencyOperation = BindingIdempotencyOperation(idempotencyOperation, detail.Snapshot);

        while (true)
        {
            var binding = await _profileService.GetBindingAsync(
                owner,
                agentKind,
                ct).ConfigureAwait(false);
            if (BindingTargetsPublishedSnapshot(binding.Binding, detail.Snapshot, options))
            {
                _logger.LogInformation(
                    "System Agent Profile bootstrap binding current: agentKind={AgentKind}, profileSlug={ProfileSlug}, publishedRevision={PublishedRevision}",
                    agentKind,
                    profileSlug,
                    detail.Snapshot.PublishedRevision);
                return;
            }

            if (!setAccepted)
            {
                try
                {
                    var receipt = await _profileService.SetBindingAsync(
                        new AgentProfileBindingUpdateRequest(
                            owner,
                            agentKind,
                            new AgentProfileReference
                            {
                                OwnerKind = AgentProfileReferenceOwnerKind.System,
                                ProfileSlug = profileSlug,
                            },
                            binding.AuthorityStateVersion,
                            IdempotencyKey(options, profileSlug, bindingIdempotencyOperation),
                            AuditSubject,
                            Enabled: true,
                            CohortBasisPoints: options.CohortBasisPoints),
                        ct).ConfigureAwait(false);
                    setAccepted = true;
                    _logger.LogInformation(
                        "System Agent Profile bootstrap binding set accepted: agentKind={AgentKind}, profileSlug={ProfileSlug}, publishedRevision={PublishedRevision}, operationId={OperationId}, commandId={CommandId}",
                        agentKind,
                        profileSlug,
                        detail.Snapshot.PublishedRevision,
                        receipt.OperationId,
                        receipt.CommandId);
                }
                catch (AgentProfileUnavailableException exception)
                {
                    _logger.LogInformation(
                        exception,
                        "System Agent Profile bootstrap binding is waiting for protected execution: agentKind={AgentKind}, profileSlug={ProfileSlug}, publishedRevision={PublishedRevision}",
                        agentKind,
                        profileSlug,
                        detail.Snapshot.PublishedRevision);
                }
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                _logger.LogWarning(
                    "System Agent Profile bootstrap binding did not become current before timeout: agentKind={AgentKind}, profileSlug={ProfileSlug}, publishedRevision={PublishedRevision}",
                    agentKind,
                    profileSlug,
                    detail.Snapshot.PublishedRevision);
                return;
            }

            await Task.Delay(interval, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    private async Task<ProfileBootstrapState> GetProfileBootstrapStateAsync(
        AgentProfileOwner owner,
        string profileSlug,
        CancellationToken ct)
    {
        try
        {
            var detail = await _profileService.GetAsync(owner, profileSlug, ct).ConfigureAwait(false);
            return new ProfileBootstrapState(detail, detail is not null);
        }
        catch (AgentProfileUnavailableException exception)
        {
            _logger.LogInformation(
                exception,
                "System Agent Profile bootstrap is waiting for active authority: profileSlug={ProfileSlug}",
                profileSlug);
            return new ProfileBootstrapState(null, true);
        }
    }

    private async Task<AgentProfileManagementDetail?> WaitForProfileAsync(
        AgentProfileOwner owner,
        string profileSlug,
        Func<AgentProfileManagementDetail?, bool> ready,
        NyxIdChatSystemAgentProfileBootstrapOptions options,
        CancellationToken ct)
    {
        var deadline = _timeProvider.GetUtcNow() + options.ProjectionWaitTimeout;
        var interval = options.ProjectionPollInterval <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(250)
            : options.ProjectionPollInterval;

        while (true)
        {
            var state = await GetProfileBootstrapStateAsync(owner, profileSlug, ct).ConfigureAwait(false);
            if (ready(state.Detail))
                return state.Detail;
            if (_timeProvider.GetUtcNow() >= deadline)
                return null;
            await Task.Delay(interval, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    private static bool PublishedExists(AgentProfileManagementSnapshot snapshot) =>
        snapshot.PublishedRevision > 0 &&
        snapshot.PublishedSnapshotSha256.Length == 32;

    private static bool HasAnyToolPolicy(NyxIdChatSystemAgentProfileBootstrapOptions options) =>
        HasAnyToolPolicy(options.MaximumToolPolicy) ||
        HasAnyToolPolicy(options.RecoveryToolPolicy);

    private static bool HasAnyToolPolicy(AgentProfileToolPolicyOptions policy) =>
        policy.ToolNames.Count > 0 ||
        policy.ToolSetRefs.Count > 0 ||
        policy.ConnectedServiceSelectors.Count > 0;

    private static bool BindingTargetsPublishedSnapshot(
        AgentProfileDefaultBinding? binding,
        AgentProfileManagementSnapshot snapshot,
        NyxIdChatSystemAgentProfileBootstrapOptions options) =>
        binding?.Target is not null &&
        binding.AdmissionCase == AgentProfileDefaultBinding.AdmissionOneofCase.System &&
        binding.System is not null &&
        binding.System.Enabled &&
        binding.System.CohortBasisPoints == options.CohortBasisPoints &&
        AgentProfileDeterminism.SameOwner(binding.Target.Owner, snapshot.Identity.Owner) &&
        string.Equals(binding.Target.ProfileId, snapshot.Identity.ProfileId, StringComparison.Ordinal) &&
        binding.Target.PublishedRevision == snapshot.PublishedRevision &&
        binding.Target.SnapshotSha256.Equals(snapshot.PublishedSnapshotSha256);

    private static string IdempotencyKey(
        NyxIdChatSystemAgentProfileBootstrapOptions options,
        string profileSlug,
        string operation) =>
        $"system-nyxid-chat-default:{profileSlug}:{Normalize(options.PolicyRevision)}:{operation}";

    private static string BindingIdempotencyOperation(
        string operation,
        AgentProfileManagementSnapshot snapshot) =>
        $"{operation}:r{snapshot.PublishedRevision}:{snapshot.PublishedSnapshotSha256.ToBase64()}";

    private static string Normalize(string value) => value.Trim();

    private sealed record ProfileBootstrapState(AgentProfileManagementDetail? Detail, bool Exists);
}
