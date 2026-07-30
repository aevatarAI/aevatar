#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
source "${SCRIPT_DIR}/version.env"
CODEX_VERSION="${CODEX_VERSION:-${PINNED_CODEX_VERSION}}"
RUNNER_VERSION="${RUNNER_VERSION:-${PINNED_RUNNER_VERSION}}"
IMAGE="${CODEX_RUNNER_IMAGE:-aevatar/codex-runner:${RUNNER_VERSION}-local}"
DOCKER_RUNTIME_ARGS=(
    --cpus "${CODEX_RUNNER_CPUS:-1}"
    --memory "${CODEX_RUNNER_MEMORY:-2g}"
    --pids-limit "${CODEX_RUNNER_PIDS_LIMIT:-128}"
)
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
        --build-arg "RUNNER_VERSION=${RUNNER_VERSION}" \
        --build-arg "REVISION=$(git -C "${REPO_ROOT}" rev-parse HEAD)" \
        --tag "${IMAGE}" \
        "${SCRIPT_DIR}"
fi

actual_runner_version="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.version"}}' "${IMAGE}")"
actual_codex_label="$(docker image inspect --format '{{index .Config.Labels "io.aevatar.codex.version"}}' "${IMAGE}")"
if [[ "${actual_runner_version}" != "${RUNNER_VERSION}" || "${actual_codex_label}" != "${CODEX_VERSION}" ]]; then
    echo "Unexpected image versions: runner=${actual_runner_version}, codex=${actual_codex_label}" >&2
    exit 1
fi

configured_environment="$(docker image inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "${IMAGE}")"
if grep -Eiq '(^|_)(OPENAI|NYXID|OPENSANDBOX|OPEN_SANDBOX)(_.*)?=' <<< "${configured_environment}"; then
    echo "Runner image must not contain provider or control-plane credentials." >&2
    exit 1
fi
# gVisor variant: no Credential-Proxy MITM, so SSL_CERT_FILE must NOT be set —
# the runner reaches the gateway directly with the system CA bundle.
if grep -Eiq '(^|_)SSL_CERT_FILE=' <<< "${configured_environment}"; then
    echo "gVisor variant must not pin SSL_CERT_FILE (no Credential-Proxy MITM)." >&2
    exit 1
fi

image_history="$(docker history --no-trunc --format '{{.CreatedBy}}' "${IMAGE}")"
if grep -Eiq '(OPENAI_API_KEY|NYXID_LLM_TOKEN|NYXID_LLM_DELEGATION_TOKEN|OPEN_SANDBOX_API_KEY|OPENSANDBOX_API_KEY)=' <<< "${image_history}"; then
    echo "Runner image history must not contain credential assignments." >&2
    exit 1
fi

docker run --rm --entrypoint bash "${DOCKER_RUNTIME_ARGS[@]}" "${IMAGE}" -euo pipefail -c '
    test -z "$(find "${CODEX_HOME}" -mindepth 1 -print -quit)"
    test ! -e "${HOME}/.npm"
    test ! -e "${CODEX_HOME}/auth.json"
    test ! -e "${CODEX_HOME}/config.toml"
'

CONTAINER_ID="$(docker run --detach "${DOCKER_RUNTIME_ARGS[@]}" "${IMAGE}")"

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
    test -z "${SSL_CERT_FILE:-}"
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

# gVisor variant: Codex runs no inner sandbox (the gVisor Sentry is the
# isolation boundary), so there is no local Landlock/Bubblewrap probe to run —
# isolation is a property of the deployed gVisor runtime, not the image. The
# in-image check confirms Codex can start with the inner sandbox disabled
# without falling back to a runc-only backend; end-to-end isolation is verified
# against the deployed gVisor tenant, not here.
docker exec "${CONTAINER_ID}" bash -euo pipefail -c '
    out="$(codex exec --dangerously-bypass-approvals-and-sandbox --skip-git-repo-check "noop" 2>&1 || true)"
    printf "%s\n" "${out}" | grep -Fq "danger-full-access"
    ! printf "%s\n" "${out}" | grep -Eiq "LandlockRestrict|bwrap|bubblewrap"
'

docker stop --time 5 "${CONTAINER_ID}" >/dev/null
docker wait "${CONTAINER_ID}" >/dev/null
if [[ "$(docker inspect --format '{{.State.Running}}' "${CONTAINER_ID}")" != "false" ]]; then
    echo "Runner container is still running after docker stop." >&2
    exit 1
fi
if docker top "${CONTAINER_ID}" >/dev/null 2>&1; then
    echo "Runner process tree is still visible after docker stop." >&2
    exit 1
fi
docker rm "${CONTAINER_ID}" >/dev/null
CONTAINER_ID=""

echo "Codex runner smoke test passed: ${IMAGE} (${actual_version})"
