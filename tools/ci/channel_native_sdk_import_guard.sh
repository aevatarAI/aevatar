#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# RFC §14.1 Layer 1 guard #1: forbid native vendor SDK imports from leaking
# into shared source trees.
#
# Business source must not `using` vendor SDK namespaces (Larksuite.Oapi /
# Telegram.Bot / SlackNet / Discord.*). Those SDKs are only allowed inside
# channel adapter projects under agents/channels/**.
#
# Scan:
#   - src/**/*.cs
#   - agents/Aevatar.GAgents.*/**/*.cs
# Exclude:
#   - agents/channels/**/*.cs (adapter-owned surface)

# Match plain, `global`, `static`, and alias `using` forms:
#   using Telegram.Bot;
#   global using Telegram.Bot;
#   using static Telegram.Bot;
#   using Tg = Telegram.Bot;
#   global using static Tg = Telegram.Bot;
forbidden_pattern='^[[:space:]]*(global[[:space:]]+)?using([[:space:]]+static)?[[:space:]]+([A-Za-z_][A-Za-z0-9_]*[[:space:]]*=[[:space:]]*)?(Larksuite\.Oapi|Telegram\.Bot|SlackNet|Discord\.[A-Za-z0-9_]+)'

violations=""

scan_root() {
  local root="$1"

  if [ ! -d "${root}" ]; then
    return 0
  fi

  while IFS= read -r file; do
    [ -z "${file}" ] && continue

    case "${file}" in
      */bin/*|*/obj/*)
        continue
        ;;
      agents/channels/*)
        continue
        ;;
    esac

    local hits
    hits="$(rg -n "${forbidden_pattern}" "${file}" || true)"
    if [ -n "${hits}" ]; then
      violations="${violations}
${file}
${hits}"
    fi
  done < <(rg --files "${root}" -g '*.cs' || true)
}

scan_root "src"

if [ -d "agents" ]; then
  while IFS= read -r gagent_dir; do
    [ -z "${gagent_dir}" ] && continue
    scan_root "${gagent_dir}"
  done < <(find agents -maxdepth 1 -type d -name 'Aevatar.GAgents.*' | sort)
fi

if [ -n "${violations}" ]; then
  printf '%s\n' "${violations}"
  echo "channel_native_sdk_import_guard: native vendor SDK imports are forbidden outside agents/channels/**."
  exit 1
fi

echo "channel_native_sdk_import_guard: ok"
