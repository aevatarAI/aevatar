FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

RUN apt-get update \
    && apt-get install -y --no-install-recommends protobuf-compiler \
    && rm -rf /var/lib/apt/lists/*

COPY . .
# Keep restore/build intermediates in one layer, avoid persistent compiler
# servers, and remove temporary output before Kaniko snapshots the layer. The
# runtime stage only needs /app/publish.
RUN dotnet restore src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj \
        --disable-build-servers \
        --nologo \
    && dotnet publish src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        --disable-build-servers \
        --nologo \
    && dotnet nuget locals all --clear \
    && find /src -type d \( -name bin -o -name obj \) -prune -exec rm -rf '{}' +

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Distributed

EXPOSE 8080
EXPOSE 11111
EXPOSE 30000

ENTRYPOINT ["dotnet", "Aevatar.Mainnet.Host.Api.dll"]
