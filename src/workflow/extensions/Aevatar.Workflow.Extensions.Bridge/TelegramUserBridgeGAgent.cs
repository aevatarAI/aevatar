using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Extensions.Bridge;

/// <summary>
/// Telegram user-account bridge agent.
/// Uses the same protocol as <see cref="TelegramBridgeGAgent"/>, but defaults to connector name "telegram_user".
/// </summary>
public sealed class TelegramUserBridgeGAgent : TelegramBridgeGAgent
{
    protected override string DefaultConnectorName => "telegram_user";

    public TelegramUserBridgeGAgent(
        IActorRuntime runtime,
        IBridgeCallbackTokenService tokenService,
        IConnectorRegistry connectorRegistry)
        : base(runtime, tokenService, connectorRegistry)
    {
    }
}
