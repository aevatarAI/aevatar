using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;

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
        IConnectorRegistry connectorRegistry)
        : base(runtime, connectorRegistry)
    {
    }
}
