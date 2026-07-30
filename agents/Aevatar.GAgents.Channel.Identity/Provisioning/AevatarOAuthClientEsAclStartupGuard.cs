using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.Channel.Identity;

public sealed class AevatarOAuthClientEsAclStartupGuard : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AevatarOAuthClientEsAclOptions _options;
    private readonly ILogger<AevatarOAuthClientEsAclStartupGuard> _logger;

    public AevatarOAuthClientEsAclStartupGuard(
        IServiceProvider serviceProvider,
        IOptions<AevatarOAuthClientEsAclOptions> options,
        ILogger<AevatarOAuthClientEsAclStartupGuard>? logger = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AevatarOAuthClientEsAclStartupGuard>.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_options.EnforcementMode == AevatarOAuthClientEsAclEnforcementMode.Disabled)
        {
            _logger.LogInformation(
                "AevatarOAuthClient ES ACL guard disabled (EnforcementMode=Disabled). index={IndexName}",
                AevatarOAuthClientDocumentMetadataProvider.IndexName);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var scopedProvider = scope.ServiceProvider;
        VerifyInternalProjectionWiring(scopedProvider);

        var probe = scopedProvider.GetRequiredService<IOAuthClientEsAclProbe>();
        var probeResult = await probe.ProbeAsync(cancellationToken);

        EnforceProbeResult(probeResult);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    // The guard only meaningfully protects the state-token HMAC material when the
    // OAuth-client read surface is the internal projection provider (not some
    // broader query seam). This structural check is provider-agnostic and stays
    // in place for every non-Disabled mode.
    private static void VerifyInternalProjectionWiring(IServiceProvider scopedProvider)
    {
        var oauthClientProvider = scopedProvider.GetRequiredService<IAevatarOAuthClientProvider>();
        if (oauthClientProvider.GetType() != typeof(AevatarOAuthClientProjectionProvider))
        {
            throw new InvalidOperationException(
                "AevatarOAuthClient ES ACL guard requires IAevatarOAuthClientProvider to be the internal projection provider.");
        }

        _ = scopedProvider.GetRequiredService<IProjectionDocumentReader<AevatarOAuthClientDocument, string>>();
        _ = scopedProvider.GetRequiredService<IProjectionDocumentWriter<AevatarOAuthClientDocument>>();
        _ = scopedProvider.GetRequiredService<ICurrentStateProjectionMaterializer<AevatarOAuthClientMaterializationContext>>();
    }

    private void EnforceProbeResult(EsAclProbeResult probeResult)
    {
        var attestationDeclaredRestricted = _options.GrantMatchesGrainEventStoreInternal;

        switch (_options.EnforcementMode)
        {
            case AevatarOAuthClientEsAclEnforcementMode.Strict:
                // Strict requires both an affirmative live classification and the
                // operator attestation. An unavailable or inconclusive probe cannot
                // substitute an assertion for evidence about the effective grant.
                if (probeResult.Status != EsAclProbeStatus.Restricted)
                {
                    throw new InvalidOperationException(
                        BuildFailureMessage(
                            $"the Elasticsearch security API did NOT positively confirm that the " +
                            $"'{AevatarOAuthClientDocumentMetadataProvider.IndexName}' read grant is restricted. " +
                            $"probeStatus={probeResult.Status} observedState={probeResult.ObservedState}"));
                }

                if (!attestationDeclaredRestricted)
                {
                    throw new InvalidOperationException(
                        BuildFailureMessage(
                            $"the operator attestation {AevatarOAuthClientEsAclOptions.SectionName}:GrantMatchesGrainEventStoreInternal is false. " +
                            $"probeStatus={probeResult.Status} observedState={probeResult.ObservedState}"));
                }

                _logger.LogInformation(
                    "AevatarOAuthClient ES ACL guard (Strict) passed. index={IndexName} probeStatus={ProbeStatus} observedState={ObservedState}",
                    AevatarOAuthClientDocumentMetadataProvider.IndexName,
                    probeResult.Status,
                    probeResult.ObservedState);

                return;

            case AevatarOAuthClientEsAclEnforcementMode.Warn:
            default:
                // Non-breaking mode: probe, but never throw. When the grant is not
                // confirmed restricted (unrestricted, unverifiable, or unavailable)
                // or the attestation is declared false, log an actionable warning
                // with the observed state so operators can act without a crash loop.
                if (probeResult.Status == EsAclProbeStatus.Restricted && attestationDeclaredRestricted)
                {
                    _logger.LogInformation(
                        "AevatarOAuthClient ES ACL guard (Warn) confirmed the '{IndexName}' read grant is restricted. observedState={ObservedState}",
                        AevatarOAuthClientDocumentMetadataProvider.IndexName,
                        probeResult.ObservedState);
                    return;
                }

                _logger.LogWarning(
                    "AevatarOAuthClient ES ACL guard (Warn): the '{IndexName}' index stores state-token HMAC key material and its read grant is NOT confirmed restricted. " +
                    "probeStatus={ProbeStatus} attestationRestricted={AttestationRestricted} observedState={ObservedState}. " +
                    "Limit the index read grant to grain/event-store internal services and set {SectionName}:GrantMatchesGrainEventStoreInternal=true; " +
                    "set {SectionName}:EnforcementMode=Strict to fail closed once verified.",
                    AevatarOAuthClientDocumentMetadataProvider.IndexName,
                    probeResult.Status,
                    attestationDeclaredRestricted,
                    probeResult.ObservedState,
                    AevatarOAuthClientEsAclOptions.SectionName,
                    AevatarOAuthClientEsAclOptions.SectionName);
                return;
        }
    }

    private static string BuildFailureMessage(string reason) =>
        "AevatarOAuthClient Elasticsearch projection contains state-token HMAC keys, but " + reason + " " +
        $"Limit the '{AevatarOAuthClientDocumentMetadataProvider.IndexName}' index read grant to grain/event-store internal services " +
        $"(and set {AevatarOAuthClientEsAclOptions.SectionName}:GrantMatchesGrainEventStoreInternal=true) before starting in Strict mode.";
}
