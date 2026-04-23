#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "${repo_root}"

if rg -n "CreateAsync<NyxIdChatGAgent>|GetAsync<NyxIdChatGAgent>" \
  agents/Aevatar.GAgents.ChannelRuntime \
  agents/channels
then
  echo "Channel relay/runtime code must not talk to NyxIdChatGAgent directly. Go through ConversationGAgent + deferred LLM reply pipeline."
  exit 1
fi

relay_method_body="$(
  awk '
    /HandleRelayWebhookAsync\(/ { capture=1 }
    capture {
      print
      opened += gsub(/\{/, "{")
      closed += gsub(/\}/, "}")
      if (opened > 0 && opened == closed) {
        exit
      }
    }
  ' agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.cs
)"

if printf "%s\n" "${relay_method_body}" | rg -n "CreateAsync<NyxIdChatGAgent>|GetAsync<NyxIdChatGAgent>"; then
  echo "Nyx relay webhook handler must not create or query NyxIdChatGAgent directly. Dispatch ChatActivity into ConversationGAgent instead."
  exit 1
fi

echo "Channel relay NyxIdChat direct-create guard passed."
