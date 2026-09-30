using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity.Broker;

internal static class OAuthBindingVerification
{
    internal enum IssuedBindingProbeResult
    {
        Usable,
        MissingRequiredAccess,
        Invalid,
        Unavailable,
    }

    internal static async Task<IssuedBindingProbeResult> ProbeIssuedBindingAsync(
        INyxIdCapabilityBroker capabilityBroker,
        ExternalSubjectRef subject,
        string bindingId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await capabilityBroker
                .IssueShortLivedByBindingIdAsync(
                    subject,
                    bindingId,
                    new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy },
                    ct)
                .ConfigureAwait(false);
            // The binding id itself is a bearer credential, so only its
            // irreversible digest prefix is correlatable from logs.
            logger.LogInformation(
                "New channel NyxID binding cleared the configured runtime floor. probe_result={ProbeResult}, binding_digest={BindingDigest}",
                nameof(IssuedBindingProbeResult.Usable),
                BindingDigest(bindingId));
            return IssuedBindingProbeResult.Usable;
        }
        catch (BindingScopeMismatchException ex)
        {
            logger.LogInformation(
                ex,
                "New channel NyxID binding lacks the required proxy scope. probe_result={ProbeResult}, binding_digest={BindingDigest}",
                nameof(IssuedBindingProbeResult.MissingRequiredAccess),
                BindingDigest(bindingId));
            return IssuedBindingProbeResult.MissingRequiredAccess;
        }
        catch (BindingServiceAccessMismatchException ex)
        {
            logger.LogInformation(
                ex,
                "New channel NyxID binding lacks one or more required services. probe_result={ProbeResult}, binding_digest={BindingDigest}, unsatisfied_resource_count={UnsatisfiedResourceCount}",
                nameof(IssuedBindingProbeResult.MissingRequiredAccess),
                BindingDigest(bindingId),
                ex.RequiredResources.Count);
            return IssuedBindingProbeResult.MissingRequiredAccess;
        }
        catch (BindingRevokedException ex)
        {
            logger.LogWarning(
                ex,
                "New channel NyxID binding was already revoked before adoption. probe_result={ProbeResult}, binding_digest={BindingDigest}",
                nameof(IssuedBindingProbeResult.Invalid),
                BindingDigest(bindingId));
            return IssuedBindingProbeResult.Invalid;
        }
        catch (BindingNotFoundException ex)
        {
            logger.LogWarning(
                ex,
                "New channel NyxID binding was not found before adoption. probe_result={ProbeResult}, binding_digest={BindingDigest}",
                nameof(IssuedBindingProbeResult.Invalid),
                BindingDigest(bindingId));
            return IssuedBindingProbeResult.Invalid;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "New channel NyxID binding could not be verified before adoption. probe_result={ProbeResult}, binding_digest={BindingDigest}",
                nameof(IssuedBindingProbeResult.Unavailable),
                BindingDigest(bindingId));
            return IssuedBindingProbeResult.Unavailable;
        }
    }

    internal static string BindingDigest(string bindingId) =>
        NyxIdRemoteCapabilityBroker.BindingDigest(bindingId);

    internal static string? ResolveOwnerScopeId(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("uid", out var uid) &&
                uid.ValueKind == System.Text.Json.JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(uid.GetString()))
            {
                return uid.GetString()!.Trim();
            }

            if (doc.RootElement.TryGetProperty("sub", out var sub) &&
                sub.ValueKind == System.Text.Json.JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(sub.GetString()))
            {
                return sub.GetString()!.Trim();
            }
        }
        catch (FormatException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        return null;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

}
