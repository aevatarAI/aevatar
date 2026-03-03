FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish apps/sisyphus/src/Sisyphus.Host/Sisyphus.Host.csproj \
    -c Release -o /app/publish

FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY apps/sisyphus/skills /app/skills
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sisyphus.Host.dll"]
