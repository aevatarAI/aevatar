using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Scripting.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgentService.Infrastructure.Orchestration;

public sealed class ScriptingServiceRevisionRepublishHook : ICommittedStatePublicationHook
{
    private readonly IServiceScriptingRepublishCandidateQueryReader _candidateReader;
    private readonly IServiceCommandPort _serviceCommandPort;
    private readonly ILogger<ScriptingServiceRevisionRepublishHook> _logger;

    public ScriptingServiceRevisionRepublishHook(
        IServiceScriptingRepublishCandidateQueryReader candidateReader,
        IServiceCommandPort serviceCommandPort,
        ILogger<ScriptingServiceRevisionRepublishHook>? logger = null)
    {
        _candidateReader = candidateReader ?? throw new ArgumentNullException(nameof(candidateReader));
        _serviceCommandPort = serviceCommandPort ?? throw new ArgumentNullException(nameof(serviceCommandPort));
        _logger = logger ?? NullLogger<ScriptingServiceRevisionRepublishHook>.Instance;
    }

    public async Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var promoted = TryUnpackPromotedEvent(context);
        if (promoted == null)
            return;

        var normalizedScopeId = promoted.ScopeId?.Trim();
        var normalizedScriptId = promoted.ScriptId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedScopeId) || string.IsNullOrWhiteSpace(normalizedScriptId))
            return;

        var candidates = await _candidateReader.QueryServingByScopeScriptAsync(
            normalizedScopeId,
            normalizedScriptId,
            ct);

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (AlreadyServingPromotedRevision(candidate, promoted))
                continue;

            try
            {
                var revisionId = BuildRepublishedRevisionId(candidate, promoted);
                await EnsureRevisionAsync(candidate, promoted, revisionId, ct);
                await _serviceCommandPort.PrepareRevisionAsync(
                    new PrepareServiceRevisionCommand
                    {
                        Identity = candidate.Identity.Clone(),
                        RevisionId = revisionId,
                    },
                    ct);
                await _serviceCommandPort.PublishRevisionAsync(
                    new PublishServiceRevisionCommand
                    {
                        Identity = candidate.Identity.Clone(),
                        RevisionId = revisionId,
                    },
                    ct);
                await _serviceCommandPort.SetDefaultServingRevisionAsync(
                    new SetDefaultServingRevisionCommand
                    {
                        Identity = candidate.Identity.Clone(),
                        RevisionId = revisionId,
                    },
                    ct);
                await _serviceCommandPort.ActivateServiceRevisionAsync(
                    new ActivateServiceRevisionCommand
                    {
                        Identity = candidate.Identity.Clone(),
                        RevisionId = revisionId,
                    },
                    ct);

                _logger.LogInformation(
                    "Republished scripting service {ServiceKey} from revision {CurrentRevisionId} to revision {NewRevisionId} for promoted script {ScriptId}@{ScriptRevision}.",
                    ServiceKeys.Build(candidate.Identity),
                    candidate.CurrentServingRevisionId,
                    revisionId,
                    promoted.ScriptId,
                    promoted.Revision);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to republish scripting service {ServiceKey} for promoted script {ScriptId}@{ScriptRevision}; committed event publication will continue.",
                    ServiceKeys.Build(candidate.Identity),
                    promoted.ScriptId,
                    promoted.Revision);
            }
        }
    }

    private static ScriptCatalogRevisionPromotedEvent? TryUnpackPromotedEvent(CommittedStatePublicationContext context)
    {
        var payload = context.Published.StateEvent?.EventData;
        if (payload == null || !payload.Is(ScriptCatalogRevisionPromotedEvent.Descriptor))
            return null;

        return payload.Unpack<ScriptCatalogRevisionPromotedEvent>();
    }

    private static bool AlreadyServingPromotedRevision(
        ServiceScriptingRepublishCandidateSnapshot candidate,
        ScriptCatalogRevisionPromotedEvent promoted)
    {
        return string.Equals(candidate.Scripting.Revision, promoted.Revision, StringComparison.Ordinal) &&
               string.Equals(candidate.Scripting.SourceHash, promoted.SourceHash, StringComparison.Ordinal) &&
               string.Equals(candidate.Scripting.DefinitionActorId, promoted.DefinitionActorId, StringComparison.Ordinal);
    }

    private static string BuildRepublishedRevisionId(
        ServiceScriptingRepublishCandidateSnapshot candidate,
        ScriptCatalogRevisionPromotedEvent promoted)
    {
        var baseRevisionId = candidate.CurrentServingRevisionId?.Trim();
        if (string.IsNullOrWhiteSpace(baseRevisionId))
            baseRevisionId = "rev";

        var promotedRevision = promoted.Revision?.Trim();
        if (string.IsNullOrWhiteSpace(promotedRevision))
            promotedRevision = "script";

        var stableHash = BuildStableHash(
            promoted.ScriptId,
            promoted.Revision,
            promoted.DefinitionActorId,
            promoted.SourceHash);
        return $"{baseRevisionId}-script-{SanitizeSegment(promotedRevision)}-{stableHash}";
    }

    private static ServiceRevisionSpec BuildRevisionSpec(
        ServiceScriptingRepublishCandidateSnapshot candidate,
        ScriptCatalogRevisionPromotedEvent promoted,
        string revisionId)
    {
        var spec = new ServiceRevisionSpec
        {
            Identity = candidate.Identity.Clone(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Scripting,
            ScriptingSpec = new ScriptingServiceRevisionSpec
            {
                ScriptId = promoted.ScriptId ?? string.Empty,
                Revision = promoted.Revision ?? string.Empty,
                DefinitionActorId = promoted.DefinitionActorId ?? string.Empty,
                SourceHash = promoted.SourceHash ?? string.Empty,
            },
        };

        return spec;
    }

    private static string SanitizeSegment(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[length++] = char.ToLowerInvariant(ch);
                continue;
            }

            if (length == 0 || buffer[length - 1] == '-')
                continue;

            buffer[length++] = '-';
        }

        while (length > 0 && buffer[length - 1] == '-')
            length--;

        return length == 0 ? "script" : new string(buffer[..length]);
    }

    private async Task EnsureRevisionAsync(
        ServiceScriptingRepublishCandidateSnapshot candidate,
        ScriptCatalogRevisionPromotedEvent promoted,
        string revisionId,
        CancellationToken ct)
    {
        try
        {
            await _serviceCommandPort.CreateRevisionAsync(
                new CreateServiceRevisionCommand
                {
                    Spec = BuildRevisionSpec(candidate, promoted, revisionId),
                },
                ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
        {
            _logger.LogDebug(
                ex,
                "Republish revision {RevisionId} already exists for service {ServiceKey}; continuing lifecycle commands.",
                revisionId,
                ServiceKeys.Build(candidate.Identity));
        }
    }

    private static string BuildStableHash(params string?[] values)
    {
        var buffer = string.Join("|", values.Select(value => value?.Trim() ?? string.Empty));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(buffer));
        return Convert.ToHexString(hash).ToLowerInvariant()[..12];
    }
}
