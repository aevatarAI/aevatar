#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
CODEX_VERSION="${CODEX_VERSION:-0.144.5}"
IMAGE="${CODEX_RUNNER_IMAGE:-aevatar/codex-runner:${CODEX_VERSION}-local}"
CONTAINER_ID=""

cleanup() {
    if [[ -n "${CONTAINER_ID}" ]]; then
        docker rm --force "${CONTAINER_ID}" >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT

if [[ "${SKIP_CODEX_RUNNER_BUILD:-0}" != "1" ]]; then
    docker build \
        --build-arg "CODEX_VERSION=${CODEX_VERSION}" \
        --build-arg "REVISION=$(git -C "${REPO_ROOT}" rev-parse HEAD)" \
        --tag "${IMAGE}" \
        "${SCRIPT_DIR}"
fi

configured_environment="$(docker image inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "${IMAGE}")"
if rg -i '(^|_)(OPENAI|NYXID|OPENSANDBOX)(_.*)?=' <<< "${configured_environment}"; then
    echo "Runner image must not contain provider or control-plane credentials." >&2
    exit 1
fi

docker run --rm --entrypoint bash "${IMAGE}" -euo pipefail -c '
    test -z "$(find "${CODEX_HOME}" -mindepth 1 -print -quit)"
    test ! -e "${HOME}/.npm"
    test ! -e "${CODEX_HOME}/auth.json"
    test ! -e "${CODEX_HOME}/config.toml"
'

CONTAINER_ID="$(docker run --detach "${IMAGE}")"

actual_version="$(docker exec "${CONTAINER_ID}" codex --version)"
if [[ "${actual_version}" != "codex-cli ${CODEX_VERSION}" ]]; then
    echo "Unexpected Codex version: ${actual_version}" >&2
    exit 1
fi

docker exec "${CONTAINER_ID}" bash -euo pipefail -c '
    test "$(id -u)" = "10001"
    test "$(id -g)" = "10001"
    test "${HOME}" = "/home/codex"
    test "${CODEX_HOME}" = "/home/codex/.codex"
    test "$(stat --format "%u:%g" /home/codex)" = "10001:10001"
    test ! -e "${HOME}/.npm"
    test ! -e "${CODEX_HOME}/auth.json"
    test ! -e "${CODEX_HOME}/config.toml"
    test "${PWD}" = "/workspace"
    test -w /workspace
    test -w "${CODEX_HOME}"
    git init --quiet
    git config user.name "Aevatar Codex Runner"
    git config user.email "codex-runner@invalid"
    printf "%s\n" "# Managed codex_exec workspace" > README.md
    git add README.md
    git commit --quiet --message "Initialize managed workspace"
    test "$(git rev-parse --is-inside-work-tree)" = "true"
    test "$(git rev-list --count HEAD)" = "1"
    codex exec --help >/dev/null
'

docker stop --time 5 "${CONTAINER_ID}" >/dev/null
docker wait "${CONTAINER_ID}" >/dev/null
docker rm "${CONTAINER_ID}" >/dev/null
CONTAINER_ID=""

echo "Codex runner smoke test passed: ${IMAGE} (${actual_version})"
