using System.Text.Json;
using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Services;

/// <summary>
/// Pure projection helpers shared by every host that turns a
/// <see cref="ServiceCatalogSnapshot"/> + <see cref="ServiceRevisionCatalogSnapshot"/>
/// pair into an HTTP-shaped endpoint contract response. Lives in
/// Abstractions because both the legacy scope-default route and the
/// member-first Studio route need exactly the same revision-selection,
/// stream-frame, and example-payload logic; the only thing that legitimately
/// differs between them is the invoke URL shape and the wrapping response
/// record. Anything that depends on URL identity stays at the host
/// boundary; anything pure lives here.
/// </summary>
public static class ServiceEndpointContractMath
{
    public const string StreamFrameFormatWorkflow = "workflow-run-event";
    public const string StreamFrameFormatAgui = "agui";
    public const string ImplementationKindWorkflow = "Workflow";
    public const string ImplementationKindStatic = "Static";
    public const string ImplementationKindScripting = "Scripting";

    public static readonly JsonSerializerOptions PrettyJsonSerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Picks the revision whose contract should be served for a given
    /// endpoint id. Prefers the default-serving revision, then the
    /// active-serving revision, then any revision that contains the
    /// endpoint, then the first revision overall as a last resort. The
    /// fallback to <c>revisions[0]</c> matches the legacy behavior — it
    /// keeps the response stable when the catalog is in a transient state
    /// (e.g. just after rollout, before serving rebinds).
    /// </summary>
    public static ServiceRevisionSnapshot? ResolveCurrentContractRevision(
        ServiceCatalogSnapshot service,
        ServiceRevisionCatalogSnapshot? revisions,
        string endpointId)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        if (revisions == null || revisions.Revisions.Count == 0)
            return null;

        foreach (var preferredRevisionId in EnumeratePreferredContractRevisionIds(service))
        {
            var preferredRevision = revisions.Revisions.FirstOrDefault(x =>
                string.Equals(x.RevisionId, preferredRevisionId, StringComparison.Ordinal) &&
                RevisionContainsEndpoint(x, endpointId));
            if (preferredRevision != null)
                return preferredRevision;
        }

        return revisions.Revisions.FirstOrDefault(x => RevisionContainsEndpoint(x, endpointId))
               ?? revisions.Revisions[0];
    }

    public static IEnumerable<string> EnumeratePreferredContractRevisionIds(ServiceCatalogSnapshot service)
    {
        ArgumentNullException.ThrowIfNull(service);

        var defaultRevisionId = NullIfEmpty(service.DefaultServingRevisionId);
        if (defaultRevisionId != null)
            yield return defaultRevisionId;

        var activeRevisionId = NullIfEmpty(service.ActiveServingRevisionId);
        if (activeRevisionId != null &&
            !string.Equals(activeRevisionId, defaultRevisionId, StringComparison.Ordinal))
        {
            yield return activeRevisionId;
        }
    }

    public static bool RevisionContainsEndpoint(ServiceRevisionSnapshot revision, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return revision.Endpoints.Any(endpoint =>
            string.Equals(endpoint.EndpointId, endpointId, StringComparison.Ordinal));
    }

    public static bool IsChatEndpoint(string? endpointKind) =>
        string.Equals(endpointKind?.Trim(), "chat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps (supportsSse, implementationKind) to the SSE frame format the
    /// frontend should decode. Workflow runs emit run-event frames; static
    /// and scripted runs emit AGUI frames; non-SSE endpoints have no frame
    /// format. Implementation-kind matching is case-insensitive because the
    /// snapshot's ImplementationKind is the proto enum's <c>.ToString()</c>
    /// and casing has shifted across versions.
    /// </summary>
    public static string? ResolveStreamFrameFormat(bool supportsSse, string? implementationKind)
    {
        if (!supportsSse)
            return null;

        if (string.Equals(implementationKind, ImplementationKindWorkflow, StringComparison.OrdinalIgnoreCase))
            return StreamFrameFormatWorkflow;

        if (string.Equals(implementationKind, ImplementationKindStatic, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(implementationKind, ImplementationKindScripting, StringComparison.OrdinalIgnoreCase))
        {
            return StreamFrameFormatAgui;
        }

        return null;
    }

    public static string? BuildTypedInvokeRequestExampleBody(string? requestTypeUrl, bool prettyPrinted)
    {
        var normalized = NullIfEmpty(requestTypeUrl);
        if (normalized == null)
            return null;

        return JsonSerializer.Serialize(
            new
            {
                payloadTypeUrl = normalized,
                payloadBase64 = BuildBase64PayloadPlaceholder(normalized),
            },
            prettyPrinted ? PrettyJsonSerializerOptions : null);
    }

    public static string BuildBase64PayloadPlaceholder(string requestTypeUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestTypeUrl);

        var typeName = requestTypeUrl
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return string.IsNullOrWhiteSpace(typeName)
            ? "<base64-encoded-protobuf-bytes>"
            : $"<base64-encoded-{typeName}-protobuf-bytes>";
    }

    public static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
