FROM node:22-bookworm-slim AS builder

WORKDIR /app

COPY package.json package-lock.json* ./
RUN npm ci --ignore-scripts

COPY tsconfig.json tsoa.json ./
COPY src/ src/
RUN npm run build

FROM node:22-bookworm-slim

WORKDIR /app

ENV NODE_ENV=production

# Install tectonic via GitHub release binary (more reliable than drop-sh for cross-platform)
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl ca-certificates libfontconfig1 libssl3 && \
    ARCH=$(dpkg --print-architecture) && \
    if [ "$ARCH" = "amd64" ]; then TECTONIC_ARCH="x86_64"; else TECTONIC_ARCH="aarch64"; fi && \
    curl -fsSL "https://github.com/tectonic-typesetting/tectonic/releases/download/tectonic%400.15.0/tectonic-0.15.0-${TECTONIC_ARCH}-unknown-linux-gnu.tar.gz" \
      -o /tmp/tectonic.tar.gz && \
    tar xzf /tmp/tectonic.tar.gz -C /usr/local/bin tectonic && \
    chmod +x /usr/local/bin/tectonic && \
    rm -f /tmp/tectonic.tar.gz && \
    apt-get purge -y curl && \
    apt-get autoremove -y && \
    rm -rf /var/lib/apt/lists/*

# Pre-warm tectonic cache
RUN echo '\documentclass{article}\usepackage{amsmath,amssymb,amsthm}\begin{document}Hello\end{document}' > /tmp/warmup.tex && \
    tectonic --untrusted /tmp/warmup.tex || true && \
    rm -f /tmp/warmup.tex /tmp/warmup.pdf

COPY package.json package-lock.json* ./
RUN npm ci --ignore-scripts --omit=dev && npm cache clean --force

COPY --from=builder /app/dist dist/
COPY --from=builder /app/src/generated/swagger.json dist/generated/swagger.json

EXPOSE 8080
USER node

CMD ["node", "dist/index.js"]
