using System.Reflection;

namespace Aevatar.BackendConsole.Hosting;

public sealed record BackendConsoleAsset(
    string LogicalName,
    Assembly Assembly,
    string ResourceSuffix,
    string ContentType,
    bool InjectHostConfiguration);
