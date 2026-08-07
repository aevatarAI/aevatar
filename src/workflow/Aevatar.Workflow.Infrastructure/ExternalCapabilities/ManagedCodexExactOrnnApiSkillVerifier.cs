using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Workflow.Infrastructure.ExternalCapabilities;

internal sealed partial class ManagedCodexExactOrnnApiSkillVerifier : IExactServiceApiSkillVerifier
{
    private const string ChronoSandboxServiceSlug = "chrono-sandbox";
    private const string OrnnApiServiceSlug = "ornn-api";
    private const long ExactOrnnReadMaxBytes = 1_048_576;

    private readonly IManagedCodexCredentialQueryPort _credentialQueryPort;
    private readonly ISecretVault _secretVault;
    private readonly INyxIdApiClientFactory _clientFactory;
    private readonly TimeProvider _timeProvider;

    public ManagedCodexExactOrnnApiSkillVerifier(
        IManagedCodexCredentialQueryPort credentialQueryPort,
        ISecretVault secretVault,
        INyxIdApiClientFactory clientFactory,
        TimeProvider? timeProvider = null)
    {
        _credentialQueryPort = credentialQueryPort ?? throw new ArgumentNullException(nameof(credentialQueryPort));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ExactServiceApiSkillVerificationResult> VerifyAsync(
        ExactServiceApiSkillVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Access);
        ArgumentNullException.ThrowIfNull(request.Input);
        ArgumentNullException.ThrowIfNull(request.Candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var candidateValidation = ValidateCandidateIdentity(request.Candidate);
        if (candidateValidation is not null)
            return candidateValidation;

        var owner = ResolveOwner(request);
        var snapshot = await _credentialQueryPort.ResolveAsync(owner, cancellationToken)
            .ConfigureAwait(false);
        var credential = ValidateCredential(snapshot?.Credential, owner);
        var apiKey = await ResolveApiKeyAsync(credential, owner, cancellationToken)
            .ConfigureAwait(false);

        using var client = _clientFactory.CreateClient();
        var detailRead = await ReadOrnnAsync(
                client,
                apiKey,
                credential.OrnnApiUserServiceId,
                $"/api/v1/skills/{Uri.EscapeDataString(request.Candidate.Guid)}?version={Uri.EscapeDataString(request.Candidate.LiteralVersion)}",
                cancellationToken)
            .ConfigureAwait(false);
        if (!detailRead.Succeeded)
            return ExactServiceApiSkillVerificationResult.Rejected(ServiceApiNoReliableSkillReason.ExactSkillReadFailed);

        var packageRead = await ReadOrnnAsync(
                client,
                apiKey,
                credential.OrnnApiUserServiceId,
                $"/api/v1/skills/{Uri.EscapeDataString(request.Candidate.Guid)}/json?version={Uri.EscapeDataString(request.Candidate.LiteralVersion)}",
                cancellationToken)
            .ConfigureAwait(false);
        if (!packageRead.Succeeded)
            return ExactServiceApiSkillVerificationResult.Rejected(ServiceApiNoReliableSkillReason.ExactSkillReadFailed);

        if (!TryParseDetail(detailRead.Content, out var detail) ||
            !TryParsePackage(packageRead.Content, out var package))
        {
            return ExactServiceApiSkillVerificationResult.Rejected(ServiceApiNoReliableSkillReason.ExactSkillReadFailed);
        }

        return VerifyCandidateAgainstExactPackage(request.Candidate, detail, package);
    }

    private static async Task<NyxIdProxyTextResponse> ReadOrnnAsync(
        NyxIdApiClient client,
        string apiKey,
        string ornnApiUserServiceId,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.ProxyRequestBoundedWithApiKeyAsync(
                    apiKey,
                    OrnnApiServiceSlug,
                    ornnApiUserServiceId,
                    path,
                    HttpMethod.Get.Method,
                    body: null,
                    ExactOrnnReadMaxBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new NyxIdProxyTextResponse(
                Succeeded: false,
                Content: string.Empty,
                Detail: "ornn_exact_read_failed");
        }
    }

    private ExactServiceApiSkillVerificationResult? ValidateCandidateIdentity(
        ReliableServiceApiSkillCandidate candidate)
    {
        if (!Guid.TryParseExact(candidate.Guid, "D", out var parsedGuid) ||
            parsedGuid == Guid.Empty ||
            !string.Equals(parsedGuid.ToString("D"), candidate.Guid, StringComparison.Ordinal) ||
            !LiteralVersionPattern().IsMatch(candidate.LiteralVersion) ||
            string.IsNullOrWhiteSpace(candidate.CanonicalName) ||
            string.IsNullOrWhiteSpace(candidate.PublisherId))
        {
            return ExactServiceApiSkillVerificationResult.Rejected(
                ServiceApiNoReliableSkillReason.SkillIdentityMismatch);
        }

        if (!Sha256Pattern().IsMatch(candidate.SkillHash))
        {
            return ExactServiceApiSkillVerificationResult.Rejected(
                ServiceApiNoReliableSkillReason.SkillIntegrityMismatch);
        }

        return null;
    }

    private ExactServiceApiSkillVerificationResult VerifyCandidateAgainstExactPackage(
        ReliableServiceApiSkillCandidate candidate,
        OrnnExactSkillDetail detail,
        OrnnExactSkillPackage package)
    {
        if (!string.Equals(detail.Guid, candidate.Guid, StringComparison.Ordinal) ||
            !string.Equals(package.Version, candidate.LiteralVersion, StringComparison.Ordinal))
        {
            return ExactServiceApiSkillVerificationResult.Rejected(
                ServiceApiNoReliableSkillReason.SkillIdentityMismatch);
        }

        if (!string.Equals(detail.Name, candidate.CanonicalName, StringComparison.Ordinal) ||
            !string.Equals(package.Name, candidate.CanonicalName, StringComparison.Ordinal) ||
            !string.Equals(detail.SkillHash, candidate.SkillHash, StringComparison.Ordinal) ||
            !string.Equals(detail.CreatedBy, candidate.PublisherId, StringComparison.Ordinal) ||
            !EvidenceExists(candidate.Evidence, package.Files))
        {
            return ExactServiceApiSkillVerificationResult.Rejected(
                ServiceApiNoReliableSkillReason.SkillIntegrityMismatch);
        }

        var provenance = new ExactOrnnApiSkillProvenance
        {
            CanonicalName = candidate.CanonicalName,
            Guid = candidate.Guid,
            LiteralVersion = candidate.LiteralVersion,
            SkillHash = candidate.SkillHash,
            PublisherId = candidate.PublisherId,
        };
        provenance.Evidence.AddRange(candidate.Evidence.Select(static item => item.Clone()));

        return ExactServiceApiSkillVerificationResult.Verified(provenance);
    }

    private static bool EvidenceExists(
        IEnumerable<ExactOrnnApiSkillEvidence> evidence,
        IReadOnlyDictionary<string, string> files)
    {
        var skillMarkdownFiles = files
            .Where(static item => string.Equals(FileName(item.Key), "SKILL.md", StringComparison.Ordinal))
            .ToArray();
        if (skillMarkdownFiles.Length != 1 || string.IsNullOrWhiteSpace(skillMarkdownFiles[0].Value))
            return false;

        var seen = false;
        foreach (var locator in evidence)
        {
            if (string.IsNullOrWhiteSpace(locator.SkillFilePath) ||
                string.IsNullOrWhiteSpace(locator.Section) ||
                string.IsNullOrWhiteSpace(locator.OperationId) ||
                !files.TryGetValue(locator.SkillFilePath, out var content) ||
                string.IsNullOrWhiteSpace(content) ||
                !TryReadMarkdownSection(content, locator.Section, out var sectionContent) ||
                !ContainsOperationId(sectionContent, locator.OperationId))
            {
                return false;
            }

            seen = true;
        }

        return seen;
    }

    private static bool TryReadMarkdownSection(
        string markdown,
        string expectedSection,
        out string sectionContent)
    {
        sectionContent = string.Empty;
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sectionStart = -1;
        var sectionLevel = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            if (!TryReadMarkdownHeading(lines[index], out var headingLevel, out var heading))
                continue;

            if (sectionStart < 0)
            {
                if (!string.Equals(heading, expectedSection, StringComparison.Ordinal))
                    continue;

                sectionStart = index + 1;
                sectionLevel = headingLevel;
                continue;
            }

            if (headingLevel <= sectionLevel)
            {
                sectionContent = string.Join('\n', lines[sectionStart..index]);
                return true;
            }
        }

        if (sectionStart < 0)
            return false;

        sectionContent = string.Join('\n', lines[sectionStart..]);
        return true;
    }

