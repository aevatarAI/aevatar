FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish apps/sisyphus/src/Sisyphus.Host/Sisyphus.Host.csproj \
    -c Release -o /app/publish

FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates fontconfig \
    && curl -fsSL -o /tmp/tectonic.tar.gz \
       https://github.com/tectonic-typesetting/tectonic/releases/download/tectonic%400.15.0/tectonic-0.15.0-x86_64-unknown-linux-musl.tar.gz \
    && tar xzf /tmp/tectonic.tar.gz -C /usr/local/bin tectonic \
    && rm /tmp/tectonic.tar.gz \
    && chmod +x /usr/local/bin/tectonic \
    && apt-get purge -y curl && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
COPY apps/sisyphus/skills /app/skills
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sisyphus.Host.dll"]
