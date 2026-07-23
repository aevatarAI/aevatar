using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public abstract class AgentProfileApplicationException : AgentProfileBoundaryException
{
    protected AgentProfileApplicationException(
        string code,
        IReadOnlyList<AgentProfileSafeDiagnostic>? diagnostics = null)
        : base(code, diagnostics)
    {
    }
}

public sealed class AgentProfileRequestException : AgentProfileApplicationException
{
    public AgentProfileRequestException(
        string code,
        IReadOnlyList<AgentProfileSafeDiagnostic>? diagnostics = null)
        : base(code, diagnostics)
    {
    }
}

public sealed class AgentProfileNotFoundException : AgentProfileApplicationException
{
    public AgentProfileNotFoundException()
        : base("AGENT_PROFILE_NOT_FOUND")
    {
    }
}

public sealed class AgentProfilePreconditionException : AgentProfileApplicationException
{
    public AgentProfilePreconditionException(
        long expectedAuthorityStateVersion,
        long observedAuthorityStateVersion)
        : base("AGENT_PROFILE_STALE_VERSION")
    {
        ExpectedAuthorityStateVersion = expectedAuthorityStateVersion;
        ObservedAuthorityStateVersion = observedAuthorityStateVersion;
    }

    public long ExpectedAuthorityStateVersion { get; }

    public long ObservedAuthorityStateVersion { get; }
}

public sealed class AgentProfileAuthenticationRequiredException : AgentProfileApplicationException
{
    public AgentProfileAuthenticationRequiredException(
        IReadOnlyList<AgentProfileSafeDiagnostic> diagnostics)
        : base("ORNN_ACCESS_TOKEN_REQUIRED", diagnostics)
    {
    }
}

public sealed class AgentProfilePublishValidationException : AgentProfileApplicationException
{
    public AgentProfilePublishValidationException(
        IReadOnlyList<AgentProfileSafeDiagnostic> diagnostics)
        : base(FirstCode(diagnostics, "AGENT_PROFILE_PUBLISH_INVALID"), diagnostics)
    {
    }

    private static string FirstCode(
        IReadOnlyList<AgentProfileSafeDiagnostic>? diagnostics,
        string fallback) =>
        diagnostics?.FirstOrDefault(static diagnostic =>
            !string.IsNullOrWhiteSpace(diagnostic.Code))?.Code ?? fallback;
}

public sealed class AgentProfileDependencyUnavailableException : AgentProfileApplicationException
{
    public AgentProfileDependencyUnavailableException(
        IReadOnlyList<AgentProfileSafeDiagnostic> diagnostics)
        : base("ORNN_DEPENDENCY_UNAVAILABLE", diagnostics)
    {
    }
}

public sealed class AgentProfileDispatchRejectedException : AgentProfileApplicationException
{
    public AgentProfileDispatchRejectedException()
        : base("AGENT_PROFILE_DISPATCH_REJECTED")
    {
    }
}
