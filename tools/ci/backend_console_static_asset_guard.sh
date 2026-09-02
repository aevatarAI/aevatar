#!/usr/bin/env bash
#
# Backend console static asset guard.
#
# The internal backend console stays zero-build: static shells are embedded .html assets,
# host facts are injected from appsettings/options at serve time, and console page endpoints
# do not introduce a wwwroot or frontend build chain.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

ROOT="${REPO_ROOT}"
if [[ "${1:-}" == "--scan" ]] && [[ -n "${2:-}" ]]; then
  ROOT="$(cd -- "$2" && pwd)"
fi

violations=0

asset_files=(
  "src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html"
  "src/Aevatar.Mainnet.Host.Api/BackendConsole/auto-callback.html"
  "src/Aevatar.Mainnet.Host.Api/Status/status.html"
  "src/Aevatar.Mainnet.Host.Api/Cqrs/cqrs-observatory.html"
  "src/Aevatar.Mainnet.Host.Api/Voice/voice-console.html"
  "src/Aevatar.Mainnet.Host.Api/Skills/workflow-skills.html"
  "src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/workflow-observatory.html"
  "src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/workflow-studio.html"
  "agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/channels.html"
)

config_asset_files=(
  "src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html"
  "src/Aevatar.Mainnet.Host.Api/BackendConsole/auto-callback.html"
  "src/Aevatar.Mainnet.Host.Api/Cqrs/cqrs-observatory.html"
  "src/Aevatar.Mainnet.Host.Api/Voice/voice-console.html"
  "src/Aevatar.Mainnet.Host.Api/Skills/workflow-skills.html"
  "src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/workflow-observatory.html"
  "src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/workflow-studio.html"
  "agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/channels.html"
)

old_carriers=(
  "src/Aevatar.Mainnet.Host.Api/BackendConsole/AutoConsoleCallbackPage.cs"
  "src/Aevatar.Mainnet.Host.Api/Status/StatusHtml.cs"
  "src/Aevatar.Mainnet.Host.Api/Cqrs/CqrsObservatoryPage.cs"
  "src/Aevatar.Mainnet.Host.Api/Voice/VoiceConsolePage.cs"
  "src/Aevatar.Mainnet.Host.Api/Skills/WorkflowSkillsPage.cs"
  "src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/WorkflowRunObservatoryPage.cs"
  "src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/WorkflowStudioPage.cs"
  "agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelsPage.cs"
)

pure_static_endpoint_files=(
  "src/Aevatar.Mainnet.Host.Api/BackendConsole/AdminConsoleEndpoints.cs"
  "src/Aevatar.Mainnet.Host.Api/BackendConsole/AutoConsoleCallbackEndpoints.cs"
  "src/Aevatar.Mainnet.Host.Api/Cqrs/CqrsObservatoryPageEndpoints.cs"
  "src/Aevatar.Mainnet.Host.Api/Voice/VoiceConsoleEndpoints.cs"
  "src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/WorkflowStudioEndpoints.cs"
  "agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelsEndpoints.cs"
)

project_files=(
  "src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj"
  "src/workflow/Aevatar.Workflow.Infrastructure/Aevatar.Workflow.Infrastructure.csproj"
  "agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/Aevatar.GAgents.Channel.NyxIdRelay.csproj"
)

for file in "${asset_files[@]}"; do
  path="${ROOT}/${file}"
  if [[ ! -f "${path}" ]]; then
    echo "${file}: expected backend console embedded asset file."
    violations=$((violations + 1))
    continue
  fi

  host_fact_hits="$(rg -n -P 'https://nyx\.chrono-ai\.fun|https://nyx-api\.chrono-ai\.fun|37a93189-2734-406e-bca1-7dbdf25c5a53|aevatar-console:nyxid:pkce|openid profile email proxy' "${path}" || true)"
  if [[ -n "${host_fact_hits}" ]]; then
    echo "${host_fact_hits}"
    echo "${file}: backend console page assets must not hardcode Nyx/OIDC host facts."
    violations=$((violations + 1))
  fi

  build_hits="$(rg -n -P '\b(wwwroot|npm|pnpm|yarn|webpack|vite|node_modules)\b|(?:^|[/"'"'"'[:space:]])(?:rollup\.config|rollup-plugin|@rollup/|rollup/dist)\b' "${path}" || true)"
  if [[ -n "${build_hits}" ]]; then
    echo "${build_hits}"
    echo "${file}: backend console page assets must stay zero-build and not reference wwwroot/build tooling."
    violations=$((violations + 1))
  fi
done

for file in "${config_asset_files[@]}"; do
  path="${ROOT}/${file}"
  if [[ -f "${path}" ]] && ! rg -q -F '__BACKEND_CONSOLE_CONFIG__' "${path}"; then
    echo "${file}: expected __BACKEND_CONSOLE_CONFIG__ host configuration placeholder."
    violations=$((violations + 1))
  fi
done

for file in "${old_carriers[@]}"; do
  if [[ -f "${ROOT}/${file}" ]]; then
    echo "${file}: old C# raw-string page carrier must not exist."
    violations=$((violations + 1))
  fi
done

raw_string_hits="$(rg -n -P 'const\s+string\s+(Html|Page)\s*=' \
  "${ROOT}/src/Aevatar.Mainnet.Host.Api/BackendConsole" \
  "${ROOT}/src/Aevatar.Mainnet.Host.Api/Status" \
  "${ROOT}/src/Aevatar.Mainnet.Host.Api/Cqrs" \
  "${ROOT}/src/Aevatar.Mainnet.Host.Api/Voice" \
  "${ROOT}/src/Aevatar.Mainnet.Host.Api/Skills" \
  "${ROOT}/src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi" \
  "${ROOT}/agents/channels/Aevatar.GAgents.Channel.NyxIdRelay" \
  -g '*.cs' 2>/dev/null || true)"
if [[ -n "${raw_string_hits}" ]]; then
  echo "${raw_string_hits}"
  echo "Backend console pages must use embedded .html assets, not C# raw-string page constants."
  violations=$((violations + 1))
fi

for file in "${pure_static_endpoint_files[@]}"; do
  path="${ROOT}/${file}"
  if [[ ! -f "${path}" ]]; then
    echo "${file}: expected static shell endpoint file."
    violations=$((violations + 1))
    continue
  fi
  mutating_hits="$(rg -n -P '\.Map(Post|Put|Delete|Patch)\s*\(' "${path}" || true)"
  if [[ -n "${mutating_hits}" ]]; then
    echo "${mutating_hits}"
    echo "${file}: static shell endpoint files must not map mutating data endpoints."
    violations=$((violations + 1))
  fi
done

for asset in "${asset_files[@]}"; do
  filename="$(basename "${asset}")"
  found=0
  for project in "${project_files[@]}"; do
    path="${ROOT}/${project}"
    if [[ -f "${path}" ]] && rg -q -F "${filename}" "${path}"; then
      found=1
      break
    fi
  done
  if [[ ${found} -ne 1 ]]; then
    echo "${asset}: expected matching EmbeddedResource entry in an owning project file."
    violations=$((violations + 1))
  fi
done

if [[ ${violations} -gt 0 ]]; then
  echo
  echo "backend_console_static_asset_guard: FAILED — ${violations} violation(s)."
  exit 1
fi

echo "backend_console_static_asset_guard: ok"
