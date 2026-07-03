namespace Aevatar.Audit.Core.Identity;

public static class AuditCanonicalActorKeys
{
    public static string ForNyxIdUser(string subject)
    {
        return Build("nyxid", subject);
    }

    public static string ForChannelSender(string platform, string registrationScopeId, string senderId)
    {
        return Build("channel", platform, registrationScopeId, senderId);
    }

    public static string ForSystem()
    {
        return "system";
    }

    public static string ForSchedule(string scheduleId)
    {
        return Build("schedule", scheduleId);
    }

    public static string ForService(string scopeId, string serviceId)
    {
        return Build("service", scopeId, serviceId);
    }

    private static string Build(string prefix, params string[] segments)
    {
        var normalizedSegments = segments.Select(NormalizeSegment).ToArray();
        return string.Join(':', new[] { prefix }.Concat(normalizedSegments));
    }

    private static string NormalizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Canonical actor key segment is required.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Canonical actor key segments cannot contain ':'.", nameof(value));
        }

        return trimmed;
    }
}
