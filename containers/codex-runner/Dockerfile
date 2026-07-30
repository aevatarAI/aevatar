# syntax=docker/dockerfile:1.7

ARG NODE_IMAGE=docker.io/library/node:22-bookworm-slim@sha256:6c74791e557ce11fc957704f6d4fe134a7bc8d6f5ca4403205b2966bd488f6b3
FROM ${NODE_IMAGE}

ARG CODEX_VERSION=0.144.5
ARG RUNNER_VERSION=0.144.5-gvisor
ARG REVISION=unknown

LABEL org.opencontainers.image.title="Aevatar Codex runner" \
      org.opencontainers.image.description="One-shot non-root Codex CLI runner for managed codex_exec on a gVisor runtime" \
      org.opencontainers.image.source="https://github.com/aevatarAI/aevatar" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.version="${RUNNER_VERSION}" \
      io.aevatar.codex.version="${CODEX_VERSION}"

ENV NPM_CONFIG_UPDATE_NOTIFIER=false \
    NPM_CONFIG_FUND=false \
    NPM_CONFIG_AUDIT=false

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends \
        bash \
        ca-certificates \
        git \
        tini \
    && rm -rf /var/lib/apt/lists/* \
    && npm install --global "@openai/codex@${CODEX_VERSION}" \
    && rm -rf /root/.npm \
    && groupadd --gid 10001 codex \
    && useradd --uid 10001 --gid codex --create-home --shell /bin/bash codex \
    && install --directory --owner codex --group codex --mode 0700 /home/codex/.codex \
    && install --directory --owner codex --group codex --mode 0750 \
        /workspace \
        /workspace/.aevatar

# gVisor variant: Codex runs no inner sandbox (gVisor is the isolation
# boundary), so bubblewrap is not installed and use_legacy_landlock is not
# used. There is no Credential-Proxy MITM under gVisor, so SSL_CERT_FILE is not
# set — the runner reaches the NyxID gateway directly with the system CA
# bundle. /opt/aevatar-sandbox-probe existed only for the Landlock preflight
# and is dropped.
ENV HOME=/home/codex \
    CODEX_HOME=/home/codex/.codex

WORKDIR /workspace
USER codex:codex

ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["sleep", "infinity"]
