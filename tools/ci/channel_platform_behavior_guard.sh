#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

relay="agents/channels/Aevatar.GAgents.Channel.NyxIdRelay"
runtime="agents/Aevatar.GAgents.Channel.Runtime"
abstractions="agents/Aevatar.GAgents.Channel.Abstractions"
runner="agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs"
tool="src/Aevatar.AI.ToolProviders.ChannelAdmin/ChannelRegistrationTool.cs"

if rg -n 'INyxChannelBotProvisioningService|BuildProvisioningServiceMap|NyxLarkProvisioningService|NyxTelegramProvisioningService|IChannelRegistrationBotConnectionPort' "$relay" "$runtime" src/Aevatar.AI.ToolProviders.ChannelAdmin -g '*.cs' -g '!obj/**' -g '!bin/**'; then
    echo "Channel registration must use one neutral existing-Bot adoption service, without legacy provisioners or service preparation."
    exit 1
fi

if rg -n 'CreateChannelBotAsync|DeleteChannelBotAsync|CreateServiceAsync|DeleteServiceAsync' "$relay" -g '*.cs' -g '!obj/**' -g '!bin/**'; then
    echo "Channel registration and cleanup must not create/delete NyxID-owned Bots or UserServices."
    exit 1
fi

if rg -n 'IChannelInboundAdmissionPolicy|IChannelPlatformAdapter' agents src -g '*.cs' -g '!**/obj/**' -g '!**/bin/**'; then
    echo "Fixed authentication and message integrity cannot be delegated to a catch-all platform adapter."
    exit 1
fi

if rg -n 'SupportedPlatforms|CanonicalPlatforms' "$runtime/ChannelBotRegistrationGAgent.cs" src/Aevatar.Foundation.Abstractions/OwnerScope.Partial.cs; then
    echo "Actor and shared OwnerScope must not maintain platform catalogs."
    exit 1
fi

if rg -n 'Capabilities\??\.Supports|capabilities\.Supports' "$runner" "$relay/NyxIdRelayTransport.cs" agents/platforms/Aevatar.GAgents.Platform.Telegram/TelegramMessageComposer.cs agents/platforms/Aevatar.GAgents.Platform.Lark/LarkMessageComposer.cs; then
    echo "Channel behavior must be selected by implemented interfaces, not diagnostic capability booleans."
    exit 1
fi

if rg -n 'IsLark|TrySendImmediateLark|LarkJsonTableFormatter|string.Equals\([^;]*("lark"|"telegram")' "$runner" "$relay/NyxIdRelayTransport.cs"; then
    echo "The fixed Relay and turn paths must resolve narrow behaviors instead of branching on platform names."
    exit 1
fi

if rg -n 'PlatformServiceBinding|nyx_user_service_id' "$runtime/protos/channel_bot_registration.proto"; then
    echo "Registration must not acquire a platform UserService binding."
    exit 1
fi

if ! rg -q 'ChannelPlatformId' "$runtime/ChannelRegistrationAuthorizationContract.cs"; then
    echo "Registration validation must use the shared canonical platform contract."
    exit 1
fi

echo "Channel platform behavior guards passed."
