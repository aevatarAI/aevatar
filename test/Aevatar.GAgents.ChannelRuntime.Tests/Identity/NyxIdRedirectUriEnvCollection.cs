using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Serializes test classes that mutate <c>AEVATAR_OAUTH_REDIRECT_BASE_URL</c>.
/// xUnit runs test classes in parallel collections by default; two classes
/// each saving / overwriting / restoring the same process-wide env var
/// could race and restore each other's stale values, producing flaky
/// redirect-URI assertions.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NyxIdRedirectUriEnvCollection
{
    public const string Name = "NyxIdRedirectUriEnv";
}
