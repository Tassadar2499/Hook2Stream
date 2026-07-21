ARG DOTNET_SDK_VERSION=10.0.302
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/Hook2Stream.Bootstrapper/Hook2Stream.Bootstrapper.csproj
RUN dotnet publish src/Hook2Stream.Bootstrapper/Hook2Stream.Bootstrapper.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "Hook2Stream.Bootstrapper.dll"]
