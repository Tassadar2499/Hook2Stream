ARG DOTNET_SDK_VERSION=10.0.302
ARG DOTNET_SDK_DIGEST=sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION}@${DOTNET_SDK_DIGEST} AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/Hook2Stream.Worker/Hook2Stream.Worker.csproj
RUN dotnet publish src/Hook2Stream.Worker/Hook2Stream.Worker.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:1dcd9841b075d1d1013caa170b86ae58b8a8a563de9a3e319fd46a45e7ecc130 AS runtime
USER root
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ffmpeg ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Hook2Stream.Worker.dll"]
