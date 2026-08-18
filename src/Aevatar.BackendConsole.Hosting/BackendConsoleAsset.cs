using System.Reflection;

namespace Aevatar.BackendConsole.Hosting;

public sealed record BackendConsoleAsset(
    string LogicalName,
    Assembly Assembly,
    string ResourceSuffix,
    string ContentType,
    bool InjectHostConfiguration,
    string ConfigurationPlaceholder = "__BACKEND_CONSOLE_CONFIG__",
    BackendConsoleAssetConfigurationProfile ConfigurationProfile = BackendConsoleAssetConfigurationProfile.Full);

public enum BackendConsoleAssetConfigurationProfile
{
    Full = 0,
    AIAuthentication = 1,
    AuthenticationCallback = 2,
}
