#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# Interactive reply abstraction guard (ADR-0014):
#
# Outbound rich interactions must flow through a composer + dispatcher; raw Lark 2.0 card
# JSON literals (msg_type=interactive / schema=2.0 / tag=button / tag=form / tag=input)
# inside agents/Aevatar.GAgents.Authoring or agents/Aevatar.GAgents.NyxidChat are a
# regression signal that someone sidestepped the composer.
#
# Scan:
#   - agents/Aevatar.GAgents.Authoring/**/*.cs
#   - agents/Aevatar.GAgents.NyxidChat/**/*.cs
# Exclude:
#   - */bin/*, */obj/*
#
# Lark composer ownership boundary lives under agents/channels/Aevatar.GAgents.Channel.Lark
# and is intentionally not scanned here.

forbidden_patterns=(
  '"msg_type"[[:space:]]*:[[:space:]]*"interactive"'
  'msg_type[[:space:]]*=[[:space:]]*"interactive"'
  '"schema"[[:space:]]*:[[:space:]]*"2\.0"'
  'schema[[:space:]]*=[[:space:]]*"2\.0"'
  '"tag"[[:space:]]*:[[:space:]]*"button"'
  'tag[[:space:]]*=[[:space:]]*"button"'
  '"tag"[[:space:]]*:[[:space:]]*"form"'
  'tag[[:space:]]*=[[:space:]]*"form"'
  '"tag"[[:space:]]*:[[:space:]]*"input"'
  'tag[[:space:]]*=[[:space:]]*"input"'
)

violations=""

# Files that still build Lark card JSON directly. Tracked for migration through the composer;
# the guard grandfathers them so new code cannot add further offenders while this list shrinks.
# TODO(#350-followup): migrate AgentBuilderCardFlow onto IChannelMessageComposerRegistry then drop the allowlist entry.
allowlist=(
  "agents/Aevatar.GAgents.Authoring/AgentBuilderCardFlow.cs"
)

is_allowlisted() {
  local file="$1"
  for entry in "${allowlist[@]}"; do
    if [ "${file}" = "${entry}" ]; then
      return 0
    fi
  done
  return 1
}

scan_project() {
  local project_root="$1"

  if [ ! -d "${project_root}" ]; then
    return 0
  fi

  while IFS= read -r file; do
    [ -z "${file}" ] && continue

    case "${file}" in
      */bin/*|*/obj/*)
        continue
        ;;
    esac

    if is_allowlisted "${file}"; then
      continue
    fi

    for pattern in "${forbidden_patterns[@]}"; do
      local hits
      hits="$(rg -n "${pattern}" "${file}" || true)"
      if [ -n "${hits}" ]; then
        violations="${violations}
${file}
${hits}"
      fi
    done
  done < <(rg --files "${project_root}" -g '*.cs' || true)
}

scan_project "agents/Aevatar.GAgents.Authoring"
scan_project "agents/Aevatar.GAgents.NyxidChat"

if [ -n "${violations}" ]; then
  printf '%s\n' "${violations}"
  echo "channel_card_literal_guard: raw Lark card JSON literals detected outside the composer. Route card construction through IChannelMessageComposerRegistry / LarkMessageComposer instead."
  exit 1
fi

echo "channel_card_literal_guard: ok"
