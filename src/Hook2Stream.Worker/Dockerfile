ARG DOTNET_SDK_VERSION=10.0.302
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/Hook2Stream.Worker/Hook2Stream.Worker.csproj
RUN dotnet publish src/Hook2Stream.Worker/Hook2Stream.Worker.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ffmpeg ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "Hook2Stream.Worker.dll"]