    private static bool TryReadMarkdownHeading(
        string line,
        out int headingLevel,
        out string heading)
    {
        headingLevel = 0;
        heading = string.Empty;
        var match = MarkdownHeadingPattern().Match(line);
        if (!match.Success)
            return false;

        headingLevel = match.Groups["marks"].Length;
        heading = match.Groups["title"].Value;
        return heading.Length > 0;
    }

    private static bool ContainsOperationId(string sectionContent, string operationId)
    {
        var searchIndex = 0;
        while (searchIndex < sectionContent.Length)
        {
            var matchIndex = sectionContent.IndexOf(
                operationId,
                searchIndex,
                StringComparison.Ordinal);
            if (matchIndex < 0)
                return false;

            var beforeMatches = matchIndex == 0 ||
                                !IsOperationIdCharacter(sectionContent[matchIndex - 1]);
            var afterIndex = matchIndex + operationId.Length;
            var afterMatches = afterIndex == sectionContent.Length ||
                               !IsOperationIdCharacter(sectionContent[afterIndex]);
            if (beforeMatches && afterMatches)
                return true;

            searchIndex = matchIndex + operationId.Length;
        }

        return false;
    }

    private static bool IsOperationIdCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '_';

    private ManagedCodexCredentialDescriptor ValidateCredential(
        ManagedCodexCredentialDescriptor? credential,
        ExternalSubjectRef owner)
    {
        var ownerScopeKey = ManagedCodexCredentialActorIdentity.From(owner);
        try
        {
            if (credential?.Owner is null ||
                !string.Equals(
                    ManagedCodexCredentialActorIdentity.From(credential.Owner),
                    ownerScopeKey,
                    StringComparison.Ordinal) ||
                credential.Status != ManagedCodexCredentialStatus.Active ||
                credential.ExpiresAt is null ||
                credential.ExpiresAt.ToDateTimeOffset() <= _timeProvider.GetUtcNow() ||
                !string.Equals(
                    credential.ChronoSandboxServiceSlug,
                    ChronoSandboxServiceSlug,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    credential.OrnnApiServiceSlug,
                    OrnnApiServiceSlug,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(credential.ChronoSandboxUserServiceId) ||
                string.IsNullOrWhiteSpace(credential.ChronoLlmUserServiceId) ||
                string.IsNullOrWhiteSpace(credential.OrnnApiUserServiceId) ||
                new HashSet<string>(
                    [
                        credential.ChronoSandboxUserServiceId,
                        credential.ChronoLlmUserServiceId,
                        credential.OrnnApiUserServiceId,
                    ],
                    StringComparer.Ordinal).Count != 3 ||
                credential.SecretReference is null ||
                string.IsNullOrWhiteSpace(credential.SecretReference.Ref) ||
                !string.Equals(
                    credential.SecretReference.Purpose,
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    credential.SecretReference.OwnerScopeKey,
                    ownerScopeKey,
                    StringComparison.Ordinal) ||
                credential.SecretReference.Version <= 0 ||
                string.IsNullOrWhiteSpace(credential.SecretReference.Fingerprint))
            {
                throw new InvalidOperationException();
            }

            return credential;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("Managed Codex credential is invalid.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException("Managed Codex credential is invalid.", exception);
        }
    }

    private async Task<string> ResolveApiKeyAsync(
        ManagedCodexCredentialDescriptor credential,
        ExternalSubjectRef owner,
        CancellationToken cancellationToken)
    {
        var ownerScopeKey = ManagedCodexCredentialActorIdentity.From(owner);
        var reference = credential.SecretReference;
        ResolveSecretResult resolved;
        try
        {
            resolved = await _secretVault.ResolveAsync(
                    new ResolveSecretRequest(
                        reference.Ref,
                        CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                        ownerScopeKey,
                        ManagedCodexCredentialActorIdentity.SecretSubjectId,
                        "managed-service-api-skill-verification"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Managed Codex credential secret is unavailable.", exception);
        }

        if (!resolved.Resolved ||
            resolved.Reference is null ||
            string.IsNullOrWhiteSpace(resolved.Secret) ||
            !ReferenceMatches(resolved.Reference, reference))
        {
            throw new InvalidOperationException("Managed Codex credential secret is unavailable.");
        }

        return resolved.Secret;
    }

    private static ExternalSubjectRef ResolveOwner(ExactServiceApiSkillVerificationRequest request)
    {
        var callerId = request.Input.CallerId?.Trim();
        if (string.IsNullOrWhiteSpace(callerId))
            callerId = request.Access.CallerId?.Trim();
        if (string.IsNullOrWhiteSpace(callerId))
            throw new InvalidOperationException("A native NyxID caller identity is required for exact Ornn skill verification.");

        return new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = string.Empty,
            ExternalUserId = callerId,
        };
    }

    private static bool TryParseDetail(string content, out OrnnExactSkillDetail detail)
    {
        detail = default;
        try
        {
            using var document = JsonDocument.Parse(content);
            if (!TryGetDataObject(document.RootElement, out var data))
                return false;

            detail = new OrnnExactSkillDetail(
                ReadString(data, "guid"),
                ReadString(data, "name"),
                ReadString(data, "skillHash"),
                ReadString(data, "createdBy"));
            return !string.IsNullOrWhiteSpace(detail.Guid) &&
                   !string.IsNullOrWhiteSpace(detail.Name) &&
                   !string.IsNullOrWhiteSpace(detail.SkillHash) &&
                   !string.IsNullOrWhiteSpace(detail.CreatedBy);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParsePackage(string content, out OrnnExactSkillPackage package)
    {
        package = default;
        try
        {
            using var document = JsonDocument.Parse(content);
            if (!TryGetDataObject(document.RootElement, out var data) ||
                !data.TryGetProperty("files", out var filesElement) ||
                filesElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in filesElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    return false;
                files[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            package = new OrnnExactSkillPackage(
                ReadString(data, "name"),
                ReadString(data, "version"),
                files);
            return !string.IsNullOrWhiteSpace(package.Name) &&
                   !string.IsNullOrWhiteSpace(package.Version);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetDataObject(JsonElement root, out JsonElement data)
    {
        data = default;
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty("data", out data) &&
               data.ValueKind == JsonValueKind.Object;
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string FileName(string path)
    {
        var slashIndex = path.LastIndexOf('/');
        var backslashIndex = path.LastIndexOf('\\');
        var index = Math.Max(slashIndex, backslashIndex);
        return index < 0 ? path : path[(index + 1)..];
    }

    private static bool ReferenceMatches(SecretReference actual, SecretReference expected) =>
        string.Equals(actual.Ref, expected.Ref, StringComparison.Ordinal) &&
        string.Equals(actual.Purpose, expected.Purpose, StringComparison.Ordinal) &&
        string.Equals(actual.OwnerScopeKey, expected.OwnerScopeKey, StringComparison.Ordinal) &&
        actual.Version == expected.Version &&
        string.Equals(actual.Fingerprint, expected.Fingerprint, StringComparison.Ordinal);

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralVersionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(
        "^(?<marks>#{1,6})[ \\t]+(?<title>.*?)(?:[ \\t]+#+)?[ \\t]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownHeadingPattern();

    private readonly record struct OrnnExactSkillDetail(
        string Guid,
        string Name,
        string SkillHash,
        string CreatedBy);

    private readonly record struct OrnnExactSkillPackage(
        string Name,
        string Version,
        IReadOnlyDictionary<string, string> Files);
}
