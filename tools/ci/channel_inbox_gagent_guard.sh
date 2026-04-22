#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# RFC §14.1 Layer 1 guard #3: forbid technical-function inbox actors.
#
# Durable inbox semantics must ride the Orleans Streams persistent-provider
# path. Introducing a dedicated InboxGAgent / ChannelInboxGAgent actor would
# regress to a technical-function actor, violating CLAUDE.md "Actor 即业务实体"
# and aevatar-channel-architecture.md §14.1.
#
# Scan the whole repo for class declarations named *InboxGAgent and reject any
# new ones. The docs/ tree is allowed to discuss the pattern.

forbidden_names=(
  'InboxGAgent'
  'ChannelInboxGAgent'
)

violations=""

for name in "${forbidden_names[@]}"; do
  pattern="(class|record|struct|interface)[[:space:]]+${name}\\b"
  while IFS= read -r hit; do
    [ -z "${hit}" ] && continue
    violations="${violations}${hit}
"
  done < <(
    rg -n "${pattern}" \
      --glob '*.cs' \
      --glob '!**/bin/**' \
      --glob '!**/obj/**' \
      --glob '!docs/**' \
      --glob '!tools/ci/channel_inbox_gagent_guard.sh' \
      || true
  )
done

if [ -n "${violations}" ]; then
  printf '%s' "${violations}"
  echo "channel_inbox_gagent_guard: technical-function InboxGAgent / ChannelInboxGAgent actors are forbidden."
  exit 1
fi

echo "channel_inbox_gagent_guard: ok"
