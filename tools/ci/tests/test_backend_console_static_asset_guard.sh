#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
GUARD="${REPO_ROOT}/tools/ci/backend_console_static_asset_guard.sh"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

write_fixture() {
  local root="$1"
  mkdir -p \
    "${root}/src/Aevatar.Mainnet.Host.Api/BackendConsole" \
    "${root}/src/Aevatar.Mainnet.Host.Api/Status" \
    "${root}/src/Aevatar.Mainnet.Host.Api/Cqrs" \
    "${root}/src/Aevatar.Mainnet.Host.Api/Voice" \
    "${root}/src/Aevatar.Mainnet.Host.Api/Skills" \
    "${root}/src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi" \
    "${root}/agents/channels/Aevatar.GAgents.Channel.NyxIdRelay"

  printf '<!doctype html><script>const cfg = __BACKEND_CONSOLE_CONFIG__;</script>\n' \
    > "${root}/src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html"
  printf '<!doctype html><script>const cfg = __BACKEND_CONSOLE_CONFIG__;</script>\n' \
    > "${root}/src/Aevatar.Mainnet.Host.Api/BackendConsole/auto-callback.html"
  printf '<!doctype html><script>fetch("/api/status")</script>\n' \
    > "${root}/src/Aevatar.Mainnet.Host.Api/Status/status.html"
  printf '<!doctype html><script>const cfg = __BACKEND_CONSOLE_CONFIG__;</script>\n' \
    > "${root}/src/Aevatar.Mainnet.Host.Api/Cqrs/cqrs-observatory.html"
  printf '<!doctype html><script>const cfg = __BACKEND_CONSOLE_CONFIG__;</script>\n' \
    > "${root}/src/Aevatar.Mainnet.Host.Api/Voice/voice-console.html"
  printf '<!doctype html><script>const cfg = __BACKEND_CONSOLE_CONFIG__;</script>\n' \
    > "${root}/src/Aevatar.Mainnet.Host.Api/Skills/workflow-skills.html"
  printf '<!doctype html><script>const cfg = __BACKEND_CONSOLE_CONFIG__;</script>\n' \
    > "${root}/src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/workflow-observatory.html"
  printf '<!doctype html><script>const cfg = __BACKEND_CONSOLE_CONFIG__;</script>\n' \
    > "${root}/src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/workflow-studio.html"
  printf '<!doctype html><script>const cfg = __BACKEND_CONSOLE_CONFIG__;</script>\n' \
    > "${root}/agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/channels.html"

  cat > "${root}/src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj" <<'XML'
<Project><ItemGroup>
  <EmbeddedResource Include="BackendConsole\admin.html" />
  <EmbeddedResource Include="BackendConsole\auto-callback.html" />
  <EmbeddedResource Include="Status\status.html" />
  <EmbeddedResource Include="Cqrs\cqrs-observatory.html" />
  <EmbeddedResource Include="Voice\voice-console.html" />
  <EmbeddedResource Include="Skills\workflow-skills.html" />
</ItemGroup></Project>
XML
  cat > "${root}/src/workflow/Aevatar.Workflow.Infrastructure/Aevatar.Workflow.Infrastructure.csproj" <<'XML'
<Project><ItemGroup>
  <EmbeddedResource Include="CapabilityApi\workflow-observatory.html" />
  <EmbeddedResource Include="CapabilityApi\workflow-studio.html" />
</ItemGroup></Project>
XML
  cat > "${root}/agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/Aevatar.GAgents.Channel.NyxIdRelay.csproj" <<'XML'
<Project><ItemGroup>
  <EmbeddedResource Include="channels.html" />
</ItemGroup></Project>
XML

  for endpoint in \
    src/Aevatar.Mainnet.Host.Api/BackendConsole/AdminConsoleEndpoints.cs \
    src/Aevatar.Mainnet.Host.Api/BackendConsole/AutoConsoleCallbackEndpoints.cs \
    src/Aevatar.Mainnet.Host.Api/Cqrs/CqrsObservatoryPageEndpoints.cs \
    src/Aevatar.Mainnet.Host.Api/Voice/VoiceConsoleEndpoints.cs \
    src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/WorkflowStudioEndpoints.cs \
    agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelsEndpoints.cs
  do
    mkdir -p "$(dirname "${root}/${endpoint}")"
    printf 'public static class Fixture { public void Map(dynamic app) { app.MapGet("/x", Get); } }\n' > "${root}/${endpoint}"
  done
}

assert_fails_with() {
  local expected="$1"
  local root="$2"
  local output="${TMP_DIR}/failure.out"

  set +e
  bash "${GUARD}" --scan "${root}" > "${output}" 2>&1
  local status=$?
  set -e

  if [[ ${status} -eq 0 ]]; then
    echo "Expected backend console guard to fail."
    cat "${output}"
    exit 1
  fi

  if ! rg -q "${expected}" "${output}"; then
    echo "Expected failure output to contain: ${expected}"
    cat "${output}"
    exit 1
  fi
}

passing="${TMP_DIR}/passing"
write_fixture "${passing}"
bash "${GUARD}" --scan "${passing}" >/dev/null

hardcoded="${TMP_DIR}/hardcoded"
write_fixture "${hardcoded}"
printf '<script>const authority = "https://nyx.chrono-ai.fun";</script>\n' \
  >> "${hardcoded}/src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html"
assert_fails_with "hardcode Nyx/OIDC host facts" "${hardcoded}"

raw_string="${TMP_DIR}/raw-string"
write_fixture "${raw_string}"
cat > "${raw_string}/src/Aevatar.Mainnet.Host.Api/Status/StatusHtml.cs" <<'CS'
public static class StatusHtml { public const string Page = "<!doctype html>"; }
CS
assert_fails_with "raw-string page carrier" "${raw_string}"

mutating="${TMP_DIR}/mutating"
write_fixture "${mutating}"
cat > "${mutating}/src/Aevatar.Mainnet.Host.Api/Voice/VoiceConsoleEndpoints.cs" <<'CS'
public static class VoiceConsoleEndpoints { public void Map(dynamic app) { app.MapPost("/voice", Handle); } }
CS
assert_fails_with "must not map mutating data endpoints" "${mutating}"

wwwroot="${TMP_DIR}/wwwroot"
write_fixture "${wwwroot}"
printf '<script src="/wwwroot/app.js"></script>\n' \
  >> "${wwwroot}/src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/workflow-studio.html"
assert_fails_with "zero-build" "${wwwroot}"

echo "backend console static asset guard tests passed"
