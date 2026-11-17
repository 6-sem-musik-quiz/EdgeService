FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so this layer is cached until the csproj changes
COPY global.json Directory.Build.props ./
COPY src/EdgeService.Api/EdgeService.Api.csproj src/EdgeService.Api/
RUN dotnet restore src/EdgeService.Api/EdgeService.Api.csproj

COPY src/ src/
RUN dotnet publish src/EdgeService.Api/EdgeService.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
# The non-root "app" user (uid 1654) that the .NET images ship with.
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "EdgeService.Api.dll"]
