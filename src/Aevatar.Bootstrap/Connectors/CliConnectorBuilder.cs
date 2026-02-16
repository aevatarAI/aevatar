using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Core.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Connectors;

public sealed class CliConnectorBuilder : IConnectorBuilder
{
    public string Type => "cli";

    public bool TryBuild(ConnectorConfigEntry entry, ILogger logger, out IConnector? connector)
    {
        connector = null;
        if (string.IsNullOrWhiteSpace(entry.Cli.Command))
        {
            logger.LogWarning("Skip connector {Name}: cli.command is required", entry.Name);
            return false;
        }

        if (entry.Cli.Command.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Skip connector {Name}: cli.command must be a preinstalled command", entry.Name);
            return false;
        }

        connector = new CliConnector(
            entry.Name,
            entry.Cli.Command,
            entry.Cli.FixedArguments,
            entry.Cli.AllowedOperations,
            entry.Cli.AllowedInputKeys,
            entry.Cli.WorkingDirectory,
            entry.Cli.Environment,
            entry.TimeoutMs);
        return true;
    }
}
