#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

allowed_document_files_regex='^(agents/Aevatar\.GAgents\.Channel\.Identity/(DependencyInjection/IdentityServiceCollectionExtensions\.cs|Provisioning/AevatarOAuthClient(Document\.Partial|DocumentMetadataProvider|EsAclOptions|EsAclStartupGuard|ProjectionProvider|Projector)\.cs|protos/aevatar_oauth_client\.proto)|src/Aevatar\.Mainnet\.Host\.Api/Hosting/(MainnetAgentProjectionDocumentStoresExtensions|HttpOAuthClientEsAclProbe)\.cs|test/)'

document_hits="$(
  rg -n "AevatarOAuthClientDocument|IProjectionDocumentReader<AevatarOAuthClientDocument|IProjectionDocumentWriter<AevatarOAuthClientDocument" \
    agents src \
    -g '*.cs' \
    -g '*.proto' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    | awk -F: -v allowed="${allowed_document_files_regex}" '$1 !~ allowed { print }' \
    || true
)"

event_store_hits="$(
  rg -l "AevatarOAuthClient" \
    agents src \
    -g '*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    | xargs rg -n "IEventStore|ReadEventsAsync|Replay|EventStore" \
    | rg -v "agents/Aevatar.GAgents.Channel.Identity/Provisioning/AevatarOAuthClientGAgent.cs:.*EventStoreOptimisticConcurrencyException" \
    | rg -v "GrantMatchesGrainEventStoreInternal|grain/event-store internal|AevatarOAuthClient ES ACL startup guard" \
    | rg -v "MainnetHostBuilderExtensions.cs:.*IdentityAssertionReplayGuard" \
    || true
)"

guard_registration_hits="$(
  rg -n "AevatarOAuthClientEsAclStartupGuard|GrantMatchesGrainEventStoreInternal|AevatarOAuthClientEsAclOptions" \
    src/Aevatar.Mainnet.Host.Api/Hosting/MainnetAgentProjectionDocumentStoresExtensions.cs \
    src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs \
    agents/Aevatar.GAgents.Channel.Identity/Provisioning/AevatarOAuthClientEsAclStartupGuard.cs \
    agents/Aevatar.GAgents.Channel.Identity/Provisioning/AevatarOAuthClientProjectionProvider.cs \
    agents/Aevatar.GAgents.Channel.Identity/Provisioning/AevatarOAuthClientProjector.cs \
    || true
)"

if [[ -n "${document_hits}" ]]; then
  echo "${document_hits}"
  echo "AevatarOAuthClientDocument carries HMAC keys. Only the projector, internal provider, metadata, startup guard, Mainnet Host composition root, proto, and tests may reference it."
  exit 1
fi

if [[ -n "${event_store_hits}" ]]; then
  echo "${event_store_hits}"
  echo "AevatarOAuthClient HMAC-key facts must not be read from event store by query/API/projection side paths."
  exit 1
fi

if ! grep -q "AevatarOAuthClientEsAclStartupGuard" <<<"${guard_registration_hits}" ||
   ! grep -q "GrantMatchesGrainEventStoreInternal" <<<"${guard_registration_hits}" ||
   ! grep -q "AevatarOAuthClientEsAclOptions" <<<"${guard_registration_hits}"; then
  echo "${guard_registration_hits}"
  echo "Mainnet Host must own the AevatarOAuthClient ES ACL startup guard and operator-configurable enforcement policy."
  exit 1
fi

echo "AevatarOAuthClient ES ACL guard passed."
