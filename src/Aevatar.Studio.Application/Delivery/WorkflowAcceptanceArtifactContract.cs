using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.GAgents.ContentArtifacts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Delivery;

internal static class WorkflowAcceptanceArtifactContract
{
    public const string Kind = "structured_document";
    public const string MediaType = "application/json";
    public const string Classification = "internal";
    public const string PrincipalKind = "user";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static WorkflowAcceptanceArtifactIdentity BuildIdentity(
        WorkflowDeliverySnapshot delivery,
        string runId)
    {
        var installation = delivery.Installation
                           ?? throw new InvalidOperationException("Workflow installation is required.");
        var normalizedRunId = NormalizeRequired(runId, nameof(runId));
        var dedupKey =
            $"workflow-delivery/{NormalizeRequired(delivery.DeliveryId, nameof(delivery.DeliveryId))}" +
            $"/installation/{NormalizeRequired(installation.InstallationId, nameof(installation.InstallationId))}" +
            $"/run/{normalizedRunId}/acceptance-output";
        var artifactId = ContentArtifactConventions.BuildArtifactId(installation.ScopeId, dedupKey);
        return new WorkflowAcceptanceArtifactIdentity(
            dedupKey,
            artifactId,
            ContentArtifactConventions.BuildRevisionId(artifactId, 1));
    }

    public static ContentArtifactPrincipalContract ResolveOwner(WorkflowInstallationSnapshot installation)
    {
        var principalId = installation.AuthenticatedOwner?.Owner?.OwnerSubject;
        return new ContentArtifactPrincipalContract(
            NormalizeRequired(principalId, "installation.authenticatedOwner.owner.ownerSubject"),
            PrincipalKind);
    }

    public static WorkflowAcceptanceOutputValidation ValidateOutput(
        string expectedWorkflowName,
        string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return WorkflowAcceptanceOutputValidation.Invalid(
                "acceptance_output_missing",
                "The completed acceptance run did not produce an output document.");
        }

        byte[] content;
        try
        {
            content = StrictUtf8.GetBytes(output);
        }
        catch (EncoderFallbackException)
        {
            return WorkflowAcceptanceOutputValidation.Invalid(
                "acceptance_output_invalid_utf8",
                "The completed acceptance run output is not valid UTF-8.");
        }
        return ValidateContent(expectedWorkflowName, content);
    }

    public static WorkflowAcceptanceOutputValidation ValidateContent(
        string expectedWorkflowName,
        byte[]? content)
    {
        if (content == null || content.Length == 0)
        {
            return WorkflowAcceptanceOutputValidation.Invalid(
                "acceptance_output_missing",
                "The acceptance artifact content is empty.");
        }
        if (content.Length > ContentArtifactConventions.MaxInlineContentBytes)
        {
            return WorkflowAcceptanceOutputValidation.Invalid(
                "acceptance_output_too_large",
                $"The acceptance output exceeds the {ContentArtifactConventions.MaxInlineContentBytes}-byte evidence limit.");
        }

        try
        {
            _ = StrictUtf8.GetString(content);
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return InvalidContract("The acceptance output must be a JSON object.");

            var root = document.RootElement;
            if (!root.TryGetProperty("workflow", out var workflow) ||
                workflow.ValueKind != JsonValueKind.String ||
                !string.Equals(workflow.GetString(), expectedWorkflowName, StringComparison.Ordinal))
            {
                return InvalidContract("The acceptance output workflow identity does not match the delivered package.");
            }
            if (!root.TryGetProperty("mode", out var mode) ||
                mode.ValueKind != JsonValueKind.String ||
                !string.Equals(mode.GetString(), "preview", StringComparison.Ordinal))
            {
                return InvalidContract("The acceptance output must be produced in preview mode.");
            }
            if (!root.TryGetProperty("side_effects", out var sideEffects) ||
                sideEffects.ValueKind != JsonValueKind.False)
            {
                return InvalidContract("The acceptance output must prove boolean side_effects=false.");
            }
        }
        catch (JsonException)
        {
            return InvalidContract("The acceptance output is not valid JSON.");
        }
        catch (DecoderFallbackException)
        {
            return WorkflowAcceptanceOutputValidation.Invalid(
                "acceptance_output_invalid_utf8",
                "The acceptance artifact content is not valid UTF-8.");
        }

        return WorkflowAcceptanceOutputValidation.Valid(
            content.ToArray(),
            Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    private static WorkflowAcceptanceOutputValidation InvalidContract(string message) =>
        WorkflowAcceptanceOutputValidation.Invalid(
            "acceptance_output_contract_invalid",
            message);

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0
            ? throw new InvalidOperationException($"{name} is required.")
            : normalized;
    }
}

internal sealed record WorkflowAcceptanceArtifactIdentity(
    string DedupKey,
    string ArtifactId,
    string RevisionId);

internal sealed record WorkflowAcceptanceOutputValidation(
    bool IsValid,
    byte[]? Content,
    string? ContentHash,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static WorkflowAcceptanceOutputValidation Valid(byte[] content, string contentHash) =>
        new(true, content, contentHash, null, null);

    public static WorkflowAcceptanceOutputValidation Invalid(string code, string message) =>
        new(false, null, null, code, message);
}
