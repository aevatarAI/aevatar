FROM --platform=$BUILDPLATFORM node:20-bookworm-slim AS console-web-build
WORKDIR /src/apps/aevatar-console-web

RUN corepack enable \
    && corepack prepare pnpm@10.2.1 --activate

COPY apps/aevatar-console-web/package.json apps/aevatar-console-web/pnpm-lock.yaml apps/aevatar-console-web/.npmrc ./
RUN pnpm install --frozen-lockfile

COPY apps/aevatar-console-web/ ./
ARG NYXID_BASE_URL
ARG NYXID_CLIENT_ID
ARG NYXID_SCOPE
ARG ORNN_BASE_URL
RUN AEVATAR_CONSOLE_PUBLIC_PATH=/ai-assets/ \
    NYXID_BASE_URL="${NYXID_BASE_URL:-https://nyx-api.chrono-ai.fun}" \
    NYXID_CLIENT_ID="${NYXID_CLIENT_ID:-a6ff2946-f02f-4c35-8203-1ec46132b660}" \
    NYXID_REDIRECT_URI=/auth/callback \
    NYXID_SCOPE="${NYXID_SCOPE:-openid profile email offline_access urn:nyxid:scope:broker_binding proxy}" \
    ORNN_BASE_URL="${ORNN_BASE_URL:-https://ornn.chrono-ai.fun}" \
    pnpm build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

RUN apt-get update \
    && apt-get install -y --no-install-recommends protobuf-compiler \
    && rm -rf /var/lib/apt/lists/*

COPY . .
RUN dotnet restore src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj --nologo
RUN dotnet publish src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    --nologo
COPY --from=console-web-build /src/apps/aevatar-console-web/dist /app/publish/AIWorkspaceWeb

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Distributed

EXPOSE 8080
EXPOSE 11111
EXPOSE 30000

ENTRYPOINT ["dotnet", "Aevatar.Mainnet.Host.Api.dll"]
