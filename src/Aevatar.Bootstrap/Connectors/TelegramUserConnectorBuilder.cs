using System.Globalization;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Connectors;

/// <summary>
/// Builds in-process Telegram user-account (MTProto) connectors.
/// </summary>
public sealed class TelegramUserConnectorBuilder : IConnectorBuilder
{
    public string Type => "telegram_user";

    public bool TryBuild(ConnectorConfigEntry entry, ILogger logger, out IConnector? connector)
    {
        connector = null;
        var cfg = entry.TelegramUser;

        if (!int.TryParse(cfg.ApiId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var apiId) || apiId <= 0)
        {
            logger.LogWarning("Skip connector {Name}: telegramUser.apiId is required for type telegram_user", entry.Name);
            return false;
        }

        if (string.IsNullOrWhiteSpace(cfg.ApiHash))
        {
            logger.LogWarning("Skip connector {Name}: telegramUser.apiHash is required for type telegram_user", entry.Name);
            return false;
        }

        var sessionPath = ResolveSessionPath(entry.Name, cfg.SessionPath);
        connector = new TelegramUserConnector(
            entry.Name,
            apiId,
            cfg.ApiHash.Trim(),
            cfg.PhoneNumber?.Trim() ?? string.Empty,
            cfg.VerificationCode?.Trim() ?? string.Empty,
            cfg.Password?.Trim() ?? string.Empty,
            sessionPath,
            cfg.DeviceModel?.Trim() ?? string.Empty,
            cfg.SystemVersion?.Trim() ?? string.Empty,
            cfg.AppVersion?.Trim() ?? string.Empty,
            cfg.SystemLangCode?.Trim() ?? string.Empty,
            cfg.LangCode?.Trim() ?? string.Empty,
            cfg.AllowedOperations,
            entry.TimeoutMs,
            logger);

        return true;
    }

    private static string ResolveSessionPath(string connectorName, string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var trimmed = configuredPath.Trim();
            if (Path.IsPathRooted(trimmed))
                return trimmed;
            return Path.Combine(AevatarPaths.Root, trimmed);
        }

        var safeName = string.Concat(
            (connectorName ?? "default")
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        return Path.Combine(AevatarPaths.Root, "telegram-user", $"{safeName}.session");
    }
}
